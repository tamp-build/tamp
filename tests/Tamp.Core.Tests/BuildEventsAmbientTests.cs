using System.IO;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests for the ambient <see cref="BuildEvents"/> emitter (#13): a diagnostic
/// emitted from inside a target body lands in the canonical stream, parented to
/// that target's span, and calls are a no-op when no build is active.
/// </summary>
public sealed class BuildEventsAmbientTests
{
    private sealed class CapturingSink : IBuildEventSink
    {
        public readonly List<BuildEvent> Events = new();
        public void Emit(BuildEvent e) => Events.Add(e);
    }

    private sealed class DiagBuild : TampBuild
    {
        public Target Run => _ => _.Executes(() =>
            BuildEvents.Diagnostic("CS1002", "error", "; expected", "Foo.cs", 88));
    }

    [Fact]
    public void Ambient_Diagnostic_Is_Emitted_Parented_To_Current_Target_Span()
    {
        var targets = TampBuild.CollectTargets(new DiagBuild());
        var sink = new CapturingSink();
        new Executor(new TargetGraph(targets), output: TextWriter.Null, eventSink: sink).Run("Run");

        var diag = sink.Events.Single(e => e.Type == BuildEventTypes.DiagnosticEmitted
            && ((DiagnosticEmittedPayload)e.Payload).RuleId == "CS1002");   // not the #63 build-level advisory
        var targetStarted = sink.Events.Single(e => e.Type == BuildEventTypes.TargetStarted && e.TargetId == "Run");

        Assert.Equal("Run", diag.TargetId);
        Assert.Equal(targetStarted.SpanId, diag.ParentSpanId);   // parented to the target span
        Assert.NotEqual(targetStarted.SpanId, diag.SpanId);      // its own span id

        var p = Assert.IsType<DiagnosticEmittedPayload>(diag.Payload);
        Assert.Equal("CS1002", p.RuleId);
        Assert.Equal("error", p.Level);
        Assert.Equal("; expected", p.Message);
        Assert.Equal("Foo.cs", p.Location!.File);
        Assert.Equal(88, p.Location.Line);
    }

    [Fact]
    public void Diagnostic_Is_NoOp_When_No_Build_Active()
    {
        Assert.False(BuildEvents.IsActive);
        BuildEvents.Diagnostic("X", "warning", "should not throw");   // no active scope → silent no-op
    }
}
