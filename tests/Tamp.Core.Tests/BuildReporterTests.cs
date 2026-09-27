using System.IO;
using System.Linq;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests for the machine output channel on stdout — <c>--reporter=json</c>. As of the
/// ADR-0019 hard cutover (1.16) this emits the <b>canonical</b> <see cref="BuildEvent"/>
/// envelope (identical to <c>--events</c>), not the pre-1.15 flat shape. Verifies the
/// lifecycle ordering, per-status payloads, success vs failure vs skipped paths, and that
/// the default text reporter still emits no NDJSON. The legacy <c>JsonBuildReporter</c>
/// flat schema is gone (#68).
/// </summary>
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class BuildReporterTests
{
    private sealed class TestBuild : TampBuild
    {
        public static int RestoreCount;
        public static int CompileCount;
        public static bool ThrowFromCompile;

        public Target Restore => _ => _.Description("Restore").Executes(() => { RestoreCount++; });

        public Target Compile => _ => _
            .DependsOn(nameof(Restore))
            .Executes(() =>
            {
                CompileCount++;
                if (ThrowFromCompile) throw new InvalidOperationException("synthetic compile failure");
            });

        public static void Reset()
        {
            RestoreCount = 0;
            CompileCount = 0;
            ThrowFromCompile = false;
        }
    }

    private static (string Stdout, int Exit) RunWithCapturedStdout(string[] args, bool throwFromCompile = false)
    {
        TestBuild.Reset();
        TestBuild.ThrowFromCompile = throwFromCompile;
        var stdout = new StringWriter();
        var prev = Console.Out;
        Console.SetOut(stdout);
        try
        {
            var exit = TampBuild.Execute<TestBuild>(args);
            return (stdout.ToString(), exit);
        }
        finally
        {
            Console.SetOut(prev);
        }
    }

    /// <summary>
    /// Deserialize the captured stdout as canonical <see cref="BuildEvent"/>s and keep only this
    /// run's events. Console.Out is process-global; a parallel test in another collection can bleed
    /// a line into the same writer — filtering by the run's <see cref="BuildEvent.BuildId"/> (and
    /// tolerating non-canonical lines) keeps the assertions focused on this build's stream.
    /// </summary>
    private static List<BuildEvent> EventsForThisRun(string text)
    {
        var all = new List<BuildEvent>();
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t[0] != '{') continue;
            BuildEvent? ev = null;
            try { ev = BuildEventJson.Deserialize(t); } catch { /* not a canonical line */ }
            if (ev is not null) all.Add(ev);
        }
        var start = all.FirstOrDefault(e => e.Type == BuildEventTypes.BuildStarted);
        return start is null ? all : all.Where(e => e.BuildId == start.BuildId).ToList();
    }

    private static readonly HashSet<string> Lifecycle = new()
    {
        BuildEventTypes.BuildStarted, BuildEventTypes.TargetStarted,
        BuildEventTypes.TargetFinished, BuildEventTypes.BuildFinished,
    };

    // ─── Canonical envelope + lifecycle ordering — happy path ─────────────

    [Fact]
    public void Json_Reporter_Emits_Canonical_Lifecycle_In_Order()
    {
        var (output, exit) = RunWithCapturedStdout(new[] { "Compile", "--reporter=json" });
        Assert.Equal(0, exit);
        var events = EventsForThisRun(output);

        // Every line is the canonical envelope, not the old flat shape.
        Assert.All(events, e => Assert.Equal(BuildEventSchema.Version, e.SchemaVersion));
        Assert.Contains(events, e => e.Type == BuildEventTypes.BuildStarted);   // not "build.start"

        var types = events.Select(e => e.Type).Where(Lifecycle.Contains).ToList();
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

    [Fact]
    public void Json_Reporter_BuildStarted_Carries_RequestedTargets_And_Closure()
    {
        var (output, _) = RunWithCapturedStdout(new[] { "Compile", "--reporter=json" });
        var events = EventsForThisRun(output);
        var start = (BuildStartedPayload)events.Single(e => e.Type == BuildEventTypes.BuildStarted).Payload;
        Assert.Equal("Compile", start.RequestedTargets[0]);
        Assert.Contains("Restore", start.ExecutionClosure);
        Assert.Contains("Compile", start.ExecutionClosure);
    }

    [Fact]
    public void Json_Reporter_TargetFinished_Reports_Success_And_Duration_For_Happy_Path()
    {
        var (output, _) = RunWithCapturedStdout(new[] { "Compile", "--reporter=json" });
        var finished = EventsForThisRun(output)
            .Where(e => e.Type == BuildEventTypes.TargetFinished)
            .Select(e => (TargetFinishedPayload)e.Payload)
            .ToList();
        Assert.Equal(2, finished.Count);
        Assert.All(finished, p =>
        {
            Assert.Equal(BuildEventStatus.Success, p.Status);
            Assert.NotNull(p.DurationMs);
        });
    }

    [Fact]
    public void Json_Reporter_BuildFinished_Reports_Succeeded_And_Exit_Zero()
    {
        var (output, _) = RunWithCapturedStdout(new[] { "Compile", "--reporter=json" });
        var end = (BuildFinishedPayload)EventsForThisRun(output).Single(e => e.Type == BuildEventTypes.BuildFinished).Payload;
        Assert.Equal("succeeded", end.Status);
        Assert.Equal(0, end.ExitCode);
        Assert.Null(end.FirstFailedTarget);
    }

    // ─── Failure path ─────────────────────────────────────────────────────

    [Fact]
    public void Json_Reporter_BuildFinished_Reports_Failed_With_FirstFailedTarget()
    {
        var (output, exit) = RunWithCapturedStdout(new[] { "Compile", "--reporter=json" }, throwFromCompile: true);
        Assert.NotEqual(0, exit);
        var end = (BuildFinishedPayload)EventsForThisRun(output).Single(e => e.Type == BuildEventTypes.BuildFinished).Payload;
        Assert.Equal("failed", end.Status);
        Assert.Equal("Compile", end.FirstFailedTarget);
    }

    [Fact]
    public void Json_Reporter_TargetFinished_Failure_Carries_Reason_When_Target_Throws()
    {
        var (output, _) = RunWithCapturedStdout(new[] { "Compile", "--reporter=json" }, throwFromCompile: true);
        var compile = (TargetFinishedPayload)EventsForThisRun(output)
            .Single(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == "Compile").Payload;
        Assert.Equal(BuildEventStatus.Failure, compile.Status);
        Assert.Contains("synthetic compile failure", compile.Reason);
    }

    // ─── Skipped path ─────────────────────────────────────────────────────

    [Fact]
    public void Json_Reporter_TargetFinished_Skipped_When_Target_User_Skipped()
    {
        var (output, exit) = RunWithCapturedStdout(new[] { "Compile", "--skip", "Restore", "--reporter=json" });
        Assert.Equal(0, exit);
        var restore = (TargetFinishedPayload)EventsForThisRun(output)
            .Single(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == "Restore").Payload;
        Assert.Equal(BuildEventStatus.Skipped, restore.Status);
        Assert.Equal("skipped by --skip", restore.Reason);
    }

    // ─── Stdout discipline: every line is a canonical event ───────────────

    [Fact]
    public void Json_Reporter_Every_Line_Is_A_Canonical_BuildEvent()
    {
        var (output, _) = RunWithCapturedStdout(new[] { "Compile", "--reporter=json" });
        var events = EventsForThisRun(output);
        Assert.All(events, e =>
        {
            Assert.Contains(e.Type, BuildEventSchema.Types);      // known vocabulary
            Assert.Equal(e.BuildId, e.TraceId);                   // canonical envelope invariant
        });
        // At minimum the build.started / build.finished pair (guard against a silenced stream).
        Assert.Contains(events, e => e.Type == BuildEventTypes.BuildStarted);
        Assert.Contains(events, e => e.Type == BuildEventTypes.BuildFinished);
    }

    // ─── Default (text) reporter is preserved — no NDJSON on stdout ────────

    [Fact]
    public void Text_Reporter_Default_Does_Not_Emit_Ndjson()
    {
        var (output, _) = RunWithCapturedStdout(new[] { "Compile" });
        Assert.Contains("==>", output);                            // human target header present
        Assert.DoesNotContain("\"type\":\"build.started\"", output); // no canonical stream
        Assert.DoesNotContain("\"event\":\"build.start\"", output);  // and certainly not the old flat shape
    }

    // ─── ParseInvocation handles the --reporter flag ─────────────────────

    [Fact]
    public void ParseInvocation_Captures_Reporter_Flag()
    {
        var targets = TampBuild.CollectTargets(new TestBuild());
        var (_, _, _, _, _, _, _, _, reporter) = TampBuild.ParseInvocation(
            new[] { "--reporter=json" }, targets);
        Assert.Equal(TampBuild.ReporterKind.Json, reporter);
    }

    [Fact]
    public void ParseInvocation_Defaults_To_Text_Reporter()
    {
        var targets = TampBuild.CollectTargets(new TestBuild());
        var (_, _, _, _, _, _, _, _, reporter) = TampBuild.ParseInvocation(
            new[] { "Compile" }, targets);
        Assert.Equal(TampBuild.ReporterKind.Text, reporter);
    }

    [Fact]
    public void ParseInvocation_Rejects_Unknown_Reporter_Value()
    {
        var targets = TampBuild.CollectTargets(new TestBuild());
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TampBuild.ParseInvocation(new[] { "--reporter=yaml" }, targets));
        Assert.Contains("--reporter", ex.Message);
    }

    // ─── Noop reporter sanity ───────────────────────────────────────────

    [Fact]
    public void Noop_Reporter_Methods_Are_No_Ops()
    {
        var r = NoopBuildReporter.Instance;
        r.OnBuildStart("id", new[] { "A" }, new[] { "A" });
        r.OnTargetStart("A");
        r.OnTargetSucceeded("A", TimeSpan.FromSeconds(1));
        r.OnTargetFailed(new TargetFailureDetail
        {
            TargetName = "A",
            Duration = TimeSpan.FromSeconds(1),
            FailureReason = "x",
        });
        r.OnTargetSkipped("A", "reason");
        r.OnTargetNotRun("A", "reason");
        r.OnBuildEnd("succeeded", null, 0, TimeSpan.FromSeconds(2));
    }
}
