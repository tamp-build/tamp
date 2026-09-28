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

/// <summary>A build that exposes its build <see cref="Tamp.Configuration"/> (Debug/Release) to components.</summary>
public interface IHazConfiguration
{
    /// <summary>The build configuration. Components read this; the build provides it (often an injected <c>[Parameter]</c> with a default). Builds needing a configuration outside the <see cref="Tamp.Configuration"/> enum override the affected target.</summary>
    Configuration Configuration { get; }
}

/// <summary>A build that exposes its artifacts output directory to components that emit files.</summary>
public interface IHazArtifacts
{
    /// <summary>Directory into which packable/output artifacts are written (e.g. <c>.nupkg</c> from <see cref="IPack"/>).</summary>
    AbsolutePath ArtifactsDirectory { get; }
}
