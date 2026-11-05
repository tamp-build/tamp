namespace Tamp.Sarif;

/// <summary>
/// Collapse duplicate SARIF results. The default key is
/// <c>(ruleId, first-location URI, startLine, startColumn, first-location
/// fullyQualifiedName)</c> — the minimal identity for "this rule fired at this
/// spot." Multi-TFM .NET builds emit the same source-level finding once per TFM
/// via <c>/p:ErrorLog</c>; this helper folds those triplets back to a single
/// entry without losing the tool-of-origin (first occurrence's run is
/// preserved).
/// </summary>
/// <remarks>
/// <para>
/// The fully-qualified logical-location component is what keeps findings that
/// have <em>no</em> file to point at — a finding per advertised MCP tool, say —
/// from all collapsing onto one <c>(ruleId, (no-uri), 0:0)</c> key. For a result
/// with no logical location the component is a constant, so dedup behaviour for
/// findings that only carry a physical location is unchanged.
/// </para>
/// <para>
/// <strong>When the default key is enough, and when it is not.</strong> "This rule fired at this
/// spot" is the identity of a <em>code</em> finding. It is not the identity of a <em>package</em>
/// finding, where the identity is the affected component and version and the only location available
/// is the manifest or lockfile. The default key is sufficient exactly when the producer puts
/// component identity in <c>logicalLocations.fullyQualifiedName</c> — that slot is the one part of
/// the key able to carry it. It is <strong>not</strong> sufficient when the producer puts the
/// <em>path</em> there, or omits logical locations entirely.
/// </para>
/// <para>
/// Measured on grype 0.112.0's native SARIF, where every result shares a lockfile URI at
/// <c>1:1</c> and the logical <c>fullyQualifiedName</c> is the lockfile path rather than the
/// component: 299 results collapse to <strong>268</strong>. Two results for <c>vite 5.4.21</c> and
/// <c>vite 7.3.1</c> under one advisory match on all four components — the version appears only in
/// the message text — so one of the two is dropped. The same document emitted by
/// <c>Tamp.Grype</c> 0.2.0, which puts the purl in <c>fullyQualifiedName</c>, passes through
/// 299 → 299. See <see href="https://github.com/tamp-build/tamp-grype/issues/6">tamp-grype#6</see>
/// for the worked example and
/// <see href="https://github.com/tamp-build/tamp/issues/155">tamp#155</see> for this measurement.
/// </para>
/// <para>
/// For a producer whose output does not satisfy that condition, pass a key selector to
/// <see cref="Distinct(SarifLog, Func{SarifResult, string})"/> rather than choosing between full
/// dedup and none.
/// </para>
/// <para>
/// <strong>Why the producer's own fingerprints are not the default key.</strong> SARIF nominates
/// <c>fingerprints</c> / <c>partialFingerprints</c> as a result's identity, so keying on them looks
/// like the principled choice. It is not safe as a default: a producer's fingerprint is only as good
/// as the producer. grype 0.112.0's <c>primaryLocationLineHash</c> identifies the package-version
/// rather than the finding — 35 distinct advisories against one package version share one
/// fingerprint — so the same 299 results carry just <strong>62</strong> distinct fingerprints.
/// Preferring them would turn a 10% collapse into a 79% one. A caller who trusts their producer's
/// fingerprints can select on them explicitly.
/// </para>
/// </remarks>
public static class SarifDedup
{
    /// <summary>
    /// Return a copy of <paramref name="log"/> with duplicate results
    /// collapsed under <see cref="DefaultKey"/>. Each result is kept on the run
    /// where it first appears; later runs lose their copy but the run itself is
    /// preserved (so a consumer counting tool invocations still sees
    /// every tool that ran).
    /// </summary>
    /// <remarks>
    /// Read the class remarks before using this on package-level findings: the default key has no
    /// component or version term of its own, and relies on the producer having put component
    /// identity in <c>logicalLocations.fullyQualifiedName</c>.
    /// <para>
    /// <strong>Attribution.</strong> The <c>seen</c> set spans runs, so when two tools both report
    /// one finding the merged log keeps it on whichever run came first in input order and shows the
    /// second tool reporting nothing for it. Every run survives, so "which tools ran" stays
    /// answerable; "which tools found this" does not. For deliberately overlapping scanners that
    /// corroboration is often the point, so use <see cref="SarifMerge.Combine"/> when it matters.
    /// </para>
    /// </remarks>
    public static SarifLog Distinct(SarifLog log) => Distinct(log, DefaultKey);

    /// <summary>
    /// <see cref="Distinct(SarifLog)"/> with a caller-supplied identity. Use this when the default
    /// key cannot express what makes two results distinct — most often a package finding, where the
    /// affected version is in the message or the property bag and neither is part of the default key.
    /// </summary>
    /// <param name="log">The log to collapse.</param>
    /// <param name="keySelector">
    /// Identity for a result. Two results with equal keys are duplicates; the first wins. Compose
    /// onto <see cref="DefaultKey"/> rather than replacing it when the goal is "the default, plus
    /// component identity":
    /// <code>
    /// static string PackageAware(SarifResult r)
    /// {
    ///     var purl = r.Properties?.AdditionalProperties is { } p
    ///         &amp;&amp; p.TryGetValue("grype.purl", out var v) ? v.GetString() : null;
    ///     return $"{SarifDedup.DefaultKey(r)}|{purl ?? "(no-purl)"}";
    /// }
    ///
    /// var merged = SarifMerge.CombineDistinct(logs, PackageAware);
    /// </code>
    /// Where the property lives is producer-specific, which is why this is a seam rather than a
    /// built-in "package key" that would have to guess.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> or <paramref name="keySelector"/> is null.</exception>
    public static SarifLog Distinct(SarifLog log, Func<SarifResult, string> keySelector)
    {
        if (log is null) throw new ArgumentNullException(nameof(log));
        if (keySelector is null) throw new ArgumentNullException(nameof(keySelector));

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
                if (seen.Add(keySelector(result)))
                    kept.Add(result);
            }

            rewrittenRuns.Add(run with { Results = kept });
        }

        return log with { Runs = rewrittenRuns };
    }

    /// <summary>
    /// The default dedup key:
    /// <c>{ruleId}|{uri}|{line}:{col}|{fullyQualifiedName}</c>, with a placeholder for each absent
    /// component.
    /// </summary>
    /// <remarks>
    /// Public so a caller can compose onto it instead of reimplementing it — see
    /// <see cref="Distinct(SarifLog, Func{SarifResult, string})"/>. The exact string is an
    /// implementation detail: compare keys to each other, never parse one or persist one.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static string DefaultKey(SarifResult result)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));

        var ruleId = string.IsNullOrEmpty(result.RuleId) ? "(none)" : result.RuleId;

        var firstLocation = result.Locations is { Count: > 0 } ? result.Locations[0] : null;
        var physical = firstLocation?.PhysicalLocation;
        var uri = physical?.ArtifactLocation?.Uri ?? "(no-uri)";
        var line = physical?.Region?.StartLine ?? 0;
        var col = physical?.Region?.StartColumn ?? 0;

        // Logical-location identity for findings with no file to point at, and the one slot in this
        // key able to carry component identity for a package finding. A constant "(no-logical)" when
        // absent, so findings that only carry a physical location keep the exact key they had.
        var logical = firstLocation?.LogicalLocations is { Count: > 0 } logicals
            ? logicals[0].FullyQualifiedName ?? logicals[0].Name ?? "(no-logical)"
            : "(no-logical)";

        return $"{ruleId}|{uri}|{line}:{col}|{logical}";
    }
}
