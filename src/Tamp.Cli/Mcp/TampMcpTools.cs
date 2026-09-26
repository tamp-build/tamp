using System.Text.Json;
using Tamp;

namespace Tamp.Cli.Mcp;

/// <summary>
/// Runs the build project on behalf of the MCP tools. The real implementation shells to
/// the located build project (as the CLI already does); tests inject a fake.
/// </summary>
internal interface IBuildInvoker
{
    (int ExitCode, string Stdout, string Stderr) Run(IReadOnlyList<string> args);
}

/// <summary>
/// The #22 MCP control-surface logic — pure and transport-agnostic (no MCP SDK types),
/// so it's unit-testable with a fake <see cref="IBuildInvoker"/>. The SDK wiring is a thin
/// layer over this. Read tools need no elevation; <see cref="RunTarget"/> drives the build
/// under <c>--enforce=agent</c> (default-deny side effects, #20) unless elevated.
/// </summary>
internal sealed class TampMcpTools
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly IBuildInvoker _invoker;
    private IReadOnlyList<BuildEvent> _lastRunEvents = Array.Empty<BuildEvent>();

    public TampMcpTools(IBuildInvoker invoker) => _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));

    /// <summary>The full target catalog as JSON (targets with deps / produces / capability, parameters).</summary>
    public string ListTargets() => _invoker.Run(new[] { "--list", "--all", "--format", "json" }).Stdout;

    /// <summary>A single target's catalog entry (inputs/outputs/deps/capability tier), or an error object if unknown.</summary>
    public string DescribeTarget(string name)
    {
        using var doc = JsonDocument.Parse(_invoker.Run(new[] { "--list", "--all", "--format", "json" }).Stdout);
        foreach (var t in doc.RootElement.GetProperty("targets").EnumerateArray())
            if (t.GetProperty("name").GetString() == name)
                return t.GetRawText();
        return JsonSerializer.Serialize(new { error = $"unknown target '{name}'" }, Json);
    }

    /// <summary>The resolved execution order for a target (DAG slice), without running anything.</summary>
    public string Plan(string target) => _invoker.Run(new[] { target, "--plan", "--format", "json" }).Stdout;

    /// <summary>
    /// Run a target under agent enforcement (default-deny side effects unless
    /// <paramref name="allowSideEffects"/>). Captures the canonical event stream for
    /// <see cref="GetResult"/> / <see cref="GetLog"/>; returns an exit + per-target summary.
    /// </summary>
    public string RunTarget(string target, bool allowSideEffects = false)
    {
        var eventsFile = Path.Combine(Path.GetTempPath(), $"tamp-mcp-{Guid.NewGuid():N}.ndjson");
        // #23: --capture-logs so get_log can lazily fetch the one failing target's full log
        // from .tamp/logs/<buildId>/<target>.log instead of holding the whole output stream.
        var args = new List<string> { target, "--enforce", "agent", "--reporter", "json", "--events", eventsFile, "--capture-logs" };
        if (allowSideEffects) args.Add("--allow-side-effects");

        var (exit, _, stderr) = _invoker.Run(args);
        _lastRunEvents = ReadEvents(eventsFile);

        var finished = _lastRunEvents
            .Where(e => e.Type == BuildEventTypes.TargetFinished && e.Payload is TargetFinishedPayload)
            .Select(e => (TargetFinishedPayload)e.Payload)
            .Select(p => new { target = p.Target, status = p.Status, remedy = p.Remedy })
            .ToList();
        var blocked = _lastRunEvents.Any(e => e.Type == BuildEventTypes.GateEvaluated
            && e.Payload is GateEvaluatedPayload g && g.Blocks);

        return JsonSerializer.Serialize(new
        {
            target,
            exitCode = exit,
            blockedByCapability = blocked,
            targets = finished,
            stderr = string.IsNullOrWhiteSpace(stderr) ? null : stderr.Trim(),
        }, Json);
    }

    /// <summary>The typed result (#12) for a target from the last <see cref="RunTarget"/> — status, outputs, inputsHash, remedy.</summary>
    public string GetResult(string target)
    {
        var e = _lastRunEvents.LastOrDefault(x => x.Type == BuildEventTypes.TargetFinished
            && x.Payload is TargetFinishedPayload p && p.Target == target);
        if (e is null) return JsonSerializer.Serialize(new { error = $"no result for '{target}' — run_target first" }, Json);
        return BuildEventJson.Serialize(e);
    }

    /// <summary>
    /// The per-target log from the last <see cref="RunTarget"/> (#23 agent economics). Prefers the
    /// full addressable per-target log file (<c>.tamp/logs/&lt;buildId&gt;/&lt;target&gt;.log</c>, redacted)
    /// captured under <c>--capture-logs</c>; falls back to the event-derived tool list + failure tail
    /// when no file was captured. Lets an agent fetch one failing log lazily rather than hold the stream.
    /// </summary>
    public string GetLog(string target)
    {
        var forTarget = _lastRunEvents.Where(e => e.TargetId == target).ToList();
        if (forTarget.Count == 0) return JsonSerializer.Serialize(new { error = $"no log for '{target}' — run_target first" }, Json);

        var finished = forTarget.LastOrDefault(e => e.Type == BuildEventTypes.TargetFinished)?.Payload as TargetFinishedPayload;

        // #23: prefer the full captured log file when present.
        if (finished?.LogPath is { } rel && ResolveWorktreePath(rel) is { } full && File.Exists(full))
        {
            try
            {
                return JsonSerializer.Serialize(new { target, logPath = rel, log = File.ReadAllText(full) }, Json);
            }
            catch { /* fall through to the event tail */ }
        }

        var tools = forTarget.Where(e => e.Type == BuildEventTypes.ToolExited && e.Payload is ToolExitedPayload)
            .Select(e => (ToolExitedPayload)e.Payload)
            .Select(p => new { tool = p.Tool, exitCode = p.ExitCode, durationMs = p.DurationMs })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            target,
            tools,
            outputTail = finished?.OutputTail,
            logPath = finished?.LogPath,
            note = "no captured log file (run without --capture-logs, or it was unavailable); showing the last run's event tail.",
        }, Json);
    }

    /// <summary>Resolve a worktree-relative log path against the build root (best-effort; falls back to the current directory).</summary>
    private static string? ResolveWorktreePath(string relative)
    {
        try { return Path.Combine(TampBuild.RootDirectory.Value, relative); }
        catch { }
        try { return Path.Combine(Directory.GetCurrentDirectory(), relative); }
        catch { return null; }
    }

    private static IReadOnlyList<BuildEvent> ReadEvents(string path)
    {
        if (!File.Exists(path)) return Array.Empty<BuildEvent>();
        try
        {
            var events = new List<BuildEvent>();
            foreach (var line in File.ReadAllLines(path))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (BuildEventJson.Deserialize(t) is { } e) events.Add(e);
            }
            return events;
        }
        catch { return Array.Empty<BuildEvent>(); }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }
}
