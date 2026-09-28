using Tamp;
using Tamp.Components;
using Tamp.NetCli.V10;

namespace Tamp.Components.NetCli.V10;

// Concrete .NET SDK bodies for the Tamp.Components target shapes (ADR 0020, Phase 3). Each interface
// provides the tool-abstract *Plan() member from its shape via the `dotnet` CLI (Tamp.NetCli.V10),
// reading the build's injected values through the IHaz* contracts. The executor dispatches the returned
// CommandPlan declaratively (fails the target on a non-zero exit; emits tool.invoked / tool.exited).
//
// The bodies chain by best practice: Compile --no-restore (Restore ran as a dep), Test/Pack --no-build
// (Compile ran as a dep), and Test uses --blame-crash so a test-host crash names the in-flight test.
//
//     class Build : TampBuild, IDotNetTest, IDotNetPack
//     {
//         [Solution] readonly Solution _sln = null!;
//         public Solution Solution => _sln;
//         public Configuration Configuration => Configuration.Release;
//         public AbsolutePath ArtifactsDirectory => TampBuild.RootDirectory / "artifacts";
//     }

/// <summary><c>dotnet restore</c> body for <see cref="IRestore"/>.</summary>
public interface IDotNetRestore : IRestore
{
    /// <inheritdoc/>
    CommandPlan IRestore.RestorePlan() => DotNet.Restore(s => s
        .SetProject(Solution.Path));
}

/// <summary><c>dotnet build</c> body for <see cref="ICompile"/> (chains <c>--no-restore</c>).</summary>
public interface IDotNetCompile : ICompile, IDotNetRestore
{
    /// <inheritdoc/>
    CommandPlan ICompile.CompilePlan() => DotNet.Build(s => s
        .SetProject(Solution.Path)
        .SetConfiguration(Configuration)
        .SetNoRestore(true));   // Restore ran as a dependency
}

/// <summary><c>dotnet test</c> body for <see cref="ITest"/> (chains <c>--no-build</c>, enables <c>--blame-crash</c>).</summary>
public interface IDotNetTest : ITest, IDotNetCompile
{
    /// <inheritdoc/>
    CommandPlan ITest.TestPlan() => DotNet.Test(s => s
        .SetProject(Solution.Path)
        .SetConfiguration(Configuration)
        .SetNoBuild(true)                 // Compile ran as a dependency
        .SetBlameCrash(true));            // name the in-flight test if the host crashes
}

/// <summary><c>dotnet pack</c> body for <see cref="IPack"/> (chains <c>--no-build</c>, outputs to <see cref="IHazArtifacts.ArtifactsDirectory"/>).</summary>
public interface IDotNetPack : IPack, IDotNetCompile
{
    /// <inheritdoc/>
    CommandPlan IPack.PackPlan() => DotNet.Pack(s => s
        .SetProject(Solution.Path)
        .SetConfiguration(Configuration)
        .SetNoBuild(true)                 // Compile ran as a dependency
        .SetOutput(ArtifactsDirectory));
}
