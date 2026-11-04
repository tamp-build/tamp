using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// #125 part 2: <see cref="ManagedProcess"/>. The child processes are plain <c>sh</c> / <c>cmd</c> sleepers; HTTP and
/// TCP readiness are checked against listeners in this process, so nothing here needs Node or a real server. They run
/// on their own (no parallelism with other classes): real processes and listeners load a small CI runner, and
/// timing-sensitive tests elsewhere (Polling) shouldn't feel it.
/// </summary>
[Collection(nameof(ManagedProcessTests))]
public sealed class ManagedProcessTests
{
    private static readonly bool Windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static CommandPlan Shell(string sh, string cmd) => Windows
        ? new CommandPlan { Executable = "cmd.exe", Arguments = new[] { "/c", cmd } }
        : new CommandPlan { Executable = "/bin/sh", Arguments = new[] { "-c", sh } };

    private static CommandPlan ReadyThenSleep(int seconds = 30) =>
        Shell($"echo ready; sleep {seconds}", $"echo ready & ping -n {seconds + 1} 127.0.0.1 >nul");

    private static CommandPlan Sleep(int seconds = 30) =>
        Shell($"sleep {seconds}", $"ping -n {seconds + 1} 127.0.0.1 >nul");

    private static ManagedProcessOptions Options(Readiness? readiness, TimeSpan? readyTimeout = null) => new()
    {
        Kind = "test-service",
        Readiness = readiness,
        ReadyTimeout = readyTimeout ?? TimeSpan.FromSeconds(30),
        PollInterval = TimeSpan.FromMilliseconds(50),
        ServiceUrl = "http://localhost:1234/",
        Port = 1234,
        Output = TextWriter.Null,
    };

    private static Readiness SaysReady() => Readiness.OutputMatches(new Regex("^ready", RegexOptions.CultureInvariant));

    // ── Running inside a build, for the events ───────────────────────────────

    private sealed class LambdaBuild : TampBuild
    {
        public static Func<CancellationToken, Task> Body = _ => Task.CompletedTask;
        public static Func<CancellationToken, Task> Second = _ => Task.CompletedTask;
        public Target First => _ => _.Executes(async ct => await Body(ct));
        public Target Next => _ => _.DependsOn(nameof(First)).Executes(async ct => await Second(ct));
    }

    private static (ExecutionResult Result, List<BuildEvent> Events) RunBuild(Func<CancellationToken, Task> first, Func<CancellationToken, Task>? next = null)
    {
        LambdaBuild.Body = first;
        LambdaBuild.Second = next ?? (_ => Task.CompletedTask);
        var (result, events, _) = BuildCancellationTests.Run(new LambdaBuild(), nameof(LambdaBuild.Next));
        return (result, events);
    }

    private static List<BuildEvent> OnSpan(List<BuildEvent> events, string type) => events.Where(e => e.Type == type).ToList();

    [Fact]
    public void Lifecycle_Events_Share_One_Span_Parented_To_The_Starting_Target()
    {
        int pid = 0;
        var (result, events) = RunBuild(async ct =>
        {
            await using var p = await ManagedProcess.StartAsync(ReadyThenSleep(), Options(SaysReady()), ct);
            pid = p.Pid;
            Assert.False(p.HasExited);
        });
        Assert.Equal(0, result.ExitCode);

        var target = events.Single(e => e.Type == BuildEventTypes.TargetStarted && e.TargetId == nameof(LambdaBuild.First));
        var sequence = events.Where(e => e.Type is BuildEventTypes.ToolInvoked or BuildEventTypes.ServiceReady
            or BuildEventTypes.ServiceStopped or BuildEventTypes.ToolExited).ToList();
        Assert.Equal(new[] { BuildEventTypes.ToolInvoked, BuildEventTypes.ServiceReady, BuildEventTypes.ServiceStopped, BuildEventTypes.ToolExited },
            sequence.Select(e => e.Type));
        Assert.Single(sequence.Select(e => e.SpanId).Distinct());
        Assert.All(sequence, e => Assert.Equal(target.SpanId, e.ParentSpanId));
        Assert.All(sequence, e => Assert.Equal(nameof(LambdaBuild.First), e.TargetId));

        var ready = (ServiceReadyPayload)sequence[1].Payload;
        Assert.Equal("test-service", ready.Kind);
        Assert.Equal("http://localhost:1234/", ready.Url);
        Assert.Equal(1234, ready.Port);
        Assert.Equal(pid, ready.Pid);
        Assert.Equal("output matching /^ready/", ready.ReadinessProbe);
        Assert.Equal(ServiceStopReason.Requested, ((ServiceStoppedPayload)sequence[2].Payload).Reason);

        // A deliberately killed service doesn't raise the "tool exited non-zero but the target passed" advisory.
        Assert.DoesNotContain(events, e => e.Payload is DiagnosticEmittedPayload d && d.RuleId == "tamp.tool.nonzero_ignored");
        Assert.True(BuildCancellationTests.WaitUntil(() => !IsAlive(pid), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Exiting_Before_Ready_Throws_With_The_Output_Tail()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ManagedProcess.StartAsync(
            Shell("echo boom; exit 3", "echo boom & exit 3"), Options(Readiness.When(_ => Task.FromResult(false), "never"))));
        Assert.Contains("code 3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exiting_Before_Ready_Is_Recorded_With_Reason_Exited()
    {
        var (_, events) = RunBuild(async ct =>
        {
            try { await ManagedProcess.StartAsync(Shell("exit 3", "exit 3"), Options(Readiness.When(_ => Task.FromResult(false), "never")), ct); }
            catch (InvalidOperationException) { }
        });
        Assert.Empty(OnSpan(events, BuildEventTypes.ServiceReady));
        var stopped = (ServiceStoppedPayload)OnSpan(events, BuildEventTypes.ServiceStopped).Single().Payload;
        Assert.Equal(ServiceStopReason.Exited, stopped.Reason);
        Assert.Equal(3, stopped.ExitCode);
    }

    [Fact]
    public void Readiness_Timeout_Stops_The_Process_With_Reason_Timeout()
    {
        var (_, events) = RunBuild(async ct =>
        {
            var ex = await Assert.ThrowsAsync<TimeoutException>(() => ManagedProcess.StartAsync(Sleep(),
                Options(Readiness.When(_ => Task.FromResult(false), "the probe"), TimeSpan.FromMilliseconds(500)), ct));
            Assert.Contains("the probe", ex.Message, StringComparison.Ordinal);
            Assert.Contains("check returned false", ex.Message, StringComparison.Ordinal);
        });
        Assert.Equal(ServiceStopReason.Timeout, ((ServiceStoppedPayload)OnSpan(events, BuildEventTypes.ServiceStopped).Single().Payload).Reason);
        Assert.Single(OnSpan(events, BuildEventTypes.ToolExited));
    }

    [Fact]
    public async Task Http_Readiness_Waits_For_A_Success_Status_Not_An_Open_Port()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var status = 503;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                ctx.Response.StatusCode = Volatile.Read(ref status);
                ctx.Response.Close();
            }
        });
        _ = Task.Run(async () => { await Task.Delay(600); Volatile.Write(ref status, 200); });

        var clock = Stopwatch.StartNew();
        await using var p = await ManagedProcess.StartAsync(Sleep(), Options(Readiness.Http(new Uri($"http://localhost:{port}/health"))));
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(500), "a 503 must not count as ready");
        listener.Stop();
    }

    [Fact]
    public async Task Http_Readiness_Reports_The_Last_Status_On_Timeout()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try { var ctx = await listener.GetContextAsync(); ctx.Response.StatusCode = 404; ctx.Response.Close(); } catch { return; }
            }
        });
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => ManagedProcess.StartAsync(Sleep(),
            Options(Readiness.Http(new Uri($"http://localhost:{port}/x")), TimeSpan.FromMilliseconds(500))));
        Assert.Contains("HTTP 404", ex.Message, StringComparison.Ordinal);
        listener.Stop();
    }

    [Fact]
    public async Task Tcp_Readiness_Waits_For_The_Port()
    {
        var port = FreePort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        _ = Task.Run(async () => { await Task.Delay(500); listener.Start(); });
        try
        {
            var clock = Stopwatch.StartNew();
            await using var p = await ManagedProcess.StartAsync(Sleep(), Options(Readiness.Tcp("127.0.0.1", port)));
            Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(400));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task No_Readiness_Means_Ready_Once_Started()
    {
        await using var p = await ManagedProcess.StartAsync(Sleep(), Options(readiness: null));
        Assert.False(p.HasExited);
    }

    [Fact]
    public async Task Concurrent_Stops_All_Wait_For_The_Tree_To_Be_Dead()
    {
        var p = await ManagedProcess.StartAsync(ReadyThenSleep(), Options(SaysReady()));
        using var go = new ManualResetEventSlim();
        var others = Enumerable.Range(0, 3).Select(_ => Task.Run(() => { go.Wait(); p.Dispose(); })).ToArray();
        go.Set();
        p.Dispose();
        Assert.True(p.HasExited);
        Assert.False(IsAlive(p.Pid));
        await Task.WhenAll(others);
        Assert.Equal(ServiceStopReason.Requested, p.StopReason);
        p.Dispose(); // still idempotent
    }

    [Fact]
    public async Task Async_And_Sync_Stops_Together_Record_One_Stop()
    {
        var (_, events) = RunBuild(async ct =>
        {
            var p = await ManagedProcess.StartAsync(ReadyThenSleep(), Options(SaysReady()), ct);
            var stops = new[]
            {
                p.DisposeAsync().AsTask(),
                p.DisposeAsync().AsTask(),
                Task.Run(() => p.Dispose()),
            };
            await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(30), ct);
            Assert.True(p.HasExited);
            Assert.False(IsAlive(p.Pid));
            await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct); // already complete
        });
        Assert.Single(OnSpan(events, BuildEventTypes.ServiceStopped));
        Assert.Single(OnSpan(events, BuildEventTypes.ToolExited));
    }

    [Fact]
    public async Task The_Whole_Tree_Is_Killed()
    {
        if (Windows) return;
        var pidFile = Path.Combine(Path.GetTempPath(), $"tamp-mp-{Guid.NewGuid():N}.pid");
        try
        {
            var p = await ManagedProcess.StartAsync(
                Shell($"sleep 30 & echo $! > '{pidFile}'; echo ready; wait", ""), Options(SaysReady()));
            var grandchild = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(IsAlive(grandchild));
            await p.DisposeAsync();
            Assert.True(BuildCancellationTests.WaitUntil(() => !IsAlive(grandchild), TimeSpan.FromSeconds(5)), "the service's own child must die too");
        }
        finally { File.Delete(pidFile); }
    }

    [Fact]
    public void Exiting_On_Its_Own_After_Ready_Is_Recorded_At_Once()
    {
        var (_, events) = RunBuild(async ct =>
        {
            var p = await ManagedProcess.StartAsync(
                Shell("echo ready; sleep 1; exit 4", "echo ready & ping -n 2 127.0.0.1 >nul & exit 4"), Options(SaysReady()), ct);
            await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(20), ct);
            Assert.True(p.HasExited);
            Assert.Equal(4, p.ExitCode);
            Assert.Equal(ServiceStopReason.Exited, p.StopReason);
            p.Dispose(); // nothing more to record
        });
        var stopped = OnSpan(events, BuildEventTypes.ServiceStopped).Single();
        Assert.Equal(ServiceStopReason.Exited, ((ServiceStoppedPayload)stopped.Payload).Reason);
        Assert.Equal(4, ((ToolExitedPayload)OnSpan(events, BuildEventTypes.ToolExited).Single().Payload).ExitCode);
    }

    [Fact]
    public void An_Interrupt_Stops_It_At_Once_With_Reason_Signal()
    {
        int pid = 0;
        var clock = Stopwatch.StartNew();
        var (result, events) = RunBuild(async ct =>
        {
            var p = await ManagedProcess.StartAsync(ReadyThenSleep(), Options(SaysReady()), ct);
            pid = p.Pid;
            BuildCancellationTests.Interrupt();
            Assert.True(p.HasExited, "the build token's cancellation stops it before the target resumes");
            await Task.Delay(Timeout.Infinite, ct);
        });
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15));
        Assert.Equal(130, result.ExitCode);
        Assert.False(IsAlive(pid));
        Assert.Equal(ServiceStopReason.Signal, ((ServiceStoppedPayload)OnSpan(events, BuildEventTypes.ServiceStopped).Single().Payload).Reason);
    }

    [Fact]
    public void A_Service_Can_Serve_Later_Targets_And_Is_Stopped_When_The_Build_Ends()
    {
        ManagedProcess? kept = null;
        var (result, events) = RunBuild(
            async ct => { kept = await ManagedProcess.StartAsync(ReadyThenSleep(), Options(SaysReady()), ct); },
            _ => { Assert.False(kept!.HasExited, "still running in the next target"); return Task.CompletedTask; });
        Assert.Equal(0, result.ExitCode);
        Assert.True(kept!.HasExited);
        Assert.Equal(ServiceStopReason.Requested, kept.StopReason);

        // Its stop is on the stream, parented to the target that started it, before build.finished.
        var types = events.Select(e => e.Type).ToList();
        Assert.True(types.IndexOf(BuildEventTypes.ServiceStopped) < types.LastIndexOf(BuildEventTypes.BuildFinished));
        var first = events.Single(e => e.Type == BuildEventTypes.TargetStarted && e.TargetId == nameof(LambdaBuild.First));
        Assert.Equal(first.SpanId, OnSpan(events, BuildEventTypes.ServiceStopped).Single().ParentSpanId);
    }

    [Fact]
    public async Task Graceful_Stop_Sends_SIGTERM_First()
    {
        if (Windows) return;
        var p = await ManagedProcess.StartAsync(
            Shell("trap 'echo bye; exit 0' TERM; echo ready; while true; do sleep 0.1; done", ""),
            new ManagedProcessOptions
            {
                Kind = "graceful",
                Readiness = SaysReady(),
                GracefulStopTimeout = TimeSpan.FromSeconds(10),
                Output = TextWriter.Null,
            });
        await p.DisposeAsync();
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("bye", p.Tail);
    }

    [Fact]
    public async Task Outside_A_Build_It_Works_Without_Events()
    {
        Assert.False(BuildEvents.IsActive);
        var output = new StringWriter();
        var p = await ManagedProcess.StartAsync(ReadyThenSleep(), new ManagedProcessOptions
        {
            Kind = "standalone",
            Readiness = SaysReady(),
            PollInterval = TimeSpan.FromMilliseconds(50),
            Output = output,
        });
        await p.DisposeAsync();
        Assert.Contains("ready", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("ready", p.Tail.First(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Options_Are_Validated()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ManagedProcess.StartAsync(Sleep(), new ManagedProcessOptions { Kind = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() => ManagedProcess.StartAsync(Sleep(), new ManagedProcessOptions { Kind = "k", ReadyTimeout = TimeSpan.Zero }));
        await Assert.ThrowsAsync<ArgumentException>(() => ManagedProcess.StartAsync(Sleep(), new ManagedProcessOptions { Kind = "k", GracefulStopTimeout = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentException>(() => Readiness.Http(new Uri("ftp://localhost/")));
        Assert.Throws<ArgumentOutOfRangeException>(() => Readiness.Tcp("localhost", 0));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("dev.localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("contoso.com", false)]
    [InlineData("localhost.contoso.com", false)]
    public void Loopback_Hosts(string host, bool expected) => Assert.Equal(expected, Readiness.IsLoopbackHost(host));

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    // Alive and not a zombie (containers whose PID 1 doesn't reap leave killed orphans as zombies).
    private static bool IsAlive(int pid)
    {
        if (!BuildCancellationTests.IsRunning(pid)) return false;
        if (!OperatingSystem.IsLinux()) return true;
        try { return File.ReadAllText($"/proc/{pid}/stat").Split(' ')[2] != "Z"; }
        catch (IOException) { return false; }
    }
}

/// <summary>Keeps <see cref="ManagedProcessTests"/> from running in parallel with the rest of the suite.</summary>
[CollectionDefinition(nameof(ManagedProcessTests), DisableParallelization = true)]
public sealed class ManagedProcessCollection;
