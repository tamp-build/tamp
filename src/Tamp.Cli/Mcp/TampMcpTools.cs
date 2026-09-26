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
        var args = new List<string> { target, "--enforce", "agent", "--reporter", "json", "--events", eventsFile };
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
    /// The per-target log from the last <see cref="RunTarget"/>: the tool invocations and, on
    /// failure, the captured output tail. (Full addressable per-(buildId,targetId) log storage
    /// is #23; this returns what the last run captured.)
    /// </summary>
    public string GetLog(string target)
    {
        var forTarget = _lastRunEvents.Where(e => e.TargetId == target).ToList();
        if (forTarget.Count == 0) return JsonSerializer.Serialize(new { error = $"no log for '{target}' — run_target first" }, Json);

        var tools = forTarget.Where(e => e.Type == BuildEventTypes.ToolExited && e.Payload is ToolExitedPayload)
            .Select(e => (ToolExitedPayload)e.Payload)
            .Select(p => new { tool = p.Tool, exitCode = p.ExitCode, durationMs = p.DurationMs })
            .ToList();
        var finished = forTarget.LastOrDefault(e => e.Type == BuildEventTypes.TargetFinished)?.Payload as TargetFinishedPayload;

        return JsonSerializer.Serialize(new
        {
            target,
            tools,
            outputTail = finished?.OutputTail,
            note = "per-(buildId,targetId) addressable log storage is tracked in #23; this is the last run's capture.",
        }, Json);
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
