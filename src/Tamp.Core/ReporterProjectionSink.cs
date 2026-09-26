namespace Tamp;

/// <summary>
/// Projects the canonical <see cref="BuildEvent"/> stream onto the public
/// <see cref="IBuildReporter"/> surface (`#0a`, ADR 0019). This makes
/// <see cref="IBuildReporter"/> a <em>projection</em> rather than a second
/// hand-synced emission path: the <c>Executor</c> emits canonical events once, and
/// this sink translates them back into the existing reporter callbacks — so every
/// adopter reporter (Telegram, <see cref="CompositeBuildReporter"/>,
/// <see cref="JsonBuildReporter"/> from <c>--reporter=json</c>) keeps working
/// unchanged.
/// </summary>
internal sealed class ReporterProjectionSink : IBuildEventSink
{
    private readonly IBuildReporter _reporter;

    public ReporterProjectionSink(IBuildReporter reporter)
        => _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));

    public void Emit(BuildEvent e)
    {
        switch (e.Payload)
        {
            case BuildStartedPayload p:
                _reporter.OnBuildStart(e.BuildId, p.RequestedTargets, p.ExecutionClosure);
                break;

            case TargetStartedPayload p:
                _reporter.OnTargetStart(p.Target);
                break;

            case TargetFinishedPayload p:
                switch (p.Status)
                {
                    case BuildEventStatus.Success:
                        _reporter.OnTargetSucceeded(p.Target, MsToSpan(p.DurationMs));
                        break;
                    case BuildEventStatus.Failure:
                        _reporter.OnTargetFailed(new TargetFailureDetail
                        {
                            TargetName = p.Target,
                            Duration = MsToSpan(p.DurationMs),
                            FailureReason = p.Reason ?? string.Empty,
                            OutputTail = p.OutputTail ?? Array.Empty<string>(),
                        });
                        break;
                    case BuildEventStatus.Skipped:
                        _reporter.OnTargetSkipped(p.Target, p.Reason ?? string.Empty);
                        break;
                    case BuildEventStatus.NotRun:
                        _reporter.OnTargetNotRun(p.Target, p.Reason ?? string.Empty);
                        break;
                }
                break;

            case BuildFinishedPayload p:
                _reporter.OnBuildEnd(p.Status, p.FirstFailedTarget, p.ExitCode, MsToSpan(p.DurationMs));
                break;
        }
    }

    private static TimeSpan MsToSpan(double? ms) => TimeSpan.FromMilliseconds(ms ?? 0);
}
