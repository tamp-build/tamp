using System.IO;
using System.Text.Json;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests for the canonical <see cref="BuildEvent"/> stream (`#0a`, ADR 0019):
/// lifecycle ordering, envelope identity + span linkage, per-status payloads,
/// the pinned schema vocabulary, NDJSON round-trip + camelCase wire shape, and
/// the <c>ReporterProjectionSink</c> equivalence that keeps <see cref="IBuildReporter"/>
/// working as a projection.
/// </summary>
public sealed class CanonicalEventModelTests
{
    private sealed class CapturingSink : IBuildEventSink
    {
        public readonly List<BuildEvent> Events = new();
        public void Emit(BuildEvent e) => Events.Add(e);
    }

    private sealed class TB : TampBuild
    {
        public static bool ThrowFromCompile;
        public Target Restore => _ => _.Executes(() => { });
        public Target Compile => _ => _
            .DependsOn(nameof(Restore))
            .Executes(() => { if (ThrowFromCompile) throw new InvalidOperationException("synthetic failure"); });
    }

    private static (List<BuildEvent> Events, ExecutionResult Result) RunCaptured(
        bool throwFromCompile = false,
        IReadOnlySet<string>? skip = null,
        IBuildReporter? reporter = null)
    {
        TB.ThrowFromCompile = throwFromCompile;
        try
        {
            var targets = TampBuild.CollectTargets(new TB());
            var graph = new TargetGraph(targets);
            var sink = new CapturingSink();
            var ex = new Executor(graph, output: TextWriter.Null, skippedByUser: skip, reporter: reporter, eventSink: sink);
            var result = ex.Run("Compile");
            return (sink.Events, result);
        }
        finally { TB.ThrowFromCompile = false; }
    }

    // ─── Lifecycle ordering ───────────────────────────────────────────────

    [Fact]
    public void Emits_BuildStarted_Then_Target_Events_Then_BuildFinished()
    {
        var (events, _) = RunCaptured();
        var types = events.Select(e => e.Type).ToList();
        Assert.Equal(new[]
        {
            BuildEventTypes.BuildStarted,
            BuildEventTypes.TargetStarted,   // Restore
            BuildEventTypes.TargetFinished,  // Restore
            BuildEventTypes.TargetStarted,   // Compile
            BuildEventTypes.TargetFinished,  // Compile
            BuildEventTypes.BuildFinished,
        }, types);
    }

    // ─── Envelope identity + span linkage ─────────────────────────────────

    [Fact]
    public void All_Events_Share_Build_Trace_Run_Worker_Identity_And_Monotonic_Seq()
    {
        var (events, _) = RunCaptured();
        var first = events[0];

        Assert.All(events, e =>
        {
            Assert.Equal(first.BuildId, e.BuildId);
            Assert.Equal(first.RunId, e.RunId);
            Assert.Equal(first.WorkerId, e.WorkerId);
            Assert.Equal(e.BuildId, e.TraceId);              // traceId == buildId
            Assert.Equal(BuildEventSchema.Version, e.SchemaVersion);
        });

        var seqs = events.Select(e => e.Seq).ToList();
        Assert.Equal(seqs.OrderBy(x => x).ToList(), seqs);   // strictly increasing order
        Assert.Equal(seqs.Distinct().Count(), seqs.Count);
    }

    [Fact]
    public void Build_Span_Is_Root_And_Targets_Parent_To_It_And_Started_Finished_Share_A_Span()
    {
        var (events, _) = RunCaptured();
        var buildStart = events.First(e => e.Type == BuildEventTypes.BuildStarted);
        Assert.Null(buildStart.ParentSpanId);

        var buildSpan = buildStart.SpanId;
        foreach (var target in new[] { "Restore", "Compile" })
        {
            var started = events.First(e => e.Type == BuildEventTypes.TargetStarted && e.TargetId == target);
            var finished = events.First(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == target);
            Assert.Equal(buildSpan, started.ParentSpanId);
            Assert.Equal(buildSpan, finished.ParentSpanId);
            Assert.Equal(started.SpanId, finished.SpanId);   // same target span for start + finish
            Assert.Matches("^[0-9a-f]{16}$", started.SpanId); // 16-hex span id
        }
    }

    // ─── Per-status payloads ──────────────────────────────────────────────

    [Fact]
    public void TargetFinished_Success_Carries_Status_And_Duration()
    {
        var (events, _) = RunCaptured();
        var restore = (TargetFinishedPayload)events
            .First(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == "Restore").Payload;
        Assert.Equal(BuildEventStatus.Success, restore.Status);
        Assert.NotNull(restore.DurationMs);
    }

    [Fact]
    public void Failing_Target_Emits_TargetFinished_Failure_With_Reason()
    {
        var (events, result) = RunCaptured(throwFromCompile: true);
        Assert.NotEqual(0, result.ExitCode);
        var compile = (TargetFinishedPayload)events
            .First(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == "Compile").Payload;
        Assert.Equal(BuildEventStatus.Failure, compile.Status);
        Assert.Contains("synthetic failure", compile.Reason);

        var finished = (BuildFinishedPayload)events.Last().Payload;
        Assert.Equal("failed", finished.Status);
        Assert.Equal("Compile", finished.FirstFailedTarget);
        Assert.Equal(1, finished.Failed);
    }

    [Fact]
    public void UserSkipped_Target_Emits_TargetFinished_Skipped()
    {
        var (events, _) = RunCaptured(skip: new HashSet<string>(StringComparer.Ordinal) { "Restore" });
        var restore = (TargetFinishedPayload)events
            .First(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == "Restore").Payload;
        Assert.Equal(BuildEventStatus.Skipped, restore.Status);
        Assert.Equal("skipped by --skip", restore.Reason);
    }

    // ─── Schema vocabulary is pinned (additive-only contract) ─────────────

    [Fact]
    public void Schema_Types_Are_Pinned()
    {
        Assert.Equal(new[]
        {
            "build.started", "build.finished", "target.started", "target.finished",
        }, BuildEventSchema.Types);
        Assert.Equal("1.0", BuildEventSchema.Version);
    }

    // ─── NDJSON round-trip + wire shape ───────────────────────────────────

    [Fact]
    public void Events_RoundTrip_Through_Ndjson_Preserving_Type_And_Payload()
    {
        var (events, _) = RunCaptured();
        foreach (var e in events)
        {
            var line = BuildEventJson.Serialize(e);
            Assert.DoesNotContain("\n", line);               // one line per event
            var back = BuildEventJson.Deserialize(line);
            Assert.NotNull(back);
            Assert.Equal(e.Type, back!.Type);
            Assert.Equal(e.Payload.GetType(), back.Payload.GetType());
            Assert.Equal(e.Seq, back.Seq);
            Assert.Equal(e.SpanId, back.SpanId);
        }
    }

    [Fact]
    public void Wire_Shape_Is_CamelCase_With_Payload_Kind_And_Drops_Nulls()
    {
        var (events, _) = RunCaptured();
        var buildStart = events.First(e => e.Type == BuildEventTypes.BuildStarted);
        using var doc = JsonDocument.Parse(BuildEventJson.Serialize(buildStart));
        var root = doc.RootElement;

        Assert.Equal("build.started", root.GetProperty("type").GetString());
        Assert.True(root.TryGetProperty("buildId", out _));        // camelCase
        Assert.True(root.TryGetProperty("spanId", out _));
        Assert.False(root.TryGetProperty("targetId", out _));      // null dropped for build-level event

        var payload = root.GetProperty("payload");
        Assert.Equal("build.started", payload.GetProperty("kind").GetString());  // payload discriminator
        Assert.True(payload.TryGetProperty("requestedTargets", out _));
    }

    // ─── IBuildReporter is a faithful projection ──────────────────────────

    private sealed class RecordingReporter : IBuildReporter
    {
        public readonly List<string> Calls = new();
        public void OnBuildStart(string b, IReadOnlyList<string> r, IReadOnlyList<string> c) => Calls.Add($"start:{string.Join(",", r)}");
        public void OnTargetStart(string n) => Calls.Add($"t.start:{n}");
        public void OnTargetSucceeded(string n, TimeSpan d) => Calls.Add($"t.ok:{n}");
        public void OnTargetFailed(TargetFailureDetail d) => Calls.Add($"t.fail:{d.TargetName}:{d.FailureReason}");
        public void OnTargetSkipped(string n, string r) => Calls.Add($"t.skip:{n}:{r}");
        public void OnTargetNotRun(string n, string r) => Calls.Add($"t.notrun:{n}");
        public void OnBuildEnd(string s, string? f, int e, TimeSpan d) => Calls.Add($"end:{s}:{e}");
    }

    [Fact]
    public void ReporterProjection_Delivers_Expected_Callbacks_On_Success()
    {
        var reporter = new RecordingReporter();
        RunCaptured(reporter: reporter);
        Assert.Equal(new[]
        {
            "start:Compile",
            "t.start:Restore", "t.ok:Restore",
            "t.start:Compile", "t.ok:Compile",
            "end:succeeded:0",
        }, reporter.Calls);
    }

    [Fact]
    public void ReporterProjection_Delivers_Failure_Callback_With_Detail()
    {
        var reporter = new RecordingReporter();
        RunCaptured(throwFromCompile: true, reporter: reporter);
        Assert.Contains(reporter.Calls, c => c.StartsWith("t.fail:Compile:") && c.Contains("synthetic failure"));
        Assert.Contains(reporter.Calls, c => c.StartsWith("end:failed:"));
    }
}
