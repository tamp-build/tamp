using System.IO;
using System.Linq;
using System.Text.Json;
using Tamp;
using Xunit;

namespace Tamp.Components.Tests;

/// <summary>
/// ADR 0020 Phase 2: the <c>Tamp.Components</c> target-shape interfaces (IRestore/ICompile/ITest/IPack)
/// wire the standard chain (Restore → Compile → { Test, Pack }). Verified through the public
/// <c>--plan --format json</c> surface, which resolves the execution order without running anything —
/// so the tool-abstract <c>*Plan()</c> bodies never need a real toolchain here. (IHaz* injection into
/// the concrete plans is covered by the Tamp.Components.NetCli.V10 tests.)
/// </summary>
public sealed class ComponentContractsTests
{
    // A standard build: ITest + IPack (⇒ ICompile ⇒ IRestore). The *Plan() bodies are never invoked
    // by `--plan` (it resolves order and runs nothing), so trivial stubs suffice.
    private sealed class StdBuild : TampBuild, ITest, IPack
    {
        public Solution Solution => null!;
        public Configuration Configuration => Configuration.Release;
        public AbsolutePath ArtifactsDirectory => AbsolutePath.Create(Path.GetTempPath());

        private static CommandPlan Noop() => new() { Executable = "noop", Arguments = System.Array.Empty<string>() };
        CommandPlan IRestore.RestorePlan() => Noop();
        CommandPlan ICompile.CompilePlan() => Noop();
        CommandPlan ITest.TestPlan() => Noop();
        CommandPlan IPack.PackPlan() => Noop();
    }

    /// <summary>Resolve the execution order for <paramref name="target"/> via `--plan --format json`.</summary>
    private static IReadOnlyList<string> PlanOrder(string target)
    {
        var so = new StringWriter();
        var prev = Console.Out;
        Console.SetOut(so);
        try { TampBuild.Execute<StdBuild>(new[] { target, "--plan", "--format", "json" }); }
        finally { Console.SetOut(prev); }

        using var doc = JsonDocument.Parse(so.ToString());
        return doc.RootElement.GetProperty("order")
            .EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToList();
    }

    [Fact]
    public void Test_Target_Wires_Restore_Then_Compile_Then_Test()
        => Assert.Equal(new[] { "Restore", "Compile", "Test" }, PlanOrder("Test"));

    [Fact]
    public void Pack_Target_Wires_Restore_Then_Compile_Then_Pack()
        => Assert.Equal(new[] { "Restore", "Compile", "Pack" }, PlanOrder("Pack"));

    [Fact]
    public void Compile_Alone_Does_Not_Drag_In_Test_Or_Pack()
    {
        var order = PlanOrder("Compile");
        Assert.Equal(new[] { "Restore", "Compile" }, order);
        Assert.DoesNotContain("Test", order);
        Assert.DoesNotContain("Pack", order);
    }
}
