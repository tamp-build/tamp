using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// <see cref="Polling.Until(Func{CancellationToken, Task{bool}}, TimeSpan, Backoff?, Logger?, CancellationToken)"/>.
/// Anything about budgets and waits runs on <see cref="VirtualTime"/>, where a wait advances the clock
/// instantly. Those tests assert exact attempt counts and exact delays, and a slow or stalled runner can't
/// change the result (#142). Only tests that are independent of timing use the real clock.
/// </summary>
public class PollingTests
{
    // ------------------------------------------------------------------ real clock, timing-independent

    [Fact]
    public async Task Returns_True_When_Condition_Succeeds_Immediately()
    {
        var attempts = 0;

        var result = await Polling.Until(
            ct => { attempts++; return Task.FromResult(true); },
            timeout: TimeSpan.FromSeconds(5));

        Assert.True(result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Non_Positive_Timeout_Allows_One_Check()
    {
        var attempts = 0;

        var result = await Polling.Until(
            ct => { attempts++; return Task.FromResult(true); },
            timeout: TimeSpan.Zero);

        Assert.True(result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Cancellation_Propagates_OperationCanceledException()
    {
        using var cts = new CancellationTokenSource();

        // A budget no runner stall can exhaust, so the only way out is the cancellation.
        var task = Polling.Until(
            async ct => { await Task.Delay(100, ct).ConfigureAwait(false); return false; },
            timeout: TimeSpan.FromMinutes(10),
            backoff: Backoff.Constant(TimeSpan.FromMilliseconds(20)),
            cancellationToken: cts.Token);

        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TaskCanceledException>(() => task);
    }

    [Fact]
    public async Task Cancellation_Before_Start_Throws_Immediately()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Polling.Until(
            ct => Task.FromResult(true),
            timeout: TimeSpan.FromSeconds(5),
            cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Condition_Exception_Propagates()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Polling.Until(
            ct => throw new InvalidOperationException("boom"),
            timeout: TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Null_Condition_Throws_ArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Polling.Until(
            null!,
            timeout: TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Null_Time_Provider_Throws_ArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Polling.Until(
            ct => Task.FromResult(true), TimeSpan.FromSeconds(1), time: null!));
    }

    // ------------------------------------------------------------------ virtual clock: exact budgets and waits

    [Fact]
    public async Task Returns_True_After_N_Attempts()
    {
        var time = new VirtualTime();
        var attempts = 0;

        var result = await Polling.Until(
            ct => { attempts++; return Task.FromResult(attempts >= 3); },
            TimeSpan.FromSeconds(5), time, Backoff.Constant(TimeSpan.FromMilliseconds(10)));

        Assert.True(result);
        Assert.Equal(3, attempts);
        Assert.Equal([Ms(10), Ms(10)], time.Delays);
    }

    [Fact]
    public async Task Returns_False_When_Timeout_Elapses_Without_Overrunning_The_Budget()
    {
        var time = new VirtualTime();
        var attempts = 0;

        var result = await Polling.Until(
            ct => { attempts++; return Task.FromResult(false); },
            TimeSpan.FromMilliseconds(150), time, Backoff.Constant(TimeSpan.FromMilliseconds(30)));

        Assert.False(result);
        // Checks at t = 0, 30, 60, 90, 120 and 150 ms. At 150 the budget is spent, so the loop stops.
        Assert.Equal(6, attempts);
        Assert.Equal(Ms(150), time.Elapsed);
    }

    [Fact]
    public async Task The_Last_Wait_Is_Clipped_To_The_Remaining_Budget()
    {
        var time = new VirtualTime();

        await Polling.Until(ct => Task.FromResult(false),
            TimeSpan.FromMilliseconds(100), time, Backoff.Constant(TimeSpan.FromMilliseconds(30)));

        Assert.Equal([Ms(30), Ms(30), Ms(30), Ms(10)], time.Delays);
        Assert.Equal(Ms(100), time.Elapsed);
    }

    [Fact]
    public async Task Uses_Backoff_Delay_Between_Attempts()
    {
        var time = new VirtualTime();
        var stamps = new List<DateTimeOffset>();

        await Polling.Until(
            ct => { stamps.Add(time.GetUtcNow()); return Task.FromResult(stamps.Count >= 3); },
            TimeSpan.FromSeconds(5), time, Backoff.Constant(TimeSpan.FromMilliseconds(50)));

        Assert.Equal(3, stamps.Count);
        Assert.Equal(Ms(50), stamps[1] - stamps[0]);
        Assert.Equal(Ms(50), stamps[2] - stamps[1]);
    }

    [Fact]
    public async Task Each_Wait_Follows_The_Backoff_Formula()
    {
        var time = new VirtualTime();
        var attempts = 0;

        await Polling.Until(ct => Task.FromResult(++attempts >= 4),
            TimeSpan.FromMinutes(1), time, Backoff.Exponential(TimeSpan.FromMilliseconds(100)));

        Assert.Equal([Ms(100), Ms(200), Ms(400)], time.Delays);
    }

    [Fact]
    public async Task A_Slow_Condition_Spends_The_Budget_Too()
    {
        // The budget is measured on the clock, so time spent inside the condition counts against it.
        var time = new VirtualTime();
        var attempts = 0;

        var result = await Polling.Until(
            ct => { attempts++; time.Advance(Ms(200)); return Task.FromResult(false); },
            TimeSpan.FromMilliseconds(150), time, Backoff.Constant(TimeSpan.FromMilliseconds(30)));

        Assert.False(result);
        Assert.Equal(1, attempts);
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task Logger_Receives_Trace_Per_Attempt_And_Warn_On_Timeout()
    {
        using var writer = new StringWriter();
        var logger = new Logger(writer, LogLevel.Trace);

        var result = await Polling.Until(ct => Task.FromResult(false),
            TimeSpan.FromMilliseconds(80), new VirtualTime(), Backoff.Constant(TimeSpan.FromMilliseconds(20)), logger);

        Assert.False(result);
        var output = writer.ToString();
        // Checks at 0, 20, 40, 60 and 80 ms: four waits traced, then the timeout warning.
        Assert.Equal(4, CountOf(output, "[TRACE]"));
        Assert.Contains("[WARN]", output);
        Assert.Contains("timed out after 5 attempt(s)", output);
    }

    [Fact]
    public async Task Logger_Receives_Debug_On_Success()
    {
        using var writer = new StringWriter();
        var logger = new Logger(writer, LogLevel.Debug);

        var result = await Polling.Until(ct => Task.FromResult(true),
            TimeSpan.FromSeconds(1), new VirtualTime(), logger: logger);

        Assert.True(result);
        var output = writer.ToString();
        Assert.Contains("[DEBUG]", output);
        Assert.Contains("satisfied", output);
    }

    [Fact]
    public async Task Cancellation_During_A_Wait_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var time = new VirtualTime { BeforeWait = cts.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Polling.Until(
            ct => Task.FromResult(false), TimeSpan.FromSeconds(5), time,
            Backoff.Constant(TimeSpan.FromMilliseconds(10)), cancellationToken: cts.Token));
    }

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private static int CountOf(string text, string token)
    {
        var count = 0;
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// A clock that only moves when told to. <c>Task.Delay(d, this)</c> records <c>d</c>, advances the clock
    /// by it and completes at once, so a test never waits on real time.
    /// </summary>
    private sealed class VirtualTime : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private DateTimeOffset _now = Start;

        public List<TimeSpan> Delays { get; } = [];
        public TimeSpan Elapsed => _now - Start;
        public Action? BeforeWait { get; init; }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            BeforeWait?.Invoke();
            Delays.Add(dueTime);
            _now += dueTime;
            // Fire off the caller's stack: Task.Delay finishes wiring up its timer before the callback runs.
            ThreadPool.UnsafeQueueUserWorkItem(_ => callback(state), null);
            return new FiredTimer();
        }

        private sealed class FiredTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
