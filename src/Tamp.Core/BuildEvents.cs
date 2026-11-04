using System.Linq;

namespace Tamp;

/// <summary>
/// Ambient emitter for canonical <see cref="BuildEvent"/>s that originate from
/// <em>inside a target</em> or a helper library — diagnostics (#13), produced
/// artifacts (#12), gate verdicts (#20), and imperative tool dispatch (#66) —
/// rather than from the executor's own lifecycle. The executor activates a scope
/// for the duration of a run and keeps it pointed at the current target's span,
/// so ambient events land in the same stream with correct envelope identity and
/// parenting.
/// </summary>
/// <remarks>
/// <b>No-op when inactive.</b> Outside a run (or in unit code with no build
/// active) every method is a silent no-op, so library code may call these
/// unconditionally. <b>Threading:</b> the scope flows via <see cref="System.Threading.AsyncLocal{T}"/>,
/// so it reaches async continuations of the target body but not threads the target
/// spawns itself; target code is normally synchronous on the executor thread.
/// </remarks>
public static class BuildEvents
{
    private static readonly System.Threading.AsyncLocal<BuildEventScope?> Current = new();

    // #66: when the executor is dispatching a CommandPlan through its own loop it emits
    // tool.invoked / tool.exited itself, then calls ProcessRunner.Execute. Suppress the
    // ambient emission for exactly that call so the declarative path is not double-emitted.
    private static readonly System.Threading.AsyncLocal<bool> Suppressed = new();

    /// <summary>True when a build run has an active emission scope.</summary>
    public static bool IsActive => Current.Value is not null;

    /// <summary>Emit a SARIF-normalized <c>diagnostic.emitted</c> event. No-op if inactive.</summary>
    public static void Diagnostic(string ruleId, string level, string message, string? file = null, int? line = null, string? fixHint = null)
    {
        var scope = Current.Value;
        if (scope is null) return;
        scope.Emit(BuildEventTypes.DiagnosticEmitted, new DiagnosticEmittedPayload
        {
            RuleId = ruleId,
            Level = level,
            Message = message,
            Location = file is null ? null : new DiagnosticLocation { File = file, Line = line },
            FixHint = fixHint,
        });
    }

    /// <summary>
    /// Emit a <c>conformance.evaluated</c> event (ADR 0023) — an ADR-conformance verdict. No-op if
    /// inactive. <paramref name="verdict"/> is four-valued (<see cref="ConformanceVerdict"/>);
    /// <paramref name="adrQuote"/> + <paramref name="codeEvidence"/> are the required structured reason
    /// on a <c>fail</c>. The optional <paramref name="ztPillar"/>/<paramref name="ztFunction"/>/
    /// <paramref name="ztStage"/> (maturity) or <paramref name="mandateId"/> (binary mandate) carry the
    /// producing rule's Zero-Trust overlay onto the evidence (additive; schema stays 1.0).
    /// </summary>
    public static void Conformance(
        string adrRef,
        string ruleId,
        string verdict,
        string method,
        string? adrQuote = null,
        string? codeEvidence = null,
        string? file = null,
        int? line = null,
        bool blocks = false,
        Provenance? provenance = null,
        IReadOnlyList<string>? controlRefs = null,
        string? ztPillar = null,
        string? ztFunction = null,
        int? ztStage = null,
        string? mandateId = null)
    {
        var scope = Current.Value;
        if (scope is null) return;
        scope.Emit(BuildEventTypes.ConformanceEvaluated, new ConformanceEvaluatedPayload
        {
            AdrRef = adrRef,
            RuleId = ruleId,
            Verdict = verdict,
            Method = method,
            AdrQuote = adrQuote,
            CodeEvidence = codeEvidence,
            Location = file is null ? null : new DiagnosticLocation { File = file, Line = line },
            Blocks = blocks,
            Provenance = provenance,
            ControlRefs = controlRefs,
            ZtPillar = ztPillar,
            ZtFunction = ztFunction,
            ZtStage = ztStage,
            MandateId = mandateId,
        });
    }

    /// <summary>Emit an <c>artifact.produced</c> event. No-op if inactive. (The framework also synthesizes these from a target's <c>Produces</c> globs; this is for explicit/incremental announcements from target code.)</summary>
    public static void Artifact(string path, string? hash = null, string? kind = null, long? sizeBytes = null)
    {
        var scope = Current.Value;
        if (scope is null) return;
        scope.Emit(BuildEventTypes.ArtifactProduced, new ArtifactProducedPayload
        {
            Path = path,
            Hash = hash,
            Kind = kind,
            SizeBytes = sizeBytes,
        });
    }

    /// <summary>
    /// #124: emit <c>service.ready</c>: a service the target started is usable at <paramref name="url"/>.
    /// No-op if inactive; returns the span the event went on, or <c>null</c> when inactive.
    /// </summary>
    /// <param name="kind">The service kind, as the wrapper names it (e.g. <c>heft-dev-server</c>).</param>
    /// <param name="url">Where to reach the service.</param>
    /// <param name="spanId">
    /// The span to emit on. Pass the command span <see cref="ToolInvoked"/> returned for the service's process so
    /// <c>tool.invoked</c>, <c>service.ready</c>, <c>service.stopped</c> and <c>tool.exited</c> share one span. When
    /// null, a new span parented to the current target is used; pass the returned id to <see cref="ServiceStopped"/>.
    /// </param>
    /// <param name="port">The listening port.</param>
    /// <param name="pid">PID of the root process serving it.</param>
    /// <param name="processStartTime">When that process started (UTC).</param>
    /// <param name="readinessProbe">What answered the readiness check, when it differs from <paramref name="url"/>.</param>
    /// <param name="statePath">A state file describing the running service, when one is written.</param>
    /// <remarks>
    /// The scope flows through <see cref="System.Threading.AsyncLocal{T}"/>, so a call from a thread the target
    /// didn't start (a signal handler, a process-exit callback) is a no-op. Emit from the target's own flow.
    /// </remarks>
    public static string? ServiceReady(
        string kind,
        string url,
        string? spanId = null,
        int? port = null,
        int? pid = null,
        DateTimeOffset? processStartTime = null,
        string? readinessProbe = null,
        string? statePath = null)
    {
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("kind is required.", nameof(kind));
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("url is required.", nameof(url));
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), port, "port must be 0-65535.");
        var scope = Current.Value;
        if (scope is null) return null;

        var span = spanId ?? scope.NewSpanId();
        scope.Emit(BuildEventTypes.ServiceReady, span, new ServiceReadyPayload
        {
            Kind = kind,
            Url = scope.Redact(url),
            Port = port,
            Pid = pid,
            ProcessStartTime = processStartTime?.ToUniversalTime(),
            ReadinessProbe = readinessProbe is null ? null : scope.Redact(readinessProbe),
            StatePath = statePath,
        });
        return span;
    }

    /// <summary>
    /// #124: emit <c>service.stopped</c> with a <see cref="ServiceStopReason"/>. No-op if inactive. Emit it whether
    /// or not the service ever became ready (a failed start is <c>exited</c> or <c>timeout</c>).
    /// </summary>
    /// <param name="kind">Same as for <see cref="ServiceReady"/>.</param>
    /// <param name="reason">One of <see cref="ServiceStopReason"/>.</param>
    /// <param name="spanId">The span <see cref="ServiceReady"/> used (or the command span); null emits on a new span.</param>
    /// <param name="pid">PID of the root process.</param>
    /// <param name="exitCode">The process's exit code, when known.</param>
    /// <param name="durationMs">How long the service ran.</param>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is not in the pinned vocabulary.</exception>
    public static void ServiceStopped(
        string kind,
        string reason,
        string? spanId = null,
        int? pid = null,
        int? exitCode = null,
        double? durationMs = null)
    {
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("kind is required.", nameof(kind));
        if (!ServiceStopReason.All.Contains(reason))
            throw new ArgumentException(
                $"'{reason}' is not a service stop reason; use one of {string.Join(", ", ServiceStopReason.All)}.", nameof(reason));
        if (durationMs is < 0) throw new ArgumentOutOfRangeException(nameof(durationMs), durationMs, "durationMs must not be negative.");
        var scope = Current.Value;
        if (scope is null) return;

        scope.Emit(BuildEventTypes.ServiceStopped, spanId ?? scope.NewSpanId(), new ServiceStoppedPayload
        {
            Kind = kind,
            Reason = reason,
            Pid = pid,
            ExitCode = exitCode,
            DurationMs = durationMs,
        });
    }

    /// <summary>
    /// #66: announce that a child process is about to be spawned for <paramref name="plan"/> —
    /// emits <c>secret.access.requested</c> for each declared secret and <c>tool.invoked</c> (with
    /// argv scrubbed through the build's redaction table), all sharing one command span parented to
    /// the current target. Returns that span id to hand to <see cref="ToolExited"/>, or <c>null</c>
    /// when inactive or suppressed (the paired <see cref="ToolExited"/> then no-ops).
    /// </summary>
    /// <remarks>
    /// Called by <see cref="ProcessRunner.Execute"/> so an <em>imperative</em> dispatch
    /// (<c>Executes(() =&gt; { ProcessRunner.Execute(plan); })</c>) and a failure-handler dispatch
    /// are visible on the canonical stream, not only on the ADR-0018 span. The executor's own
    /// declarative loop emits these itself and suppresses this call to avoid double emission.
    /// </remarks>
    public static string? ToolInvoked(CommandPlan plan)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        var scope = Current.Value;
        if (scope is null || Suppressed.Value) return null;

        scope.RegisterSecrets(plan);
        var cmdSpanId = scope.NewSpanId();
        foreach (var secret in plan.Secrets)
            scope.Emit(BuildEventTypes.SecretAccessRequested, cmdSpanId,
                new SecretAccessRequestedPayload { Name = secret.Name, Capability = "secret.reveal" });
        scope.Emit(BuildEventTypes.ToolInvoked, cmdSpanId, new ToolInvokedPayload
        {
            Tool = plan.Executable,
            ArgvRedacted = plan.Arguments.Select(a => scope.Redact(a)).ToArray(),
            Cwd = plan.WorkingDirectory,
        });
        return cmdSpanId;
    }

    /// <summary>#66: pair of <see cref="ToolInvoked"/> — emit <c>tool.exited</c> on the same command
    /// span. No-op when <paramref name="cmdSpanId"/> is <c>null</c> (i.e. the invoke was inactive or
    /// suppressed).</summary>
    public static void ToolExited(string? cmdSpanId, string tool, int exitCode, double durationMs)
    {
        var scope = Current.Value;
        if (scope is null || cmdSpanId is null) return;
        scope.Emit(BuildEventTypes.ToolExited, cmdSpanId, new ToolExitedPayload
        {
            Tool = tool,
            ExitCode = exitCode,
            DurationMs = durationMs,
        });
    }

    /// <summary>#125: capture the active scope at the current target, for emission from other threads later. Null when inactive.</summary>
    internal static CapturedEventScope? Capture() => Current.Value?.Capture();

    /// <summary>Activate <paramref name="scope"/> for the current async flow; dispose restores the prior scope. Executor-internal.</summary>
    internal static IDisposable Activate(BuildEventScope scope)
    {
        var prev = Current.Value;
        Current.Value = scope;
        return new Restorer(() => Current.Value = prev);
    }

    /// <summary>#66: suppress ambient tool.invoked/tool.exited for the current flow — used by the
    /// executor around its own declarative dispatch, which emits those events itself. Executor-internal.</summary>
    internal static IDisposable SuppressToolEvents()
    {
        var prev = Suppressed.Value;
        Suppressed.Value = true;
        return new Restorer(() => Suppressed.Value = prev);
    }

    private sealed class Restorer : IDisposable
    {
        private readonly Action _restore;
        public Restorer(Action restore) => _restore = restore;
        public void Dispose() => _restore();
    }
}

/// <summary>
/// Executor-owned emission scope backing <see cref="BuildEvents"/>. Holds the
/// callback that stamps the canonical envelope (buildId / traceId / seq) and
/// parents ambient events to the current target span, plus the executor's span-id
/// generator and redaction table so ambient tool events (#66) share the same
/// command-span and secret-scrubbing as the declarative path. The executor updates
/// <see cref="Emit(string,BuildEventPayload)"/>'s target context as each target runs.
/// </summary>
internal sealed class BuildEventScope
{
    private readonly Action<string, string?, BuildEventPayload> _emit;
    private readonly Func<string> _newSpanId;
    private readonly RedactionTable _redaction;
    private readonly Action<string, string?, string, string?, BuildEventPayload>? _emitRaw;
    private readonly Func<(string? TargetId, string ParentSpanId)>? _currentParent;

    /// <param name="emit">Ambient emission, parented to whatever target is current when it's called.</param>
    /// <param name="newSpanId">Span id factory.</param>
    /// <param name="redaction">The build's redaction table.</param>
    /// <param name="emitRaw">#125: emission with an explicit target and parent span, safe from any thread.</param>
    /// <param name="currentParent">#125: the current target and its span (the build span between targets).</param>
    public BuildEventScope(Action<string, string?, BuildEventPayload> emit, Func<string> newSpanId, RedactionTable redaction,
        Action<string, string?, string, string?, BuildEventPayload>? emitRaw = null,
        Func<(string? TargetId, string ParentSpanId)>? currentParent = null)
    {
        _emit = emit;
        _newSpanId = newSpanId;
        _redaction = redaction;
        _emitRaw = emitRaw;
        _currentParent = currentParent;
    }

    /// <summary>
    /// #125: freeze the current target as the parent, for a long-lived emitter (a <see cref="ManagedProcess"/>)
    /// that keeps emitting after that target moved on, and from other threads. Null when the executor didn't
    /// supply the raw emitter.
    /// </summary>
    internal CapturedEventScope? Capture()
    {
        if (_emitRaw is null || _currentParent is null) return null;
        var (targetId, parent) = _currentParent();
        return new CapturedEventScope(this, _emitRaw, targetId, parent);
    }

    /// <summary>Emit with an executor-generated span id (for standalone events).</summary>
    public void Emit(string type, BuildEventPayload payload) => _emit(type, null, payload);

    /// <summary>Emit on a caller-chosen span id (so a tool's invoked/exited share one command span).</summary>
    public void Emit(string type, string spanId, BuildEventPayload payload) => _emit(type, spanId, payload);

    /// <summary>Mint a span id in the executor's format.</summary>
    public string NewSpanId() => _newSpanId();

    /// <summary>Scrub registered secret values out of a candidate string.</summary>
    public string Redact(string input) => _redaction.Redact(input);

    /// <summary>Register a plan's declared secrets so its argv/output can be redacted.</summary>
    public void RegisterSecrets(CommandPlan plan) => _redaction.RegisterAll(plan);
}

/// <summary>
/// #125: a build event scope frozen at one target, usable from any thread for as long as the run lasts. Events go
/// straight to the executor's (locked) emitter with the captured target as parent, so they don't feed per-target
/// bookkeeping such as the non-zero-tool advisory (a killed service exits non-zero by design).
/// </summary>
internal sealed class CapturedEventScope
{
    private readonly BuildEventScope _scope;
    private readonly Action<string, string?, string, string?, BuildEventPayload> _emitRaw;

    internal CapturedEventScope(BuildEventScope scope, Action<string, string?, string, string?, BuildEventPayload> emitRaw, string? targetId, string parentSpanId)
    {
        _scope = scope;
        _emitRaw = emitRaw;
        TargetId = targetId;
        ParentSpanId = parentSpanId;
    }

    /// <summary>The target that was current when the scope was captured (null between targets).</summary>
    internal string? TargetId { get; }

    /// <summary>The span captured events are parented to.</summary>
    internal string ParentSpanId { get; }

    internal string NewSpanId() => _scope.NewSpanId();

    internal string Redact(string input) => _scope.Redact(input);

    internal void Emit(string type, string spanId, BuildEventPayload payload) => _emitRaw(type, TargetId, spanId, ParentSpanId, payload);

    /// <summary>
    /// The <see cref="BuildEvents.ToolInvoked"/> sequence on a new command span: register the plan's secrets, emit
    /// <c>secret.access.requested</c> per secret, then <c>tool.invoked</c> with redacted argv. Returns the span.
    /// </summary>
    internal string ToolInvoked(CommandPlan plan)
    {
        _scope.RegisterSecrets(plan);
        var span = NewSpanId();
        foreach (var secret in plan.Secrets)
            Emit(BuildEventTypes.SecretAccessRequested, span, new SecretAccessRequestedPayload { Name = secret.Name, Capability = "secret.reveal" });
        Emit(BuildEventTypes.ToolInvoked, span, new ToolInvokedPayload
        {
            Tool = plan.Executable,
            ArgvRedacted = plan.Arguments.Select(a => Redact(a)).ToArray(),
            Cwd = plan.WorkingDirectory,
        });
        return span;
    }
}
