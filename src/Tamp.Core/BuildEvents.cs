namespace Tamp;

/// <summary>
/// Ambient emitter for canonical <see cref="BuildEvent"/>s that originate from
/// <em>inside a target</em> or a helper library — diagnostics (#13), produced
/// artifacts (#12), gate verdicts (#20) — rather than from the executor's own
/// lifecycle. The executor activates a scope for the duration of a run and keeps
/// it pointed at the current target's span, so ambient events land in the same
/// stream with correct envelope identity and parenting.
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

    /// <summary>Activate <paramref name="scope"/> for the current async flow; dispose restores the prior scope. Executor-internal.</summary>
    internal static IDisposable Activate(BuildEventScope scope)
    {
        var prev = Current.Value;
        Current.Value = scope;
        return new Restorer(prev);
    }

    private sealed class Restorer : IDisposable
    {
        private readonly BuildEventScope? _prev;
        public Restorer(BuildEventScope? prev) => _prev = prev;
        public void Dispose() => Current.Value = _prev;
    }
}

/// <summary>
/// Executor-owned emission scope backing <see cref="BuildEvents"/>. Holds the
/// callback that stamps the canonical envelope (buildId / traceId / seq) and
/// parents ambient events to the current target span. The executor updates
/// <see cref="Emit"/>'s target context as each target runs.
/// </summary>
internal sealed class BuildEventScope
{
    private readonly Action<string, BuildEventPayload> _emit;
    public BuildEventScope(Action<string, BuildEventPayload> emit) => _emit = emit;
    public void Emit(string type, BuildEventPayload payload) => _emit(type, payload);
}
