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
    /// on a <c>fail</c>.
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
        IReadOnlyList<string>? controlRefs = null)
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

    public BuildEventScope(Action<string, string?, BuildEventPayload> emit, Func<string> newSpanId, RedactionTable redaction)
    {
        _emit = emit;
        _newSpanId = newSpanId;
        _redaction = redaction;
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
