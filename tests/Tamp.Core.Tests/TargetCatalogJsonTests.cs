using System.IO;
using System.Text.Json;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests for <c>--list --format=json</c> (TAM-139). Validates the JSON
/// catalog shape: tamp_version, build_assembly, defaults, targets[],
/// parameters[]. Verifies field names match the IDE-extension contract
/// in the filed ticket.
/// </summary>
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class TargetCatalogJsonTests
{
    private sealed class CatalogBuild : TampBuild
    {
        [Parameter("Build configuration")] public string Configuration { get; init; } = "Debug";

        [Parameter] public string? Version { get; init; }

        public Target Restore => _ => _
            .Description("Restore packages");

        public Target Compile => _ => _
            .Phase(Phase.Build)
            .DependsOn(nameof(Restore))
            .Description("Build the solution");

        public Target Test => _ => _
            .Phase(Phase.Test)
            .DependsOn(nameof(Compile))
            .Default();

        public Target SetupDb => _ => _
            .Internal()
            .DependsOn(nameof(Restore));
    }

    private static JsonElement RunListJson(string[] args, out int exitCode)
    {
        var stdout = new StringWriter();
        var prev = Console.Out;
        Console.SetOut(stdout);
        try
        {
            exitCode = TampBuild.Execute<CatalogBuild>(args);
        }
        finally
        {
            Console.SetOut(prev);
        }
        var text = stdout.ToString();
        // The banner is suppressed in --list, but we still need to skip any non-JSON
        // prelude defensively — find the first '{' and parse from there.
        var firstBrace = text.IndexOf('{');
        Assert.True(firstBrace >= 0, $"No JSON found in output:\n{text}");
        var jsonSpan = text[firstBrace..];
        return JsonDocument.Parse(jsonSpan).RootElement;
    }

    [Fact]
    public void List_Format_Json_Emits_Valid_JSON()
    {
        var root = RunListJson(new[] { "--list", "--format=json" }, out var exit);
        Assert.Equal(0, exit);
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
    }

    [Fact]
    public void Catalog_Includes_Required_Top_Level_Properties()
    {
        var root = RunListJson(new[] { "--list", "--format=json" }, out _);
        Assert.True(root.TryGetProperty("tamp_version", out _));
        Assert.True(root.TryGetProperty("build_assembly", out _));
        Assert.True(root.TryGetProperty("defaults", out _));
        Assert.True(root.TryGetProperty("targets", out _));
        Assert.True(root.TryGetProperty("parameters", out _));
    }

    [Fact]
    public void Catalog_Targets_Listed_In_Alphabetical_Order()
    {
        var root = RunListJson(new[] { "--list", "--format=json" }, out _);
        var names = root.GetProperty("targets").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToList();
        // 3 visible (Restore, Compile, Test) — SetupDb is Internal so excluded.
        Assert.Equal(new[] { "Compile", "Restore", "Test" }, names);
    }

    [Fact]
    public void Catalog_Shows_Internal_Targets_When_All_Flag_Set()
    {
        var root = RunListJson(new[] { "--list", "--all", "--format=json" }, out _);
        var names = root.GetProperty("targets").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToList();
        Assert.Contains("SetupDb", names);
    }

    [Fact]
    public void Catalog_Target_Carries_Description_And_Phase()
    {
        var root = RunListJson(new[] { "--list", "--format=json" }, out _);
        var compile = root.GetProperty("targets").EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "Compile");
        Assert.Equal("Build the solution", compile.GetProperty("description").GetString());
        Assert.Equal("Build", compile.GetProperty("phase").GetString());
    }

    [Fact]
    public void Catalog_Target_Carries_DependsOn()
    {
        var root = RunListJson(new[] { "--list", "--format=json" }, out _);
        var test = root.GetProperty("targets").EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "Test");
        var deps = test.GetProperty("depends_on").EnumerateArray()
            .Select(d => d.GetString()!)
            .ToList();
        Assert.Equal(new[] { "Compile" }, deps);
    }

    [Fact]
    public void Catalog_Defaults_Reflects_Default_Marked_Target()
    {
        var root = RunListJson(new[] { "--list", "--format=json" }, out _);
        var defaults = root.GetProperty("defaults").EnumerateArray()
            .Select(d => d.GetString()!)
            .ToList();
        Assert.Equal(new[] { "Test" }, defaults);
    }

    [Fact]
    public void Catalog_Parameters_Include_Build_Parameters()
    {
        var root = RunListJson(new[] { "--list", "--format=json" }, out _);
        var paramNames = root.GetProperty("parameters").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()!)
            .ToList();
        Assert.Contains("Configuration", paramNames);
        Assert.Contains("Version", paramNames);
    }

    [Fact]
    public void Catalog_Parameter_Includes_Description_And_EnvVar()
    {
        var root = RunListJson(new[] { "--list", "--format=json" }, out _);
        var config = root.GetProperty("parameters").EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == "Configuration");
        Assert.Equal("Build configuration", config.GetProperty("description").GetString());
        Assert.Equal("CONFIGURATION", config.GetProperty("env_var").GetString());
        Assert.Equal("Debug", config.GetProperty("default").GetString());
    }

    [Fact]
    public void Catalog_Has_Tamp_Version_Three_Part_String()
    {
        var root = RunListJson(new[] { "--list", "--format=json" }, out _);
        var version = root.GetProperty("tamp_version").GetString();
        Assert.NotNull(version);
        Assert.Matches(@"^\d+\.\d+\.\d+", version);
    }

    [Fact]
    public void Catalog_Json_Works_With_ListTree_Mode_Too()
    {
        // --list-tree --format=json should produce the same catalog shape.
        var root = RunListJson(new[] { "--list-tree", "--format=json" }, out var exit);
        Assert.Equal(0, exit);
        Assert.True(root.TryGetProperty("targets", out _));
    }

    [Fact]
    public void ParseInvocation_Captures_FormatJson_Flag()
    {
        var targets = TampBuild.CollectTargets(new CatalogBuild());
        var (_, _, listMode, _, _, _, _, format, _) = TampBuild.ParseInvocation(
            new[] { "--list", "--format=json" }, targets);
        Assert.Equal(TampBuild.ListMode.Flat, listMode);
        Assert.Equal(TampBuild.OutputFormat.Json, format);
    }

    [Fact]
    public void ParseInvocation_Defaults_To_Text_Format()
    {
        var targets = TampBuild.CollectTargets(new CatalogBuild());
        var (_, _, _, _, _, _, _, format, _) = TampBuild.ParseInvocation(
            new[] { "--list" }, targets);
        Assert.Equal(TampBuild.OutputFormat.Text, format);
    }

    [Fact]
    public void ParseInvocation_Throws_On_Unknown_Format_Value()
    {
        var targets = TampBuild.CollectTargets(new CatalogBuild());
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TampBuild.ParseInvocation(new[] { "--list", "--format=yaml" }, targets));
        Assert.Contains("--format", ex.Message);
    }
}
