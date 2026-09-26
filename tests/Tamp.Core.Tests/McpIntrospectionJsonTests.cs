using System.IO;
using System.Text.Json;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests for the #22 introspection JSON the MCP control surface consumes: the target
/// catalog (`--list --format json`) now carries `produces` + `capability`, and
/// `--plan --format json` emits the resolved execution order.
/// </summary>
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class McpIntrospectionJsonTests
{
    private sealed class TB : TampBuild
    {
        public Target Restore => _ => _.Executes(() => { });
        public Target Pack => _ => _
            .DependsOn(nameof(Restore))
            .Produces("artifacts/*.nupkg")
            .Capability(CapabilityTier.SideEffectful)
            .Executes(() => { });
    }

    private static string CapturedStdout(string[] args)
    {
        var stdout = new StringWriter();
        var prev = Console.Out;
        Console.SetOut(stdout);
        try { TampBuild.Execute<TB>(args); return stdout.ToString(); }
        finally { Console.SetOut(prev); }
    }

    private static JsonElement ParseObject(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith('{')) { /* first { starts the doc */ }
        }
        var start = text.IndexOf('{');
        return JsonDocument.Parse(text[start..]).RootElement;
    }

    [Fact]
    public void Catalog_Json_Includes_Produces_And_Capability()
    {
        var root = ParseObject(CapturedStdout(new[] { "--list", "--format", "json" }));
        var pack = root.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Pack");
        Assert.Equal("artifacts/*.nupkg", pack.GetProperty("produces")[0].GetString());
        Assert.Equal("SideEffectful", pack.GetProperty("capability").GetString());
        Assert.Equal("Restore", pack.GetProperty("depends_on")[0].GetString());
    }

    [Fact]
    public void Plan_Json_Emits_Execution_Order_With_Capability()
    {
        var root = ParseObject(CapturedStdout(new[] { "Pack", "--plan", "--format", "json" }));
        var order = root.GetProperty("order").EnumerateArray().Select(s => s.GetProperty("name").GetString()!).ToList();
        Assert.Equal(new[] { "Restore", "Pack" }, order);   // dependency ordered
        var pack = root.GetProperty("order").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "Pack");
        Assert.Equal("SideEffectful", pack.GetProperty("capability").GetString());
        Assert.Equal("Pack", root.GetProperty("roots")[0].GetString());
    }
}
