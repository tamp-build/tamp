using Tamp;

namespace Tamp.Components;

// The IHaz* injection contracts (ADR 0020, decision 2). A component that needs an injected
// value declares it as an abstract interface property; the concrete build satisfies it from its
// injected member — explicit, type-safe, analyzer-visible. C#'s type system is the wiring; there
// is no service locator. Example:
//
//     class Build : TampBuild, ICompile
//     {
//         [Solution] readonly Solution _sln = null!;
//         public Solution Solution => _sln;               // satisfies IHazSolution
//         public string Configuration => "Release";        // satisfies IHazConfiguration
//     }

/// <summary>A build that exposes its <see cref="Tamp.Solution"/> to components that need it.</summary>
public interface IHazSolution
{
    /// <summary>The solution the component targets operate on (typically an injected <c>[Solution]</c> member).</summary>
    Solution Solution { get; }
}

/// <summary>A build that exposes its build configuration (e.g. <c>Debug</c> / <c>Release</c>) to components.</summary>
public interface IHazConfiguration
{
    /// <summary>The build configuration — conventionally <c>Debug</c> or <c>Release</c>. Components read this; the build provides it (often an injected <c>[Parameter]</c> with a default).</summary>
    string Configuration { get; }
}

/// <summary>A build that exposes its artifacts output directory to components that emit files.</summary>
public interface IHazArtifacts
{
    /// <summary>Directory into which packable/output artifacts are written (e.g. <c>.nupkg</c> from <see cref="IPack"/>).</summary>
    AbsolutePath ArtifactsDirectory { get; }
}
