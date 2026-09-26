using System.IO;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests for #16 in-target rule filter (`--rule`): parsing/exposure via
/// <see cref="TampBuild.RuleFilter"/>, and its integration into `remedy.reproduce`
/// (echo active filters, else infer a single error-level diagnostic rule).
/// </summary>
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class RuleFilterTests
{
    private sealed class CapturingSink : IBuildEventSink
    {
        public readonly List<BuildEvent> Events = new();
        public void Emit(BuildEvent e) => Events.Add(e);
    }

    private sealed class FailBuild : TampBuild
    {
        public static Action Body = () => { };
        public Target Run => _ => _.Executes(() => Body());
    }

    private static TargetRemedy Remedy(IReadOnlyList<string>? ruleFilters, Action body)
    {
        FailBuild.Body = () => { body(); throw new InvalidOperationException("boom"); };
        var targets = TampBuild.CollectTargets(new FailBuild());
        var sink = new CapturingSink();
        new Executor(new TargetGraph(targets), output: TextWriter.Null, eventSink: sink, ruleFilters: ruleFilters).Run("Run");
        var finished = (TargetFinishedPayload)sink.Events.Last(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == "Run").Payload;
        return finished.Remedy!;
    }

    // ─── remedy.reproduce integration ─────────────────────────────────────

    [Fact]
    public void Reproduce_Echoes_Active_Rule_Filters()
    {
        var r = Remedy(new[] { "CS1002" }, () => { });
        Assert.Equal("tamp Run --rule CS1002", r.Reproduce);
    }

    [Fact]
    public void Reproduce_Echoes_Multiple_Active_Rule_Filters()
    {
        var r = Remedy(new[] { "CS1002", "CS0219" }, () => { });
        Assert.Equal("tamp Run --rule CS1002 --rule CS0219", r.Reproduce);
    }

    [Fact]
    public void Reproduce_Infers_Single_Error_Rule_From_Diagnostics()
    {
        var r = Remedy(null, () => BuildEvents.Diagnostic("CS1002", "error", "; expected", "Foo.cs", 88));
        Assert.Equal("tamp Run --rule CS1002", r.Reproduce);
    }

    [Fact]
    public void Reproduce_Falls_Back_With_Multiple_Distinct_Error_Rules()
    {
        var r = Remedy(null, () =>
        {
            BuildEvents.Diagnostic("CS1002", "error", "a");
            BuildEvents.Diagnostic("CS0219", "error", "b");
        });
        Assert.Equal("tamp Run", r.Reproduce);
    }

    [Fact]
    public void Reproduce_Ignores_Warning_Level_Diagnostics()
    {
        var r = Remedy(null, () => BuildEvents.Diagnostic("CS0168", "warning", "unused"));
        Assert.Equal("tamp Run", r.Reproduce);
    }

    // ─── parsing + exposure ───────────────────────────────────────────────

    [Fact]
    public void ResolveRuleFilters_Reads_Forms_And_Repeats()
    {
        Assert.Equal(new[] { "CS1002" }, TampBuild.ResolveRuleFilters(new[] { "Run", "--rule", "CS1002" }));
        Assert.Equal(new[] { "CS1002" }, TampBuild.ResolveRuleFilters(new[] { "--rule=CS1002" }));
        Assert.Equal(new[] { "A", "B" }, TampBuild.ResolveRuleFilters(new[] { "--rule", "A", "--rule=B" }));
        Assert.Empty(TampBuild.ResolveRuleFilters(new[] { "Run" }));
    }

    private sealed class RecordBuild : TampBuild
    {
        public static IReadOnlyList<string> Seen = Array.Empty<string>();
        public Target Run => _ => _.Executes(() => { Seen = RuleFilter; });
    }

    [Fact]
    public void RuleFilter_Is_Exposed_To_Build_Code()
    {
        RecordBuild.Seen = Array.Empty<string>();
        var prev = Console.Out;
        Console.SetOut(new StringWriter());
        try { TampBuild.Execute<RecordBuild>(new[] { "Run", "--rule", "CS1002" }); }
        finally { Console.SetOut(prev); }
        Assert.Contains("CS1002", RecordBuild.Seen);
    }
}
