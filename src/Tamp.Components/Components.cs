using Tamp;

namespace Tamp.Components;

// Target-shape interfaces (ADR 0020, decision 5). Each defines a target's NAME, DESCRIPTION and
// DEPENDENCY WIRING, and delegates the tool-specific work to an abstract Run* method — "the tool
// call abstract where it must be". The concrete bodies ship as Tamp.Components.NetCli.V{N}
// (e.g. `void ICompile.RunCompile() => DotNet.Build(...)`); a build with a custom toolchain can
// implement the Run* method itself. Compose the standard chain by implementing the interfaces:
//
//     class Build : TampBuild, ITest, IPack   // Restore -> Compile -> { Test, Pack }
//
// The chain is expressed by interface inheritance (ICompile : IRestore, ITest/IPack : ICompile),
// so implementing ITest brings Compile + Restore with it.

/// <summary>Restore the solution's dependencies. Root of the standard chain.</summary>
public interface IRestore : IHazSolution
{
    /// <summary>The <c>Restore</c> target.</summary>
    Target Restore => _ => _
        .Description("Restore the solution's dependencies")
        .Executes(RunRestore);

    /// <summary>Tool-specific restore body. Supplied by <c>Tamp.Components.NetCli.V{N}</c>, or by the build for a custom toolchain.</summary>
    void RunRestore();
}

/// <summary>Compile the solution. Depends on <see cref="IRestore.Restore"/>.</summary>
public interface ICompile : IRestore, IHazConfiguration
{
    /// <summary>The <c>Compile</c> target — depends on <c>Restore</c>.</summary>
    Target Compile => _ => _
        .Description("Compile the solution")
        .DependsOn(Restore)
        .Executes(RunCompile);

    /// <summary>Tool-specific compile body.</summary>
    void RunCompile();
}

/// <summary>Run the solution's tests. Depends on <see cref="ICompile.Compile"/>.</summary>
public interface ITest : ICompile
{
    /// <summary>The <c>Test</c> target — depends on <c>Compile</c>.</summary>
    Target Test => _ => _
        .Description("Run the solution's tests")
        .DependsOn(Compile)
        .Executes(RunTest);

    /// <summary>Tool-specific test body.</summary>
    void RunTest();
}

/// <summary>Pack NuGet packages. Depends on <see cref="ICompile.Compile"/>.</summary>
public interface IPack : ICompile, IHazArtifacts
{
    /// <summary>The <c>Pack</c> target — depends on <c>Compile</c>.</summary>
    Target Pack => _ => _
        .Description("Pack NuGet packages")
        .DependsOn(Compile)
        .Executes(RunPack);

    /// <summary>Tool-specific pack body.</summary>
    void RunPack();
}
