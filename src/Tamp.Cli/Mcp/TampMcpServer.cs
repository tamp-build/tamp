using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Tamp.Cli.Mcp;

/// <summary>
/// The <c>tamp mcp</c> server (#22): exposes the build graph to an agent over MCP (stdio).
/// Read tools (<c>list_targets</c> / <c>describe_target</c> / <c>plan</c> / <c>get_result</c> /
/// <c>get_log</c>) need no elevation; <c>run_target</c> drives the build under agent enforcement
/// (default-deny side effects, #20) unless elevated. All logic lives in <see cref="TampMcpTools"/>;
/// this is the transport wiring.
/// </summary>
internal static class TampMcpServer
{
    public static async Task<int> RunAsync(string buildProject)
    {
        var builder = Host.CreateApplicationBuilder();
        // stdio is the MCP transport — keep stdout clean; route any logs to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace);

        builder.Services.AddSingleton<IBuildInvoker>(new BuildProjectInvoker(buildProject));
        builder.Services.AddSingleton<TampMcpTools>();
        builder.Services.AddSingleton<TampMcpToolset>();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<TampMcpToolset>();

        await builder.Build().RunAsync();
        return 0;
    }
}

/// <summary>MCP tool surface (#22) — thin delegation to <see cref="TampMcpTools"/>.</summary>
[McpServerToolType]
internal sealed class TampMcpToolset
{
    private readonly TampMcpTools _tools;
    public TampMcpToolset(TampMcpTools tools) => _tools = tools;

    [McpServerTool(Name = "list_targets"), Description("List the build's targets with their dependencies, produced artifacts, and capability tier (JSON).")]
    public string ListTargets() => _tools.ListTargets();

    [McpServerTool(Name = "describe_target"), Description("Describe one target: inputs/outputs, dependencies, and capability tier (JSON).")]
    public string DescribeTarget([Description("The target name.")] string name) => _tools.DescribeTarget(name);

    [McpServerTool(Name = "plan"), Description("Show the resolved execution order (DAG slice) for a target without running anything (JSON).")]
    public string Plan([Description("The target to plan.")] string target) => _tools.Plan(target);

    [McpServerTool(Name = "run_target"), Description("Run a target under agent capability enforcement (side-effectful work is denied unless allowSideEffects=true). Returns an exit + per-target summary.")]
    public string RunTarget(
        [Description("The target to run.")] string target,
        [Description("Elevate to permit side-effectful work (publish/deploy/secret-reveal). Default false.")] bool allowSideEffects = false)
        => _tools.RunTarget(target, allowSideEffects);

    [McpServerTool(Name = "get_result"), Description("The typed result of a target from the last run_target: status, outputs (path/hash/kind), inputsHash, remedy (JSON).")]
    public string GetResult([Description("The target name.")] string target) => _tools.GetResult(target);

    [McpServerTool(Name = "get_log"), Description("The per-target log from the last run_target: tool invocations and, on failure, the captured output tail (JSON).")]
    public string GetLog([Description("The target name.")] string target) => _tools.GetLog(target);
}
