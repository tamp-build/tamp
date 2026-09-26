using System.IO;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests for the #11 vocabulary expansion: <c>tool.invoked</c> / <c>tool.exited</c>
/// and <c>secret.access.requested</c> emitted by the executor at CommandPlan
/// dispatch — command-span parenting, argv redaction, and the secret name (never
/// the value) reaching the stream. Uses <c>dotnet --version</c> as a trivial,
/// always-present cross-platform child process.
/// </summary>
public sealed class EventVocabularyTests
{
    private sealed class CapturingSink : IBuildEventSink
    {
        public readonly List<BuildEvent> Events = new();
        public void Emit(BuildEvent e) => Events.Add(e);
    }

    private sealed class ToolBuild : TampBuild
    {
        public static CommandPlan Plan = null!;
        public Target Run => _ => _.Executes(() => Plan);
    }

    private static List<BuildEvent> RunWith(CommandPlan plan)
    {
        ToolBuild.Plan = plan;
        var targets = TampBuild.CollectTargets(new ToolBuild());
        var graph = new TargetGraph(targets);
        var sink = new CapturingSink();
        var ex = new Executor(graph, output: TextWriter.Null, eventSink: sink);
        ex.Run("Run");
        return sink.Events;
    }

    [Fact]
    public void ToolInvoked_And_ToolExited_Are_Emitted_With_Command_Span_Parented_To_Target()
    {
        var events = RunWith(new CommandPlan { Executable = "dotnet", Arguments = new[] { "--version" } });

        var invoked = events.Single(e => e.Type == BuildEventTypes.ToolInvoked);
        var exited = events.Single(e => e.Type == BuildEventTypes.ToolExited);
        var targetStarted = events.Single(e => e.Type == BuildEventTypes.TargetStarted && e.TargetId == "Run");

        // Command span is distinct, shared by invoked+exited, and parented to the target span.
        Assert.Equal(invoked.SpanId, exited.SpanId);
        Assert.NotEqual(targetStarted.SpanId, invoked.SpanId);
        Assert.Equal(targetStarted.SpanId, invoked.ParentSpanId);
        Assert.Equal(targetStarted.SpanId, exited.ParentSpanId);

        var ip = Assert.IsType<ToolInvokedPayload>(invoked.Payload);
        Assert.Equal("dotnet", ip.Tool);
        Assert.Equal(new[] { "--version" }, ip.ArgvRedacted);

        var ep = Assert.IsType<ToolExitedPayload>(exited.Payload);
        Assert.Equal("dotnet", ep.Tool);
        Assert.Equal(0, ep.ExitCode);
        Assert.True(ep.DurationMs >= 0);
    }

    [Fact]
    public void SecretAccessRequested_Carries_Name_Not_Value_And_Argv_Is_Redacted()
    {
        var secretValue = "s3cr3t-" + Guid.NewGuid().ToString("N");
        var events = RunWith(new CommandPlan
        {
            Executable = "dotnet",
            Arguments = new[] { "--version", secretValue },   // secret value appears in argv
            Secrets = new[] { new Secret("MyToken", secretValue) },
        });

        // secret.access.requested emitted (name, capability) — never the value.
        var access = events.Single(e => e.Type == BuildEventTypes.SecretAccessRequested);
        var ap = Assert.IsType<SecretAccessRequestedPayload>(access.Payload);
        Assert.Equal("MyToken", ap.Name);
        Assert.Equal("secret.reveal", ap.Capability);

        // argv is redaction-scrubbed: the raw value is gone, the placeholder is present.
        var invoked = (ToolInvokedPayload)events.Single(e => e.Type == BuildEventTypes.ToolInvoked).Payload;
        Assert.DoesNotContain(secretValue, invoked.ArgvRedacted);
        Assert.Contains("<Secret:MyToken>", invoked.ArgvRedacted);

        // The value must never appear anywhere in the serialized stream.
        foreach (var e in events)
            Assert.DoesNotContain(secretValue, BuildEventJson.Serialize(e));
    }

    [Fact]
    public void New_Vocabulary_Types_RoundTrip_Through_Ndjson()
    {
        // The three defined-but-executor-unemitted types (#13/#12/#20 producers) still
        // round-trip so the pinned wire shape is exercised now.
        BuildEvent Wrap(BuildEventPayload p, string type) => new()
        {
            Type = type, BuildId = "b", RunId = "r", TraceId = "b", SpanId = "0123456789abcdef",
            WorkerId = "human:test", Seq = 0, Payload = p,
        };

        var samples = new[]
        {
            Wrap(new DiagnosticEmittedPayload { RuleId = "CS1002", Level = "error", Message = "; expected", Location = new DiagnosticLocation { File = "Foo.cs", Line = 88 } }, BuildEventTypes.DiagnosticEmitted),
            Wrap(new ArtifactProducedPayload { Path = "bin/x.dll", Hash = "sha256:abc", Kind = "assembly", SizeBytes = 123 }, BuildEventTypes.ArtifactProduced),
            Wrap(new GateEvaluatedPayload { Gate = "criticalCves", Verdict = "pass", Blocks = true }, BuildEventTypes.GateEvaluated),
        };

        foreach (var e in samples)
        {
            var back = BuildEventJson.Deserialize(BuildEventJson.Serialize(e));
            Assert.NotNull(back);
            Assert.Equal(e.Type, back!.Type);
            Assert.Equal(e.Payload.GetType(), back.Payload.GetType());
        }
    }
}
