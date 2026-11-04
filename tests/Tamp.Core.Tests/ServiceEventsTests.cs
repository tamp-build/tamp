using System.IO;
using System.Linq;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// #124: <c>service.ready</c> / <c>service.stopped</c>, for targets that start a long-running service (a dev
/// server, an emulator). Covers the ambient emitters (no-op when inactive, argument validation), span sharing with
/// the service's <c>tool.invoked</c> command span, URL redaction, the pinned stop-reason vocabulary and the NDJSON
/// wire shape.
/// </summary>
public sealed class ServiceEventsTests
{
    private sealed class CapturingSink : IBuildEventSink
    {
        public readonly List<BuildEvent> Events = new();
        public void Emit(BuildEvent e) => Events.Add(e);
    }

    private sealed class ServiceBuild : TampBuild
    {
        public static Action Body = () => { };
        public Target Run => _ => _.Executes(() => Body());
    }

    private static List<BuildEvent> RunWith(Action body)
    {
        ServiceBuild.Body = body;
        var graph = new TargetGraph(TampBuild.CollectTargets(new ServiceBuild()));
        var sink = new CapturingSink();
        new Executor(graph, output: TextWriter.Null, eventSink: sink).Run("Run");
        return sink.Events;
    }

    private static readonly DateTimeOffset Started = new(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Inactive_Emitters_Are_NoOps()
    {
        Assert.False(BuildEvents.IsActive);
        Assert.Null(BuildEvents.ServiceReady("heft-dev-server", "https://localhost:4321/", port: 4321));
        BuildEvents.ServiceStopped("heft-dev-server", ServiceStopReason.Requested); // no throw
    }

    [Fact]
    public void Ready_And_Stopped_Share_A_Span_Parented_To_The_Target()
    {
        string? span = null;
        var events = RunWith(() =>
        {
            span = BuildEvents.ServiceReady("heft-dev-server", "https://localhost:4321/", port: 4321, pid: 1234,
                processStartTime: Started, readinessProbe: "https://localhost:4321/temp/build/manifests.js",
                statePath: ".tamp/temp/heft-dev-server/4321.json");
            BuildEvents.ServiceStopped("heft-dev-server", ServiceStopReason.Requested, span, pid: 1234, exitCode: 137, durationMs: 1500);
        });

        var target = events.Single(e => e.Type == BuildEventTypes.TargetStarted && e.TargetId == "Run");
        var ready = events.Single(e => e.Type == BuildEventTypes.ServiceReady);
        var stopped = events.Single(e => e.Type == BuildEventTypes.ServiceStopped);

        Assert.NotNull(span);
        Assert.Equal(span, ready.SpanId);
        Assert.Equal(span, stopped.SpanId);
        Assert.NotEqual(target.SpanId, span);
        Assert.Equal(target.SpanId, ready.ParentSpanId);
        Assert.Equal(target.SpanId, stopped.ParentSpanId);
        Assert.Equal("Run", ready.TargetId);
        Assert.True(ready.Seq < stopped.Seq);

        var rp = Assert.IsType<ServiceReadyPayload>(ready.Payload);
        Assert.Equal("heft-dev-server", rp.Kind);
        Assert.Equal("https://localhost:4321/", rp.Url);
        Assert.Equal(4321, rp.Port);
        Assert.Equal(1234, rp.Pid);
        Assert.Equal(Started, rp.ProcessStartTime);
        Assert.Equal("https://localhost:4321/temp/build/manifests.js", rp.ReadinessProbe);
        Assert.Equal(".tamp/temp/heft-dev-server/4321.json", rp.StatePath);

        var sp = Assert.IsType<ServiceStoppedPayload>(stopped.Payload);
        Assert.Equal(ServiceStopReason.Requested, sp.Reason);
        Assert.Equal(137, sp.ExitCode);
        Assert.Equal(1500, sp.DurationMs);

        // A deliberate stop doesn't fail the target.
        var finished = (TargetFinishedPayload)events.Single(e => e.Type == BuildEventTypes.TargetFinished).Payload;
        Assert.Equal(BuildEventStatus.Success, finished.Status);
    }

    [Fact]
    public void Service_Events_Can_Ride_The_Tool_Command_Span()
    {
        var plan = new CommandPlan { Executable = "node", Arguments = new[] { "heft", "start" } };
        var events = RunWith(() =>
        {
            var cmd = BuildEvents.ToolInvoked(plan);
            var span = BuildEvents.ServiceReady("heft-dev-server", "http://localhost:4321/", spanId: cmd);
            Assert.Equal(cmd, span);
            BuildEvents.ServiceStopped("heft-dev-server", ServiceStopReason.Signal, cmd);
            BuildEvents.ToolExited(cmd, plan.Executable, 137, 10);
        });

        var onSpan = events.Where(e => e.Type is BuildEventTypes.ToolInvoked or BuildEventTypes.ServiceReady
            or BuildEventTypes.ServiceStopped or BuildEventTypes.ToolExited).ToList();
        Assert.Equal(new[] { BuildEventTypes.ToolInvoked, BuildEventTypes.ServiceReady, BuildEventTypes.ServiceStopped, BuildEventTypes.ToolExited },
            onSpan.Select(e => e.Type));
        Assert.Single(onSpan.Select(e => e.SpanId).Distinct());
    }

    [Fact]
    public void Without_A_Span_Each_Call_Gets_A_New_One()
    {
        var events = RunWith(() =>
        {
            BuildEvents.ServiceReady("emulator", "http://localhost:10000/");
            BuildEvents.ServiceStopped("emulator", ServiceStopReason.Exited, exitCode: 1);
        });
        var ready = events.Single(e => e.Type == BuildEventTypes.ServiceReady);
        var stopped = events.Single(e => e.Type == BuildEventTypes.ServiceStopped);
        Assert.NotEqual(ready.SpanId, stopped.SpanId);
        Assert.Equal(ready.ParentSpanId, stopped.ParentSpanId);
    }

    [Fact]
    public void Stop_Without_Ready_Is_Allowed_For_A_Failed_Start()
    {
        var events = RunWith(() => BuildEvents.ServiceStopped("heft-dev-server", ServiceStopReason.Timeout, durationMs: 300_000));
        Assert.DoesNotContain(events, e => e.Type == BuildEventTypes.ServiceReady);
        Assert.Equal(ServiceStopReason.Timeout, ((ServiceStoppedPayload)events.Single(e => e.Type == BuildEventTypes.ServiceStopped).Payload).Reason);
    }

    [Fact]
    public void Url_And_Probe_Are_Scrubbed_Through_The_Redaction_Table()
    {
        const string token = "s3cr3t-token-value";
        var plan = new CommandPlan
        {
            Executable = "node",
            Arguments = new[] { "serve" },
            Secrets = new[] { new Secret("ServeToken", token) },
        };
        var events = RunWith(() =>
        {
            var cmd = BuildEvents.ToolInvoked(plan); // registers the secret with the build's redaction table
            BuildEvents.ServiceReady("svc", $"https://localhost:5000/?code={token}", cmd, readinessProbe: $"https://localhost:5000/health?code={token}");
        });

        var rp = (ServiceReadyPayload)events.Single(e => e.Type == BuildEventTypes.ServiceReady).Payload;
        Assert.DoesNotContain(token, rp.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(token, rp.ReadinessProbe!, StringComparison.Ordinal);
        Assert.Contains("<Secret:ServeToken>", rp.Url, StringComparison.Ordinal);
        foreach (var e in events) Assert.DoesNotContain(token, BuildEventJson.Serialize(e), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stopped")]
    [InlineData("REQUESTED")]
    public void Unknown_Stop_Reasons_Are_Rejected_Even_When_Inactive(string? reason)
    {
        Assert.Throws<ArgumentException>(() => BuildEvents.ServiceStopped("svc", reason!));
    }

    [Fact]
    public void Missing_Kind_Url_Or_Bad_Numbers_Are_Rejected()
    {
        Assert.Throws<ArgumentException>(() => BuildEvents.ServiceReady("", "http://localhost/"));
        Assert.Throws<ArgumentException>(() => BuildEvents.ServiceReady("svc", " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildEvents.ServiceReady("svc", "http://localhost/", port: 70000));
        Assert.Throws<ArgumentException>(() => BuildEvents.ServiceStopped(" ", ServiceStopReason.Requested));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildEvents.ServiceStopped("svc", ServiceStopReason.Requested, durationMs: -1));
    }

    [Fact]
    public void Stop_Reason_Vocabulary_Is_Pinned()
    {
        Assert.Equal(new[] { "requested", "signal", "exited", "timeout" }, ServiceStopReason.All);
    }

    [Fact]
    public void Process_Start_Time_Is_Normalized_To_Utc()
    {
        var local = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(-8));
        var events = RunWith(() => BuildEvents.ServiceReady("svc", "http://localhost/", processStartTime: local));
        var rp = (ServiceReadyPayload)events.Single(e => e.Type == BuildEventTypes.ServiceReady).Payload;
        Assert.Equal(TimeSpan.Zero, rp.ProcessStartTime!.Value.Offset);
        Assert.Equal(local, rp.ProcessStartTime.Value);
    }

    [Fact]
    public void Payloads_RoundTrip_Through_Ndjson_With_The_Type_Discriminator()
    {
        BuildEvent Wrap(BuildEventPayload p, string type) => new()
        {
            Type = type, BuildId = "b", RunId = "r", TraceId = "b", SpanId = "0123456789abcdef",
            WorkerId = "human:test", Seq = 0, Payload = p,
        };
        var ready = new ServiceReadyPayload
        {
            Kind = "heft-dev-server", Url = "https://localhost:4321/", Port = 4321, Pid = 42, ProcessStartTime = Started,
            ReadinessProbe = "https://localhost:4321/temp/build/manifests.js", StatePath = "s.json",
        };
        var stopped = new ServiceStoppedPayload { Kind = "heft-dev-server", Reason = ServiceStopReason.Signal, Pid = 42, ExitCode = 137, DurationMs = 12.5 };

        foreach (var (payload, type, wireType) in new (BuildEventPayload, string, string)[]
                 {
                     (ready, BuildEventTypes.ServiceReady, "service.ready"),
                     (stopped, BuildEventTypes.ServiceStopped, "service.stopped"),
                 })
        {
            var json = BuildEventJson.Serialize(Wrap(payload, type));
            Assert.Contains($"\"$type\":\"{wireType}\"", json, StringComparison.Ordinal);
            var back = BuildEventJson.Deserialize(json);
            Assert.NotNull(back);
            Assert.Equal(type, back!.Type);
            Assert.Equal(payload, back.Payload);
        }

        // Optional fields may be absent on the wire.
        var minimal = BuildEventJson.Deserialize(BuildEventJson.Serialize(Wrap(new ServiceStoppedPayload { Kind = "svc", Reason = "exited" }, BuildEventTypes.ServiceStopped)));
        var mp = Assert.IsType<ServiceStoppedPayload>(minimal!.Payload);
        Assert.Null(mp.Pid);
        Assert.Null(mp.ExitCode);
    }
}
