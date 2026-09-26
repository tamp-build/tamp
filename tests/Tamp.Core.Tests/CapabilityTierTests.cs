using System.IO;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests for #20 capability enforcement: side-effectful work is gated in agent mode
/// (default-deny unless elevated), emits gate.evaluated, and never runs when blocked;
/// Off mode and Safe work are unaffected; Secrets imply side-effectful; a target-level
/// .Capability(SideEffectful) is gated at target start.
/// </summary>
public sealed class CapabilityTierTests
{
    private sealed class CapturingSink : IBuildEventSink
    {
        public readonly List<BuildEvent> Events = new();
        public void Emit(BuildEvent e) => Events.Add(e);
    }

    private sealed class Cfg : TampBuild
    {
        public static CommandPlan? Plan;
        public static CapabilityTier TargetCap = CapabilityTier.Safe;

        public Target Run => _ =>
        {
            var d = _;
            if (TargetCap != CapabilityTier.Safe) d = d.Capability(TargetCap);
            return Plan is null ? d.Executes(() => { }) : d.Executes(() => Plan);
        };

        public static void Reset() { Plan = null; TargetCap = CapabilityTier.Safe; }
    }

    private static (List<BuildEvent> Events, int Exit) Run(CapabilityMode mode, bool allow)
    {
        var targets = TampBuild.CollectTargets(new Cfg());
        var sink = new CapturingSink();
        var exit = new Executor(new TargetGraph(targets), output: TextWriter.Null, eventSink: sink,
            capabilityMode: mode, allowSideEffects: allow).Run("Run").ExitCode;
        return (sink.Events, exit);
    }

    private static CommandPlan SideEffectCmd => new()
    {
        Executable = "dotnet", Arguments = new[] { "--version" }, RequiredCapability = CapabilityTier.SideEffectful,
    };

    [Fact]
    public void Off_Mode_Runs_SideEffect_Without_Gate()
    {
        Cfg.Reset(); Cfg.Plan = SideEffectCmd;
        var (events, exit) = Run(CapabilityMode.Off, allow: false);
        Assert.Equal(0, exit);
        Assert.DoesNotContain(events, e => e.Type == BuildEventTypes.GateEvaluated);
        Assert.Contains(events, e => e.Type == BuildEventTypes.ToolExited);
    }

    [Fact]
    public void Agent_Mode_Blocks_SideEffect_And_Command_Never_Runs()
    {
        Cfg.Reset(); Cfg.Plan = SideEffectCmd;
        var (events, exit) = Run(CapabilityMode.Agent, allow: false);
        Assert.NotEqual(0, exit);

        var gate = events.Single(e => e.Type == BuildEventTypes.GateEvaluated);
        var gp = Assert.IsType<GateEvaluatedPayload>(gate.Payload);
        Assert.Equal("capability", gp.Gate);
        Assert.Equal("fail", gp.Verdict);
        Assert.True(gp.Blocks);

        Assert.DoesNotContain(events, e => e.Type == BuildEventTypes.ToolInvoked);   // command never dispatched
        var finished = (TargetFinishedPayload)events.Single(e => e.Type == BuildEventTypes.TargetFinished).Payload;
        Assert.Equal(BuildEventStatus.Failure, finished.Status);
        Assert.Contains("side-effect", finished.Reason!);
    }

    [Fact]
    public void Agent_Mode_Elevated_Runs_SideEffect_With_Passing_Gate()
    {
        Cfg.Reset(); Cfg.Plan = SideEffectCmd;
        var (events, exit) = Run(CapabilityMode.Agent, allow: true);
        Assert.Equal(0, exit);
        var gate = (GateEvaluatedPayload)events.Single(e => e.Type == BuildEventTypes.GateEvaluated).Payload;
        Assert.Equal("pass", gate.Verdict);
        Assert.False(gate.Blocks);
        Assert.Contains(events, e => e.Type == BuildEventTypes.ToolExited);
    }

    [Fact]
    public void Secrets_Imply_SideEffect_And_Are_Gated_In_Agent_Mode()
    {
        Cfg.Reset();
        Cfg.Plan = new CommandPlan
        {
            Executable = "dotnet", Arguments = new[] { "--version" },
            Secrets = new[] { new Secret("Tok", "v") },   // default Safe tier, but Secrets ⇒ side-effectful
        };
        var (events, exit) = Run(CapabilityMode.Agent, allow: false);
        Assert.NotEqual(0, exit);
        Assert.Contains(events, e => e.Type == BuildEventTypes.GateEvaluated
            && ((GateEvaluatedPayload)e.Payload).Blocks);
    }

    [Fact]
    public void Target_Level_Capability_Is_Gated_At_Target_Start()
    {
        Cfg.Reset(); Cfg.TargetCap = CapabilityTier.SideEffectful;   // library-mode side effect, no command
        var (events, exit) = Run(CapabilityMode.Agent, allow: false);
        Assert.NotEqual(0, exit);
        Assert.Contains(events, e => e.Type == BuildEventTypes.GateEvaluated
            && ((GateEvaluatedPayload)e.Payload).Blocks);
        Assert.DoesNotContain(events, e => e.Type == BuildEventTypes.TargetStarted);   // blocked before it started
    }

    [Fact]
    public void Safe_Work_Is_Never_Gated_In_Agent_Mode()
    {
        Cfg.Reset();
        Cfg.Plan = new CommandPlan { Executable = "dotnet", Arguments = new[] { "--version" } };   // Safe
        var (events, exit) = Run(CapabilityMode.Agent, allow: false);
        Assert.Equal(0, exit);
        Assert.DoesNotContain(events, e => e.Type == BuildEventTypes.GateEvaluated);
    }

    [Fact]
    public void ResolveCapabilityMode_Reads_Flags_And_Env()
    {
        Assert.Equal((CapabilityMode.Agent, true),
            TampBuild.ResolveCapabilityMode(new[] { "Run", "--enforce=agent", "--allow-side-effects" }, _ => null));
        Assert.Equal((CapabilityMode.Agent, false),
            TampBuild.ResolveCapabilityMode(new[] { "Run", "--enforce", "agent" }, _ => null));
        Assert.Equal((CapabilityMode.Off, false),
            TampBuild.ResolveCapabilityMode(new[] { "Run" }, _ => null));
        Assert.Equal((CapabilityMode.Agent, true),
            TampBuild.ResolveCapabilityMode(new[] { "Run" }, k => k switch { "TAMP_CAPABILITY_MODE" => "agent", "TAMP_ALLOW_SIDE_EFFECTS" => "1", _ => null }));
    }
}
