using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamp.Migrate.Report;

/// <summary>What happened at one site the rewriter recognised.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Outcome>))]
public enum Outcome
{
    /// <summary>Rewritten (or valid as written) with no marker: an <c>auto</c> row applied.</summary>
    Applied,
    /// <summary>Rewritten, and a marker left for a person to confirm.</summary>
    Assisted,
    /// <summary>Not rewritten; a marker says what to do by hand.</summary>
    Manual,
    /// <summary>NUKE surface with no mapping row (or a known gap); marker only.</summary>
    Unmapped,
}

/// <summary>One recognised site: where, which mapping row, what happened, and the marker if one was left.</summary>
public sealed record MigrationSite(string File, int Line, string RuleId, Outcome Outcome, string? Marker);

/// <summary>Counts per outcome.</summary>
public sealed record CoverageSummary(int Applied, int Assisted, int Manual, int Unmapped)
{
    public int Total => Applied + Assisted + Manual + Unmapped;
}

/// <summary>
/// The coverage report (<c>tamp-migration.json</c> plus a Markdown summary): every site the rewriter
/// recognised, by outcome, with the markers it left.
/// </summary>
public sealed class MigrationReport
{
    public required string MappingVersion { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public required IReadOnlyList<string> ChangedFiles { get; init; }
    public required IReadOnlyList<MigrationSite> Sites { get; init; }

    public CoverageSummary Summary => new(
        Sites.Count(s => s.Outcome == Outcome.Applied),
        Sites.Count(s => s.Outcome == Outcome.Assisted),
        Sites.Count(s => s.Outcome == Outcome.Manual),
        Sites.Count(s => s.Outcome == Outcome.Unmapped));

    /// <summary>Sites that left a marker, i.e. what a person (or the agent path) still has to resolve.</summary>
    public IEnumerable<MigrationSite> Markers => Sites.Where(s => s.Marker is not null);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(new
    {
        mappingVersion = MappingVersion,
        summary = Summary,
        files = Files,
        changedFiles = ChangedFiles,
        sites = Sites,
    }, Json);

    public string ToMarkdown()
    {
        var s = Summary;
        var md = new StringBuilder();
        md.AppendLine("# NUKE → Tamp migration report").AppendLine();
        md.AppendLine($"Mapping schema {MappingVersion}. {Files.Count} file(s) scanned, {ChangedFiles.Count} changed.").AppendLine();
        md.AppendLine("| Outcome | Sites |").AppendLine("|---|---|");
        md.AppendLine($"| applied (auto) | {s.Applied} |");
        md.AppendLine($"| assisted (rewritten, confirm) | {s.Assisted} |");
        md.AppendLine($"| manual (marker only) | {s.Manual} |");
        md.AppendLine($"| unmapped / gap | {s.Unmapped} |");
        md.AppendLine();

        var markers = Markers.ToList();
        if (markers.Count == 0)
        {
            md.AppendLine("No `TAMP-MIGRATE` markers were left.");
            return md.ToString();
        }

        md.AppendLine($"## Markers to resolve ({markers.Count})").AppendLine();
        md.AppendLine("| Where | Rule | Outcome | What to do |").AppendLine("|---|---|---|---|");
        foreach (var m in markers)
            md.AppendLine($"| `{m.File}:{m.Line}` | `{m.RuleId}` | {m.Outcome.ToString().ToLowerInvariant()} | {m.Marker!.Replace("|", "\\|", StringComparison.Ordinal)} |");
        return md.ToString();
    }
}
