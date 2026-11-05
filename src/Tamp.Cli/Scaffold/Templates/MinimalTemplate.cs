using System.Collections.Generic;
using Tamp.Scaffold;

namespace Tamp.Cli.Scaffold.Templates;

/// <summary>
/// The v0.1.0 minimal scaffold. Writes <c>build/Build.cs</c>,
/// <c>build/Build.csproj</c>, and <c>.config/dotnet-tools.json</c> (if absent).
/// Embedded into the CLI binary so the on-ramp works offline.
/// </summary>
public sealed class MinimalTemplate : IScaffoldTemplate
{
    public string Name => "minimal";
    public string Description => "Minimal .NET solution scaffold: Build.cs with Clean / Restore / Compile / Test targets.";
    public string MinimumTampCoreVersion => "1.3.0";

    public IEnumerable<FileSpec> Render(ScaffoldContext ctx)
    {
        yield return new FileSpec(ctx.RepoRoot / "build" / "Build.cs", RenderBuildCs(ctx), WriteMode.Create);
        yield return new FileSpec(ctx.RepoRoot / "build" / "Build.csproj", RenderBuildCsproj(ctx), WriteMode.Create);
        yield return new FileSpec(ctx.RepoRoot / ".config" / "dotnet-tools.json", RenderToolsJson(ctx), WriteMode.SkipIfExists);
        yield return new FileSpec(ctx.RepoRoot / "tamp.sh", RenderTampSh(), WriteMode.SkipIfExists) { Executable = true };
        yield return new FileSpec(ctx.RepoRoot / "tamp.cmd", RenderTampCmd(), WriteMode.SkipIfExists);
    }

    internal static string RenderBuildCs(ScaffoldContext ctx)
    {
        // If the probe found a single solution at the repo root, we leave [Solution]
        // un-pathed — Tamp.Core's auto-discovery resolves it. If the probe found
        // zero/multi-solution layouts, same thing: the generated template doesn't
        // try to be clever; auto-discovery either works or the user adds the path.
        return ctx.SettingsStyle == SettingsStyle.Init
            ? RenderBuildCsInitStyle()
            : RenderBuildCsFluentStyle();
    }

    private static string RenderBuildCsFluentStyle()
        => """
        using Tamp;
        using Tamp.NetCli.V10;

        class Build : TampBuild
        {
            public static int Main(string[] args) => Execute<Build>(args);

            [Parameter("Build configuration")]
            Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

            [Solution] readonly Solution Solution = null!;

            AbsolutePath Artifacts => RootDirectory / "artifacts";

            Target Clean => _ => _.Executes(() => CleanArtifacts());

            Target Restore => _ => _
                .Internal()
                .Executes(() => DotNet.Restore(s => s.SetProject(Solution.Path)));

            Target Compile => _ => _
                .DependsOn(Restore)
                .Executes(() => DotNet.Build(s => s
                    .SetProject(Solution.Path)
                    .SetConfiguration(Configuration)
                    .SetNoRestore(true)));

            Target Test => _ => _
                .DependsOn(Compile)
                .Executes(() => DotNet.Test(s => s
                    .SetProject(Solution.Path)
                    .SetConfiguration(Configuration)
                    .SetNoBuild(true)));

            Target Default => _ => _
                .Default()
                .DependsOn(Compile);
        }

        """;

    private static string RenderBuildCsInitStyle()
        => """
        using Tamp;
        using Tamp.NetCli.V10;

        class Build : TampBuild
        {
            public static int Main(string[] args) => Execute<Build>(args);

            [Parameter("Build configuration")]
            Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

            [Solution] readonly Solution Solution = null!;

            AbsolutePath Artifacts => RootDirectory / "artifacts";

            Target Clean => _ => _.Executes(() => CleanArtifacts());

            Target Restore => _ => _
                .Internal()
                .Executes(() => DotNet.Restore(new DotNetRestoreSettings
                {
                    Project = Solution.Path,
                }));

            Target Compile => _ => _
                .DependsOn(Restore)
                .Executes(() => DotNet.Build(new DotNetBuildSettings
                {
                    Project = Solution.Path,
                    Configuration = Configuration,
                    NoRestore = true,
                }));

            Target Test => _ => _
                .DependsOn(Compile)
                .Executes(() => DotNet.Test(new DotNetTestSettings
                {
                    Project = Solution.Path,
                    Configuration = Configuration,
                    NoBuild = true,
                }));

            Target Default => _ => _
                .Default()
                .DependsOn(Compile);
        }

        """;

    internal static string RenderBuildCsproj(ScaffoldContext ctx)
        => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <IsPackable>false</IsPackable>
            <RootNamespace>Build</RootNamespace>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Tamp.Core" Version="{{ctx.TampCoreVersion}}" />
            <PackageReference Include="Tamp.NetCli.V10" Version="{{ctx.TampCoreVersion}}" />
          </ItemGroup>
        </Project>

        """;

    internal static string RenderToolsJson(ScaffoldContext ctx) => BootstrapFiles.ToolsJson(ctx.TampCoreVersion);

    /// <inheritdoc cref="BootstrapFiles.TampSh"/>
    internal static string RenderTampSh() => BootstrapFiles.TampSh();

    /// <inheritdoc cref="BootstrapFiles.TampCmd"/>
    internal static string RenderTampCmd() => BootstrapFiles.TampCmd();
}
