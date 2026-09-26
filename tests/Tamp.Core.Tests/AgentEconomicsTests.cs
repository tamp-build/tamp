using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests for #23 agent economics: opt-in per-target log capture
/// (<c>--capture-logs</c> → redacted <c>.tamp/logs/&lt;buildId&gt;/&lt;target&gt;.log</c> +
/// <c>target.finished.logPath</c>) and the always-on compact failure summary.
/// </summary>
public sealed class AgentEconomicsTests
{
    private sealed class CapturingSink : IBuildEventSink
    {
        public readonly List<BuildEvent> Events = new();
        public void Emit(BuildEvent e) => Events.Add(e);
    }

    private sealed class EchoBuild : TampBuild
    {
        public static CommandPlan Plan = null!;
        public Target Run => _ => _.Executes(() => Plan);
    }

    /// <summary>A cross-platform command that echoes <paramref name="text"/> to stdout.</summary>
    private static CommandPlan EchoPlan(string text, Secret? secret = null)
    {
        var plan = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new CommandPlan { Executable = "cmd.exe", Arguments = new[] { "/c", $"echo {text}" } }
            : new CommandPlan { Executable = "/bin/sh", Arguments = new[] { "-c", $"echo {text}" } };
        if (secret is not null) plan = plan with { Secrets = new[] { secret } };
        return plan;
    }

    /// <summary>A cross-platform command that exits with a non-zero code.</summary>
    private static CommandPlan FailPlan() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new CommandPlan { Executable = "cmd.exe", Arguments = new[] { "/c", "exit 3" } }
            : new CommandPlan { Executable = "/bin/sh", Arguments = new[] { "-c", "exit 3" } };

    private static (TargetFinishedPayload Finished, string ConsoleOut) Run(CommandPlan plan, bool captureLogs, TextWriter? output = null)
    {
        EchoBuild.Plan = plan;
        var targets = TampBuild.CollectTargets(new EchoBuild());
        var sink = new CapturingSink();
        var console = output ?? TextWriter.Null;
        new Executor(new TargetGraph(targets), output: console, eventSink: sink, captureLogs: captureLogs).Run("Run");
        var finished = (TargetFinishedPayload)sink.Events.Last(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == "Run").Payload;
        return (finished, console.ToString() ?? string.Empty);
    }

    private static string ResolveLog(string relative) => Path.Combine(TampBuild.RootDirectory.Value, relative);

    [Fact]
    public void CaptureLogs_Off_Writes_No_File_And_No_LogPath()
    {
        var (finished, _) = Run(EchoPlan("HELLO-NOCAPTURE"), captureLogs: false);
        Assert.Null(finished.LogPath);
    }

    [Fact]
    public void CaptureLogs_On_Writes_Redacted_Per_Target_Log_And_Sets_LogPath()
    {
        var (finished, _) = Run(EchoPlan("HELLO-CAPTURE"), captureLogs: true);

        Assert.NotNull(finished.LogPath);
        Assert.StartsWith(".tamp/logs/", finished.LogPath!.Replace('\\', '/'));
        Assert.EndsWith("Run.log", finished.LogPath.Replace('\\', '/'));

        var full = ResolveLog(finished.LogPath);
        try
        {
            Assert.True(File.Exists(full));
            Assert.Contains("HELLO-CAPTURE", File.ReadAllText(full));
        }
        finally { TryCleanup(full); }
    }

    [Fact]
    public void CaptureLogs_Redacts_Secrets_On_The_Way_To_Disk()
    {
        const string token = "SEKRET-log-abc123";
        var (finished, _) = Run(EchoPlan($"leak-{token}", new Secret("Tok", token)), captureLogs: true);

        var full = ResolveLog(finished.LogPath!);
        try
        {
            var content = File.ReadAllText(full);
            Assert.DoesNotContain(token, content);       // secret never hits disk
            Assert.Contains("<Secret:Tok>", content);    // it was redacted, not merely absent
        }
        finally { TryCleanup(full); }
    }

    [Fact]
    public void Compact_Failure_Summary_Lists_Each_Failure_With_Reproduce()
    {
        var console = new StringWriter();
        var (_, _) = Run(FailPlan(), captureLogs: false, output: console);

        var text = console.ToString();
        Assert.Contains("FAILED (1):", text);
        Assert.Contains("Run — exit 3", text);
        Assert.Contains("reproduce: tamp Run", text);
    }

    [Fact]
    public void ResolveCaptureLogs_Reads_Flag_And_Env()
    {
        Assert.True(TampBuild.ResolveCaptureLogs(new[] { "Run", "--capture-logs" }, _ => null));
        Assert.True(TampBuild.ResolveCaptureLogs(new[] { "Run" }, k => k == "TAMP_CAPTURE_LOGS" ? "1" : null));
        Assert.False(TampBuild.ResolveCaptureLogs(new[] { "Run" }, _ => null));
        Assert.False(TampBuild.ResolveCaptureLogs(new[] { "Run" }, k => k == "TAMP_CAPTURE_LOGS" ? "false" : null));
        Assert.False(TampBuild.ResolveCaptureLogs(new[] { "Run" }, k => k == "TAMP_CAPTURE_LOGS" ? "0" : null));
    }

    private static void TryCleanup(string logFile)
    {
        try
        {
            // Remove the per-build directory (.tamp/logs/<buildId>) this test created.
            var dir = Path.GetDirectoryName(logFile);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* best effort — .tamp is gitignored */ }
    }
}
