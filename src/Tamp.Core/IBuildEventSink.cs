namespace Tamp;

/// <summary>
/// Consumer of the canonical <see cref="BuildEvent"/> stream (`#0a`, ADR 0019).
/// The <c>Executor</c> emits every lifecycle fact once; sinks fan it out. A sink is
/// a projection of the single source of truth, never a parallel producer.
/// </summary>
public interface IBuildEventSink
{
    void Emit(BuildEvent e);
}

/// <summary>No-op sink. Used when nothing consumes the stream.</summary>
public sealed class NoopBuildEventSink : IBuildEventSink
{
    public static readonly NoopBuildEventSink Instance = new();
    private NoopBuildEventSink() { }
    public void Emit(BuildEvent e) { }
}

/// <summary>
/// Fans one event out to several sinks. A misbehaving sink is isolated — its
/// exception is swallowed so one consumer can't break the build or starve the
/// others (the same defense the redaction/reporter paths already take).
/// </summary>
public sealed class CompositeBuildEventSink : IBuildEventSink
{
    private readonly IReadOnlyList<IBuildEventSink> _sinks;

    public CompositeBuildEventSink(params IBuildEventSink[] sinks)
        => _sinks = (sinks ?? Array.Empty<IBuildEventSink>()).Where(s => s is not null).ToArray();

    public void Emit(BuildEvent e)
    {
        foreach (var s in _sinks)
        {
            try { s.Emit(e); }
            catch { /* a sink must never break the build or starve siblings */ }
        }
    }
}
