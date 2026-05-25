using System.Diagnostics.Metrics;
using Tamp.Sarif;
using Tamp.Sbom;

namespace Tamp.Security.Pipeline;

/// <summary>
/// Domain-specific OTel metrics for the Tamp security pipeline. Layered on top of
/// the framework-level <c>Tamp.Build</c> meter (ADR-0018) — this meter is
/// security-domain only and keeps <c>Tamp.Core</c> tool-agnostic.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Meter name.</strong> <c>Tamp.Security.Pipeline</c>. Consumers subscribe
/// to this name explicitly OR use the wildcard <c>Tamp.*</c> form their OTel
/// MeterProvider already covers.
/// </para>
/// <para>
/// <strong>Stability contract.</strong> Metric names + tag keys are public contract
/// once shipped. Changes are breaking and require coordination with consumers
/// (tamp-beacon dashboards, tamp.findings counter rings, custom OTel pipelines).
/// </para>
/// <para>
/// <strong>Counter semantics.</strong> Each emit adds the count of items observed
/// in a single scan output, tagged by source. Re-running a scan on the same build
/// emits again — adopters who want "per-build totals" rather than "all-time" should
/// configure their OTel pipeline's aggregation accordingly.
/// </para>
/// </remarks>
public static class SecurityPipelineMetrics
{
    /// <summary>The Tamp.Security.Pipeline meter version, derived from the assembly.</summary>
    private static readonly string Version =
        typeof(SecurityPipelineMetrics).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Shared meter for security-pipeline metrics.</summary>
    public static readonly Meter Meter = new("Tamp.Security.Pipeline", Version);

    /// <summary>
    /// Counter: findings emitted by a SAST / SCA / secret scan, bucketed by
    /// <c>tool</c> (opengrep / roslyn / trivy / osvscanner / axecore / eslint / ...)
    /// and <c>severity</c> (none / note / warning / error per the SARIF level vocabulary).
    /// Each call to <see cref="EmitFindingsFromSarif"/> reads a SARIF file once and
    /// increments per (tool, severity) bucket by that bucket's count.
    /// </summary>
    public static readonly Counter<long> FindingsCount =
        Meter.CreateCounter<long>(
            "tamp.security.scan.findings_count",
            unit: "{findings}",
            description: "Findings emitted by a security scan, tagged by tool and severity.");

    /// <summary>
    /// Counter: components observed in a generated SBOM, bucketed by
    /// <c>producer</c> (cyclonedx / syft / ...) and <c>type</c> (library / application /
    /// framework / container / operating-system / device / file / firmware / data /
    /// machine-learning-model / cryptographic-asset / platform / device-driver per
    /// the CycloneDX component-type vocabulary). Each call to
    /// <see cref="EmitComponentsFromBom"/> reads a BOM once and increments per
    /// (producer, type) bucket by that bucket's count.
    /// </summary>
    public static readonly Counter<long> ComponentsCount =
        Meter.CreateCounter<long>(
            "tamp.security.sbom.components_count",
            unit: "{components}",
            description: "Components observed in a generated SBOM, tagged by producer and component type.");

    /// <summary>
    /// Parse <paramref name="sarifPath"/> and emit <see cref="FindingsCount"/> for each
    /// (tool, severity) bucket. Missing or unparseable files are skipped with a
    /// stderr warning — never throws. Returns the number of distinct buckets emitted
    /// (zero on skip).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="toolTag"/> is the canonical tool identifier (e.g. <c>"opengrep"</c>,
    /// <c>"roslyn"</c>, <c>"trivy"</c>, <c>"osvscanner"</c>). Mapping to the
    /// scanner-kind vocabulary used by downstream sinks (tamp.findings'
    /// <c>tamp-ingest-v1</c> <c>ScannerKind</c> enum) is the consumer's job — Tamp
    /// just passes the tag value through.
    /// </para>
    /// <para>
    /// Severity tag values are <c>"none"</c>, <c>"note"</c>, <c>"warning"</c>, <c>"error"</c>
    /// matching the SARIF 2.1.0 <c>level</c> field. Buckets with zero results
    /// don't emit (no need to mint a (tool, severity) point with value 0).
    /// </para>
    /// </remarks>
    public static int EmitFindingsFromSarif(AbsolutePath sarifPath, string toolTag, TextWriter? warnWriter = null)
    {
        if (sarifPath is null) throw new ArgumentNullException(nameof(sarifPath));
        if (string.IsNullOrEmpty(toolTag)) throw new ArgumentException("toolTag is required.", nameof(toolTag));

        if (!File.Exists(sarifPath))
        {
            // Silent on missing — adopters with scan targets that get skipped (e.g. opengrep not installed)
            // shouldn't see a warning every build. The scan target itself logs why it skipped.
            return 0;
        }

        SarifLog log;
        try
        {
            log = SarifReader.LoadFromFile(sarifPath);
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
        {
            (warnWriter ?? Console.Error).WriteLine($"[security.metrics] WARN: could not parse SARIF at '{sarifPath.Value}': {ex.Message} — skipping findings_count emission for {toolTag}.");
            return 0;
        }

        // Group results across all runs by SARIF level. Empty buckets are dropped.
        var buckets = new Dictionary<SarifLevel, long>();
        foreach (var run in log.Runs)
        {
            if (run.Results is null) continue;
            foreach (var result in run.Results)
            {
                buckets.TryGetValue(result.Level, out var current);
                buckets[result.Level] = current + 1;
            }
        }

        foreach (var (level, count) in buckets)
        {
            if (count <= 0) continue;
            FindingsCount.Add(count,
                new KeyValuePair<string, object?>("tool", toolTag),
                new KeyValuePair<string, object?>("severity", LevelToTag(level)));
        }

        return buckets.Count;
    }

    /// <summary>
    /// Parse <paramref name="bomPath"/> and emit <see cref="ComponentsCount"/> for each
    /// (producer, type) bucket. Missing or unparseable files are skipped with a
    /// stderr warning — never throws. Returns the number of distinct buckets emitted.
    /// </summary>
    /// <remarks>
    /// <paramref name="producerTag"/> is the canonical generator identifier (e.g.
    /// <c>"cyclonedx"</c> for dotnet-CycloneDX, <c>"syft"</c> for Anchore syft).
    /// Per-component <c>type</c> values come from the CycloneDX 1.6 vocabulary
    /// (<c>library</c>, <c>application</c>, <c>framework</c>, <c>container</c>,
    /// <c>operating-system</c>, <c>device</c>, <c>device-driver</c>, <c>firmware</c>,
    /// <c>file</c>, <c>machine-learning-model</c>, <c>data</c>, <c>cryptographic-asset</c>,
    /// <c>platform</c>). Unknown types pass through verbatim — the spec lets producers
    /// emit custom values and consumers should be liberal here.
    /// </remarks>
    public static int EmitComponentsFromBom(AbsolutePath bomPath, string producerTag, TextWriter? warnWriter = null)
    {
        if (bomPath is null) throw new ArgumentNullException(nameof(bomPath));
        if (string.IsNullOrEmpty(producerTag)) throw new ArgumentException("producerTag is required.", nameof(producerTag));

        if (!File.Exists(bomPath))
        {
            return 0;
        }

        CycloneDxBom bom;
        try
        {
            bom = SbomReader.LoadFromFile(bomPath);
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
        {
            (warnWriter ?? Console.Error).WriteLine($"[security.metrics] WARN: could not parse BOM at '{bomPath.Value}': {ex.Message} — skipping components_count emission for {producerTag}.");
            return 0;
        }

        if (bom.Components is null) return 0;

        var buckets = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var component in bom.Components)
        {
            var type = string.IsNullOrEmpty(component.Type) ? "library" : component.Type;
            buckets.TryGetValue(type, out var current);
            buckets[type] = current + 1;
        }

        foreach (var (type, count) in buckets)
        {
            if (count <= 0) continue;
            ComponentsCount.Add(count,
                new KeyValuePair<string, object?>("producer", producerTag),
                new KeyValuePair<string, object?>("type", type));
        }

        return buckets.Count;
    }

    private static string LevelToTag(SarifLevel level) => level switch
    {
        SarifLevel.None => "none",
        SarifLevel.Note => "note",
        SarifLevel.Warning => "warning",
        SarifLevel.Error => "error",
        _ => "unknown",
    };
}
