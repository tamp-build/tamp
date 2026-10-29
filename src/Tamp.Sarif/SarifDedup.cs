namespace Tamp.Sarif;

/// <summary>
/// Collapse duplicate SARIF results. The dedup key is
/// <c>(ruleId, first-location URI, startLine, startColumn, first-location
/// fullyQualifiedName)</c> — the minimal identity for "this rule fired at this
/// spot." Multi-TFM .NET builds emit the same source-level finding once per TFM
/// via <c>/p:ErrorLog</c>; this helper folds those triplets back to a single
/// entry without losing the tool-of-origin (first occurrence's run is
/// preserved).
/// </summary>
/// <remarks>
/// The fully-qualified logical-location component is what keeps findings that
/// have <em>no</em> file to point at — a finding per advertised MCP tool, say —
/// from all collapsing onto one <c>(ruleId, (no-uri), 0:0)</c> key. For a result
/// with no logical location the component is a constant, so dedup behaviour for
/// physical-location findings (every existing scanner) is unchanged.
/// </remarks>
public static class SarifDedup
{
    /// <summary>
    /// Return a copy of <paramref name="log"/> with duplicate results
    /// collapsed. Each result is kept on the run where it first
    /// appears; later runs lose their copy but the run itself is
    /// preserved (so a consumer counting tool invocations still sees
    /// every tool that ran).
    /// </summary>
    public static SarifLog Distinct(SarifLog log)
    {
        if (log is null) throw new ArgumentNullException(nameof(log));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rewrittenRuns = new List<SarifRun>(log.Runs.Count);

        foreach (var run in log.Runs)
        {
            if (run.Results is null)
            {
                rewrittenRuns.Add(run);
                continue;
            }

            var kept = new List<SarifResult>(run.Results.Count);
            foreach (var result in run.Results)
            {
                if (seen.Add(ComputeKey(result)))
                    kept.Add(result);
            }

            rewrittenRuns.Add(run with { Results = kept });
        }

        return log with { Runs = rewrittenRuns };
    }

    /// <summary>
    /// Compose the dedup key for a result. Exposed as <see langword="internal"/>
    /// so tests can assert key composition directly; not part of the public
    /// surface (callers should compare results, not keys).
    /// </summary>
    internal static string ComputeKey(SarifResult result)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));

        var ruleId = string.IsNullOrEmpty(result.RuleId) ? "(none)" : result.RuleId;

        var firstLocation = result.Locations is { Count: > 0 } ? result.Locations[0] : null;
        var physical = firstLocation?.PhysicalLocation;
        var uri = physical?.ArtifactLocation?.Uri ?? "(no-uri)";
        var line = physical?.Region?.StartLine ?? 0;
        var col = physical?.Region?.StartColumn ?? 0;

        // Logical-location identity for findings with no file to point at. A
        // constant "(no-logical)" when absent, so physical-location findings
        // (every existing scanner) keep the exact key they had before.
        var logical = firstLocation?.LogicalLocations is { Count: > 0 } logicals
            ? logicals[0].FullyQualifiedName ?? logicals[0].Name ?? "(no-logical)"
            : "(no-logical)";

        return $"{ruleId}|{uri}|{line}:{col}|{logical}";
    }
}
