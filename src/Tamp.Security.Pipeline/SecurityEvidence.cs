using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tamp.Security.Pipeline;

/// <summary>
/// Whether a piece of security evidence can be believed. The distinction this type exists to make:
/// <b>"the scanner found nothing" and "there is no scanner result" are different facts</b>, and
/// collapsing them into a count of zero reports an unknown outcome as a good one.
/// </summary>
public enum SecurityEvidenceStatus
{
    /// <summary>
    /// No output file. The scanner never ran, was skipped, or died before writing anything. There is
    /// no result — in particular this is <b>not</b> zero findings.
    /// </summary>
    NotRun,

    /// <summary>
    /// The output file exists but could not be parsed. A tool that crashed mid-write, or wrote
    /// something that is not the format it claims. No reliable result either.
    /// </summary>
    Unreadable,

    /// <summary>
    /// The scanner ran and its output parsed. The accompanying count is then a real measurement, and
    /// a count of zero genuinely means it looked and found nothing.
    /// </summary>
    Completed,
}

/// <summary>
/// One scan output the pipeline expects to exist, and the tool tag it is reported under.
/// </summary>
/// <remarks>
/// Declaring the expected set is what makes "this scan did not run" an answerable question. Without
/// it, a scanner that was never wired and a scanner that crashed are both simply absent, and nothing
/// can tell the difference. An adopter who deliberately drops a scanner removes it from
/// <c>SecurityExpectedScans</c>, which is an explicit statement rather than a silent gap.
/// </remarks>
/// <param name="Tool">Canonical tool identifier, used as the metric tag.</param>
/// <param name="Path">The SARIF file, or a directory of SARIF files when <paramref name="IsDirectory"/>.</param>
/// <param name="IsDirectory">
/// True when the tool writes several SARIFs into a directory rather than one file — the Roslyn leg
/// emits one per (project, TFM). The whole directory is then one scan for status purposes.
/// </param>
public sealed record SecurityScanSource(string Tool, AbsolutePath Path, bool IsDirectory = false);

/// <summary>One scanner's result, with its reliability attached rather than implied by a count.</summary>
public sealed record SecurityScanReport
{
    /// <summary>The canonical tool identifier, as used for the metric tag: <c>opengrep</c>, <c>trivy</c>, …</summary>
    public required string Tool { get; init; }

    /// <summary>Whether this result can be believed.</summary>
    public required SecurityEvidenceStatus Status { get; init; }

    /// <summary>Where the output was expected.</summary>
    public required string Path { get; init; }

    /// <summary>Findings parsed. Meaningful only when <see cref="IsReliable"/>; otherwise zero-by-absence.</summary>
    public int Findings { get; init; }

    /// <summary>Findings per SARIF level tag (<c>none</c> / <c>note</c> / <c>warning</c> / <c>error</c>).</summary>
    public IReadOnlyDictionary<string, int> FindingsBySeverity { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Why the status is not <see cref="SecurityEvidenceStatus.Completed"/>, when it is not.</summary>
    public string? Detail { get; init; }

    /// <summary>The scanner ran and its output parsed, so <see cref="Findings"/> is a measurement.</summary>
    public bool IsReliable => Status == SecurityEvidenceStatus.Completed;

    /// <summary>
    /// The scanner ran and found nothing — a positive result. Deliberately false for
    /// <see cref="SecurityEvidenceStatus.NotRun"/>, which is the whole point of this type.
    /// </summary>
    public bool IsClean => IsReliable && Findings == 0;

    /// <summary>Findings at or above a SARIF level, for a policy gate. Unreliable reports contribute zero.</summary>
    public int FindingsAtOrAbove(string level)
    {
        if (!IsReliable) return 0;
        var floor = SeverityRank(level);
        return FindingsBySeverity
            .Where(kv => SeverityRank(kv.Key) >= floor)
            .Sum(kv => kv.Value);
    }

    internal static int SeverityRank(string level) => level switch
    {
        "error" => 3,
        "warning" => 2,
        "note" => 1,
        _ => 0,
    };

    /// <summary>A line for a build log. Never says "0 findings" for a scan that did not run.</summary>
    public override string ToString() => Status switch
    {
        SecurityEvidenceStatus.NotRun => $"{Tool}: NOT RUN — no result ({Detail ?? Path})",
        SecurityEvidenceStatus.Unreadable => $"{Tool}: UNREADABLE — no result ({Detail ?? Path})",
        _ when Findings == 0 => $"{Tool}: clean (ran, 0 findings)",
        _ => $"{Tool}: {Findings} finding(s) ("
             + string.Join(", ", FindingsBySeverity.OrderByDescending(kv => SeverityRank(kv.Key)).Select(kv => $"{kv.Value} {kv.Key}"))
             + ")",
    };
}

/// <summary>The generated SBOM's result, with the same reliability distinction.</summary>
public sealed record SecuritySbomReport
{
    /// <summary>The generator identifier, as used for the metric tag: <c>cyclonedx</c>, <c>syft</c>, …</summary>
    public required string Producer { get; init; }

    /// <summary>Whether this result can be believed.</summary>
    public required SecurityEvidenceStatus Status { get; init; }

    /// <summary>Where the BOM was expected.</summary>
    public required string Path { get; init; }

    /// <summary>Components parsed. Meaningful only when <see cref="IsReliable"/>.</summary>
    public int Components { get; init; }

    /// <summary>Components per CycloneDX component type.</summary>
    public IReadOnlyDictionary<string, int> ComponentsByType { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Why the status is not <see cref="SecurityEvidenceStatus.Completed"/>, when it is not.</summary>
    public string? Detail { get; init; }

    /// <summary>The generator ran and its output parsed.</summary>
    public bool IsReliable => Status == SecurityEvidenceStatus.Completed;

    /// <summary>
    /// Parsed but describing nothing. Unlike a scan, zero is not a good outcome for an SBOM: an SBOM
    /// with no components cannot be scanned for anything, so every downstream SCA result derived from
    /// it is vacuously clean. Treated as unreliable by the default gate.
    /// </summary>
    public bool IsEmpty => IsReliable && Components == 0;

    /// <summary>A line for a build log.</summary>
    public override string ToString() => Status switch
    {
        SecurityEvidenceStatus.NotRun => $"sbom({Producer}): NOT RUN — no SBOM ({Detail ?? Path})",
        SecurityEvidenceStatus.Unreadable => $"sbom({Producer}): UNREADABLE — no SBOM ({Detail ?? Path})",
        _ when Components == 0 => $"sbom({Producer}): EMPTY — parsed, 0 components, so anything scanned from it is vacuously clean",
        _ => $"sbom({Producer}): {Components} component(s)",
    };
}

/// <summary>Whether an evidence publication actually landed.</summary>
public enum SecurityPublicationStatus
{
    /// <summary>
    /// Not attempted because its credentials are unset. A legitimate, green outcome — the pipeline is
    /// designed to no-op cleanly without a sink configured.
    /// </summary>
    NotConfigured,

    /// <summary>Attempted and finished.</summary>
    Completed,

    /// <summary>
    /// Attempted but did not finish — an upload accepted whose asynchronous analysis never settled, a
    /// partial import. The evidence may or may not be there, so the result cannot be relied on.
    /// </summary>
    Incomplete,

    /// <summary>Attempted and failed outright.</summary>
    Failed,
}

/// <summary>One publication leg's outcome: Dependency-Track, DefectDojo, …</summary>
public sealed record SecurityPublicationReport
{
    /// <summary>The sink: <c>dependency-track</c>, <c>defectdojo</c>.</summary>
    public required string Sink { get; init; }

    /// <summary>Whether the publication landed.</summary>
    public required SecurityPublicationStatus Status { get; init; }

    /// <summary>What happened, for a log line and the receipt. Never carries a credential.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// The publication either landed or was deliberately not configured. An
    /// <see cref="SecurityPublicationStatus.Incomplete"/> leg is specifically not reliable: that is
    /// the state an unsettled Dependency-Track analysis used to report as success.
    /// </summary>
    public bool IsReliable => Status is SecurityPublicationStatus.NotConfigured or SecurityPublicationStatus.Completed;

    /// <summary>A line for a build log.</summary>
    public override string ToString() => $"{Sink}: {Status.ToString().ToLowerInvariant()}"
        + (Detail is { Length: > 0 } ? $" — {Detail}" : "");
}

/// <summary>
/// Everything the collect and publish stages produced, for the enforcement stage to read. This is the
/// input a policy gate is written against, and the shape of the receipt the pipeline writes.
/// </summary>
public sealed record SecurityEvidenceSummary
{
    /// <summary>The SBOM, when the pipeline produces one.</summary>
    public SecuritySbomReport? Sbom { get; init; }

    /// <summary>Per-scanner results, in collection order.</summary>
    public IReadOnlyList<SecurityScanReport> Scans { get; init; } = [];

    /// <summary>Per-sink publication outcomes.</summary>
    public IReadOnlyList<SecurityPublicationReport> Publications { get; init; } = [];

    /// <summary>
    /// Scans whose result cannot be believed — never run, or unreadable. A non-empty list means the
    /// build has no information about those surfaces, which is not the same as having good news.
    /// </summary>
    public IReadOnlyList<SecurityScanReport> UnreliableScans => [.. Scans.Where(s => !s.IsReliable)];

    /// <summary>Publication legs that were attempted and did not land.</summary>
    public IReadOnlyList<SecurityPublicationReport> UnreliablePublications => [.. Publications.Where(p => !p.IsReliable)];

    /// <summary>Findings across every reliable scan.</summary>
    public int TotalFindings => Scans.Where(s => s.IsReliable).Sum(s => s.Findings);

    /// <summary>Findings at or above a SARIF level across every reliable scan, for a policy gate.</summary>
    public int FindingsAtOrAbove(string level) => Scans.Sum(s => s.FindingsAtOrAbove(level));

    /// <summary>
    /// Every reason this evidence set cannot be relied on: unreliable scans, an absent or empty SBOM,
    /// and publication legs that did not land. Empty when everything that was supposed to happen did.
    /// </summary>
    public IReadOnlyList<string> ReliabilityProblems
    {
        get
        {
            var problems = new List<string>();
            foreach (var scan in UnreliableScans)
                problems.Add($"{scan.Tool}: {StatusTag(scan.Status)} — {scan.Detail ?? scan.Path}");
            if (Sbom is { IsReliable: false } sbom)
                problems.Add($"sbom({sbom.Producer}): {StatusTag(sbom.Status)} — {sbom.Detail ?? sbom.Path}");
            else if (Sbom is { IsEmpty: true } empty)
                problems.Add($"sbom({empty.Producer}): parsed but contains 0 components, so SCA against it is vacuously clean");
            foreach (var publication in UnreliablePublications)
                problems.Add($"{publication.Sink}: {publication.Status.ToString().ToLowerInvariant()} — {publication.Detail ?? "no detail"}");
            return problems;
        }
    }

    /// <summary>The receipt: a positive artifact recording what ran, so a missing one means one thing.</summary>
    public string ToJson()
    {
        var scans = new JsonArray();
        foreach (var scan in Scans)
        {
            var severities = new JsonObject();
            foreach (var (level, count) in scan.FindingsBySeverity.OrderByDescending(kv => SecurityScanReport.SeverityRank(kv.Key)))
                severities[level] = count;
            var node = new JsonObject
            {
                ["tool"] = scan.Tool,
                ["status"] = StatusTag(scan.Status),
                ["path"] = scan.Path,
                ["reliable"] = scan.IsReliable,
            };
            // findings is written only when it is a measurement: a null here is "no result", which is
            // the distinction the whole receipt exists to preserve.
            if (scan.IsReliable)
            {
                node["findings"] = scan.Findings;
                node["findingsBySeverity"] = severities;
            }
            else
            {
                node["findings"] = null;
                node["detail"] = scan.Detail;
            }
            scans.Add(node);
        }

        var publications = new JsonArray();
        foreach (var publication in Publications)
            publications.Add(new JsonObject
            {
                ["sink"] = publication.Sink,
                ["status"] = publication.Status.ToString().ToLowerInvariant(),
                ["reliable"] = publication.IsReliable,
                ["detail"] = publication.Detail,
            });

        JsonObject? sbom = null;
        if (Sbom is { } s)
        {
            var byType = new JsonObject();
            foreach (var (type, count) in s.ComponentsByType.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                byType[type] = count;
            sbom = new JsonObject
            {
                ["producer"] = s.Producer,
                ["status"] = StatusTag(s.Status),
                ["path"] = s.Path,
                ["reliable"] = s.IsReliable,
                ["components"] = s.IsReliable ? s.Components : null,
                ["componentsByType"] = s.IsReliable ? byType : null,
                ["detail"] = s.Detail,
            };
        }

        var problems = new JsonArray();
        // (JsonNode) rather than Add(string): the generic JsonArray.Add<T> builds a
        // JsonValueCustomized<string> on .NET 8, which needs a TypeInfoResolver to serialise and
        // throws without one. The implicit string -> JsonNode conversion builds a primitive value.
        foreach (var problem in ReliabilityProblems) problems.Add((JsonNode)problem);

        var receipt = new JsonObject
        {
            ["schema"] = "tamp.security.receipt/1",
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["sbom"] = sbom,
            ["scans"] = scans,
            ["publications"] = publications,
            ["totalFindings"] = TotalFindings,
            ["reliable"] = ReliabilityProblems.Count == 0,
            ["reliabilityProblems"] = problems,
        };

        return receipt.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The metric tag and receipt spelling for a status: snake_case, stable contract.</summary>
    internal static string StatusTag(SecurityEvidenceStatus status) => status switch
    {
        SecurityEvidenceStatus.NotRun => "not_run",
        SecurityEvidenceStatus.Unreadable => "unreadable",
        SecurityEvidenceStatus.Completed => "completed",
        _ => "unknown",
    };

    /// <summary>A multi-line summary for a build log.</summary>
    public override string ToString()
    {
        var lines = new List<string>();
        if (Sbom is { } sbom) lines.Add("  " + sbom);
        foreach (var scan in Scans) lines.Add("  " + scan);
        foreach (var publication in Publications) lines.Add("  " + publication);
        return string.Join(Environment.NewLine, lines);
    }
}
