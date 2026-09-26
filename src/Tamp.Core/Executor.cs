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

    public Executor(
        TargetGraph graph,
        ExecutionMode mode = ExecutionMode.Run,
        TextWriter? output = null,
        LogLevel verbosity = LogLevel.Info,
        BuildProjectInfo? projectInfo = null,
        IReadOnlySet<string>? skippedByUser = null,
        bool skipDependencies = false,
        IBuildReporter? reporter = null,
        IBuildEventSink? eventSink = null)
    {
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
            });

        // ── Diagnostics: root build span (ADR 0018).
        using var buildSpan = TampDiagnostics.BuildSource.StartActivity("build", ActivityKind.Internal);
        if (buildSpan is not null)
        {
            buildSpan.SetTag(TampDiagnostics.Tags.BuildTargets, string.Join(",", order.Select(s => s.Name)));
            buildSpan.SetTag(TampDiagnostics.Tags.BuildCliVersion, typeof(TampBuild).Assembly.GetName().Version?.ToString());
            // Host facets — set once per build.
            buildSpan.SetTag(TampDiagnostics.Tags.HostOs, System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            buildSpan.SetTag(TampDiagnostics.Tags.HostOsVersion, System.Environment.OSVersion.VersionString);
            buildSpan.SetTag(TampDiagnostics.Tags.HostArch, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString());
            buildSpan.SetTag(TampDiagnostics.Tags.HostCpuCount, System.Environment.ProcessorCount);
            try { buildSpan.SetTag(TampDiagnostics.Tags.HostTotalMemoryBytes, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes); } catch { /* shrug */ }
            buildSpan.SetTag(TampDiagnostics.Tags.DotnetRuntimeDescription, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            var ciVendor = TampDiagnostics.DetectCiVendor();
            buildSpan.SetTag(TampDiagnostics.Tags.CiVendor, ciVendor);
            buildSpan.SetTag(TampDiagnostics.Tags.CiIsCi, ciVendor != "local");
            if (ProjectInfo is not null)
            {
                buildSpan.SetTag(TampDiagnostics.Tags.BuildProjectName, ProjectInfo.Name);
                if (ProjectInfo.Area is not null) buildSpan.SetTag(TampDiagnostics.Tags.BuildProjectArea, ProjectInfo.Area);
                buildSpan.SetTag(TampDiagnostics.Tags.BuildProjectNameSource, ProjectInfo.NameSource.ToString().ToLowerInvariant());
            }
        }
        var commandsDispatchedCount = 0;

        foreach (var spec in order)
        {
            // One span id per target iteration; shared by its started + finished events.
            var targetSpanId = NewSpanId();

            // After a failure, only AssuredAfterFailure targets keep running.
            if (buildFailedAt.HasValue && !spec.AssuredAfterFailure)
            {
                _log.WriteRaw($"==> {spec.Name} (not run: build already failed)");
                Emit(BuildEventTypes.TargetFinished, spec.Name, targetSpanId, _buildSpanId,
                    new TargetFinishedPayload { Target = spec.Name, Status = BuildEventStatus.NotRun, Reason = "build already failed" });
                records.Add(TargetExecutionRecord.NotRun(spec.Name));
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
                EmitSkippedTargetActivity(spec, userSkipReason);
                continue;
            }

            if (CheckSkippedByCondition(spec) is { } skipReason)
            {
                _log.WriteRaw($"==> {spec.Name} (skipped: {skipReason})");
                Emit(BuildEventTypes.TargetFinished, spec.Name, targetSpanId, _buildSpanId,
                    new TargetFinishedPayload { Target = spec.Name, Status = BuildEventStatus.Skipped, Reason = skipReason });
                skipped.Add(spec.Name);
                records.Add(TargetExecutionRecord.Skipped(spec.Name, skipReason));
                EmitSkippedTargetActivity(spec, skipReason);
                continue;
            }

            // Hard preconditions (Requires) — failure here aborts the build.
            if (CheckRequirementsFailed(spec) is { } reqFail)
            {
                _log.WriteRaw($"==> {spec.Name} REQUIRES failed: {reqFail}");
                Emit(BuildEventTypes.TargetFinished, spec.Name, targetSpanId, _buildSpanId,
                    new TargetFinishedPayload
                    {
                        Target = spec.Name,
                        Status = BuildEventStatus.Failure,
                        DurationMs = 0,
                        Reason = $"Requires failed: {reqFail}",
                    });
                records.Add(TargetExecutionRecord.Failed(spec.Name, TimeSpan.Zero, $"Requires failed: {reqFail}"));
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
            var capturingOutput = new CapturingTextWriter(_redactedOutput, outputBuffer);

            // ── Diagnostics: per-target span (ADR 0018). Tags are populated at known points;
            // status is set on the activity at end of try/catch.
            using var targetSpan = TampDiagnostics.TargetsSource.StartActivity($"target:{spec.Name}", ActivityKind.Internal);
            if (targetSpan is not null)
            {
                targetSpan.SetTag(TampDiagnostics.Tags.TargetName, spec.Name);
                targetSpan.SetTag(TampDiagnostics.Tags.TargetPhase, spec.Phase.ToString());
                if (spec.Dependencies.Count > 0) targetSpan.SetTag(TampDiagnostics.Tags.TargetDependsOn, string.Join(",", spec.Dependencies));
                if (spec.AssuredAfterFailure) targetSpan.SetTag(TampDiagnostics.Tags.TargetIsAssuredAfterFailure, true);
                targetSpan.SetTag(TampDiagnostics.Tags.TargetStartWorkingSetBytes, workingSetAtStart);
                targetSpan.SetTag(TampDiagnostics.Tags.TargetFailureMode, spec.FailureMode.ToString());
                targetSpan.SetTag(TampDiagnostics.Tags.TargetAttempt, 1);                  // reserved; bumps when retry mode lands
                targetSpan.SetTag(TampDiagnostics.Tags.TargetActionsCount, actionsCount);
            }

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
                        _redactionTable.RegisterAll(plan);
                        commandsForThisTarget++;
                        commandsDispatchedCount++;
                        exit = ProcessRunner.Execute(plan, capturingOutput, capturingOutput, sourceTargetName: spec.Name);
                        if (exit != 0)
                        {
                            _log.WriteRaw($"==> {spec.Name} FAILED (exit {exit})");
                            if (spec.FailureMode == FailureMode.Continue) continue;
                            sw.Stop();
                            capturingOutput.FlushPendingLine();
                            Emit(BuildEventTypes.TargetFinished, spec.Name, targetSpanId, _buildSpanId,
                                new TargetFinishedPayload
                                {
                                    Target = spec.Name,
                                    Status = BuildEventStatus.Failure,
                                    DurationMs = sw.Elapsed.TotalMilliseconds,
                                    Reason = $"exit {exit}",
                                    OutputTail = outputBuffer.Drain(),
                                });
                            records.Add(TargetExecutionRecord.Failed(spec.Name, sw.Elapsed, $"exit {exit}"));
                                        EmitTargetTerminal(targetSpan, spec, sw.Elapsed, swStartTicks, allocAtStart, workingSetAtStart, gen0AtStart, gen1AtStart, gen2AtStart, cpuAtStart, commandsForThisTarget, TampDiagnostics.Tags.OutcomeFailure, $"exit {exit}");
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
                Emit(BuildEventTypes.TargetFinished, spec.Name, targetSpanId, _buildSpanId,
                    new TargetFinishedPayload { Target = spec.Name, Status = BuildEventStatus.Success, DurationMs = sw.Elapsed.TotalMilliseconds });
                records.Add(TargetExecutionRecord.Done(spec.Name, sw.Elapsed));
                EmitTargetTerminal(targetSpan, spec, sw.Elapsed, swStartTicks, allocAtStart, workingSetAtStart, gen0AtStart, gen1AtStart, gen2AtStart, cpuAtStart, commandsForThisTarget, TampDiagnostics.Tags.OutcomeSuccess);
            }
            catch (Exception ex) when (spec.FailureMode == FailureMode.Continue)
            {
                sw.Stop();
                _log.WriteRaw($"==> {spec.Name} threw {ex.GetType().Name}; continuing per FailureMode.Continue: {ex.Message}");
                Emit(BuildEventTypes.TargetFinished, spec.Name, targetSpanId, _buildSpanId,
                    new TargetFinishedPayload { Target = spec.Name, Status = BuildEventStatus.Success, DurationMs = sw.Elapsed.TotalMilliseconds });
                records.Add(TargetExecutionRecord.Done(spec.Name, sw.Elapsed));
                EmitTargetTerminal(targetSpan, spec, sw.Elapsed, swStartTicks, allocAtStart, workingSetAtStart, gen0AtStart, gen1AtStart, gen2AtStart, cpuAtStart, commandsForThisTarget, TampDiagnostics.Tags.OutcomeSuccess);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.WriteRaw($"==> {spec.Name} threw {ex.GetType().Name}: {ex.Message}");
                capturingOutput.FlushPendingLine();
                Emit(BuildEventTypes.TargetFinished, spec.Name, targetSpanId, _buildSpanId,
                    new TargetFinishedPayload
                    {
                        Target = spec.Name,
                        Status = BuildEventStatus.Failure,
                        DurationMs = sw.Elapsed.TotalMilliseconds,
                        Reason = $"{ex.GetType().Name}: {ex.Message}",
                        OutputTail = outputBuffer.Drain(),
                    });
                records.Add(TargetExecutionRecord.Failed(spec.Name, sw.Elapsed, ex.Message));
                EmitTargetTerminal(targetSpan, spec, sw.Elapsed, swStartTicks, allocAtStart, workingSetAtStart, gen0AtStart, gen1AtStart, gen2AtStart, cpuAtStart, commandsForThisTarget, TampDiagnostics.Tags.OutcomeFailure, $"{ex.GetType().Name}: {ex.Message}");
                if (!buildFailedAt.HasValue)
                {
                    buildFailedAt = (spec.Name, 1);
                    handlersInvoked.AddRange(DispatchFailureHandlers(spec.Name));
                }
            }

            nextSpec:;
        }

        buildSw.Stop();
        WriteBuildSummary(records, buildSw.Elapsed, buildFailedAt?.Name);
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

        // ── Diagnostics: close out the root build span + counters.
        var buildOutcome = buildExitCode == 0
            ? TampDiagnostics.Tags.OutcomeSuccess
            : TampDiagnostics.Tags.OutcomeFailure;

        if (buildSpan is not null)
        {
            buildSpan.SetTag(TampDiagnostics.Tags.BuildExitCode, buildExitCode);
            buildSpan.SetTag(TampDiagnostics.Tags.OutcomeKey, buildOutcome);
            buildSpan.SetTag(TampDiagnostics.Tags.BuildDurationNs, buildDurationNs);
            buildSpan.SetTag(TampDiagnostics.Tags.BuildPeakWorkingSetBytes, peakWorkingSetBytes);
            buildSpan.SetTag(TampDiagnostics.Tags.BuildTargetsTotal, records.Count);
            buildSpan.SetTag(TampDiagnostics.Tags.BuildTargetsSucceeded, succeeded);
            buildSpan.SetTag(TampDiagnostics.Tags.BuildTargetsFailed, failed);
            buildSpan.SetTag(TampDiagnostics.Tags.BuildTargetsSkipped, skippedCount);
            buildSpan.SetTag(TampDiagnostics.Tags.BuildTargetsNotRun, notRun);
            buildSpan.SetTag(TampDiagnostics.Tags.BuildCommandsTotal, commandsDispatchedCount);
            if (buildFailedAt is { Name: var failedName2, ExitCode: var failedExit })
            {
                buildSpan.SetTag(TampDiagnostics.Tags.BuildFailureTarget, failedName2);
                buildSpan.SetTag(TampDiagnostics.Tags.BuildFailureExitCode, failedExit);
                buildSpan.SetStatus(ActivityStatusCode.Error, $"failed at: {failedName2}");
            }
            else
            {
                buildSpan.SetStatus(ActivityStatusCode.Ok);
            }
            if (handlersInvoked.Count > 0)
                buildSpan.SetTag(TampDiagnostics.Tags.BuildFailureHandlersInvoked, string.Join(",", handlersInvoked));

            // Structured snapshot — single event, easy to grep / dashboard.
            buildSpan.AddEvent(new ActivityEvent("tamp.build.summary", tags: new ActivityTagsCollection
            {
                [TampDiagnostics.Tags.BuildTargetsTotal] = records.Count,
                [TampDiagnostics.Tags.BuildTargetsSucceeded] = succeeded,
                [TampDiagnostics.Tags.BuildTargetsFailed] = failed,
                [TampDiagnostics.Tags.BuildTargetsSkipped] = skippedCount,
                [TampDiagnostics.Tags.BuildTargetsNotRun] = notRun,
                [TampDiagnostics.Tags.BuildCommandsTotal] = commandsDispatchedCount,
                [TampDiagnostics.Tags.BuildExitCode] = buildExitCode,
                [TampDiagnostics.Tags.OutcomeKey] = buildOutcome,
            }));
        }

        TampDiagnostics.BuildsTotal.Add(1, new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, buildOutcome));
        TampDiagnostics.BuildDurationMs.Record(buildSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, buildOutcome));
        TampDiagnostics.BuildPeakMemoryBytes.Record(peakWorkingSetBytes, new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, buildOutcome));

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

    /// <summary>
    /// Finalize a per-target activity and emit the matching metric samples.
    /// Centralized so success / failure / continue-on-failure paths emit identically.
    /// Includes high-res timing (Stopwatch ticks → nanoseconds), memory deltas,
    /// per-target GC-collection counts, CPU time, and command/action counts.
    /// </summary>
    private static void EmitTargetTerminal(
        Activity? span,
        TargetSpec spec,
        TimeSpan elapsed,
        long swStartTicks,
        long allocAtStart,
        long workingSetAtStart,
        int gen0AtStart,
        int gen1AtStart,
        int gen2AtStart,
        TimeSpan cpuAtStart,
        int commandsCount,
        string outcome,
        string? errorMessage = null)
    {
        var endTicks = Stopwatch.GetTimestamp();
        var durationNs = (long)((endTicks - swStartTicks) * 1_000_000_000.0 / Stopwatch.Frequency);
        var allocDelta = System.Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocAtStart);
        var gen0Delta = GC.CollectionCount(0) - gen0AtStart;
        var gen1Delta = GC.CollectionCount(1) - gen1AtStart;
        var gen2Delta = GC.CollectionCount(2) - gen2AtStart;
        long workingSetAtEnd;
        double cpuDeltaMs;
        try
        {
            using var p = Process.GetCurrentProcess();
            workingSetAtEnd = p.WorkingSet64;
            cpuDeltaMs = (p.TotalProcessorTime - cpuAtStart).TotalMilliseconds;
        }
        catch { workingSetAtEnd = 0; cpuDeltaMs = 0; }

        if (span is not null)
        {
            span.SetTag(TampDiagnostics.Tags.TargetStatus, outcome);
            span.SetTag(TampDiagnostics.Tags.TargetDurationNs, durationNs);
            span.SetTag(TampDiagnostics.Tags.TargetEndWorkingSetBytes, workingSetAtEnd);
            span.SetTag(TampDiagnostics.Tags.TargetGcAllocatedBytes, allocDelta);
            span.SetTag(TampDiagnostics.Tags.TargetGcGen0Collections, gen0Delta);
            span.SetTag(TampDiagnostics.Tags.TargetGcGen1Collections, gen1Delta);
            span.SetTag(TampDiagnostics.Tags.TargetGcGen2Collections, gen2Delta);
            span.SetTag(TampDiagnostics.Tags.TargetCpuTimeMs, cpuDeltaMs);
            span.SetTag(TampDiagnostics.Tags.TargetCommandsCount, commandsCount);
            span.SetTag(TampDiagnostics.Tags.TargetAttemptsTotal, 1);                       // reserved; retry-mode bumps later
            span.SetStatus(outcome == TampDiagnostics.Tags.OutcomeSuccess ? ActivityStatusCode.Ok : ActivityStatusCode.Error, errorMessage);
        }
        TampDiagnostics.TargetsExecuted.Add(1,
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.TargetName, spec.Name),
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, outcome));
        TampDiagnostics.TargetDurationMs.Record(elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.TargetName, spec.Name),
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, outcome));
        TampDiagnostics.TargetGcAllocatedBytes.Record(allocDelta,
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.TargetName, spec.Name),
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, outcome));
    }

    /// <summary>Emit a zero-duration "skipped" activity so dashboards see every plan-position, not just the executed ones.</summary>
    private static void EmitSkippedTargetActivity(TargetSpec spec, string reason)
    {
        using var span = TampDiagnostics.TargetsSource.StartActivity($"target:{spec.Name}", ActivityKind.Internal);
        if (span is not null)
        {
            span.SetTag(TampDiagnostics.Tags.TargetName, spec.Name);
            span.SetTag(TampDiagnostics.Tags.TargetPhase, spec.Phase.ToString());
            span.SetTag(TampDiagnostics.Tags.TargetStatus, TampDiagnostics.Tags.OutcomeSkipped);
            span.SetTag(TampDiagnostics.Tags.TargetSkipReason, reason);
            span.SetStatus(ActivityStatusCode.Ok);
        }
        TampDiagnostics.TargetsExecuted.Add(1,
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.TargetName, spec.Name),
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, TampDiagnostics.Tags.OutcomeSkipped));
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

