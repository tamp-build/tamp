namespace Tamp;

/// <summary>
/// Build-lifecycle event sink. Implementations receive structured events at
/// well-defined hooks in the Executor's run loop, independent of the
/// human-readable text emit. TAM-140.
/// </summary>
/// <remarks>
/// The default <see cref="NoopBuildReporter"/> is a no-op — the Executor's
/// existing <c>_log.WriteRaw(...)</c> calls handle human-facing text output
/// as before. This interface remains for adopter reporters registered via
/// <c>[BuildReporter]</c> (Telegram / Slack / custom); the executor drives it
/// through <see cref="ReporterProjectionSink"/>, a projection of the one
/// canonical <see cref="BuildEvent"/> stream. The machine output channel
/// (<c>--events</c> / <c>--reporter=json</c>) emits that canonical stream
/// directly as NDJSON — it is not an <see cref="IBuildReporter"/>.
/// </remarks>
public interface IBuildReporter
{
    void OnBuildStart(string buildId, IReadOnlyList<string> requestedTargets, IReadOnlyList<string> executionClosure);

    /// <summary>Called when a target's `Executes` block is about to run.</summary>
    void OnTargetStart(string name);

    /// <summary>Called when a target completed successfully.</summary>
    void OnTargetSucceeded(string name, TimeSpan duration);

    /// <summary>
    /// Called when a target failed — Executes threw, the wrapped CommandPlan
    /// exited non-zero, or a Requires precondition was unmet. The
    /// <see cref="TargetFailureDetail.OutputTail"/> on the payload carries
    /// the last N lines of merged stdout+stderr the target emitted before
    /// failing (TAM-230 background — adopter notify reporters use this for
    /// rich failure messages without parsing terminal output themselves).
    /// </summary>
    void OnTargetFailed(TargetFailureDetail detail);

    /// <summary>Called when a target was skipped (user --skip / OnlyWhen / Requires-failed / upstream-failure).</summary>
    void OnTargetSkipped(string name, string reason);

    /// <summary>Called when a target wasn't run at all (upstream failure, build aborted).</summary>
    void OnTargetNotRun(string name, string reason);

    void OnBuildEnd(string status, string? firstFailedTarget, int exitCode, TimeSpan totalDuration);
}

/// <summary>Default no-op reporter. Used when no <see cref="IBuildReporter"/> is supplied.</summary>
public sealed class NoopBuildReporter : IBuildReporter
{
    public static readonly NoopBuildReporter Instance = new();
    private NoopBuildReporter() { }
    public void OnBuildStart(string buildId, IReadOnlyList<string> requestedTargets, IReadOnlyList<string> executionClosure) { }
    public void OnTargetStart(string name) { }
    public void OnTargetSucceeded(string name, TimeSpan duration) { }
    public void OnTargetFailed(TargetFailureDetail detail) { }
    public void OnTargetSkipped(string name, string reason) { }
    public void OnTargetNotRun(string name, string reason) { }
    public void OnBuildEnd(string status, string? firstFailedTarget, int exitCode, TimeSpan totalDuration) { }
}
