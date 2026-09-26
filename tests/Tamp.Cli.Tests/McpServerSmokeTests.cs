using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tamp;
using Tamp.Cli.Mcp;
using Xunit;

namespace Tamp.Cli.Tests;

/// <summary>
/// Live end-to-end smoke of the #22 <c>tamp mcp</c> surface: wires the real MCP SDK server
/// (<see cref="TampMcpToolset"/> registered via <c>AddMcpServer().WithTools</c>) to a real MCP
/// client over an in-memory duplex-stream transport — exercising tool discovery, argument
/// binding, and the JSON-RPC round-trip that the <see cref="TampMcpTools"/> unit tests skip.
/// A fake <see cref="IBuildInvoker"/> stands in for the build project (no subprocess, no Docker).
/// </summary>
public sealed class McpServerSmokeTests
{
    private const string Catalog = """
    {"tamp_version":"1.14.0","defaults":["Ci"],"targets":[
      {"name":"Restore","depends_on":[],"produces":[],"capability":"Safe"},
      {"name":"Push","depends_on":["Restore"],"produces":["artifacts/*.nupkg"],"capability":"SideEffectful"}
    ],"parameters":[]}
    """;

    private sealed class FakeInvoker : IBuildInvoker
    {
        public (int ExitCode, string Stdout, string Stderr) Run(IReadOnlyList<string> args)
            => args.Contains("--list") ? (0, Catalog, "") : (0, "", "");
    }

    [Fact]
    public async Task Client_Discovers_Tools_And_Calls_Them_Over_The_Mcp_Transport()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // ── Server: the same DI wiring tamp mcp uses, minus the stdio host. ──
        var services = new ServiceCollection();
        services.AddSingleton<IBuildInvoker>(new FakeInvoker());
        services.AddSingleton<TampMcpTools>();
        services.AddSingleton<TampMcpToolset>();
        services.AddMcpServer().WithTools<TampMcpToolset>();
        await using var sp = services.BuildServiceProvider();
        var serverOptions = sp.GetRequiredService<IOptions<McpServerOptions>>().Value;

        // Two unidirectional pipes form the duplex link between client and server.
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        await using var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "tamp-test", NullLoggerFactory.Instance);
        await using var server = McpServer.Create(serverTransport, serverOptions, NullLoggerFactory.Instance, sp);
        var serverTask = server.RunAsync(cts.Token);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance);
        var client = await McpClient.CreateAsync(clientTransport, cancellationToken: cts.Token);

        try
        {
            // Tool discovery: all six tamp tools are advertised over the wire.
            var tools = await client.ListToolsAsync(cancellationToken: cts.Token);
            var names = tools.Select(t => t.Name).ToHashSet();
            Assert.Contains("list_targets", names);
            Assert.Contains("describe_target", names);
            Assert.Contains("plan", names);
            Assert.Contains("run_target", names);
            Assert.Contains("get_result", names);
            Assert.Contains("get_log", names);

            // A no-arg call round-trips and returns the catalog JSON.
            var list = await client.CallToolAsync("list_targets",
                new Dictionary<string, object?>(), cancellationToken: cts.Token);
            Assert.Contains("\"Push\"", TextOf(list));

            // An argument-bound call round-trips and projects the one target.
            var describe = await client.CallToolAsync("describe_target",
                new Dictionary<string, object?> { ["name"] = "Push" }, cancellationToken: cts.Token);
            var describeText = TextOf(describe);
            Assert.Contains("SideEffectful", describeText);
            Assert.Contains("artifacts/*.nupkg", describeText);
        }
        finally
        {
            await client.DisposeAsync();
            await clientToServer.Writer.CompleteAsync();
            await serverToClient.Writer.CompleteAsync();
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* server winds down on transport close */ }
        }
    }

    private static string TextOf(CallToolResult result)
        => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
}
