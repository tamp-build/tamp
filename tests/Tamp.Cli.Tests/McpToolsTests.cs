using System.IO;
using Tamp;
using Tamp.Cli.Mcp;
using Xunit;

namespace Tamp.Cli.Tests;

/// <summary>
/// Tests for the #22 MCP tool logic (<see cref="TampMcpTools"/>) with a fake build invoker —
/// no MCP transport, no real build. Covers catalog/describe/plan projection and the
/// run_target → get_result / get_log capture loop.
/// </summary>
public sealed class McpToolsTests
{
    private sealed class FakeInvoker : IBuildInvoker
    {
        public Func<IReadOnlyList<string>, (int, string, string)> Handler = _ => (0, "", "");
        public (int ExitCode, string Stdout, string Stderr) Run(IReadOnlyList<string> args) => Handler(args);
    }

    private const string Catalog = """
    {"tamp_version":"1.14.0","defaults":["Ci"],"targets":[
      {"name":"Restore","depends_on":[],"produces":[],"capability":"Safe"},
      {"name":"Push","depends_on":["Restore"],"produces":["artifacts/*.nupkg"],"capability":"SideEffectful"}
    ],"parameters":[]}
    """;

    [Fact]
    public void ListTargets_Returns_The_Catalog()
    {
        var tools = new TampMcpTools(new FakeInvoker { Handler = _ => (0, Catalog, "") });
        Assert.Contains("\"Push\"", tools.ListTargets());
    }

    [Fact]
    public void DescribeTarget_Finds_The_Target_And_Reports_Unknown()
    {
        var tools = new TampMcpTools(new FakeInvoker { Handler = _ => (0, Catalog, "") });
        var push = tools.DescribeTarget("Push");
        Assert.Contains("SideEffectful", push);
        Assert.Contains("artifacts/*.nupkg", push);
        Assert.Contains("error", tools.DescribeTarget("Nope"));
    }

    [Fact]
    public void RunTarget_Captures_Events_Then_GetResult_And_GetLog_Read_Them()
    {
        var inv = new FakeInvoker
        {
            Handler = args =>
            {
                // Emulate the build project writing the canonical stream to the --events file.
                var list = args.ToList();
                var ei = list.IndexOf("--events");
                if (ei >= 0 && ei + 1 < list.Count)
                {
                    File.WriteAllLines(list[ei + 1], new[]
                    {
                        BuildEventJson.Serialize(Ev(BuildEventTypes.ToolExited, "Push", new ToolExitedPayload { Tool = "dotnet", ExitCode = 0, DurationMs = 5 })),
                        BuildEventJson.Serialize(Ev(BuildEventTypes.TargetFinished, "Push", new TargetFinishedPayload { Target = "Push", Status = BuildEventStatus.Success })),
                    });
                }
                return (0, "", "");
            },
        };
        var tools = new TampMcpTools(inv);

        var summary = tools.RunTarget("Push", allowSideEffects: true);
        Assert.Contains("\"exitCode\":0", summary);

        var result = tools.GetResult("Push");
        Assert.Contains("target.finished", result);
        Assert.Contains("success", result);

        var log = tools.GetLog("Push");
        Assert.Contains("dotnet", log);
    }

    [Fact]
    public void GetResult_Without_A_Run_Reports_An_Error()
    {
        var tools = new TampMcpTools(new FakeInvoker());
        Assert.Contains("error", tools.GetResult("Push"));
    }

    private static BuildEvent Ev(string type, string target, BuildEventPayload payload) => new()
    {
        Type = type, BuildId = "b", RunId = "r", TraceId = "b", SpanId = "0123456789abcdef",
        TargetId = target, WorkerId = "human:test", Seq = 0, Payload = payload,
    };
}
