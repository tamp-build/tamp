using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// #66: an imperative <see cref="ProcessRunner.Execute"/> dispatch (from inside an
/// <c>Executes(Action)</c> body, or a failure handler) is now visible on the canonical
/// event stream — <c>tool.invoked</c> + <c>tool.exited</c>, parented to the target span,
/// secret-scrubbed — where before it emitted only an ADR-0018 span. The declarative path
/// still emits exactly once (no double emission). A non-zero exit that the body drops
/// raises the <c>tamp.tool.nonzero_ignored</c> advisory; <see cref="ProcessRunner.Run"/>
/// throws instead so the target fails normally.
/// </summary>
public sealed class ImperativeToolEventsTests
{
    private sealed class CapturingSink : IBuildEventSink
    {
        public readonly List<BuildEvent> Events = new();
        public void Emit(BuildEvent e) => Events.Add(e);
    }

    // ── Cross-platform plans (same idiom as AgentEconomicsTests) ──────────────
    private static CommandPlan OkPlan(Secret? secret = null)
    {
        var plan = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new CommandPlan { Executable = "cmd.exe", Arguments = new[] { "/c", "echo hi" } }
            : new CommandPlan { Executable = "/bin/sh", Arguments = new[] { "-c", "echo hi" } };
        return secret is null ? plan : plan with { Secrets = new[] { secret } };
    }

    private static CommandPlan FailPlan() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new CommandPlan { Executable = "cmd.exe", Arguments = new[] { "/c", "exit 3" } }
            : new CommandPlan { Executable = "/bin/sh", Arguments = new[] { "-c", "exit 3" } };

    // A plan whose argv contains a secret value, so we can assert argv redaction.
    private static CommandPlan EchoSecretPlan(string token) =>
        (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new CommandPlan { Executable = "cmd.exe", Arguments = new[] { "/c", $"echo {token}" } }
            : new CommandPlan { Executable = "/bin/sh", Arguments = new[] { "-c", $"echo {token}" } })
        with { Secrets = new[] { new Secret("Tok", token) } };

    private sealed class ImperativeExecuteBuild : TampBuild
    {
        public static CommandPlan Plan = null!;
        public Target Run => _ => _.Executes(() => { ProcessRunner.Execute(Plan, TextWriter.Null, TextWriter.Null); });
    }

    private sealed class ImperativeRunBuild : TampBuild
    {
        public static CommandPlan Plan = null!;
        public Target Run => _ => _.Executes(() => { ProcessRunner.Run(Plan, TextWriter.Null, TextWriter.Null); });
    }

    private sealed class DeclarativeBuild : TampBuild
    {
        public static CommandPlan Plan = null!;
        public Target Run => _ => _.Executes(() => Plan);
    }

    private static (List<BuildEvent> Events, ExecutionResult Result) Exec(TampBuild cfg)
    {
        var targets = TampBuild.CollectTargets(cfg);
        var sink = new CapturingSink();
        var result = new Executor(new TargetGraph(targets), output: TextWriter.Null, eventSink: sink).Run("Run");
        return (sink.Events, result);
    }

    private static List<BuildEvent> Of(List<BuildEvent> e, string type) => e.Where(x => x.Type == type).ToList();

    // ─── Imperative dispatch is now on the stream ─────────────────────────────

    [Fact]
    public void Imperative_Execute_Emits_ToolInvoked_And_ToolExited_On_The_Target_Span()
    {
        ImperativeExecuteBuild.Plan = OkPlan();
        var (events, result) = Exec(new ImperativeExecuteBuild());
        Assert.Equal(0, result.ExitCode);

        var invoked = Assert.Single(Of(events, BuildEventTypes.ToolInvoked));
        var exited = Assert.Single(Of(events, BuildEventTypes.ToolExited));
        var targetStarted = events.Single(e => e.Type == BuildEventTypes.TargetStarted && e.TargetId == "Run");

        // Parented to the target span, and invoked/exited share one command span.
        Assert.Equal("Run", invoked.TargetId);
        Assert.Equal(targetStarted.SpanId, invoked.ParentSpanId);
        Assert.Equal(invoked.SpanId, exited.SpanId);
        Assert.Equal(0, ((ToolExitedPayload)exited.Payload).ExitCode);
    }

    [Fact]
    public void Declarative_Dispatch_Emits_Tool_Events_Exactly_Once()
    {
        DeclarativeBuild.Plan = OkPlan();
        var (events, _) = Exec(new DeclarativeBuild());
        // The executor's own loop emits these; ambient emission is suppressed for that call,
        // so there is no double emission.
        Assert.Single(Of(events, BuildEventTypes.ToolInvoked));
        Assert.Single(Of(events, BuildEventTypes.ToolExited));
    }

    // ─── The dropped-exit-code advisory ───────────────────────────────────────

    [Fact]
    public void Imperative_Nonzero_Exit_Left_Unchecked_Emits_The_Advisory_And_Target_Succeeds()
    {
        ImperativeExecuteBuild.Plan = FailPlan();
        var (events, result) = Exec(new ImperativeExecuteBuild());

        // The body dropped the non-zero code, so the target is Done and the build is green...
        Assert.Equal(0, result.ExitCode);
        var finished = (TargetFinishedPayload)events.Single(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == "Run").Payload;
        Assert.Equal(BuildEventStatus.Success, finished.Status);

        // ...but the non-zero tool exit is on the stream, and so is the advisory.
        Assert.Equal(3, ((ToolExitedPayload)Of(events, BuildEventTypes.ToolExited).Single().Payload).ExitCode);
        var advisory = events.Single(e => e.Type == BuildEventTypes.DiagnosticEmitted
            && ((DiagnosticEmittedPayload)e.Payload).RuleId == "tamp.tool.nonzero_ignored");
        var d = (DiagnosticEmittedPayload)advisory.Payload;
        Assert.Equal("note", d.Level);
        Assert.Contains("ProcessRunner.Run", d.Message);
    }

    [Fact]
    public void ProcessRunner_Run_Throws_On_Nonzero_So_The_Target_Fails_And_No_Advisory()
    {
        ImperativeRunBuild.Plan = FailPlan();
        var (events, result) = Exec(new ImperativeRunBuild());

        // Run() threw -> target failed -> build non-zero (the exit-code-safe path).
        Assert.NotEqual(0, result.ExitCode);
        var finished = (TargetFinishedPayload)events.Single(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == "Run").Payload;
        Assert.Equal(BuildEventStatus.Failure, finished.Status);

        // The target failed rather than reporting Done, so the "dropped exit code" advisory must NOT fire.
        Assert.DoesNotContain(events, e => e.Type == BuildEventTypes.DiagnosticEmitted
            && ((DiagnosticEmittedPayload)e.Payload).RuleId == "tamp.tool.nonzero_ignored");
    }

    // ─── Secrets are scrubbed on the imperative path too ──────────────────────

    [Fact]
    public void Imperative_Execute_Redacts_Secret_Argv_And_Emits_Secret_Access()
    {
        const string token = "super-secret-token-xyz";
        ImperativeExecuteBuild.Plan = EchoSecretPlan(token);
        var (events, _) = Exec(new ImperativeExecuteBuild());

        var invoked = (ToolInvokedPayload)Of(events, BuildEventTypes.ToolInvoked).Single().Payload;
        var argv = string.Join(" ", invoked.ArgvRedacted);
        Assert.DoesNotContain(token, argv);
        Assert.Contains("<Secret:Tok>", argv);

        var access = (SecretAccessRequestedPayload)events.Single(e => e.Type == BuildEventTypes.SecretAccessRequested).Payload;
        Assert.Equal("Tok", access.Name);
    }

    // ─── Standalone (no build scope) is unaffected ────────────────────────────

    [Fact]
    public void Standalone_Execute_Outside_A_Build_Returns_The_Code_And_Is_Inactive()
    {
        Assert.False(BuildEvents.IsActive);
        var exit = ProcessRunner.Execute(FailPlan(), TextWriter.Null, TextWriter.Null);
        Assert.Equal(3, exit);          // returns the code, no throw, no ambient emission
        Assert.False(BuildEvents.IsActive);
    }
}
