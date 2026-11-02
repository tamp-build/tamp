using System;
using System.IO;
using Xunit;

namespace Tamp.Cli.Tests;

/// <summary>
/// #119: NU1605 (package downgrade) on a Tamp package becomes one actionable message.
/// Fixtures are verbatim NuGet output from the issue's scenario (Tamp.Heft.V1 0.1.0 on a
/// 1.17.3 scaffold), captured with SDK 10.0.1xx.
/// </summary>
public sealed class RestoreDowngradeDiagnosticTests
{
    // Message text as stored in obj/project.assets.json → logs[].message.
    private const string AssetsMessage =
        "Warning As Error: Detected package downgrade: Tamp.Core from 1.17.5 to 1.17.3. Reference the package directly from the project to select a different version. \n" +
        " Build -> Tamp.Heft.V1 0.1.0 -> Tamp.Core (>= 1.17.5) \n" +
        " Build -> Tamp.Core (>= 1.17.3)";

    // Same failure as printed by `dotnet build` / `dotnet run` (MSBuild prefix on every line).
    private const string ConsoleOutput =
        "  Determining projects to restore...\r\n" +
        "/repo/build/Build.csproj : error NU1605: Warning As Error: Detected package downgrade: Tamp.Core from 1.17.5 to 1.17.3. Reference the package directly from the project to select a different version. \r\n" +
        "/repo/build/Build.csproj : error NU1605:  Build -> Tamp.Heft.V1 0.1.0 -> Tamp.Core (>= 1.17.5) \r\n" +
        "/repo/build/Build.csproj : error NU1605:  Build -> Tamp.Core (>= 1.17.3)\r\n" +
        "  Failed to restore /repo/build/Build.csproj (in 2.05 sec).\r\n";

    [Fact]
    public void Parses_Assets_Log_Message()
    {
        var d = Assert.Single(RestoreDowngradeDiagnostic.Parse(AssetsMessage));
        Assert.Equal("Tamp.Core", d.Package);
        Assert.Equal("1.17.5", d.Required);
        Assert.Equal("1.17.3", d.Pinned);
        Assert.Equal("Tamp.Heft.V1 0.1.0", d.RequiredBy);
    }

    [Fact]
    public void Parses_Console_Output_With_MSBuild_Prefixes_And_CRLF()
    {
        var d = Assert.Single(RestoreDowngradeDiagnostic.Parse(ConsoleOutput));
        Assert.Equal(new RestoreDowngradeDiagnostic.Downgrade("Tamp.Core", "1.17.5", "1.17.3", "Tamp.Heft.V1 0.1.0"), d);
    }

    [Fact]
    public void Parses_Message_Flattened_Onto_One_Line()
    {
        var d = Assert.Single(RestoreDowngradeDiagnostic.Parse(AssetsMessage.Replace("\n", " ")));
        Assert.Equal("Tamp.Heft.V1 0.1.0", d.RequiredBy);
    }

    [Fact]
    public void Dedupes_Repeats_And_Keeps_Distinct_Downgrades()
    {
        // The summary at the end of a build repeats every error; and a satellite may also lift NetCli.
        var netcli =
            "error NU1605: Detected package downgrade: Tamp.NetCli.V10 from 1.17.5 to 1.17.3. Reference the package directly from the project to select a different version.\n" +
            "error NU1605:  Build -> Tamp.Spfx.V1 0.2.0 -> Tamp.NetCli.V10 (>= 1.17.5)\n" +
            "error NU1605:  Build -> Tamp.NetCli.V10 (>= 1.17.3)\n";
        var all = RestoreDowngradeDiagnostic.Parse(ConsoleOutput + ConsoleOutput + netcli);
        Assert.Equal(2, all.Count);
        Assert.Equal("Tamp.Spfx.V1 0.2.0", all[1].RequiredBy);
    }

    [Fact]
    public void Finds_The_Requiring_Package_In_A_Longer_Chain()
    {
        const string text =
            "Detected package downgrade: Tamp.Core from 1.18.0 to 1.17.3. Reference the package directly from the project to select a different version.\n" +
            " Build -> Contoso.Build 2.0.0 -> Tamp.Heft.V1 0.3.0 -> Tamp.Core (>= 1.18.0)\n" +
            " Build -> Tamp.Core (>= 1.17.3)";
        Assert.Equal("Tamp.Heft.V1 0.3.0", Assert.Single(RestoreDowngradeDiagnostic.Parse(text)).RequiredBy);
    }

    [Fact]
    public void Handles_Prerelease_Versions()
    {
        const string text =
            "Detected package downgrade: Tamp.Core from 1.18.0-rc.1 to 1.17.3. Reference the package directly from the project to select a different version.\n" +
            " Build -> Tamp.Heft.V1 0.4.0-beta.2 -> Tamp.Core (>= 1.18.0-rc.1)\n";
        var d = Assert.Single(RestoreDowngradeDiagnostic.Parse(text));
        Assert.Equal("1.18.0-rc.1", d.Required);
        Assert.Equal("1.17.3", d.Pinned);
        Assert.Equal("Tamp.Heft.V1 0.4.0-beta.2", d.RequiredBy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("error CS1002: ; expected")]
    [InlineData("error NU1101: Unable to find package Tamp.Nope. No packages exist with this id")]
    public void Unrelated_Output_Yields_Nothing(string text)
        => Assert.Empty(RestoreDowngradeDiagnostic.Parse(text));

    [Fact]
    public void Format_Says_Who_Needs_What_And_How_To_Fix()
    {
        var msg = RestoreDowngradeDiagnostic.Format(RestoreDowngradeDiagnostic.Parse(AssetsMessage), "build/Build.csproj");
        Assert.NotNull(msg);
        Assert.Contains("NU1605", msg);
        Assert.Contains("Tamp.Heft.V1 0.1.0 needs Tamp.Core >= 1.17.5; build/Build.csproj pins Tamp.Core 1.17.3.", msg);
        Assert.Contains("raise Tamp.Core and Tamp.NetCli.V10 in build/Build.csproj, and dotnet-tamp in .config/dotnet-tools.json, to 1.17.5", msg);
    }

    [Fact]
    public void Format_Targets_The_Highest_Required_Core_Version()
    {
        var downgrades = new[]
        {
            new RestoreDowngradeDiagnostic.Downgrade("Tamp.Core", "1.17.5", "1.17.3", "Tamp.Heft.V1 0.1.0"),
            new RestoreDowngradeDiagnostic.Downgrade("Tamp.NetCli.V10", "1.17.10", "1.17.3", "Tamp.Spfx.V1 0.2.0"),
        };
        var msg = RestoreDowngradeDiagnostic.Format(downgrades, "build/Build.csproj");
        Assert.Contains("to 1.17.10", msg);
    }

    [Fact]
    public void Format_Gives_Per_Package_Fix_For_Non_Core_Tamp_Packages()
    {
        var msg = RestoreDowngradeDiagnostic.Format(
            [new RestoreDowngradeDiagnostic.Downgrade("Tamp.Http", "0.2.0", "0.1.0", "Tamp.Ingest.V1 0.6.0")],
            "build/Build.csproj");
        Assert.Contains("Fix: raise Tamp.Http in build/Build.csproj to 0.2.0.", msg);
        Assert.DoesNotContain("dotnet-tamp", msg);
    }

    [Fact]
    public void Format_Ignores_Non_Tamp_Downgrades()
    {
        var msg = RestoreDowngradeDiagnostic.Format(
            [new RestoreDowngradeDiagnostic.Downgrade("Newtonsoft.Json", "13.0.3", "12.0.1", "Contoso.Lib 1.0.0")],
            "build/Build.csproj");
        Assert.Null(msg);
    }

    // ─── TryDescribe: reads obj/project.assets.json next to the build project ───

    private static string WriteBuildProject(string? assetsJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tamp-nu1605-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "obj"));
        var csproj = Path.Combine(dir, "Build.csproj");
        File.WriteAllText(csproj, "<Project />");
        if (assetsJson is not null) File.WriteAllText(Path.Combine(dir, "obj", "project.assets.json"), assetsJson);
        return csproj;
    }

    private static string AssetsWithLogs(string logsJson) => $$"""{"version":3,"targets":{},"logs":{{logsJson}}}""";

    [Fact]
    public void TryDescribe_Reads_NU1605_From_Assets_File()
    {
        var logs = $$"""[{"code":"NU1605","level":"Error","message":{{System.Text.Json.JsonSerializer.Serialize(AssetsMessage)}},"libraryId":"Tamp.Core","targetGraphs":["net10.0"]}]""";
        var csproj = WriteBuildProject(AssetsWithLogs(logs));
        try
        {
            var msg = RestoreDowngradeDiagnostic.TryDescribe(csproj);
            Assert.NotNull(msg);
            Assert.Contains("Tamp.Heft.V1 0.1.0 needs Tamp.Core >= 1.17.5", msg);
        }
        finally { Directory.Delete(Path.GetDirectoryName(csproj)!, recursive: true); }
    }

    [Fact]
    public void TryDescribe_Uses_Captured_Output_When_There_Is_No_Assets_File()
    {
        var csproj = WriteBuildProject(assetsJson: null);
        try { Assert.Contains("needs Tamp.Core >= 1.17.5", RestoreDowngradeDiagnostic.TryDescribe(csproj, ConsoleOutput)); }
        finally { Directory.Delete(Path.GetDirectoryName(csproj)!, recursive: true); }
    }

    [Theory]
    [InlineData("""{"version":3,"logs":[]}""")]
    [InlineData("""{"version":3}""")]
    [InlineData("""{"version":3,"logs":[{"code":"NU1603","level":"Warning","message":"approx match"}]}""")]
    [InlineData("not json at all")]
    public void TryDescribe_Returns_Null_When_Nothing_To_Say(string assets)
    {
        var csproj = WriteBuildProject(assets);
        try { Assert.Null(RestoreDowngradeDiagnostic.TryDescribe(csproj)); }
        finally { Directory.Delete(Path.GetDirectoryName(csproj)!, recursive: true); }
    }

    [Fact]
    public void TryDescribe_Returns_Null_For_Missing_Project()
        => Assert.Null(RestoreDowngradeDiagnostic.TryDescribe(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N"), "Build.csproj")));
}
