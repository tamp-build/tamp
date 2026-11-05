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
    /// Counter: one point per scan output read, tagged by <c>tool</c> and <c>status</c>
    /// (<c>not_run</c> / <c>unreadable</c> / <c>completed</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the metric that makes a clean scan distinguishable from an absent one.</b>
    /// <see cref="FindingsCount"/> cannot do it: it drops empty buckets, so a scanner that ran and
    /// found nothing emits no point at all — exactly like a scanner that never ran. Reading
    /// <c>findings_count</c> alone, zero and absent are the same observation, and the absent case is
    /// the one that should raise an alarm.
    /// </para>
    /// <para>
    /// With this counter the three states separate: <c>status{status="completed"}</c> with no findings
    /// point is a clean scan, <c>status{status="completed"}</c> with findings points is a scan with
    /// findings, and <c>status{status="not_run"}</c> is no result at all. Alert on the third.
    /// </para>
    /// </remarks>
    public static readonly Counter<long> ScanStatus =
        Meter.CreateCounter<long>(
            "tamp.security.scan.status",
            unit: "{scans}",
            description: "One point per scan output read, tagged by tool and status (not_run / unreadable / completed). "
                       + "Distinguishes a clean scan from an absent one, which findings_count alone cannot.");

    /// <summary>
    /// Counter: one point per SBOM read, tagged by <c>producer</c> and <c>status</c>. The SBOM
    /// equivalent of <see cref="ScanStatus"/>, for the same reason.
    /// </summary>
    public static readonly Counter<long> SbomStatus =
        Meter.CreateCounter<long>(
            "tamp.security.sbom.status",
            unit: "{boms}",
            description: "One point per SBOM read, tagged by producer and status (not_run / unreadable / completed).");

    /// <summary>
    /// Parse <paramref name="sarifPath"/> and emit <see cref="ScanStatus"/> plus
    /// <see cref="FindingsCount"/> for each (tool, severity) bucket. Never throws. Returns the number
    /// of distinct severity buckets found (zero when the scan did not run or could not be parsed).
    /// <para>
    /// A missing or unparseable file no longer passes silently: <see cref="ScanStatus"/> is emitted
    /// with <c>status="not_run"</c> or <c>"unreadable"</c>, so an absent scan is visible in the
    /// metrics instead of looking like a clean one. Prefer <see cref="ReadSarifScan"/> +
    /// <see cref="Emit(SecurityScanReport)"/> when the caller also needs the outcome, such as a
    /// policy gate.
    /// </para>
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
        var report = ReadSarifScan(sarifPath, toolTag, warnWriter);
        Emit(report);
        return report.FindingsBySeverity.Count;
    }

    /// <summary>
    /// Read <paramref name="sarifPath"/> into a <see cref="SecurityScanReport"/> without emitting
    /// anything. This is the reader a policy gate uses: it reports <b>why</b> there is no result
    /// rather than returning a count of zero for a scan that never ran.
    /// </summary>
    /// <remarks>
    /// Never throws. A missing file is <see cref="SecurityEvidenceStatus.NotRun"/>, an unparseable one
    /// is <see cref="SecurityEvidenceStatus.Unreadable"/>, and only a file that parsed yields
    /// <see cref="SecurityEvidenceStatus.Completed"/> — at which point a count of zero is a real
    /// measurement and means the scanner looked and found nothing.
    /// </remarks>
    public static SecurityScanReport ReadSarifScan(AbsolutePath sarifPath, string toolTag, TextWriter? warnWriter = null)
    {
        if (sarifPath is null) throw new ArgumentNullException(nameof(sarifPath));
        if (string.IsNullOrEmpty(toolTag)) throw new ArgumentException("toolTag is required.", nameof(toolTag));

        if (!File.Exists(sarifPath))
            return new SecurityScanReport
            {
                Tool = toolTag,
                Status = SecurityEvidenceStatus.NotRun,
                Path = sarifPath.Value,
                Detail = "no SARIF at this path: the scan was skipped, or the tool died before writing",
            };

        SarifLog log;
        try
        {
            log = SarifReader.LoadFromFile(sarifPath);
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
        {
            (warnWriter ?? Console.Error).WriteLine($"[security.metrics] WARN: could not parse SARIF at '{sarifPath.Value}': {ex.Message} — reporting {toolTag} as unreadable, not as zero findings.");
            return new SecurityScanReport
            {
                Tool = toolTag,
                Status = SecurityEvidenceStatus.Unreadable,
                Path = sarifPath.Value,
                Detail = $"could not parse SARIF: {ex.Message}",
            };
        }

        // Group results across all runs by SARIF level. A parsed log with no results is a real,
        // reliable zero — that is the one case where zero is good news.
        var buckets = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var run in log.Runs)
        {
            if (run.Results is null) continue;
            foreach (var result in run.Results)
            {
                var tag = LevelToTag(result.Level);
                buckets.TryGetValue(tag, out var current);
                buckets[tag] = current + 1;
                total++;
            }
        }

        return new SecurityScanReport
        {
            Tool = toolTag,
            Status = SecurityEvidenceStatus.Completed,
            Path = sarifPath.Value,
            Findings = total,
            FindingsBySeverity = buckets,
        };
    }

    /// <summary>
    /// Read every <c>*.sarif</c> in <paramref name="sarifDirectory"/> as one scan. The Roslyn leg
    /// writes one SARIF per (project, TFM), and for reliability purposes those are a single scan of a
    /// single surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An absent directory, or one containing no SARIF at all, is
    /// <see cref="SecurityEvidenceStatus.NotRun"/> — not a clean scan.
    /// </para>
    /// <para>
    /// If any file in the directory fails to parse the whole scan is
    /// <see cref="SecurityEvidenceStatus.Unreadable"/>, deliberately fail-closed: a partial read of a
    /// multi-file scan has an unknown shortfall, and reporting the files that happened to parse would
    /// understate the surface by an amount nobody can measure.
    /// </para>
    /// <para>
    /// Findings are <b>summed</b> across files rather than deduplicated, which matches what
    /// per-file emission has always reported. A finding present in several target frameworks
    /// therefore counts once per framework.
    /// </para>
    /// </remarks>
    public static SecurityScanReport ReadSarifScanDirectory(AbsolutePath sarifDirectory, string toolTag, TextWriter? warnWriter = null)
    {
        if (sarifDirectory is null) throw new ArgumentNullException(nameof(sarifDirectory));
        if (string.IsNullOrEmpty(toolTag)) throw new ArgumentException("toolTag is required.", nameof(toolTag));

        if (!Directory.Exists(sarifDirectory.Value))
            return new SecurityScanReport
            {
                Tool = toolTag,
                Status = SecurityEvidenceStatus.NotRun,
                Path = sarifDirectory.Value,
                Detail = "no SARIF directory at this path: the scan was skipped, or the tool died before writing",
            };

        var files = Directory.GetFiles(sarifDirectory.Value, "*.sarif", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0)
            return new SecurityScanReport
            {
                Tool = toolTag,
                Status = SecurityEvidenceStatus.NotRun,
                Path = sarifDirectory.Value,
                Detail = "SARIF directory exists but contains no *.sarif: the scan produced nothing",
            };

        var buckets = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        var unreadable = new List<string>();

        foreach (var file in files)
        {
            var report = ReadSarifScan(AbsolutePath.Create(file), toolTag, warnWriter);
            if (!report.IsReliable)
            {
                unreadable.Add(Path.GetFileName(file));
                continue;
            }
            total += report.Findings;
            foreach (var (level, count) in report.FindingsBySeverity)
            {
                buckets.TryGetValue(level, out var current);
                buckets[level] = current + count;
            }
        }

        if (unreadable.Count > 0)
            return new SecurityScanReport
            {
                Tool = toolTag,
                Status = SecurityEvidenceStatus.Unreadable,
                Path = sarifDirectory.Value,
                Detail = $"{unreadable.Count} of {files.Length} SARIF file(s) could not be parsed ({string.Join(", ", unreadable)}), "
                       + "so the findings total would understate the surface by an unknown amount",
            };

        return new SecurityScanReport
        {
            Tool = toolTag,
            Status = SecurityEvidenceStatus.Completed,
            Path = sarifDirectory.Value,
            Findings = total,
            FindingsBySeverity = buckets,
        };
    }

    /// <summary>
    /// Read one <see cref="SecurityScanSource"/>, dispatching on whether it names a file or a
    /// directory of SARIFs.
    /// </summary>
    public static SecurityScanReport ReadScan(SecurityScanSource source, TextWriter? warnWriter = null)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        return source.IsDirectory
            ? ReadSarifScanDirectory(source.Path, source.Tool, warnWriter)
            : ReadSarifScan(source.Path, source.Tool, warnWriter);
    }

    /// <summary>
    /// Emit <see cref="ScanStatus"/> for <paramref name="report"/>, and <see cref="FindingsCount"/>
    /// for each non-empty severity bucket. The status point is emitted for <b>every</b> status, so a
    /// scan that did not run is visible in the metrics rather than absent from them.
    /// </summary>
    public static void Emit(SecurityScanReport report)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));

        ScanStatus.Add(1,
            new KeyValuePair<string, object?>("tool", report.Tool),
            new KeyValuePair<string, object?>("status", SecurityEvidenceSummary.StatusTag(report.Status)));

        if (!report.IsReliable) return;   // no counts from a result that cannot be believed

        foreach (var (level, count) in report.FindingsBySeverity)
        {
            if (count <= 0) continue;
            FindingsCount.Add(count,
                new KeyValuePair<string, object?>("tool", report.Tool),
                new KeyValuePair<string, object?>("severity", level));
        }
    }

    /// <summary>
    /// Parse <paramref name="bomPath"/> and emit <see cref="SbomStatus"/> plus
    /// <see cref="ComponentsCount"/> for each (producer, type) bucket. Never throws. Returns the
    /// number of distinct type buckets found.
    /// <para>
    /// As with <see cref="EmitFindingsFromSarif"/>, a missing or unparseable BOM is now reported
    /// through <see cref="SbomStatus"/> rather than passing silently. Prefer <see cref="ReadBom"/> +
    /// <see cref="Emit(SecuritySbomReport)"/> when the caller needs the outcome.
    /// </para>
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
        var report = ReadBom(bomPath, producerTag, warnWriter);
        Emit(report);
        return report.ComponentsByType.Count;
    }

    /// <summary>
    /// Read <paramref name="bomPath"/> into a <see cref="SecuritySbomReport"/> without emitting
    /// anything. Like <see cref="ReadSarifScan"/>, a missing BOM is reported as
    /// <see cref="SecurityEvidenceStatus.NotRun"/> rather than as zero components.
    /// </summary>
    public static SecuritySbomReport ReadBom(AbsolutePath bomPath, string producerTag, TextWriter? warnWriter = null)
    {
        if (bomPath is null) throw new ArgumentNullException(nameof(bomPath));
        if (string.IsNullOrEmpty(producerTag)) throw new ArgumentException("producerTag is required.", nameof(producerTag));

        if (!File.Exists(bomPath))
            return new SecuritySbomReport
            {
                Producer = producerTag,
                Status = SecurityEvidenceStatus.NotRun,
                Path = bomPath.Value,
                Detail = "no BOM at this path: generation was skipped, or the tool died before writing",
            };

        CycloneDxBom bom;
        try
        {
            bom = SbomReader.LoadFromFile(bomPath);
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
        {
            (warnWriter ?? Console.Error).WriteLine($"[security.metrics] WARN: could not parse BOM at '{bomPath.Value}': {ex.Message} — reporting {producerTag} as unreadable, not as zero components.");
            return new SecuritySbomReport
            {
                Producer = producerTag,
                Status = SecurityEvidenceStatus.Unreadable,
                Path = bomPath.Value,
                Detail = $"could not parse BOM: {ex.Message}",
            };
        }

        var buckets = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var component in bom.Components ?? [])
        {
            var type = string.IsNullOrEmpty(component.Type) ? "library" : component.Type;
            buckets.TryGetValue(type, out var current);
            buckets[type] = current + 1;
            total++;
        }

        return new SecuritySbomReport
        {
            Producer = producerTag,
            Status = SecurityEvidenceStatus.Completed,
            Path = bomPath.Value,
            Components = total,
            ComponentsByType = buckets,
        };
    }

    /// <summary>
    /// Emit <see cref="SbomStatus"/> for <paramref name="report"/>, and <see cref="ComponentsCount"/>
    /// for each non-empty type bucket.
    /// </summary>
    public static void Emit(SecuritySbomReport report)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));

        SbomStatus.Add(1,
            new KeyValuePair<string, object?>("producer", report.Producer),
            new KeyValuePair<string, object?>("status", SecurityEvidenceSummary.StatusTag(report.Status)));

        if (!report.IsReliable) return;

        foreach (var (type, count) in report.ComponentsByType)
        {
            if (count <= 0) continue;
            ComponentsCount.Add(count,
                new KeyValuePair<string, object?>("producer", report.Producer),
                new KeyValuePair<string, object?>("type", type));
        }
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
