using System.IO;
using System.Text.Json;
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
        // Use "--version" itself as the secret value so the child process stays clean
        // (exit 0, single stdout line, no concurrent stderr) — deterministic. The event
        // argv must still be scrubbed to the placeholder. (The RedactingTextWriter race
        // under concurrent stdout+stderr is tracked separately.)
        const string secretValue = "--version";
        var events = RunWith(new CommandPlan
        {
            Executable = "dotnet",
            Arguments = new[] { "--version" },                // the sole arg == the secret value
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
            Wrap(new ConformanceEvaluatedPayload
            {
                AdrRef = "0018", RuleId = "0018-r1", Verdict = ConformanceVerdict.Fail, Method = ConformanceMethod.Semantic,
                AdrQuote = "diagnostics are additive-only", CodeEvidence = "renamed gate.evaluated", Blocks = true,
                Location = new DiagnosticLocation { File = "src/Tamp.Core/BuildEventSchema.cs", Line = 40 },
                Provenance = new Provenance { CommitSha = "abc123", RulesSha = "sha256:def", Method = ConformanceMethod.Semantic, ModelId = "claude-opus-4-8", VerifyVerdict = ConformanceVerdict.Fail },
                ControlRefs = new[] { "CM-6", "SA-15" },
            }, BuildEventTypes.ConformanceEvaluated),
        };

        foreach (var e in samples)
        {
            var back = BuildEventJson.Deserialize(BuildEventJson.Serialize(e));
            Assert.NotNull(back);
            Assert.Equal(e.Type, back!.Type);
            Assert.Equal(e.Payload.GetType(), back.Payload.GetType());
        }
    }

    // ─── conformance.evaluated (ADR 0023) — attestation evidence contract ──

    [Fact]
    public void Conformance_Verdict_Vocabulary_Is_Four_Valued_And_Distinct()
    {
        // Unknown/Error are representable and NOT the same as pass — the load-bearing distinction.
        var set = new HashSet<string>
        {
            ConformanceVerdict.Pass, ConformanceVerdict.Fail, ConformanceVerdict.Unknown, ConformanceVerdict.Error,
        };
        Assert.Equal(4, set.Count);
        Assert.Equal("unknown", ConformanceVerdict.Unknown);
        Assert.NotEqual(ConformanceVerdict.Pass, ConformanceVerdict.Unknown);
    }

    [Fact]
    public void Conformance_Event_Preserves_Reason_Provenance_And_Controls_Through_Ndjson()
    {
        var e = new BuildEvent
        {
            Type = BuildEventTypes.ConformanceEvaluated, BuildId = "b", RunId = "r", TraceId = "b",
            SpanId = "0123456789abcdef", WorkerId = "agent:tamp", Seq = 0,
            Payload = new ConformanceEvaluatedPayload
            {
                AdrRef = "0018", RuleId = "0018-r1", Verdict = ConformanceVerdict.Unknown, Method = ConformanceMethod.Deterministic,
                Blocks = true,
                Provenance = new Provenance { CommitSha = "abc123", RulesSha = "sha256:def", ModelId = null },
                ControlRefs = new[] { "CM-6" },
            },
        };

        var json = BuildEventJson.Serialize(e);
        using var doc = JsonDocument.Parse(json);
        var payload = doc.RootElement.GetProperty("payload");

        Assert.Equal("conformance.evaluated", payload.GetProperty("$type").GetString());   // discriminator
        Assert.Equal("unknown", payload.GetProperty("verdict").GetString());               // four-valued, not pass/fail
        Assert.Equal("abc123", payload.GetProperty("provenance").GetProperty("commitSha").GetString());  // camelCase, nested
        Assert.Equal("CM-6", payload.GetProperty("controlRefs")[0].GetString());
        Assert.False(payload.TryGetProperty("adrQuote", out _));                            // null reason dropped
        Assert.False(payload.GetProperty("provenance").TryGetProperty("modelId", out _));   // null nested field dropped

        var back = (ConformanceEvaluatedPayload)BuildEventJson.Deserialize(json)!.Payload;
        Assert.Equal(ConformanceVerdict.Unknown, back.Verdict);
        Assert.Equal("abc123", back.Provenance!.CommitSha);
        Assert.Equal(new[] { "CM-6" }, back.ControlRefs);
    }
}
