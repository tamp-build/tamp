using System.IO;
using System.Linq;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// #63: a run that executes at least one target but declares NO Produces anywhere emits a
/// build-level `diagnostic.emitted` (RuleId `tamp.produces.none`) so an attestation consumer
/// sees the coverage gap; a run with any Produces declaration does not.
/// </summary>
public sealed class ProducesCoverageAdvisoryTests
{
    private sealed class CapturingSink : IBuildEventSink
    {
        public readonly List<BuildEvent> Events = new();
        public void Emit(BuildEvent e) => Events.Add(e);
    }

    private sealed class NoProducesBuild : TampBuild
    {
        public Target Run => _ => _.Executes(() => { });
    }

    private sealed class WithProducesBuild : TampBuild
    {
        public Target Run => _ => _.Produces("artifacts/*.nupkg").Executes(() => { });
    }

    private static CapturingSink RunBuild(TampBuild cfg)
    {
        var targets = TampBuild.CollectTargets(cfg);
        var sink = new CapturingSink();
        new Executor(new TargetGraph(targets), output: TextWriter.Null, eventSink: sink).Run("Run");
        return sink;
    }

    private static bool HasCoverageAdvisory(CapturingSink sink) =>
        sink.Events.Any(e => e.Type == BuildEventTypes.DiagnosticEmitted
            && e.Payload is DiagnosticEmittedPayload d && d.RuleId == "tamp.produces.none");

    [Fact]
    public void Zero_Produces_Across_The_Run_Emits_The_Advisory()
    {
        var sink = RunBuild(new NoProducesBuild());
        Assert.True(HasCoverageAdvisory(sink));
        var d = (DiagnosticEmittedPayload)sink.Events.Single(e =>
            e.Type == BuildEventTypes.DiagnosticEmitted && ((DiagnosticEmittedPayload)e.Payload).RuleId == "tamp.produces.none").Payload;
        Assert.Equal("note", d.Level);              // note = quiet on the human console (not rendered by ReporterProjectionSink)
        Assert.Contains("--list", d.Message);       // points at the audit surface
    }

    [Fact]
    public void Any_Produces_Declaration_Suppresses_The_Advisory()
    {
        var sink = RunBuild(new WithProducesBuild());
        Assert.False(HasCoverageAdvisory(sink));
    }
}
