using System.Buffers;
using System.Text.Json;

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
    /// <strong>Attribution.</strong> Duplicates are collapsed across runs, so when two tools both
    /// report one finding the merged log keeps it on whichever run came first in input order and the
    /// other tool's run shows nothing for it. The corroboration is <em>not</em> lost: the surviving
    /// result carries <see cref="AlsoReportedByProperty"/> naming every other tool that reported it
    /// (tamp#156). Agreement between two independent scanners is a signal, and a finding both tools
    /// see is worth triaging differently from one only one tool sees.
    /// </para>
    /// <para>
    /// The property is written only when a <em>different</em> tool's copy was dropped. The multi-TFM
    /// Roslyn case — the same tool reporting the same finding once per target framework — adds
    /// nothing, because "roslyn also reported this roslyn finding" is noise.
    /// </para>
    /// <para>
    /// Counts still move: there is one result per finding, not one per reporting tool. Use
    /// <see cref="SarifMerge.Combine"/> when a consumer needs a result per tool rather than a
    /// property listing them.
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

        // Two passes, because corroboration is discovered after the surviving result has been
        // chosen: the second tool to report a finding appears later in the input than the first.
        var owner = new Dictionary<string, (int Run, int Index)>(StringComparer.Ordinal);
        var corroborators = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        for (var runIndex = 0; runIndex < log.Runs.Count; runIndex++)
        {
            var results = log.Runs[runIndex].Results;
            if (results is null) continue;

            for (var index = 0; index < results.Count; index++)
            {
                var key = keySelector(results[index]);
                if (owner.TryAdd(key, (runIndex, index))) continue;

                // A duplicate. Record the tool that also reported it — unless it is the same tool as
                // the survivor's, which is the multi-TFM case this helper exists for: "roslyn also
                // reported this roslyn finding" is noise, not corroboration.
                var ownerTool = ToolName(log.Runs[owner[key].Run]);
                var duplicateTool = ToolName(log.Runs[runIndex]);
                if (string.Equals(ownerTool, duplicateTool, StringComparison.Ordinal)) continue;

                if (!corroborators.TryGetValue(key, out var tools))
                {
                    tools = [];
                    corroborators[key] = tools;
                }
                if (!tools.Contains(duplicateTool, StringComparer.Ordinal)) tools.Add(duplicateTool);
            }
        }

        var rewrittenRuns = new List<SarifRun>(log.Runs.Count);

        for (var runIndex = 0; runIndex < log.Runs.Count; runIndex++)
        {
            var run = log.Runs[runIndex];
            if (run.Results is null)
            {
                rewrittenRuns.Add(run);
                continue;
            }

            var kept = new List<SarifResult>(run.Results.Count);
            for (var index = 0; index < run.Results.Count; index++)
            {
                var result = run.Results[index];
                var key = keySelector(result);
                if (owner[key] != (runIndex, index)) continue;        // a later copy; dropped

                kept.Add(corroborators.TryGetValue(key, out var tools)
                    ? WithAlsoReportedBy(result, tools)
                    : result);
            }

            rewrittenRuns.Add(run with { Results = kept });
        }

        return log with { Runs = rewrittenRuns };
    }

    /// <summary>
    /// The result property naming the other tools that reported a finding, written only when a
    /// <em>different</em> tool's copy was dropped: a JSON array of tool-driver names, in the order
    /// they appeared in the input.
    /// </summary>
    /// <remarks>
    /// This name is public contract once shipped (tamp#156). It exists because agreement between two
    /// independent scanners is a signal, and collapsing duplicates across runs would otherwise
    /// discard it silently — the survivor's run keeps the finding and the other tool's run shows
    /// nothing for it, with no record that it concurred.
    /// </remarks>
    public const string AlsoReportedByProperty = "tamp.alsoReportedBy";

    /// <summary>Copy <paramref name="result"/> with <see cref="AlsoReportedByProperty"/> attached.</summary>
    /// <remarks>
    /// Copies the property bag rather than mutating it: the input log's results are shared with the
    /// caller, and a dedup pass must not edit them in place.
    /// </remarks>
    private static SarifResult WithAlsoReportedBy(SarifResult result, IReadOnlyList<string> tools)
    {
        var properties = result.Properties?.AdditionalProperties is { } existing
            ? new Dictionary<string, JsonElement>(existing, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        properties[AlsoReportedByProperty] = JsonArrayOf(tools);

        return result with
        {
            Properties = (result.Properties ?? new SarifPropertyBag()) with { AdditionalProperties = properties },
        };
    }

    /// <summary>A JSON string array as a <see cref="JsonElement"/>.</summary>
    /// <remarks>
    /// Written with <see cref="Utf8JsonWriter"/> rather than <c>JsonSerializer.SerializeToElement</c>
    /// on purpose: this package is source-generation-only so it stays AOT- and trimming-friendly, and
    /// the reflection-based serialiser throws without a type-info resolver wherever that is enforced.
    /// </remarks>
    private static JsonElement JsonArrayOf(IReadOnlyList<string> values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var value in values) writer.WriteStringValue(value);
            writer.WriteEndArray();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>The run's tool name, with a stable placeholder when it has none.</summary>
    private static string ToolName(SarifRun run)
        => string.IsNullOrEmpty(run.Tool.Driver.Name) ? "(unnamed tool)" : run.Tool.Driver.Name;

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
