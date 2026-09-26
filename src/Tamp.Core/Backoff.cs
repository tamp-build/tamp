namespace Tamp;

/// <summary>
/// How a target's retry waits between attempts.
/// </summary>
public abstract record Backoff
{
    public abstract TimeSpan Delay(int attempt);

    public static Backoff Linear(TimeSpan step) => new LinearBackoff(step);
    public static Backoff Exponential(TimeSpan initial, double factor = 2.0) => new ExponentialBackoff(initial, factor);
    public static Backoff Constant(TimeSpan delay) => new ConstantBackoff(delay);
    public static Backoff Custom(Func<int, TimeSpan> formula) => new CustomBackoff(formula);
}

internal sealed record LinearBackoff(TimeSpan Step) : Backoff
{
    public override TimeSpan Delay(int attempt) => Step * Math.Max(0, attempt);
}

internal sealed record ExponentialBackoff(TimeSpan Initial, double Factor) : Backoff
{
    public override TimeSpan Delay(int attempt)
    {
        if (attempt <= 0) return TimeSpan.Zero;
        var ms = Initial.TotalMilliseconds * Math.Pow(Factor, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(ms, TimeSpan.MaxValue.TotalMilliseconds / 2));
    }
}

internal sealed record ConstantBackoff(TimeSpan Interval) : Backoff
{
    public override TimeSpan Delay(int attempt) => attempt <= 0 ? TimeSpan.Zero : Interval;
}

internal sealed record CustomBackoff(Func<int, TimeSpan> Formula) : Backoff
{
    public override TimeSpan Delay(int attempt) => Formula(attempt);
}

public enum FailureMode
{
    /// <summary>Default: target failure aborts the build.</summary>
    Fatal,
    /// <summary>
    /// Target failure does not <em>abort</em> the build — remaining plans in the target and
    /// subsequent targets still run — but the target is still recorded as failed and the build
    /// exits non-zero. Use it to "run everything, then report," not to swallow failures (#4).
    /// </summary>
    Continue,
    /// <summary>Target is retried per its <see cref="Backoff"/>.</summary>
    Retry,
}

public enum RunMode
{
    /// <summary>Always run when scheduled. (Default.)</summary>
    Always,
    /// <summary>Run only when declared inputs have changed since the last successful run.</summary>
    WhenInputsChanged,
    /// <summary>Run only when explicitly requested on the command line.</summary>
    Manual,
}
