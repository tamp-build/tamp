namespace Tamp;

/// <summary>
/// Generic poll-until-condition helper. First user is the
/// <c>Tamp.DependencyTrack</c> wrapper, which has to wait for DT's async
/// SBOM analysis to settle before exporting findings; the same shape also
/// fits Azure deployment polling, NuGet index propagation, and other
/// "wait for an external system to catch up" pain points.
/// </summary>
/// <remarks>
/// Returns a bool rather than throwing on timeout so adopters can choose
/// their own error path (some want a hard fail, others want a warning +
/// fall-through with a defaulted result). Cancellation and exceptions
/// from the condition are NOT swallowed — they propagate so debugging
/// stays straightforward.
/// </remarks>
public static class Polling
{
    /// <summary>
    /// Poll <paramref name="condition"/> repeatedly until it returns true,
    /// the <paramref name="timeout"/> elapses, or
    /// <paramref name="cancellationToken"/> is signalled.
    /// </summary>
    /// <param name="condition">Returns true when the awaited state is reached.</param>
    /// <param name="timeout">Total wall-clock budget. A non-positive value short-circuits to one condition check.</param>
    /// <param name="backoff">Wait strategy between attempts. Defaults to a constant 2-second interval.</param>
    /// <param name="logger">Optional logger; receives Trace per attempt and Warn on timeout.</param>
    /// <param name="cancellationToken">Cancels the polling loop.</param>
    /// <returns>true if the condition was satisfied within the timeout; false on timeout.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    public static Task<bool> Until(
        Func<CancellationToken, Task<bool>> condition,
        TimeSpan timeout,
        Backoff? backoff = null,
        Logger? logger = null,
        CancellationToken cancellationToken = default)
        => Until(condition, timeout, TimeProvider.System, backoff, logger, cancellationToken);

    /// <summary>
    /// <see cref="Until(Func{CancellationToken, Task{bool}}, TimeSpan, Backoff?, Logger?, CancellationToken)"/>
    /// against <paramref name="time"/>: the clock the budget is measured on, and the source of the waits.
    /// Tests pass a virtual clock so their assertions don't depend on how fast the machine is (#142).
    /// </summary>
    internal static async Task<bool> Until(
        Func<CancellationToken, Task<bool>> condition,
        TimeSpan timeout,
        TimeProvider time,
        Backoff? backoff = null,
        Logger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (condition is null) throw new ArgumentNullException(nameof(condition));
        if (time is null) throw new ArgumentNullException(nameof(time));

        backoff ??= Backoff.Constant(TimeSpan.FromSeconds(2));
        var deadline = time.GetUtcNow() + timeout;
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            if (await condition(cancellationToken).ConfigureAwait(false))
            {
                logger?.Debug($"Polling.Until satisfied on attempt {attempt}.");
                return true;
            }

            var remaining = deadline - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                logger?.Warn($"Polling.Until timed out after {attempt} attempt(s); {timeout.TotalSeconds:F1}s budget exhausted.");
                return false;
            }

            var delay = backoff.Delay(attempt);
            if (delay > remaining) delay = remaining;

            logger?.Trace($"Polling.Until attempt {attempt} returned false; waiting {delay.TotalMilliseconds:F0}ms.");
            await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
        }
    }
}
