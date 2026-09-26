using System.IO;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// #21 confirming tests — closing the loop between the secret-access audit (#11) and
/// the capability gate (#20). secret.access.requested is emitted at dispatch when the
/// reveal is permitted, and is NOT emitted when the capability gate blocks it (the
/// reveal was prevented, so it must not be audited as having happened).
/// </summary>
public sealed class SecretAccessGateTests
{
    private sealed class CapturingSink : IBuildEventSink
    {
        public readonly List<BuildEvent> Events = new();
        public void Emit(BuildEvent e) => Events.Add(e);
    }

    private sealed class Cfg : TampBuild
    {
        public Target Run => _ => _.Executes(() => new CommandPlan
        {
            Executable = "dotnet",
            Arguments = new[] { "--version" },
            Secrets = new[] { new Secret("Tok", "v") },   // Secrets ⇒ side-effectful (secret reveal)
        });
    }

    private static List<BuildEvent> Run(CapabilityMode mode, bool allow)
    {
        var targets = TampBuild.CollectTargets(new Cfg());
        var sink = new CapturingSink();
        new Executor(new TargetGraph(targets), output: TextWriter.Null, eventSink: sink,
            capabilityMode: mode, allowSideEffects: allow).Run("Run");
        return sink.Events;
    }

    [Fact]
    public void Permitted_Access_Emits_Secret_Access_Requested_With_Name_Not_Value()
    {
        var events = Run(CapabilityMode.Off, allow: false);
        var access = events.Single(e => e.Type == BuildEventTypes.SecretAccessRequested);
        var p = Assert.IsType<SecretAccessRequestedPayload>(access.Payload);
        Assert.Equal("Tok", p.Name);
        Assert.Equal("secret.reveal", p.Capability);
        foreach (var e in events) Assert.DoesNotContain("\"v\"", BuildEventJson.Serialize(e));  // value never streamed
    }

    [Fact]
    public void Blocked_Access_Emits_No_Secret_Access_Requested()
    {
        var events = Run(CapabilityMode.Agent, allow: false);
        // The reveal was prevented by the capability gate, so it must NOT be audited as happened.
        Assert.DoesNotContain(events, e => e.Type == BuildEventTypes.SecretAccessRequested);
        Assert.Contains(events, e => e.Type == BuildEventTypes.GateEvaluated
            && ((GateEvaluatedPayload)e.Payload).Blocks);
    }

    [Fact]
    public void Elevated_Access_Is_Permitted_And_Audited()
    {
        var events = Run(CapabilityMode.Agent, allow: true);
        Assert.Contains(events, e => e.Type == BuildEventTypes.SecretAccessRequested);
        Assert.Contains(events, e => e.Type == BuildEventTypes.GateEvaluated
            && ((GateEvaluatedPayload)e.Payload).Verdict == "pass");
    }
}
