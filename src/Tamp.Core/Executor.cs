using System.Diagnostics;
using System.Security.Cryptography;
using Tamp.Diagnostics;

namespace Tamp;

/// <summary>
/// Runs a target graph in topological order. v0 is sequential; resource
/// scheduling, parallelism, and per-target retry are recorded on the spec
/// but not yet honored here. (Filed in TAM-25 follow-ups.)
/// </summary>
/// <remarks>
/// The executor honors <see cref="ITargetDefinition.OnlyWhen"/> conditions,
/// <see cref="ITargetDefinition.Requires"/> hard preconditions,
/// <see cref="ITargetDefinition.AssuredAfterFailure"/> cleanup semantics,
/// and <see cref="ITargetDefinition.OnFailureOf"/> failure handlers.
///
/// On a target failure, the executor:
/// <list type="number">
///   <item>Records the failure (target name + exit code or exception).</item>
///   <item>Looks up handlers via <see cref="TargetGraph.HandlersFor"/> and
///         runs each handler's full sub-tree (handler + its own deps).</item>
///   <item>Continues iterating the main plan, but only runs targets that
///         declared <see cref="ITargetDefinition.AssuredAfterFailure"/>;
///         everything else is marked <see cref="TargetStatus.NotRun"/>.</item>
///   <item>Returns the original failure's exit code regardless of whether
///         handlers or assured-after-failure cleanups succeeded.</item>
/// </list>
/// </remarks>
public sealed class Executor
{
    private readonly RedactionTable _redactionTable;
    private readonly RedactingTextWriter _redactedOutput;
    private readonly Logger _log;

    // ── Canonical event stream (`#0a`, ADR 0019). The executor emits every
    // lifecycle fact once through _sink; consumers are projections over it.
    // In `#0a` the sink fans out to a ReporterProjectionSink (so the public
    // IBuildReporter surface keeps working) plus any additional sink (the
    // agent NDJSON sink lands in #9). The ADR-0018 ActivitySource/Meter
    // emission below stays inline for now and is folded into a projection —
    // and this dual path removed — in #0c.
    private readonly IBuildEventSink _sink;
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private readonly string _workerId = WorkerIdResolver.Resolve();
    private long _seq;
    private string _buildId = Guid.NewGuid().ToString("N");
    private string _traceId = string.Empty;
    private string _buildSpanId = string.Empty;
    // Current target context for ambient events (#13, BuildEvents). Updated per
    // iteration so a diagnostic/artifact emitted from a target body parents to
    // that target's span; null between targets (ambient events parent to the build span).
    private string? _currentTargetId;
    private string? _currentTargetSpanId;
    // #20 capability enforcement.
    private readonly CapabilityMode _capabilityMode;
    private readonly bool _allowSideEffects;
    // #15 --from: when set, only these targets run; others in the order are
    // external upstream, assumed satisfied (fail-closed on their declared Produces).
    private readonly IReadOnlySet<string>? _runOnly;
    // #16 --rule: active in-target rule filters, echoed into remedy.reproduce.
    private readonly IReadOnlyList<string> _ruleFilters;
    // #16: distinct error-level diagnostic ruleIds emitted during the current target,
    // used to infer a rule-scoped reproduce command. Reset per target.
    private readonly HashSet<string> _currentTargetErrorRules = new(StringComparer.Ordinal);
    // #17 cache advisory.
    private readonly bool _cacheAdvice;
    private readonly AbsolutePath? _hashCachePath;
    private HashCache? _hashCache;
    private string? _currentInputsHash;   // computed once per target (start), reused at terminal
    private bool? _currentWouldSkip;       // advisory: inputs matched last successful run
    // #23 agent economics: opt-in per-target log capture (tee target output to a redacted
    // .tamp/logs/<buildId>/<target>.log so an agent can fetch one failing log lazily).
    private readonly bool _captureLogs;
    private string? _currentLogPath;       // worktree-rel path of the current target's log (capture on + ran)
    // #23: reproduce command per failed target, for the compact failure summary.
    private readonly Dictionary<string, string> _failureReproduce = new(StringComparer.Ordinal);

    public Executor(
        TargetGraph graph,
        ExecutionMode mode = ExecutionMode.Run,
        TextWriter? output = null,
        LogLevel verbosity = LogLevel.Info,
        BuildProjectInfo? projectInfo = null,
        IReadOnlySet<string>? skippedByUser = null,
        bool skipDependencies = false,
        IBuildReporter? reporter = null,
        IBuildEventSink? eventSink = null,
        CapabilityMode capabilityMode = CapabilityMode.Off,
        bool allowSideEffects = false,
        IReadOnlySet<string>? runOnly = null,
        IReadOnlyList<string>? ruleFilters = null,
        bool cacheAdvice = false,
        AbsolutePath? hashCachePath = null,
        bool captureLogs = false)
    {
        _capabilityMode = capabilityMode;
        _allowSideEffects = allowSideEffects;
        _runOnly = runOnly;
        _ruleFilters = ruleFilters ?? Array.Empty<string>();
        _cacheAdvice = cacheAdvice;
        _hashCachePath = hashCachePath;
        _captureLogs = captureLogs;
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Mode = mode;
        Output = output ?? Console.Out;
        ProjectInfo = projectInfo;
        SkippedByUser = skippedByUser ?? new HashSet<string>(StringComparer.Ordinal);
        SkipDependencies = skipDependencies;
        Reporter = reporter ?? NoopBuildReporter.Instance;
        _sink = eventSink is null
            ? new CompositeBuildEventSink(new ReporterProjectionSink(Reporter))
            : new CompositeBuildEventSink(new ReporterProjectionSink(Reporter), eventSink);
        _redactionTable = new RedactionTable();
        _redactedOutput = new RedactingTextWriter(Output, _redactionTable);
        _log = new Logger(_redactedOutput, verbosity);
    }

    /// <summary>Mint a fresh 16-hex (8-byte) span id for the canonical event stream.</summary>
    private static string NewSpanId()
    {
        Span<byte> b = stackalloc byte[8];
        RandomNumberGenerator.Fill(b);
        return Convert.ToHexString(b).ToLowerInvariant();
    }

    /// <summary>Repo root for the build.started payload; null when it can't be resolved (RootDirectory throws off-repo).</summary>
    private static string? SafeWorktree()
    {
        try { return TampBuild.RootDirectory.Value; }
        catch { return null; }
    }

    /// <summary>
    /// Drain the per-target output ring buffer and redact registered secrets from every line.
    /// The <see cref="CapturingTextWriter"/> captures raw child output (only the console writer
    /// redacts), so the failure tail must be scrubbed before it reaches an event payload / reporter
    /// (which then forwards it to Telegram, NDJSON, etc.). Redacts both the new canonical
    /// target.finished.OutputTail and — via the ReporterProjectionSink — the IBuildReporter path.
    /// </summary>
    private IReadOnlyList<string> RedactedTail(TargetOutputBuffer buffer)
        => buffer.Drain().Select(l => _redactionTable.Redact(l)).ToArray();

    // ── #12 typed-result synthesis ──────────────────────────────────────────

    /// <summary>Emit a success target.finished with synthesized Outputs (from Produces globs) + InputsHash, plus one artifact.produced per output.</summary>
    private void EmitTargetSuccess(TargetSpec spec, string targetSpanId, TimeSpan elapsed)
    {
        var outputs = SynthesizeOutputs(spec);
        if (outputs is { Count: > 0 })
            foreach (var a in outputs)
                Emit(BuildEventTypes.ArtifactProduced, spec.Name, NewSpanId(), targetSpanId,
                    new ArtifactProducedPayload { Path = a.Path, Hash = a.Hash, Kind = a.Kind, SizeBytes = a.SizeBytes });

        // #17: record the successful input hash for the next run's advisory.
        if (_cacheAdvice && _hashCache is not null && _currentInputsHash is not null)
            _hashCache.Set(spec.Name, _currentInputsHash);

        Emit(BuildEventTypes.TargetFinished, spec.Name, targetSpanId, _buildSpanId,
            new TargetFinishedPayload
            {
                Target = spec.Name,
                Status = BuildEventStatus.Success,
                DurationMs = elapsed.TotalMilliseconds,
                Outputs = outputs,
                InputsHash = _currentInputsHash,
                WouldSkip = _currentWouldSkip,
                LogPath = _currentLogPath,
            });
    }

    /// <summary>Emit a failure target.finished with a structured remedy (reproduce + hint + coarse class) and InputsHash.</summary>
    private void EmitTargetFailure(TargetSpec spec, string targetSpanId, TimeSpan elapsed, string reason, bool isRequiresFailure, IReadOnlyList<string>? outputTail)
    {
        var reproduce = ReproduceCommand(spec.Name);
        _failureReproduce[spec.Name] = reproduce;   // #23: feed the compact failure summary
        Emit(BuildEventTypes.TargetFinished, spec.Name, targetSpanId, _buildSpanId,
            new TargetFinishedPayload
            {
                Target = spec.Name,
                Status = BuildEventStatus.Failure,
                DurationMs = elapsed.TotalMilliseconds,
                Reason = reason,
                OutputTail = outputTail,
                InputsHash = _currentInputsHash,
                WouldSkip = _currentWouldSkip,
                LogPath = _currentLogPath,
                Remedy = new TargetRemedy
                {
                    Class = isRequiresFailure ? "config" : "code",
                    Reproduce = reproduce,
                    Hint = reason,
                },
            });
    }

    /// <summary>
    /// Capability gate (#20): for a <see cref="CapabilityTier.SideEffectful"/> tier, emit a
    /// <c>gate.evaluated</c> event and return whether it is blocked (agent mode + not elevated).
    /// Safe / Grey tiers are not gated (returns false, no event).
    /// </summary>
    private bool GateSideEffectBlocked(CapabilityTier tier, string targetName, string targetSpanId, string what)
    {
        if (tier != CapabilityTier.SideEffectful) return false;
        if (_capabilityMode != CapabilityMode.Agent) return false;   // Off: no enforcement, no gate event
        var blocked = !_allowSideEffects;
        var reason = blocked
            ? $"{what}: side-effect capability required (agent mode; elevate with --allow-side-effects)"
            : $"{what}: side-effect permitted";
        Emit(BuildEventTypes.GateEvaluated, targetName, NewSpanId(), targetSpanId,
            new GateEvaluatedPayload { Gate = "capability", Verdict = blocked ? "fail" : "pass", Blocks = blocked, Reason = reason });
        return blocked;
    }

    /// <summary>
    /// #16: the reproduce command for a failed target. Echoes active <c>--rule</c> filters;
    /// else, if the target emitted exactly one distinct error-level diagnostic ruleId, scopes
    /// to it; else re-runs the whole target.
    /// </summary>
    private string ReproduceCommand(string targetName)
    {
        if (_ruleFilters.Count > 0)
            return $"tamp {targetName} " + string.Join(" ", _ruleFilters.Select(r => $"--rule {r}"));
        if (_currentTargetErrorRules.Count == 1)
            return $"tamp {targetName} --rule {_currentTargetErrorRules.First()}";
        return $"tamp {targetName}";
    }

    /// <summary>#15 fail-closed: the target's declared Produces globs that currently match no file on disk. Empty when it declares none (can't verify).</summary>
    private static IReadOnlyList<string> MissingDeclaredArtifacts(TargetSpec spec)
    {
        if (spec.ProducedGlobs.Count == 0) return Array.Empty<string>();
        var missing = new List<string>();
        foreach (var glob in spec.ProducedGlobs)
        {
            try { if (TampBuild.RootDirectory.GlobFiles(glob).Count == 0) missing.Add(glob); }
            catch { missing.Add(glob); }
        }
        return missing;
    }

    /// <summary>The target's input hash when it declared one; observability-only (#17 migration primitive). Null / best-effort otherwise.</summary>
    private static string? SafeInputsHash(TargetSpec spec)
    {
        if (spec.InputHashProducer is null) return null;
        try { return spec.InputHashProducer(); } catch { return null; }
    }

    /// <summary>Glob the target's Produces patterns (relative to the worktree), hashing each file. Null when the target declares none.</summary>
    private static IReadOnlyList<ArtifactInfo>? SynthesizeOutputs(TargetSpec spec)
    {
        if (spec.ProducedGlobs.Count == 0) return null;
        var infos = new List<ArtifactInfo>();
        foreach (var glob in spec.ProducedGlobs)
        {
            foreach (var file in TampBuild.RootDirectory.GlobFiles(glob))
            {
                if (!file.FileExists()) continue;
                string? hash = null;
                long? size = null;
                try
                {
                    var bytes = File.ReadAllBytes(file.Value);
                    hash = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    size = bytes.LongLength;
                }
                catch { /* unreadable file — still report path + kind */ }
                infos.Add(new ArtifactInfo
                {
                    Path = RelativeToRoot(file),
                    Hash = hash,
                    SizeBytes = size,
                    Kind = InferKind(file.Extension),
                });
            }
        }
        return infos;
    }

    private static string RelativeToRoot(AbsolutePath file)
    {
        try { return Path.GetRelativePath(TampBuild.RootDirectory.Value, file.Value).Replace('\\', '/'); }
        catch { return file.Value; }
    }

    /// <summary>
    /// #23: open a redacted per-target log at <c>.tamp/logs/&lt;buildId&gt;/&lt;target&gt;.log</c>.
    /// The <see cref="RedactingTextWriter"/> wraps the file stream so secrets never reach disk.
    /// Best-effort: returns null (capture silently off for this target) if the file can't be opened.
    /// </summary>
    private (StreamWriter Stream, RedactingTextWriter Writer, string RelPath)? OpenTargetLog(string targetName)
    {
        try
        {
            var dir = (TampBuild.RootDirectory / ".tamp" / "logs" / _buildId).CreateDirectory();
            var file = dir / (SanitizeLogName(targetName) + ".log");
            var stream = new StreamWriter(file.Value, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var writer = new RedactingTextWriter(stream, _redactionTable);
            return (stream, writer, RelativeToRoot(file));
        }
        catch { return null; }
    }

    /// <summary>Make a target name safe as a filename (targets are usually identifiers; be defensive about odd names).</summary>
    private static string SanitizeLogName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        var safe = new string(chars);
        return string.IsNullOrWhiteSpace(safe) ? "target" : safe;
    }

    private static string InferKind(string extension) => extension.ToLowerInvariant() switch
    {
        ".dll" or ".exe" => "assembly",
        ".nupkg" or ".snupkg" => "package",
        ".sarif" => "sarif",
        ".json" => "json",
        ".xml" => "xml",
        ".zip" or ".tar" or ".gz" or ".tgz" => "archive",
        _ => "file",
    };

    /// <summary>Emit one canonical <see cref="BuildEvent"/> to the fan-out sink.</summary>
    private void Emit(string type, string? targetId, string spanId, string? parentSpanId, BuildEventPayload payload)
        => _sink.Emit(new BuildEvent
        {
            Type = type,
            BuildId = _buildId,
            RunId = _runId,
            TargetId = targetId,
            TraceId = _traceId,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            WorkerId = _workerId,
            Seq = Interlocked.Increment(ref _seq),
            Ts = DateTimeOffset.UtcNow,
            Payload = payload,
        });

    /// <summary>
    /// Build-lifecycle event sink. Defaults to <see cref="NoopBuildReporter"/>;
    /// set via constructor to receive structured events (e.g. NDJSON via
    /// <see cref="JsonBuildReporter"/> from <c>--reporter=json</c>). TAM-140.
    /// </summary>
    public IBuildReporter Reporter { get; }

    /// <summary>
    /// User-supplied set of target names to treat as already-satisfied
    /// (via the <c>--skip &lt;target&gt;</c> CLI flag, TAM-207). Dependents
    /// of a user-skipped target still run; the skipped target's own
    /// <c>Executes</c> block is a no-op and recorded as
    /// <see cref="TargetStatus.Skipped"/> with a "skipped by --skip" reason.
    /// </summary>
    public IReadOnlySet<string> SkippedByUser { get; }

    /// <summary>
    /// When <c>true</c> (via the <c>--skip-deps</c> CLI flag), every target
    /// in the execution order that is NOT one of the explicitly-named roots
    /// is treated as skipped. Useful for "I know what I'm doing, just retry
    /// this one target" debugging loops.
    /// </summary>
    public bool SkipDependencies { get; }

    public TargetGraph Graph { get; }
    public ExecutionMode Mode { get; }
    public TextWriter Output { get; }

    /// <summary>Resolved project identification (from [BuildProject] or fallback). May be null when not provided.</summary>
    public BuildProjectInfo? ProjectInfo { get; }

    /// <summary>The build-script logger. Verbosity controls what reaches the sink.</summary>
    public Logger Log => _log;

    /// <summary>
    /// The redaction table populated as targets run. Exposed for tests and
    /// for callers who want to register additional secrets ahead of time.
    /// </summary>
    public RedactionTable RedactionTable => _redactionTable;

    /// <summary>Run / dry-run / plan one or more invoked targets.</summary>
    public ExecutionResult Run(params string[] rootTargetNames)
    {
        var order = Graph.ComputeExecutionOrder(rootTargetNames);
        var rootSet = new HashSet<string>(rootTargetNames, StringComparer.Ordinal);

        return Mode switch
        {
            ExecutionMode.Plan => RunPlan(order, rootTargetNames),
            ExecutionMode.DryRun => RunDryRun(order, rootSet),
            ExecutionMode.Run => RunActual(order, rootSet),
            _ => throw new InvalidOperationException($"Unknown execution mode: {Mode}"),
        };
    }

    /// <summary>
    /// Returns true if <paramref name="spec"/> should be skipped per user CLI
    /// configuration (<c>--skip</c> / <c>--skip-deps</c>). Out-parameter
    /// returns a human-readable reason for the build summary table.
    /// </summary>
    private bool IsSkippedByUser(TargetSpec spec, IReadOnlySet<string> rootSet, out string reason)
    {
        if (SkippedByUser.Contains(spec.Name))
        {
            reason = "skipped by --skip";
            return true;
        }
        if (SkipDependencies && !rootSet.Contains(spec.Name))
        {
            reason = "skipped by --skip-deps";
            return true;
        }
        reason = string.Empty;
        return false;
    }

    private ExecutionResult RunPlan(IReadOnlyList<TargetSpec> order, IReadOnlyList<string> roots)
    {
        var rootLabel = string.Join(", ", roots);
        _log.WriteRaw($"Plan for '{rootLabel}' ({order.Count} target{(order.Count == 1 ? "" : "s")}):");
        _log.WriteRaw();
        foreach (var spec in order)
        {
            var phase = spec.Phase == Phase.None ? string.Empty : $" [{spec.Phase}]";
            _log.WriteRaw($"  {spec.Name}{phase}");
            if (spec.Dependencies.Count > 0)
                _log.WriteRaw($"    depends on: {string.Join(", ", spec.Dependencies)}");
            if (spec.OrderAfter.Count > 0)
                _log.WriteRaw($"    after: {string.Join(", ", spec.OrderAfter)}");
            if (spec.OrderBefore.Count > 0)
                _log.WriteRaw($"    before: {string.Join(", ", spec.OrderBefore)}");
            if (spec.Triggers.Count > 0)
                _log.WriteRaw($"    triggers: {string.Join(", ", spec.Triggers)}");
            if (spec.TriggeredBy.Count > 0)
                _log.WriteRaw($"    triggered by: {string.Join(", ", spec.TriggeredBy)}");
            if (spec.OnlyWhenConditions.Count > 0)
                foreach (var c in spec.OnlyWhenConditions)
                    _log.WriteRaw($"    only when: {c.ExpressionText}");
            if (spec.Requirements.Count > 0)
                foreach (var c in spec.Requirements)
                    _log.WriteRaw($"    requires: {c.ExpressionText}");
            if (spec.AssuredAfterFailure)
                _log.WriteRaw($"    assured-after-failure: yes");
            if (spec.RequiresNetwork || spec.RequiresDocker || spec.RequiresAdmin)
            {
                var caps = new List<string>();
                if (spec.RequiresNetwork) caps.Add("network");
                if (spec.RequiresDocker) caps.Add("docker");
                if (spec.RequiresAdmin) caps.Add("admin");
                _log.WriteRaw($"    requires: {string.Join(", ", caps)}");
            }
        }
        _log.Flush();
        return new ExecutionResult { Mode = Mode, ExitCode = 0, TargetsTraversed = order.Count };
    }

    private ExecutionResult RunDryRun(IReadOnlyList<TargetSpec> order, IReadOnlySet<string> rootSet)
    {
        _log.WriteRaw("[DRY RUN] No commands will execute.");
        _log.WriteRaw();
        var planCount = 0;
        foreach (var spec in order)
        {
            if (IsSkippedByUser(spec, rootSet, out var userSkipReason))
            {
                _log.WriteRaw($"[skipped] {spec.Name} — {userSkipReason}");
                _log.WriteRaw();
                continue;
            }
            if (CheckSkippedByCondition(spec) is { } skipReason)
            {
                _log.WriteRaw($"[skipped] {spec.Name} — only-when condition false: {skipReason}");
                _log.WriteRaw();
                continue;
            }
            foreach (var factory in spec.PlanFactories)
            {
                foreach (var plan in factory())
                {
                    _redactionTable.RegisterAll(plan);
                    ProcessRunner.Print(plan, spec.Name, sourceModule: null, _redactedOutput);
                    planCount++;
                }
            }
        }
        _log.Flush();
        return new ExecutionResult { Mode = Mode, ExitCode = 0, TargetsTraversed = order.Count, CommandPlansPrinted = planCount };
    }

    private ExecutionResult RunActual(IReadOnlyList<TargetSpec> order, IReadOnlySet<string> rootSet)
    {
        var records = new List<TargetExecutionRecord>(order.Count);
        var skipped = new List<string>();
        var handlersInvoked = new List<string>();
        (string Name, int ExitCode)? buildFailedAt = null;
        var buildSw = Stopwatch.StartNew();
        var buildSwStartTicks = Stopwatch.GetTimestamp();

        // ── Canonical build.started (`#0a`). Mint the build/trace/span identity,
        // then emit; the ReporterProjectionSink turns this into IBuildReporter.OnBuildStart.
        _buildId = Guid.NewGuid().ToString("N");
        _traceId = _buildId;
        _buildSpanId = NewSpanId();
        Emit(BuildEventTypes.BuildStarted, targetId: null, spanId: _buildSpanId, parentSpanId: null,
            new BuildStartedPayload
            {
                RequestedTargets = rootSet.ToList(),
                ExecutionClosure = order.Select(s => s.Name).ToList(),
                Worktree = SafeWorktree(),
                EnforcementMode = _capabilityMode.ToString().ToLowerInvariant(),
            });

        // Activate the ambient emitter (#13) so target bodies / helpers can emit
        // diagnostic.emitted / artifact.produced (#12) parented to the current target span.
        using var ambientScope = BuildEvents.Activate(new BuildEventScope((type, payload) =>
        {
            // #16: remember error-level diagnostic ruleIds for the current target so a
            // rule-scoped remedy.reproduce can be inferred.
            if (type == BuildEventTypes.DiagnosticEmitted && payload is DiagnosticEmittedPayload d
                && d.Level == "error" && !string.IsNullOrEmpty(d.RuleId))
                _currentTargetErrorRules.Add(d.RuleId);
            Emit(type, _currentTargetId, NewSpanId(), _currentTargetSpanId ?? _buildSpanId, payload);
        }));

        // #17: load the per-worktree input-hash store for the would-skip advisory.
        if (_cacheAdvice)
            _hashCache = HashCache.Load(_hashCachePath ?? (TampBuild.RootDirectory / ".tamp" / "cache" / "input-hashes.json"));

        // ── Diagnostics: root build span (ADR 0018) — annotated via the projection (#0c).
        using var buildSpan = TampDiagnostics.BuildSource.StartActivity("build", ActivityKind.Internal);
        DiagnosticsProjection.AnnotateBuildStart(buildSpan, order.Select(s => s.Name).ToList(), ProjectInfo);
        var commandsDispatchedCount = 0;

        foreach (var spec in order)
        {
            // One span id per target iteration; shared by its started + finished events.
            var targetSpanId = NewSpanId();
            // Point ambient events (#13) at this target's span for the iteration.
            _currentTargetId = spec.Name;
            _currentTargetSpanId = targetSpanId;
            _currentTargetErrorRules.Clear();   // #16: per-target diagnostic-rule tracking

            // #17 cache advisory: compute the input hash once (reused at terminal) and,
            // when advice is on, compare to the last successful run — log "would skip" but
            // never actually skip.
            _currentInputsHash = SafeInputsHash(spec);
            _currentWouldSkip = null;
            _currentLogPath = null;   // #23: set only if this target actually runs with capture on
            if (_cacheAdvice && _hashCache is not null && _currentInputsHash is not null)
            {
                _currentWouldSkip = _hashCache.Get(spec.Name) == _currentInputsHash;
                if (_currentWouldSkip == true)
                    _log.WriteRaw($"==> {spec.Name} [cache-advice] inputs unchanged — would skip (advisory; still running)");
            }

            // After a failure, only AssuredAfterFailure targets keep running.
            if (buildFailedAt.HasValue && !spec.AssuredAfterFailure)
            {
                _log.WriteRaw($"==> {spec.Name} (not run: build already failed)");
                Emit(BuildEventTypes.TargetFinished, spec.Name, targetSpanId, _buildSpanId,
                    new TargetFinishedPayload { Target = spec.Name, Status = BuildEventStatus.NotRun, Reason = "build already failed" });
                records.Add(TargetExecutionRecord.NotRun(spec.Name));
                continue;
            }

            // #15 --from slice: a target outside the run set is external upstream,
            // assumed satisfied. Fail-closed — verify its declared Produces artifacts
            // exist on disk before skipping; a declared-but-missing artifact fails the build.
            if (_runOnly is not null && !_runOnly.Contains(spec.Name))
            {
                var missing = MissingDeclaredArtifacts(spec);
                if (missing.Count > 0)
                {
                    var reason = $"--from fail-closed: '{spec.Name}' is upstream of the slice but its declared artifact(s) are missing: {string.Join(", ", missing)}";
                    _log.WriteRaw($"==> {spec.Name} FAIL-CLOSED ({reason})");
                    EmitTargetFailure(spec, targetSpanId, TimeSpan.Zero, reason, isRequiresFailure: false, outputTail: null);
                    records.Add(TargetExecutionRecord.Failed(spec.Name, TimeSpan.Zero, reason));
                    if (!buildFailedAt.HasValue)
                    {
                        buildFailedAt = (spec.Name, 1);
                        handlersInvoked.AddRange(DispatchFailureHandlers(spec.Name));
                    }
                    continue;
                }
                const string satisfied = "assumed satisfied (outside --from slice)";
                _log.WriteRaw($"==> {spec.Name} ({satisfied})");
                Emit(BuildEventTypes.TargetFinished, spec.Name, targetSpanId, _buildSpanId,
                    new TargetFinishedPayload { Target = spec.Name, Status = BuildEventStatus.Skipped, Reason = satisfied });
                skipped.Add(spec.Name);
                records.Add(TargetExecutionRecord.Skipped(spec.Name, satisfied));
                DiagnosticsProjection.EmitSkipped(spec, satisfied);
                continue;
            }

            // User-driven skip (--skip / --skip-deps) — TAM-207. Treat the
            // target as already-satisfied so dependents continue to run.
            if (IsSkippedByUser(spec, rootSet, out var userSkipReason))
            {
                _log.WriteRaw($"==> {spec.Name} ({userSkipReason})");
                Emit(BuildEventTypes.TargetFinished, spec.Name, targetSpanId, _buildSpanId,
                    new TargetFinishedPayload { Target = spec.Name, Status = BuildEventStatus.Skipped, Reason = userSkipReason });
                skipped.Add(spec.Name);
                records.Add(TargetExecutionRecord.Skipped(spec.Name, userSkipReason));
                DiagnosticsProjection.EmitSkipped(spec, userSkipReason);
                continue;
            }

            if (CheckSkippedByCondition(spec) is { } skipReason)
            {
                _log.WriteRaw($"==> {spec.Name} (skipped: {skipReason})");
                Emit(BuildEventTypes.TargetFinished, spec.Name, targetSpanId, _buildSpanId,
                    new TargetFinishedPayload { Target = spec.Name, Status = BuildEventStatus.Skipped, Reason = skipReason });
                skipped.Add(spec.Name);
                records.Add(TargetExecutionRecord.Skipped(spec.Name, skipReason));
                DiagnosticsProjection.EmitSkipped(spec, skipReason);
                continue;
            }

            // Hard preconditions (Requires) — failure here aborts the build.
            if (CheckRequirementsFailed(spec) is { } reqFail)
            {
                _log.WriteRaw($"==> {spec.Name} REQUIRES failed: {reqFail}");
                EmitTargetFailure(spec, targetSpanId, TimeSpan.Zero, $"Requires failed: {reqFail}", isRequiresFailure: true, outputTail: null);
                records.Add(TargetExecutionRecord.Failed(spec.Name, TimeSpan.Zero, $"Requires failed: {reqFail}"));
                if (!buildFailedAt.HasValue)
                {
                    buildFailedAt = (spec.Name, 1);
                    handlersInvoked.AddRange(DispatchFailureHandlers(spec.Name));
                }
                continue;
            }

            // #20 target-level capability gate (library-mode side effects declared via .Capability()).
            if (spec.Capability == CapabilityTier.SideEffectful
                && GateSideEffectBlocked(spec.Capability, spec.Name, targetSpanId, $"target '{spec.Name}'"))
            {
                var capReason = "blocked: target requires side-effect capability (agent mode; elevate with --allow-side-effects)";
                _log.WriteRaw($"==> {spec.Name} BLOCKED ({capReason})");
                EmitTargetFailure(spec, targetSpanId, TimeSpan.Zero, capReason, isRequiresFailure: false, outputTail: null);
                records.Add(TargetExecutionRecord.Failed(spec.Name, TimeSpan.Zero, capReason));
                if (!buildFailedAt.HasValue)
                {
                    buildFailedAt = (spec.Name, 1);
                    handlersInvoked.AddRange(DispatchFailureHandlers(spec.Name));
                }
                continue;
            }

            _log.WriteRaw($"==> {spec.Name}");
            Emit(BuildEventTypes.TargetStarted, spec.Name, targetSpanId, _buildSpanId,
                new TargetStartedPayload { Target = spec.Name });
            var sw = Stopwatch.StartNew();
            var swStartTicks = Stopwatch.GetTimestamp();
            var allocAtStart = GC.GetTotalAllocatedBytes(precise: false);
            var gen0AtStart = GC.CollectionCount(0);
            var gen1AtStart = GC.CollectionCount(1);
            var gen2AtStart = GC.CollectionCount(2);
            TimeSpan cpuAtStart;
            long workingSetAtStart;
            try { using var p = Process.GetCurrentProcess(); workingSetAtStart = p.WorkingSet64; cpuAtStart = p.TotalProcessorTime; }
            catch { workingSetAtStart = 0; cpuAtStart = TimeSpan.Zero; }
            var actionsCount = spec.Actions.Count;
            var commandsForThisTarget = 0;

            // Per-target output ring buffer (TAM-230 — reporters surface failure context).
            // Wrap _redactedOutput with a CapturingTextWriter so every line the target's
            // CommandPlans emit lands in the buffer; on failure we drain the tail into
            // the TargetFailureDetail. Inline writes via _log.WriteRaw bypass this
            // (they're framework prose, not target output).
            var outputBuffer = new TargetOutputBuffer();

            // #23: when --capture-logs is on, tee this target's output to a redacted per-target
            // log file (in addition to the console). Each sink redacts independently; the file
            // never sees a secret. Best-effort — a failed open just disables capture for this target.
            var logHandle = _captureLogs ? OpenTargetLog(spec.Name) : null;
            _currentLogPath = logHandle?.RelPath;
            var innerWriter = logHandle is { } h
                ? (TextWriter)new TeeTextWriter(_redactedOutput, h.Writer)
                : _redactedOutput;
            var capturingOutput = new CapturingTextWriter(innerWriter, outputBuffer);

            // ── Diagnostics: per-target span (ADR 0018). Tags are populated at known points;
            // status is set on the activity at end of try/catch.
            using var targetSpan = TampDiagnostics.TargetsSource.StartActivity($"target:{spec.Name}", ActivityKind.Internal);
            DiagnosticsProjection.AnnotateTargetStart(targetSpan, spec, workingSetAtStart, actionsCount);

            try
            {
                foreach (var action in spec.Actions)
                    action();

                // Async-action bridge (TAM-181): await Task-returning lambdas at the target
                // boundary. .GetAwaiter().GetResult() rather than .Wait() so the caller sees
                // the original exception type, not AggregateException.
                foreach (var asyncAction in spec.AsyncActions)
                    asyncAction().GetAwaiter().GetResult();

                var exit = 0;

                // Resolve async plan factories first (await once, then dispatch like sync).
                // Splicing into the sync loop preserves the existing FailureMode / exit-code
                // behavior; the only difference is the .GetAwaiter().GetResult() boundary.
                var allFactories = spec.PlanFactories.Concat(
                    spec.AsyncPlanFactories.Select<Func<Task<IEnumerable<CommandPlan>>>, Func<IEnumerable<CommandPlan>>>(
                        af => () => af().GetAwaiter().GetResult()));

                foreach (var factory in allFactories)
                {
                    foreach (var plan in factory())
                    {
                        // #20 capability gate at dispatch. A plan is side-effectful if it
                        // stamps SideEffectful or declares Secrets (secret reveal). Blocked
                        // in agent mode unless elevated — the command never runs.
                        var planTier = plan.RequiredCapability;
                        if (plan.Secrets.Count > 0 && planTier < CapabilityTier.SideEffectful)
                            planTier = CapabilityTier.SideEffectful;
                        if (GateSideEffectBlocked(planTier, spec.Name, targetSpanId, $"command '{plan.Executable}'"))
                        {
                            sw.Stop();
                            capturingOutput.FlushPendingLine();
                            var capReason = $"blocked: side-effect capability required for '{plan.Executable}' (agent mode; elevate with --allow-side-effects)";
                            _log.WriteRaw($"==> {spec.Name} BLOCKED ({capReason})");
                            EmitTargetFailure(spec, targetSpanId, sw.Elapsed, capReason, isRequiresFailure: false, outputTail: RedactedTail(outputBuffer));
                            records.Add(TargetExecutionRecord.Failed(spec.Name, sw.Elapsed, capReason));
                            DiagnosticsProjection.AnnotateTargetTerminal(targetSpan, spec, new TargetTerminalTelemetry(sw.Elapsed, swStartTicks, allocAtStart, workingSetAtStart, gen0AtStart, gen1AtStart, gen2AtStart, cpuAtStart, commandsForThisTarget, TampDiagnostics.Tags.OutcomeFailure, capReason));
                            if (!buildFailedAt.HasValue)
                            {
                                buildFailedAt = (spec.Name, 1);
                                handlersInvoked.AddRange(DispatchFailureHandlers(spec.Name));
                            }
                            goto nextSpec;
                        }

                        _redactionTable.RegisterAll(plan);
                        commandsForThisTarget++;
                        commandsDispatchedCount++;

                        // Canonical command events (#11): a per-command span parented to
                        // the target span. secret.access.requested comes from plan.Secrets
                        // (name + capability, never the value); tool.invoked carries the
                        // redaction-scrubbed argv; tool.exited carries exit + duration.
                        var cmdSpanId = NewSpanId();
                        foreach (var secret in plan.Secrets)
                            Emit(BuildEventTypes.SecretAccessRequested, spec.Name, cmdSpanId, targetSpanId,
                                new SecretAccessRequestedPayload { Name = secret.Name, Capability = "secret.reveal" });
                        Emit(BuildEventTypes.ToolInvoked, spec.Name, cmdSpanId, targetSpanId,
                            new ToolInvokedPayload
                            {
                                Tool = plan.Executable,
                                ArgvRedacted = plan.Arguments.Select(a => _redactionTable.Redact(a)).ToArray(),
                                Cwd = plan.WorkingDirectory,
                            });
                        var cmdSw = Stopwatch.StartNew();
                        exit = ProcessRunner.Execute(plan, capturingOutput, capturingOutput, sourceTargetName: spec.Name);
                        cmdSw.Stop();
                        Emit(BuildEventTypes.ToolExited, spec.Name, cmdSpanId, targetSpanId,
                            new ToolExitedPayload { Tool = plan.Executable, ExitCode = exit, DurationMs = cmdSw.Elapsed.TotalMilliseconds });
                        if (exit != 0)
                        {
                            _log.WriteRaw($"==> {spec.Name} FAILED (exit {exit})");
                            if (spec.FailureMode == FailureMode.Continue) continue;
                            sw.Stop();
                            capturingOutput.FlushPendingLine();
                            EmitTargetFailure(spec, targetSpanId, sw.Elapsed, $"exit {exit}", isRequiresFailure: false, outputTail: RedactedTail(outputBuffer));
                            records.Add(TargetExecutionRecord.Failed(spec.Name, sw.Elapsed, $"exit {exit}"));
                            DiagnosticsProjection.AnnotateTargetTerminal(targetSpan, spec, new TargetTerminalTelemetry(sw.Elapsed, swStartTicks, allocAtStart, workingSetAtStart, gen0AtStart, gen1AtStart, gen2AtStart, cpuAtStart, commandsForThisTarget, TampDiagnostics.Tags.OutcomeFailure, $"exit {exit}"));
                            if (!buildFailedAt.HasValue)
                            {
                                buildFailedAt = (spec.Name, exit);
                                handlersInvoked.AddRange(DispatchFailureHandlers(spec.Name));
                            }
                            goto nextSpec;
                        }
                    }
                }

                sw.Stop();
                EmitTargetSuccess(spec, targetSpanId, sw.Elapsed);
                records.Add(TargetExecutionRecord.Done(spec.Name, sw.Elapsed));
                DiagnosticsProjection.AnnotateTargetTerminal(targetSpan, spec, new TargetTerminalTelemetry(sw.Elapsed, swStartTicks, allocAtStart, workingSetAtStart, gen0AtStart, gen1AtStart, gen2AtStart, cpuAtStart, commandsForThisTarget, TampDiagnostics.Tags.OutcomeSuccess, null));
            }
            catch (Exception ex) when (spec.FailureMode == FailureMode.Continue)
            {
                sw.Stop();
                _log.WriteRaw($"==> {spec.Name} threw {ex.GetType().Name}; continuing per FailureMode.Continue: {ex.Message}");
                EmitTargetSuccess(spec, targetSpanId, sw.Elapsed);
                records.Add(TargetExecutionRecord.Done(spec.Name, sw.Elapsed));
                DiagnosticsProjection.AnnotateTargetTerminal(targetSpan, spec, new TargetTerminalTelemetry(sw.Elapsed, swStartTicks, allocAtStart, workingSetAtStart, gen0AtStart, gen1AtStart, gen2AtStart, cpuAtStart, commandsForThisTarget, TampDiagnostics.Tags.OutcomeSuccess, null));
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.WriteRaw($"==> {spec.Name} threw {ex.GetType().Name}: {ex.Message}");
                capturingOutput.FlushPendingLine();
                EmitTargetFailure(spec, targetSpanId, sw.Elapsed, $"{ex.GetType().Name}: {ex.Message}", isRequiresFailure: false, outputTail: RedactedTail(outputBuffer));
                records.Add(TargetExecutionRecord.Failed(spec.Name, sw.Elapsed, ex.Message));
                DiagnosticsProjection.AnnotateTargetTerminal(targetSpan, spec, new TargetTerminalTelemetry(sw.Elapsed, swStartTicks, allocAtStart, workingSetAtStart, gen0AtStart, gen1AtStart, gen2AtStart, cpuAtStart, commandsForThisTarget, TampDiagnostics.Tags.OutcomeFailure, $"{ex.GetType().Name}: {ex.Message}"));
                if (!buildFailedAt.HasValue)
                {
                    buildFailedAt = (spec.Name, 1);
                    handlersInvoked.AddRange(DispatchFailureHandlers(spec.Name));
                }
            }

            nextSpec:;

            // #23: flush + close this target's log file on every exit path (success, failure, block, throw).
            if (logHandle is { } lh)
            {
                try { lh.Writer.Flush(); } catch { }
                try { lh.Stream.Dispose(); } catch { }
            }
        }

        // Between targets / at build end, ambient events parent to the build span.
        _currentTargetId = null;
        _currentTargetSpanId = null;

        // #17: persist the updated input-hash store for the next run's advisory.
        if (_cacheAdvice) _hashCache?.Save();

        buildSw.Stop();
        WriteBuildSummary(records, buildSw.Elapsed, buildFailedAt?.Name);
        WriteCompactFailureSummary(records);
        _log.Flush();

        var traversed = records.Count(r => r.Status is not TargetStatus.NotRun);
        var buildExitCode = buildFailedAt?.ExitCode ?? 0;
        var buildEndTicks = Stopwatch.GetTimestamp();
        var buildDurationNs = (long)((buildEndTicks - buildSwStartTicks) * 1_000_000_000.0 / Stopwatch.Frequency);

        long peakWorkingSetBytes = 0;
        try { using var p = Process.GetCurrentProcess(); peakWorkingSetBytes = p.PeakWorkingSet64; } catch { }

        var succeeded = records.Count(r => r.Status == TargetStatus.Done);
        var failed = records.Count(r => r.Status == TargetStatus.Failed);
        var skippedCount = records.Count(r => r.Status == TargetStatus.Skipped);
        var notRun = records.Count(r => r.Status == TargetStatus.NotRun);

        // ── Diagnostics: close out the root build span + counters via the projection (#0c).
        var buildOutcome = buildExitCode == 0
            ? TampDiagnostics.Tags.OutcomeSuccess
            : TampDiagnostics.Tags.OutcomeFailure;

        DiagnosticsProjection.AnnotateBuildEnd(buildSpan, new BuildEndTelemetry(
            ExitCode: buildExitCode,
            Outcome: buildOutcome,
            DurationNs: buildDurationNs,
            PeakWorkingSetBytes: peakWorkingSetBytes,
            Elapsed: buildSw.Elapsed,
            TargetsTotal: records.Count,
            Succeeded: succeeded,
            Failed: failed,
            Skipped: skippedCount,
            NotRun: notRun,
            CommandsTotal: commandsDispatchedCount,
            FailureTarget: buildFailedAt?.Name,
            FailureExitCode: buildFailedAt?.ExitCode,
            HandlersInvoked: handlersInvoked));

        // ── Canonical build.finished (`#0a`). ReporterProjectionSink turns this
        // into IBuildReporter.OnBuildEnd. Status vocabulary ("succeeded"/"failed")
        // is preserved for the reporter surface.
        Emit(BuildEventTypes.BuildFinished, targetId: null, spanId: _buildSpanId, parentSpanId: null,
            new BuildFinishedPayload
            {
                Status = buildExitCode == 0 ? "succeeded" : "failed",
                DurationMs = buildSw.Elapsed.TotalMilliseconds,
                ExitCode = buildExitCode,
                FirstFailedTarget = buildFailedAt?.Name,
                TargetsTotal = records.Count,
                Succeeded = succeeded,
                Failed = failed,
                Skipped = skippedCount,
                NotRun = notRun,
                CommandsTotal = commandsDispatchedCount,
            });

        return new ExecutionResult
        {
            Mode = Mode,
            ExitCode = buildExitCode,
            TargetsTraversed = traversed,
            FailedTarget = buildFailedAt?.Name,
            FailureHandlersInvoked = handlersInvoked,
            SkippedTargets = skipped,
            ExecutionRecords = records,
            Duration = buildSw.Elapsed,
        };
    }

    /// <summary>Returns the failure reason (for human display) when an OnlyWhen condition rejects a target; null otherwise.</summary>
    private static string? CheckSkippedByCondition(TargetSpec spec)
    {
        foreach (var c in spec.OnlyWhenConditions)
        {
            bool result;
            try { result = c.Predicate(); }
            catch (Exception ex) { return $"{c.ExpressionText} threw {ex.GetType().Name}: {ex.Message}"; }
            if (!result) return c.ExpressionText;
        }
        return null;
    }

    /// <summary>Returns the failed expression text when a Requires precondition fails; null when all hold.</summary>
    private static string? CheckRequirementsFailed(TargetSpec spec)
    {
        foreach (var c in spec.Requirements)
        {
            bool result;
            try { result = c.Predicate(); }
            catch (Exception ex) { return $"{c.ExpressionText} threw {ex.GetType().Name}: {ex.Message}"; }
            if (!result) return c.ExpressionText;
        }
        return null;
    }

    private IEnumerable<string> DispatchFailureHandlers(string failedTargetName)
    {
        var handlers = Graph.HandlersFor(failedTargetName);
        if (handlers.Count == 0) yield break;

        _log.WriteRaw();
        _log.WriteRaw($"Running {handlers.Count} failure handler{(handlers.Count == 1 ? "" : "s")} for {failedTargetName}:");
        foreach (var handler in handlers)
        {
            yield return handler.Name;
            try
            {
                var subOrder = Graph.ComputeExecutionOrder(handler.Name);
                foreach (var sub in subOrder)
                {
                    if (CheckSkippedByCondition(sub) is { } skip)
                    {
                        _log.WriteRaw($"==> {sub.Name} (skipped: {skip})");
                        continue;
                    }
                    _log.WriteRaw($"==> {sub.Name} (failure handler for {failedTargetName})");
                    foreach (var action in sub.Actions) action();
                    foreach (var asyncAction in sub.AsyncActions) asyncAction().GetAwaiter().GetResult();
                    var subAllFactories = sub.PlanFactories.Concat(
                        sub.AsyncPlanFactories.Select<Func<Task<IEnumerable<CommandPlan>>>, Func<IEnumerable<CommandPlan>>>(
                            af => () => af().GetAwaiter().GetResult()));
                    foreach (var factory in subAllFactories)
                    {
                        foreach (var plan in factory())
                        {
                            _redactionTable.RegisterAll(plan);
                            var subExit = ProcessRunner.Execute(plan, _redactedOutput, _redactedOutput);
                            if (subExit != 0)
                            {
                                _log.WriteRaw($"==> {sub.Name} (handler) FAILED (exit {subExit}); original failure stands");
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception hex)
            {
                _log.WriteRaw($"==> {handler.Name} (handler) threw {hex.GetType().Name}: {hex.Message}; original failure stands");
            }
        }
    }

    private void WriteBuildSummary(IReadOnlyList<TargetExecutionRecord> records, TimeSpan duration, string? failedTargetName)
    {
        if (records.Count == 0) return;

        _log.WriteRaw();
        _log.WriteRaw("─── Build Summary ───");
        var nameWidth = Math.Max("Target".Length, records.Max(r => r.Name.Length));
        var statusWidth = Math.Max("Status".Length, records.Max(r => r.Status.ToString().Length));

        _log.WriteRaw($"  {"Target".PadRight(nameWidth)}   {"Status".PadRight(statusWidth)}   Duration");
        foreach (var r in records)
        {
            var statusGlyph = r.Status switch
            {
                TargetStatus.Done => "✓",
                TargetStatus.Failed => "✗",
                TargetStatus.Skipped => "·",
                TargetStatus.NotRun => "—",
                _ => "?",
            };
            var status = $"{statusGlyph} {r.Status}";
            var durationText = r.Status is TargetStatus.Skipped or TargetStatus.NotRun
                ? string.Empty
                : FormatDuration(r.Duration);
            _log.WriteRaw($"  {r.Name.PadRight(nameWidth)}   {status.PadRight(statusWidth + 2)}   {durationText}");
        }
        _log.WriteRaw($"  {"".PadRight(nameWidth)}   {"".PadRight(statusWidth + 2)}   ─────");
        _log.WriteRaw($"  {"Total".PadRight(nameWidth)}   {"".PadRight(statusWidth + 2)}   {FormatDuration(duration)}");

        if (failedTargetName is not null)
        {
            _log.WriteRaw();
            _log.WriteRaw($"BUILD FAILED — first failed target: {failedTargetName}");
        }
    }

    /// <summary>
    /// #23 agent economics: after the summary table, print a terse, always-on block listing
    /// only the failed targets, each with its reason and #12 <c>remedy.reproduce</c>. This is the
    /// actionable bit an agent needs without re-reading the whole table or output stream.
    /// </summary>
    private void WriteCompactFailureSummary(IReadOnlyList<TargetExecutionRecord> records)
    {
        var failures = records.Where(r => r.Status == TargetStatus.Failed).ToList();
        if (failures.Count == 0) return;

        _log.WriteRaw();
        _log.WriteRaw($"FAILED ({failures.Count}):");
        foreach (var f in failures)
        {
            _log.WriteRaw($"  {f.Name} — {f.FailureReason}");
            if (_failureReproduce.TryGetValue(f.Name, out var reproduce))
                _log.WriteRaw($"      reproduce: {reproduce}");
        }
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalMilliseconds < 1000) return $"{d.TotalMilliseconds:F0} ms";
        if (d.TotalSeconds < 60) return $"{d.TotalSeconds:F1} s";
        return $"{(int)d.TotalMinutes}m {d.Seconds}s";
    }
}

/// <summary>Per-target outcome inside a build run.</summary>
public sealed record TargetExecutionRecord
{
    public required string Name { get; init; }
    public required TargetStatus Status { get; init; }
    public TimeSpan Duration { get; init; }
    public string? FailureReason { get; init; }

    public static TargetExecutionRecord Done(string name, TimeSpan duration)
        => new() { Name = name, Status = TargetStatus.Done, Duration = duration };
    public static TargetExecutionRecord Failed(string name, TimeSpan duration, string reason)
        => new() { Name = name, Status = TargetStatus.Failed, Duration = duration, FailureReason = reason };
    public static TargetExecutionRecord Skipped(string name, string reason)
        => new() { Name = name, Status = TargetStatus.Skipped, FailureReason = reason };
    public static TargetExecutionRecord NotRun(string name)
        => new() { Name = name, Status = TargetStatus.NotRun };
}

public enum TargetStatus { Done, Failed, Skipped, NotRun }

/// <summary>Outcome of a single executor run.</summary>
public sealed record ExecutionResult
{
    public required ExecutionMode Mode { get; init; }
    public required int ExitCode { get; init; }
    public int TargetsTraversed { get; init; }
    public int CommandPlansPrinted { get; init; }
    public string? FailedTarget { get; init; }
    public IReadOnlyList<string> FailureHandlersInvoked { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SkippedTargets { get; init; } = Array.Empty<string>();
    public IReadOnlyList<TargetExecutionRecord> ExecutionRecords { get; init; } = Array.Empty<TargetExecutionRecord>();
    public TimeSpan Duration { get; init; }
}

