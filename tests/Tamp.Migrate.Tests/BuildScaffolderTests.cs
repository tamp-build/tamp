using Tamp.Cli.Scaffold.Templates;
using Tamp.Migrate.Mapping;
using Tamp.Migrate.Report;
using Tamp.Migrate.Rewriting;
using Xunit;

namespace Tamp.Migrate.Tests;

/// <summary>
/// ADR 0021 Phase 2 step 4: the build project is rewritten in place and the <c>tamp init</c> bootstrap is
/// written beside NUKE's. Exact output, idempotent, no processes (an in-memory repository).
/// </summary>
public sealed class BuildScaffolderTests
{
    private static readonly MigrationMapping Mapping = MigrationMapping.LoadEmbedded();

    private const string NukeCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net8.0</TargetFramework>
            <RootNamespace></RootNamespace>
            <NoWarn>CS0649;CS0169;CA1050;CA1822;CA2211;IDE1006</NoWarn>
            <NukeRootDirectory>..</NukeRootDirectory>
            <NukeScriptDirectory>..</NukeScriptDirectory>
            <NukeTelemetryVersion>1</NukeTelemetryVersion>
            <IsPackable>false</IsPackable>
          </PropertyGroup>

          <ItemGroup>
            <PackageReference Include="Nuke.Common" Version="8.1.2" />
            <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
          </ItemGroup>

          <ItemGroup>
            <PackageDownload Include="GitVersion.Tool" Version="[5.12.0]" />
          </ItemGroup>

        </Project>

        """;

    private const string TampCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net8.0</TargetFramework>
            <RootNamespace></RootNamespace>
            <NoWarn>CS0649;CS0169;CA1050;CA1822;CA2211;IDE1006</NoWarn>
            <IsPackable>false</IsPackable>
          </PropertyGroup>

          <ItemGroup>
            <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
            <PackageReference Include="Tamp.Core" Version="1.19.0" />
            <PackageReference Include="Tamp.NetCli.V10" Version="1.19.0" />
          </ItemGroup>

        </Project>

        """;

    private const string DotNetBuild = "using Nuke.Common;\nusing static Nuke.Common.Tools.DotNet.DotNetTasks;\n\nclass Build : NukeBuild\n{\n    Target T => _ => _.Executes(() => DotNetBuild());\n}\n";

    [Fact]
    public void The_Nuke_Build_Project_Is_Rewritten_In_Place()
    {
        var (result, _) = Run(new() { ["build/_build.csproj"] = NukeCsproj });

        var project = Assert.Single(result.Generated, g => g.Path == "build/_build.csproj");
        Assert.False(project.Created);
        Assert.Equal(TampCsproj, project.Text);

        var site = Assert.Single(result.Report.Sites, s => s.RuleId == "boilerplate.build-project");
        Assert.Equal(Outcome.Assisted, site.Outcome);
        Assert.Contains("PackageDownload GitVersion.Tool 5.12.0 -> a [NuGetPackage(\"GitVersion.Tool\", Version = \"5.12.0\")] Tool field", site.Marker);
    }

    [Fact]
    public void A_Project_With_No_Package_References_Gets_A_New_Item_Group()
    {
        var bare = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net9.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n";
        var (result, _) = Run(new() { ["build/_build.csproj"] = bare }, cs: "class Build : NukeBuild { }\n");

        Assert.Equal(
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net9.0</TargetFramework>\n  </PropertyGroup>\n"
            + "  <ItemGroup>\n    <PackageReference Include=\"Tamp.Core\" Version=\"1.19.0\" />\n  </ItemGroup>\n</Project>\n",
            Assert.Single(result.Generated, g => g.Path == "build/_build.csproj").Text);
    }

    [Fact]
    public void An_Unsupported_Target_Framework_Moves_To_Net10_And_Says_So()
    {
        var (result, _) = Run(new() { ["build/_build.csproj"] = NukeCsproj.Replace("net8.0", "net6.0", StringComparison.Ordinal) });

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", Assert.Single(result.Generated, g => g.Path == "build/_build.csproj").Text, StringComparison.Ordinal);
        Assert.Contains("TargetFramework net6.0 -> net10.0", Assert.Single(result.Report.Sites, s => s.RuleId == "boilerplate.build-project").Marker);
    }

    [Fact]
    public void Components_Add_The_Tamp_Components_Package()
    {
        var (result, _) = Run(new() { ["build/_build.csproj"] = NukeCsproj },
            cs: "using Nuke.Common;\nusing Nuke.Components;\n\nclass Build : NukeBuild, ICompile\n{\n}\n");
        Assert.Contains("<PackageReference Include=\"Tamp.Components\" Version=\"1.19.0\" />", result.Generated.Single(g => g.Path == "build/_build.csproj").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Build_Directory_Option_Retargets_The_Project_Rule()
    {
        var files = new Dictionary<string, string> { ["tools/build/_build.csproj"] = NukeCsproj };
        var result = new NukeMigrator(Mapping, new MigrationOptions(TampVersion: "1.19.0", BuildDirectory: "tools/build"))
            .Migrate([new SourceFile("tools/build/Build.cs", DotNetBuild)], files.Keys, p => files.GetValueOrDefault(p));
        Assert.Contains(result.Generated, g => g.Path == "tools/build/_build.csproj");
    }

    [Fact]
    public void The_Bootstrap_Is_Tamp_Inits()
    {
        var (result, _) = Run(new() { ["build/_build.csproj"] = NukeCsproj, ["build.sh"] = "#!/bin/sh\n" });

        var sh = Assert.Single(result.Generated, g => g.Path == "tamp.sh");
        Assert.True(sh.Executable);
        Assert.Equal(BootstrapFiles.TampSh(), sh.Text);
        Assert.Equal(BootstrapFiles.TampCmd(), Assert.Single(result.Generated, g => g.Path == "tamp.cmd").Text);
        Assert.Equal(BootstrapFiles.ToolsJson("1.19.0"), Assert.Single(result.Generated, g => g.Path == ".config/dotnet-tools.json").Text);
    }

    [Fact]
    public void An_Existing_Tool_Manifest_Gets_Dotnet_Tamp_Beside_Nuke()
    {
        var manifest = "{\n  \"version\": 1,\n  \"isRoot\": true,\n  \"tools\": {\n    \"nuke.globaltool\": { \"version\": \"8.1.2\", \"commands\": [\"nuke\"] }\n  }\n}\n";
        var (result, _) = Run(new() { ["build/_build.csproj"] = NukeCsproj, ["build.sh"] = "", [".config/dotnet-tools.json"] = manifest });

        var json = Assert.Single(result.Generated, g => g.Path == ".config/dotnet-tools.json");
        Assert.False(json.Created);
        Assert.Contains("\"nuke.globaltool\"", json.Text, StringComparison.Ordinal);
        Assert.Contains("\"dotnet-tamp\": {\n      \"version\": \"1.19.0\"", json.Text, StringComparison.Ordinal);
        Assert.Contains(result.Report.Sites, s => s.File == ".config/dotnet-tools.json" && s.Marker!.Contains("Remove nuke.globaltool", StringComparison.Ordinal));
    }

    [Fact]
    public void Existing_Tamp_Scripts_Are_Left_Alone()
    {
        var (result, _) = Run(new() { ["build/_build.csproj"] = NukeCsproj, ["build.sh"] = "", ["tamp.sh"] = "mine", ["tamp.cmd"] = "mine" });
        Assert.DoesNotContain(result.Generated, g => g.Path is "tamp.sh" or "tamp.cmd");
    }

    [Fact]
    public void A_Second_Run_Generates_Nothing()
    {
        var (first, repo) = Run(new() { ["build/_build.csproj"] = NukeCsproj, ["build.sh"] = "" });
        foreach (var g in first.Generated) repo[g.Path] = g.Text;
        var cs = first.Files.Single();

        var second = new NukeMigrator(Mapping, new MigrationOptions(TampVersion: "1.19.0"))
            .Migrate([cs], repo.Keys, p => repo.GetValueOrDefault(p));

        Assert.Empty(second.Generated);
        Assert.Empty(second.Report.ChangedFiles);
    }

    [Fact]
    public void Crlf_Project_Files_Stay_Crlf()
    {
        var (result, _) = Run(new() { ["build/_build.csproj"] = NukeCsproj.ReplaceLineEndings("\r\n") });
        var text = Assert.Single(result.Generated, g => g.Path == "build/_build.csproj").Text;
        Assert.Equal(TampCsproj.ReplaceLineEndings("\r\n"), text);
    }

    [Theory]
    [InlineData("build/*.csproj", "build/_build.csproj", true)]
    [InlineData("build/*.csproj", "build/sub/_build.csproj", false)]
    [InlineData(".nuke/**", ".nuke/temp/build.log", true)]
    [InlineData("build.sh", "build.sh", true)]
    [InlineData("build.sh", "xbuild.sh", false)]
    [InlineData("build/*.csproj", @"build\_build.csproj", true)]
    public void Globs_Match_Per_Segment(string glob, string path, bool matches)
        => Assert.Equal(matches, Glob.Matches(glob, path));

    [Fact]
    public void Without_Git_A_Clean_Tree_Cannot_Be_Confirmed()
    {
        Assert.Contains("git wasn't found", CleanTree.Problem("/repo", git: null), StringComparison.Ordinal);
    }

    private static (MigrationResult Result, Dictionary<string, string> Repo) Run(Dictionary<string, string> repo, string cs = DotNetBuild)
    {
        var result = new NukeMigrator(Mapping, new MigrationOptions(TampVersion: "1.19.0"))
            .Migrate([new SourceFile("build/Build.cs", cs)], repo.Keys, p => repo.GetValueOrDefault(p));
        return (result, repo);
    }
}
