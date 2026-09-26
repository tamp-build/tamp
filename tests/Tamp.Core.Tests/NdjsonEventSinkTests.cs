using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests for the agent NDJSON event sink (`#0b`, ADR 0019): per-line NDJSON with
/// LF endings, best-effort file open, <c>--events</c> / <c>TAMP_EVENTS</c>
/// resolution, and the separate-channel guarantee (events to the file, human
/// console untouched on stdout).
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class NdjsonEventSinkTests
{
    private static BuildEvent SampleEvent(string type, long seq) => new()
    {
        Type = type,
        BuildId = "b0000000000000000000000000000000",
        RunId = "r0000000000000000000000000000000",
        TraceId = "b0000000000000000000000000000000",
        SpanId = "0123456789abcdef",
        WorkerId = "human:test",
        Seq = seq,
        Payload = new BuildStartedPayload { RequestedTargets = new[] { "Compile" }, ExecutionClosure = new[] { "Compile" } },
    };

    // ─── Sink line discipline ─────────────────────────────────────────────

    [Fact]
    public void Writes_One_Json_Line_Per_Event_With_Lf_Endings()
    {
        var sw = new StringWriter();
        using (var sink = new NdjsonEventSink(sw))
        {
            sink.Emit(SampleEvent(BuildEventTypes.BuildStarted, 0));
            sink.Emit(SampleEvent(BuildEventTypes.BuildFinished, 1));
        }
        var text = sw.ToString();
        Assert.DoesNotContain("\r", text);                       // LF only
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }
    }

    [Fact]
    public void Emitted_Line_RoundTrips_Through_BuildEventJson()
    {
        var sw = new StringWriter();
        using var sink = new NdjsonEventSink(sw);
        sink.Emit(SampleEvent(BuildEventTypes.BuildStarted, 7));
        var line = sw.ToString().Trim();
        var back = BuildEventJson.Deserialize(line);
        Assert.NotNull(back);
        Assert.Equal(BuildEventTypes.BuildStarted, back!.Type);
        Assert.Equal(7, back.Seq);
    }

    // ─── TryCreate: real file + best-effort open ──────────────────────────

    [Fact]
    public void TryCreate_Writes_A_Parseable_Utf8_NoBom_File()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tamp-events-{Guid.NewGuid():N}.ndjson");
        try
        {
            var sink = NdjsonEventSink.TryCreate(path, TextWriter.Null);
            Assert.NotNull(sink);
            using (sink) { sink!.Emit(SampleEvent(BuildEventTypes.BuildStarted, 0)); }

            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no UTF-8 BOM");
            var content = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("\r", content);
            Assert.NotNull(BuildEventJson.Deserialize(content.Trim()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void TryCreate_On_Bad_Target_Returns_Null_And_Warns_Without_Throwing()
    {
        // A directory path can't be opened as a file → best-effort null + warning.
        var dir = Path.Combine(Path.GetTempPath(), $"tamp-events-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var warn = new StringWriter();
            var sink = NdjsonEventSink.TryCreate(dir, warn);
            Assert.Null(sink);
            Assert.Contains("could not open event stream", warn.ToString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ─── Path resolution (flag wins over env) ─────────────────────────────

    [Fact]
    public void ResolveEventsTarget_Flag_Space_Form_Wins_Over_Env()
    {
        var got = TampBuild.ResolveEventsTarget(
            new[] { "Compile", "--events", "from-flag.ndjson" },
            _ => "from-env.ndjson");
        Assert.Equal("from-flag.ndjson", got);
    }

    [Fact]
    public void ResolveEventsTarget_Flag_Inline_Form()
        => Assert.Equal("x.ndjson",
            TampBuild.ResolveEventsTarget(new[] { "--events=x.ndjson" }, _ => null));

    [Fact]
    public void ResolveEventsTarget_Falls_Back_To_Env()
        => Assert.Equal("env.ndjson",
            TampBuild.ResolveEventsTarget(new[] { "Compile" }, k => k == "TAMP_EVENTS" ? "env.ndjson" : null));

    [Fact]
    public void ResolveEventsTarget_Null_When_Neither_Set()
        => Assert.Null(TampBuild.ResolveEventsTarget(new[] { "Compile" }, _ => null));

    // ─── Separate-channel integration via Execute<T> ──────────────────────

    private sealed class TB : TampBuild
    {
        public Target Restore => _ => _.Executes(() => { });
        public Target Compile => _ => _.DependsOn(nameof(Restore)).Executes(() => { });
    }

    [Fact]
    public void Execute_With_Events_Flag_Writes_Canonical_Ndjson_And_Keeps_Human_Console()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tamp-events-{Guid.NewGuid():N}.ndjson");
        var stdout = new StringWriter();
        var prev = Console.Out;
        Console.SetOut(stdout);
        try
        {
            var exit = TampBuild.Execute<TB>(new[] { "Compile", "--events", path });
            Assert.Equal(0, exit);

            // The events channel got the full canonical run…
            var events = File.ReadAllLines(path)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => BuildEventJson.Deserialize(l)!)
                .ToList();
            var types = events.Select(e => e.Type).ToList();
            Assert.Equal(BuildEventTypes.BuildStarted, types.First());
            Assert.Equal(BuildEventTypes.BuildFinished, types.Last());
            Assert.Contains(BuildEventTypes.TargetFinished, types);
            Assert.All(events, e => Assert.Equal(events[0].BuildId, e.BuildId));

            // …while the human console on stdout is untouched (banner + target headers).
            var human = stdout.ToString();
            Assert.Contains("==>", human);
            Assert.DoesNotContain("\"type\":\"build.started\"", human);  // canonical NDJSON did NOT go to stdout
        }
        finally
        {
            Console.SetOut(prev);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
