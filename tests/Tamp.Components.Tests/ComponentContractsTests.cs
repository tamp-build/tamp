using System.IO;
using Tamp;
using Xunit;

namespace Tamp.Components.Tests;

/// <summary>
/// ADR 0020 Phase 2: the <c>Tamp.Components</c> target-shape interfaces (IRestore/ICompile/ITest/IPack)
/// wire the standard chain (Restore → Compile → { Test, Pack }) and the <c>IHaz*</c> contracts inject
/// build-provided values into the (tool-abstract) component bodies. Driven through the public
/// <see cref="TampBuild.Execute{T}"/> entry with a static capture (xUnit serializes methods in a class).
/// </summary>
public sealed class ComponentContractsTests
{
    private static readonly List<string> Log = new();

    // A standard build: implements ITest + IPack (⇒ ICompile ⇒ IRestore), satisfies the IHaz*
    // contracts, and supplies the tool-abstract Run* bodies (here: record what ran).
    private sealed class StdBuild : TampBuild, ITest, IPack
    {
        public Solution Solution => null!;                 // not touched by the stub bodies
        public string Configuration => "Release";
        public AbsolutePath ArtifactsDirectory => AbsolutePath.Create(Path.GetTempPath());

        void IRestore.RunRestore() => Log.Add("restore");
        void ICompile.RunCompile() => Log.Add($"compile:{Configuration}");   // reads the injected IHazConfiguration
        void ITest.RunTest() => Log.Add("test");
        void IPack.RunPack() => Log.Add("pack");
    }

    private static (int Exit, IReadOnlyList<string> Ran) Run(params string[] args)
    {
        Log.Clear();
        var prevOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        try { return (TampBuild.Execute<StdBuild>(args), Log.ToArray()); }
        finally { Console.SetOut(prevOut); }
    }

    [Fact]
    public void Test_Target_Wires_Restore_Then_Compile_Then_Test()
    {
        var (exit, ran) = Run("Test");
        Assert.Equal(0, exit);
        Assert.Equal(new[] { "restore", "compile:Release", "test" }, ran);
    }

    [Fact]
    public void Pack_Target_Wires_Restore_Then_Compile_Then_Pack()
    {
        var (exit, ran) = Run("Pack");
        Assert.Equal(0, exit);
        Assert.Equal(new[] { "restore", "compile:Release", "pack" }, ran);
    }

    [Fact]
    public void IHazConfiguration_Value_Reaches_The_Component_Body()
    {
        // RunCompile records $"compile:{Configuration}"; seeing "Release" proves the build's
        // IHazConfiguration value was injected into the component's (tool-abstract) body.
        var (_, ran) = Run("Compile");
        Assert.Contains("compile:Release", ran);
        Assert.DoesNotContain("test", ran);       // Compile alone doesn't drag Test in
    }
}
