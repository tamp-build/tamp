using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// #125 PR 1: build-wide cancellation. An interrupt cancels <see cref="TampBuild.BuildCancellation"/>; the running
/// target finishes <c>cancelled</c>, the rest <c>not_run</c>, cleanup targets still run with a token bounded by the
/// grace period, <see cref="ProcessRunner"/> kills its child tree, and the build exits <c>128 + signal</c>. Most tests
/// interrupt through <see cref="BuildInterrupt.RequestInterrupt"/> (what the signal handlers call); the
/// <see cref="RealSignalTests"/> send real signals on Unix.
/// </summary>
public sealed class BuildCancellationTests
{
    internal sealed class CapturingSink : IBuildEventSink
    {
        private readonly object _gate = new();
        public readonly List<BuildEvent> Events = new();
        public void Emit(BuildEvent e) { lock (_gate) Events.Add(e); }
        public List<BuildEvent> Snapshot() { lock (_gate) return Events.ToList(); }
    }

    internal static (ExecutionResult Result, List<BuildEvent> Events, List<int> ForcedExits) Run(
        TampBuild build, string target, TimeSpan? shutdownTimeout = null, bool handleSignals = false)
    {
        var graph = new TargetGraph(TampBuild.CollectTargets(build));
        var sink = new CapturingSink();
        var forced = new List<int>();
        var ex = new Executor(graph, output: TextWriter.Null, eventSink: sink, handleSignals: handleSignals, shutdownTimeout: shutdownTimeout)
        {
            ForcedExitOverride = code => { lock (forced) forced.Add(code); },
        };
        var result = ex.Run(target);
        return (result, sink.Snapshot(), forced);
    }

    internal static void Interrupt(string signal = "SIGINT", int number = 2) =>
        BuildInterrupt.Active!.RequestInterrupt(signal, number);

    private static TargetFinishedPayload Finished(List<BuildEvent> events, string target) =>
        (TargetFinishedPayload)events.Single(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == target).Payload;

    private static BuildFinishedPayload BuildFinished(List<BuildEvent> events) =>
        (BuildFinishedPayload)events.Last(e => e.Type == BuildEventTypes.BuildFinished).Payload;

    // ── Cross-platform long-running plans ─────────────────────────────────────

    internal static CommandPlan SleepPlan(int seconds) => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? new CommandPlan { Executable = "cmd.exe", Arguments = new[] { "/c", $"ping -n {seconds + 1} 127.0.0.1 >nul" } }
        : new CommandPlan { Executable = "/bin/sh", Arguments = new[] { "-c", $"sleep {seconds}" } };

    private static void InterruptSoon(TimeSpan after)
    {
        var interrupt = BuildInterrupt.Active!;
        _ = Task.Run(async () => { await Task.Delay(after); interrupt.RequestInterrupt("SIGINT", 2); });
    }

    // ── Builds ────────────────────────────────────────────────────────────────

    private sealed class AsyncBuild : TampBuild
    {
        public static bool TokenWasCancellable;
        public static bool CleanupTokenCancelledAtStart = true;
        public static bool CleanupRan;
        public static bool HandlerRan;

        public Target Compile => _ => _.Executes(async ct =>
        {
            TokenWasCancellable = ct.CanBeCanceled;
            Interrupt();
            await Task.Delay(Timeout.Infinite, ct);
        });
        public Target Test => _ => _.DependsOn(nameof(Compile)).Executes(() => { });
        public Target Cleanup => _ => _.DependsOn(nameof(Test)).AssuredAfterFailure().Executes(() =>
        {
            CleanupRan = true;
            CleanupTokenCancelledAtStart = TampBuild.BuildCancellation.IsCancellationRequested;
        });
        public Target OnCompileFailed => _ => _.OnFailureOf(nameof(Compile)).Executes(() => { HandlerRan = true; });
    }

    [Fact]
    public void Interrupt_Cancels_The_Running_Target_Skips_The_Rest_And_Exits_130()
    {
        AsyncBuild.CleanupRan = false;
        AsyncBuild.HandlerRan = false;
        var (result, events, forced) = Run(new AsyncBuild(), nameof(AsyncBuild.Cleanup));

        Assert.True(AsyncBuild.TokenWasCancellable, "Executes(async ct => ...) must get the build token");
        Assert.Equal(130, result.ExitCode);
        Assert.Equal("SIGINT", result.InterruptSignal);
        Assert.Null(result.FailedTarget);
        Assert.Empty(forced);

        var compile = Finished(events, nameof(AsyncBuild.Compile));
        Assert.Equal(BuildEventStatus.Cancelled, compile.Status);
        Assert.Equal("signal: SIGINT", compile.Reason);
        Assert.Null(compile.Remedy);

        var test = Finished(events, nameof(AsyncBuild.Test));
        Assert.Equal(BuildEventStatus.NotRun, test.Status);
        Assert.Equal("build cancelled (signal: SIGINT)", test.Reason);

        // Cleanup still ran, with a token of its own (the build token was already cancelled).
        Assert.True(AsyncBuild.CleanupRan);
        Assert.False(AsyncBuild.CleanupTokenCancelledAtStart);
        Assert.Equal(BuildEventStatus.Success, Finished(events, nameof(AsyncBuild.Cleanup)).Status);

        // No failure handlers: nothing failed.
        Assert.False(AsyncBuild.HandlerRan);

        var build = BuildFinished(events);
        Assert.Equal(BuildEventStatus.Cancelled, build.Status);
        Assert.Equal("signal: SIGINT", build.Reason);
        Assert.Equal(130, build.ExitCode);
        Assert.Equal(1, build.Cancelled);
        Assert.Equal(1, build.NotRun);
        Assert.Equal(TargetStatus.Cancelled, result.ExecutionRecords.Single(r => r.Name == nameof(AsyncBuild.Compile)).Status);
    }

    private sealed class SignalBuild : TampBuild
    {
        public static string Signal = "SIGTERM";
        public static int Number = 15;
        public Target Run => _ => _.Executes(async ct => { Interrupt(Signal, Number); await Task.Delay(Timeout.Infinite, ct); });
    }

    [Theory]
    [InlineData("SIGINT", 2, 130)]
    [InlineData("SIGTERM", 15, 143)]
    [InlineData("SIGHUP", 1, 129)]
    public void Exit_Code_Is_128_Plus_The_Signal(string signal, int number, int expected)
    {
        SignalBuild.Signal = signal;
        SignalBuild.Number = number;
        var (result, events, _) = Run(new SignalBuild(), nameof(SignalBuild.Run));
        Assert.Equal(expected, result.ExitCode);
        Assert.Equal($"signal: {signal}", BuildFinished(events).Reason);
    }

    private sealed class ProcessBuild : TampBuild
    {
        public Target Run => _ => _.Executes(() => { InterruptSoon(TimeSpan.FromMilliseconds(500)); return SleepPlan(30); });
    }

    [Fact]
    public void Declarative_Plan_Is_Killed_On_Cancellation_And_Its_Tool_Exit_Recorded()
    {
        var clock = Stopwatch.StartNew();
        var (result, events, _) = Run(new ProcessBuild(), nameof(ProcessBuild.Run));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"the 30s child should have been killed (took {clock.Elapsed})");
        Assert.Equal(130, result.ExitCode);
        Assert.Equal(BuildEventStatus.Cancelled, Finished(events, nameof(ProcessBuild.Run)).Status);

        var invoked = events.Single(e => e.Type == BuildEventTypes.ToolInvoked);
        var exited = events.Single(e => e.Type == BuildEventTypes.ToolExited);
        Assert.Equal(invoked.SpanId, exited.SpanId);
    }

    private sealed class ImperativeBuild : TampBuild
    {
        public static string GrandchildPidFile = "";
        public static bool SecondPlanStarted;
        public Target Run => _ => _.Executes(() =>
        {
            InterruptSoon(TimeSpan.FromMilliseconds(500));
            // A tool with a child of its own: the whole tree must go.
            var plan = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? SleepPlan(30)
                : new CommandPlan { Executable = "/bin/sh", Arguments = new[] { "-c", $"sleep 30 & echo $! > '{GrandchildPidFile}'; wait" } };
            try { ProcessRunner.Run(plan, TextWriter.Null, TextWriter.Null); }
            finally
            {
                // After cancellation nothing new starts.
                try { ProcessRunner.Execute(SleepPlan(1), TextWriter.Null, TextWriter.Null); SecondPlanStarted = true; }
                catch (OperationCanceledException) { }
            }
        });
    }

    [Fact]
    public void Imperative_ProcessRunner_Kills_The_Tree_And_Refuses_To_Start_Anything_New()
    {
        ImperativeBuild.GrandchildPidFile = Path.Combine(Path.GetTempPath(), $"tamp-cancel-{Guid.NewGuid():N}.pid");
        ImperativeBuild.SecondPlanStarted = false;
        try
        {
            var (result, events, _) = Run(new ImperativeBuild(), nameof(ImperativeBuild.Run));
            Assert.Equal(130, result.ExitCode);
            Assert.Equal(BuildEventStatus.Cancelled, Finished(events, nameof(ImperativeBuild.Run)).Status);
            Assert.False(ImperativeBuild.SecondPlanStarted);
            // The imperative dispatch is still visible on the stream (#66), with its exit.
            Assert.Single(events, e => e.Type == BuildEventTypes.ToolExited);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var grandchild = int.Parse(File.ReadAllText(ImperativeBuild.GrandchildPidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(WaitUntil(() => !IsRunning(grandchild), TimeSpan.FromSeconds(5)), "the tool's own child must be killed too");
            }
        }
        finally
        {
            File.Delete(ImperativeBuild.GrandchildPidFile);
        }
    }

    private sealed class SlowCleanupBuild : TampBuild
    {
        public Target Work => _ => _.Executes(async ct => { Interrupt(); await Task.Delay(Timeout.Infinite, ct); });
        public Target Cleanup => _ => _.DependsOn(nameof(Work)).AssuredAfterFailure()
            .Executes(async ct => await Task.Delay(Timeout.Infinite, ct));
    }

    [Fact]
    public void A_Cleanup_Target_Is_Cancelled_When_The_Grace_Period_Runs_Out()
    {
        var clock = Stopwatch.StartNew();
        var (result, events, forced) = Run(new SlowCleanupBuild(), nameof(SlowCleanupBuild.Cleanup), shutdownTimeout: TimeSpan.FromMilliseconds(300));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"cleanup should end at the grace deadline (took {clock.Elapsed})");
        Assert.Empty(forced); // it honoured its token, so no forced exit
        Assert.Equal(130, result.ExitCode);
        var cleanup = Finished(events, nameof(SlowCleanupBuild.Cleanup));
        Assert.Equal(BuildEventStatus.Cancelled, cleanup.Status);
        Assert.Equal("shutdown grace period ran out (signal: SIGINT)", cleanup.Reason);
    }

    private sealed class FailingCleanupBuild : TampBuild
    {
        public Target Work => _ => _.Executes(async ct => { Interrupt(); await Task.Delay(Timeout.Infinite, ct); });
        public Target Cleanup => _ => _.DependsOn(nameof(Work)).AssuredAfterFailure()
            .Executes(new Action(() => throw new InvalidOperationException("teardown broke")));
    }

    [Fact]
    public void A_Cleanup_Target_Can_Still_Genuinely_Fail_After_An_Interrupt()
    {
        var (result, events, _) = Run(new FailingCleanupBuild(), nameof(FailingCleanupBuild.Cleanup));
        var cleanup = Finished(events, nameof(FailingCleanupBuild.Cleanup));
        Assert.Equal(BuildEventStatus.Failure, cleanup.Status);
        Assert.Contains("teardown broke", cleanup.Reason, StringComparison.Ordinal);
        // The interrupt still decides the exit code; the failure stays on the stream and in the summary.
        Assert.Equal(130, result.ExitCode);
        Assert.Equal(BuildEventStatus.Cancelled, BuildFinished(events).Status);
        Assert.Equal(nameof(FailingCleanupBuild.Cleanup), result.FailedTarget);
    }

    private sealed class StubbornBuild : TampBuild
    {
        public static bool SecondInterrupt;
        public Target Run => _ => _.Executes(() =>
        {
            Interrupt();
            if (SecondInterrupt) Interrupt();
            else Thread.Sleep(TimeSpan.FromSeconds(3)); // ignores the token; outlasts grace (0.2s) + slack (2s)
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_Second_Interrupt_Or_The_Grace_Period_Forces_The_Exit(bool secondInterrupt)
    {
        StubbornBuild.SecondInterrupt = secondInterrupt;
        var (_, events, forced) = Run(new StubbornBuild(), nameof(StubbornBuild.Run), shutdownTimeout: TimeSpan.FromMilliseconds(200));
        Assert.Equal(new[] { 130 }, forced);
        var forcedFinish = (BuildFinishedPayload)events.First(e => e.Type == BuildEventTypes.BuildFinished).Payload;
        Assert.Equal(BuildEventStatus.Cancelled, forcedFinish.Status);
        Assert.EndsWith("forced exit", forcedFinish.Reason, StringComparison.Ordinal);
        Assert.Equal(130, forcedFinish.ExitCode);
    }

    private sealed class FinishesAnywayBuild : TampBuild
    {
        public Target Run => _ => _.Executes(() => { Interrupt(); });
    }

    [Fact]
    public void A_Target_That_Completes_Despite_The_Interrupt_Is_Done()
    {
        var (result, events, _) = Run(new FinishesAnywayBuild(), nameof(FinishesAnywayBuild.Run));
        Assert.Equal(BuildEventStatus.Success, Finished(events, nameof(FinishesAnywayBuild.Run)).Status);
        Assert.Equal(130, result.ExitCode);
    }

    private sealed class NoInterruptBuild : TampBuild
    {
        public static bool Cancellable;
        public Target Run => _ => _.Executes(() => { Cancellable = TampBuild.BuildCancellation.CanBeCanceled; }).Executes(() => SleepPlan(0));
    }

    [Fact]
    public void Without_An_Interrupt_Nothing_Changes()
    {
        Assert.False(TampBuild.BuildCancellation.CanBeCanceled); // outside a run
        var (result, events, forced) = Run(new NoInterruptBuild(), nameof(NoInterruptBuild.Run));
        Assert.True(NoInterruptBuild.Cancellable);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.InterruptSignal);
        var build = BuildFinished(events);
        Assert.Equal("succeeded", build.Status);
        Assert.Null(build.Reason);
        Assert.Equal(0, build.Cancelled);
        Assert.Empty(forced);
    }

    [Fact]
    public void Reporters_Hear_About_Cancellation_And_Default_To_NotRun()
    {
        var reporter = new RecordingReporter();
        var sink = new ReporterProjectionSink(reporter);
        sink.Emit(new BuildEvent
        {
            Type = BuildEventTypes.TargetFinished, BuildId = "b", RunId = "r", TraceId = "b", SpanId = "0123456789abcdef",
            WorkerId = "human:test", Seq = 1, TargetId = "T",
            Payload = new TargetFinishedPayload { Target = "T", Status = BuildEventStatus.Cancelled, Reason = "signal: SIGINT", DurationMs = 5 },
        });
        Assert.Equal(new[] { "notrun:T:signal: SIGINT" }, reporter.Calls);
    }

    private sealed class RecordingReporter : IBuildReporter
    {
        public readonly List<string> Calls = new();
        public void OnBuildStart(string buildId, IReadOnlyList<string> requestedTargets, IReadOnlyList<string> executionClosure) { }
        public void OnTargetStart(string name) { }
        public void OnTargetSucceeded(string name, TimeSpan duration) { }
        public void OnTargetFailed(TargetFailureDetail detail) { }
        public void OnTargetSkipped(string name, string reason) { }
        public void OnTargetNotRun(string name, string reason) => Calls.Add($"notrun:{name}:{reason}");
        public void OnBuildEnd(string status, string? firstFailedTarget, int exitCode, TimeSpan totalDuration) { }
    }

    [Fact]
    public void Shutdown_Timeout_Comes_From_The_Flag_Then_The_Environment()
    {
        string? NoEnv(string _) => null;
        Assert.Equal(TimeSpan.FromSeconds(10), TampBuild.ResolveShutdownTimeout(new[] { "Ci" }, NoEnv));
        Assert.Equal(TimeSpan.FromSeconds(30), TampBuild.ResolveShutdownTimeout(new[] { "Ci", "--shutdown-timeout", "30" }, NoEnv));
        Assert.Equal(TimeSpan.FromSeconds(2.5), TampBuild.ResolveShutdownTimeout(new[] { "--shutdown-timeout=2.5", "Ci" }, NoEnv));
        Assert.Equal(TimeSpan.Zero, TampBuild.ResolveShutdownTimeout(new[] { "Ci" }, k => k == "TAMP_SHUTDOWN_TIMEOUT" ? "0" : null));
        Assert.Equal(TimeSpan.FromSeconds(5), TampBuild.ResolveShutdownTimeout(new[] { "--shutdown-timeout", "5" }, k => k == "TAMP_SHUTDOWN_TIMEOUT" ? "60" : null));
    }

    [Theory]
    [InlineData("--shutdown-timeout=-1")]
    [InlineData("--shutdown-timeout=soon")]
    [InlineData("--shutdown-timeout")]
    public void A_Bad_Shutdown_Timeout_Is_An_Error(string arg)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TampBuild.ResolveShutdownTimeout(new[] { "Ci", arg }, _ => null));
        Assert.Contains("--shutdown-timeout", ex.Message, StringComparison.Ordinal);
    }

    private sealed class ParseBuild : TampBuild
    {
        public Target Compile => _ => _.Executes(() => { });
    }

    [Fact]
    public void The_Shutdown_Timeout_Value_Is_Not_Taken_As_A_Target()
    {
        var targets = TampBuild.CollectTargets(new ParseBuild());
        var (_, names, _, _, _, _, _, _, _) = TampBuild.ParseInvocation(
            new[] { "--shutdown-timeout", "30", "Compile" }, targets, new HashSet<string>(StringComparer.Ordinal));
        Assert.Equal(new[] { "Compile" }, names);
    }

    [Fact]
    public void Cancelled_Is_In_The_Status_Vocabulary()
    {
        Assert.Equal("cancelled", BuildEventStatus.Cancelled);
        Assert.Equal("cancelled", Tamp.Diagnostics.TampDiagnostics.Tags.OutcomeCancelled);
    }

    internal static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    internal static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}

/// <summary>Real signals to this process; Unix only, and not in parallel with anything else.</summary>
[CollectionDefinition(nameof(RealSignalTests), DisableParallelization = true)]
public sealed class RealSignalCollection;

[Collection(nameof(RealSignalTests))]
public sealed class RealSignalTests
{
    private sealed class WaitForSignalBuild : TampBuild
    {
        public static string Signal = "TERM";
        public Target Run => _ => _.Executes(async ct =>
        {
            using (var kill = Process.Start("kill", $"-{Signal} {Environment.ProcessId}")) kill.WaitForExit();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        });
    }

    [Theory]
    [InlineData("TERM", 143)]
    [InlineData("INT", 130)]
    [InlineData("HUP", 129)]
    public void A_Real_Signal_Cancels_The_Build_And_The_Process_Survives(string signal, int expected)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        WaitForSignalBuild.Signal = signal;
        var clock = Stopwatch.StartNew();
        var (result, events, forced) = BuildCancellationTests.Run(new WaitForSignalBuild(), nameof(WaitForSignalBuild.Run), handleSignals: true);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"the signal should have cancelled the 30s wait (took {clock.Elapsed})");
        Assert.Equal(expected, result.ExitCode);
        Assert.Empty(forced);
        Assert.Equal(BuildEventStatus.Cancelled,
            ((TargetFinishedPayload)events.Single(e => e.Type == BuildEventTypes.TargetFinished).Payload).Status);
    }
}
