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

    internal static string RenderToolsJson(ScaffoldContext ctx)
        => $$"""
        {
          "version": 1,
          "isRoot": true,
          "tools": {
            "dotnet-tamp": {
              "version": "{{ctx.TampCoreVersion}}",
              "commands": ["dotnet-tamp"]
            }
          }
        }

        """;

    /// <summary>
    /// POSIX shim. Restores the local tool manifest on first run so a fresh
    /// clone works without an explicit `dotnet tool restore`, then forwards
    /// args verbatim to <c>dotnet tamp</c>. Marker file under <c>.config/</c>
    /// avoids re-running restore on every invocation.
    /// </summary>
    /// <remarks>
    /// Always LF, whatever line endings this source file had when the CLI was
    /// compiled (a Windows checkout with <c>core.autocrlf</c> would otherwise
    /// bake CRLF into the raw literal and break bash). See tamp#120.
    /// </remarks>
    internal static string RenderTampSh()
        => """
        #!/usr/bin/env bash
        # Tamp build-script entry point. Scaffolded by `tamp init` — safe to edit; safe to delete.
        # Restores the local tool manifest on first run, then forwards to `dotnet tamp <args>`.
        set -euo pipefail

        SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
        STAMP="$SCRIPT_DIR/.config/.tamp-tools-restored"

        if [ -f "$SCRIPT_DIR/.config/dotnet-tools.json" ] && [ ! -f "$STAMP" ]; then
            (cd "$SCRIPT_DIR" && dotnet tool restore) && mkdir -p "$SCRIPT_DIR/.config" && touch "$STAMP"
        fi

        cd "$SCRIPT_DIR" && exec dotnet tamp "$@"
        """.ReplaceLineEndings("\n");

    /// <summary>
    /// Windows shim (cmd.exe). Mirrors <see cref="RenderTampSh"/> behavior so
    /// adopters on Windows have the same one-line entry point.
    /// </summary>
    /// <remarks>
    /// tamp#120: cmd.exe misparses batch files that are LF-only, especially when
    /// they also contain multi-byte characters under an OEM code page such as
    /// 437 (it loses its place and runs a fragment of a later line, e.g.
    /// <c>'m' is not recognized</c>). So this shim is always CRLF, normalized
    /// here because the raw literal carries the line endings of whatever
    /// checkout the CLI was built from (LF on the ubuntu release runner), and
    /// the text must stay pure ASCII. <c>%~dp0</c> always ends in <c>\</c>, so
    /// paths are joined directly onto it with no trim.
    /// </remarks>
    internal static string RenderTampCmd()
        => """
        @echo off
        rem Tamp build-script entry point. Scaffolded by `tamp init` - safe to edit; safe to delete.
        rem Restores the local tool manifest on first run, then forwards to `dotnet tamp <args>`.
        setlocal

        set "SCRIPT_DIR=%~dp0"
        set "STAMP=%SCRIPT_DIR%.config\.tamp-tools-restored"

        if exist "%SCRIPT_DIR%.config\dotnet-tools.json" if not exist "%STAMP%" (
            pushd "%SCRIPT_DIR%" >nul
            dotnet tool restore || exit /b 1
            popd >nul
            if not exist "%SCRIPT_DIR%.config" mkdir "%SCRIPT_DIR%.config"
            type nul > "%STAMP%"
        )

        pushd "%SCRIPT_DIR%" >nul
        dotnet tamp %*
        set "EXIT=%ERRORLEVEL%"
        popd >nul
        exit /b %EXIT%

        """.ReplaceLineEndings("\r\n");
}
