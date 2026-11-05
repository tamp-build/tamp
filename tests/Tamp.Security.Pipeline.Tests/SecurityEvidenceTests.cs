using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tamp.Sarif;
using Tamp.Sbom;
using Tamp.Security.Pipeline;
using Xunit;

namespace Tamp.Security.Pipeline.Tests;

/// <summary>
/// #151 — a skipped scan reported as a clean scan, and enforcement entangled with publication.
/// </summary>
/// <remarks>
/// The whole point of this file is one distinction: <b>"the scanner found nothing" and "there is no
/// scanner result" are different facts.</b> Before #151 both were a count of zero, so an unknown
/// outcome was reported as a good one — the failure direction a security control must never take.
/// </remarks>
public sealed class SecurityEvidenceTests : IDisposable
{
    private readonly string _tempRoot;

    public SecurityEvidenceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "tamp-sec-evidence-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
    }

    private AbsolutePath TempPath(string name) => AbsolutePath.Create(Path.Combine(_tempRoot, name));

    private AbsolutePath WriteSarif(string name, params SarifResult[] results)
    {
        var path = TempPath(name);
        SarifWriter.WriteToFile(new SarifLog { Runs = [new SarifRun { Tool = new(), Results = results }] }, path);
        return path;
    }

    private static SarifResult Finding(SarifLevel level) =>
        new() { RuleId = "X1", Level = level, Message = new SarifMessage { Text = "finding" } };

    // ─── the central distinction ──────────────────────────────────────────

    [Fact]
    public void A_Missing_Scan_Is_Not_Run_Rather_Than_Zero_Findings()
    {
        var report = SecurityPipelineMetrics.ReadSarifScan(TempPath("never-written.sarif"), "opengrep");

        Assert.Equal(SecurityEvidenceStatus.NotRun, report.Status);
        Assert.False(report.IsReliable);
        // The assertion that matters: this is NOT good news.
        Assert.False(report.IsClean);
        Assert.Contains("NOT RUN", report.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("0 findings", report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_Scan_That_Ran_And_Found_Nothing_Is_Clean()
    {
        var report = SecurityPipelineMetrics.ReadSarifScan(WriteSarif("empty.sarif"), "opengrep");

        Assert.Equal(SecurityEvidenceStatus.Completed, report.Status);
        Assert.True(report.IsReliable);
        Assert.True(report.IsClean);
        Assert.Equal(0, report.Findings);
        Assert.Contains("clean", report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_Two_Cases_That_Used_To_Be_Identical_Now_Differ_In_Every_Observable_Way()
    {
        var absent = SecurityPipelineMetrics.ReadSarifScan(TempPath("absent.sarif"), "opengrep");
        var clean = SecurityPipelineMetrics.ReadSarifScan(WriteSarif("ran.sarif"), "opengrep");

        // Both have Findings == 0 — which is exactly why a count was never enough to tell them apart.
        Assert.Equal(absent.Findings, clean.Findings);
        Assert.NotEqual(absent.Status, clean.Status);
        Assert.NotEqual(absent.IsReliable, clean.IsReliable);
        Assert.NotEqual(absent.IsClean, clean.IsClean);
        Assert.NotEqual(absent.ToString(), clean.ToString());
    }

    [Fact]
    public void An_Unparseable_Scan_Is_Unreadable_Rather_Than_Zero_Findings()
    {
        var path = TempPath("garbage.sarif");
        File.WriteAllText(path.Value, "{ this is not sarif");

        var report = SecurityPipelineMetrics.ReadSarifScan(path, "opengrep", TextWriter.Null);

        Assert.Equal(SecurityEvidenceStatus.Unreadable, report.Status);
        Assert.False(report.IsClean);
        Assert.Contains("could not parse", report.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void Findings_Are_Counted_Per_Severity()
    {
        var path = WriteSarif("mixed.sarif",
            Finding(SarifLevel.Error), Finding(SarifLevel.Error),
            Finding(SarifLevel.Warning), Finding(SarifLevel.Note));

        var report = SecurityPipelineMetrics.ReadSarifScan(path, "opengrep");

        Assert.Equal(4, report.Findings);
        Assert.Equal(2, report.FindingsBySeverity["error"]);
        Assert.Equal(1, report.FindingsBySeverity["warning"]);
        Assert.Equal(3, report.FindingsAtOrAbove("warning"));
        Assert.Equal(2, report.FindingsAtOrAbove("error"));
        Assert.Equal(4, report.FindingsAtOrAbove("note"));
    }

    [Fact]
    public void An_Unreliable_Report_Contributes_No_Findings_To_A_Gate()
    {
        // A gate asking "how many errors?" must not be told "zero" by a scan that never ran. The
        // count is zero here, but ReliabilityProblems is what the gate actually fails on.
        var absent = SecurityPipelineMetrics.ReadSarifScan(TempPath("absent.sarif"), "trivy");

        Assert.Equal(0, absent.FindingsAtOrAbove("error"));
        var summary = new SecurityEvidenceSummary { Scans = [absent] };
        Assert.Single(summary.ReliabilityProblems);
    }

    // ─── multi-file scans (the Roslyn leg) ────────────────────────────────

    [Fact]
    public void A_Missing_Scan_Directory_Is_Not_Run()
    {
        var report = SecurityPipelineMetrics.ReadSarifScanDirectory(TempPath("no-such-dir"), "roslyn");

        Assert.Equal(SecurityEvidenceStatus.NotRun, report.Status);
        Assert.False(report.IsClean);
    }

    [Fact]
    public void An_Empty_Scan_Directory_Is_Not_Run_Not_Clean()
    {
        // The directory is created by the target before the tool runs, so "exists but holds nothing"
        // means the tool produced nothing — not that it found nothing.
        var dir = TempPath("roslyn-empty");
        Directory.CreateDirectory(dir.Value);

        var report = SecurityPipelineMetrics.ReadSarifScanDirectory(dir, "roslyn");

        Assert.Equal(SecurityEvidenceStatus.NotRun, report.Status);
        Assert.Contains("contains no *.sarif", report.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void Findings_Are_Summed_Across_Every_Sarif_In_The_Directory()
    {
        var dir = TempPath("roslyn");
        Directory.CreateDirectory(dir.Value);
        SarifWriter.WriteToFile(
            new SarifLog { Runs = [new SarifRun { Tool = new(), Results = [Finding(SarifLevel.Error)] }] },
            AbsolutePath.Create(Path.Combine(dir.Value, "a.net8.0.sarif")));
        SarifWriter.WriteToFile(
            new SarifLog { Runs = [new SarifRun { Tool = new(), Results = [Finding(SarifLevel.Error), Finding(SarifLevel.Warning)] }] },
            AbsolutePath.Create(Path.Combine(dir.Value, "a.net9.0.sarif")));

        var report = SecurityPipelineMetrics.ReadSarifScanDirectory(dir, "roslyn");

        Assert.Equal(SecurityEvidenceStatus.Completed, report.Status);
        Assert.Equal(3, report.Findings);
        Assert.Equal(2, report.FindingsBySeverity["error"]);
    }

    [Fact]
    public void One_Corrupt_File_Makes_The_Whole_Multi_File_Scan_Unreadable()
    {
        // Fail-closed on purpose: a partial read of a multi-file scan understates the surface by an
        // amount nobody can measure, and reporting the files that happened to parse would look like
        // a smaller but complete result.
        var dir = TempPath("roslyn-partial");
        Directory.CreateDirectory(dir.Value);
        SarifWriter.WriteToFile(
            new SarifLog { Runs = [new SarifRun { Tool = new(), Results = [Finding(SarifLevel.Error)] }] },
            AbsolutePath.Create(Path.Combine(dir.Value, "good.sarif")));
        File.WriteAllText(Path.Combine(dir.Value, "truncated.sarif"), "{ \"runs\": [");

        var report = SecurityPipelineMetrics.ReadSarifScanDirectory(dir, "roslyn", TextWriter.Null);

        Assert.Equal(SecurityEvidenceStatus.Unreadable, report.Status);
        Assert.Contains("truncated.sarif", report.Detail!, StringComparison.Ordinal);
        Assert.Contains("understate", report.Detail!, StringComparison.Ordinal);
    }

    // ─── the SBOM ─────────────────────────────────────────────────────────

    [Fact]
    public void A_Missing_Bom_Is_Not_Run_Rather_Than_Zero_Components()
    {
        var report = SecurityPipelineMetrics.ReadBom(TempPath("absent.cdx.json"), "cyclonedx");

        Assert.Equal(SecurityEvidenceStatus.NotRun, report.Status);
        Assert.False(report.IsReliable);
    }

    [Fact]
    public void An_Sbom_With_No_Components_Is_Empty_Which_Is_Not_Good_News()
    {
        // Every SCA result derived from a component-less SBOM is vacuously clean, so this is a
        // reliability problem rather than a scan of a project that happens to have no dependencies.
        var path = TempPath("empty.cdx.json");
        SbomWriter.WriteToFile(new CycloneDxBom { Components = [] }, path);

        var report = SecurityPipelineMetrics.ReadBom(path, "cyclonedx");

        Assert.Equal(SecurityEvidenceStatus.Completed, report.Status);
        Assert.True(report.IsEmpty);
        Assert.Contains("vacuously clean", report.ToString(), StringComparison.Ordinal);

        var summary = new SecurityEvidenceSummary { Sbom = report };
        Assert.Contains(summary.ReliabilityProblems, p => p.Contains("0 components", StringComparison.Ordinal));
    }

    // ─── the status metric: the signal that absence used to lack ──────────

    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly object _gate = new();
        private readonly List<(string Instrument, long Value, Dictionary<string, object?> Tags)> _records = [];

        public Recorder()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == "Tamp.Security.Pipeline") l.EnableMeasurementEvents(instrument);
                },
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var kv in tags) dict[kv.Key] = kv.Value;
                lock (_gate) _records.Add((instrument.Name, value, dict));
            });
            _listener.Start();
        }

        /// <summary>
        /// Measurements for one instrument, optionally narrowed to a single tool or producer.
        /// </summary>
        /// <remarks>
        /// The meter is process-wide and xUnit runs test classes in parallel, so an unfiltered read
        /// also sees measurements from other classes' tests. Every assertion here narrows by a tag
        /// value unique to its own test.
        /// </remarks>
        public IReadOnlyList<(string Instrument, long Value, Dictionary<string, object?> Tags)> For(
            string instrument, string? tagKey = null, string? tagValue = null)
        {
            lock (_gate)
                return _records
                    .Where(r => r.Instrument == instrument)
                    .Where(r => tagKey is null || (r.Tags.TryGetValue(tagKey, out var v) && Equals(v, tagValue)))
                    .ToList();
        }

        public void Dispose() => _listener.Dispose();
    }

    [Theory]
    [InlineData("not_run")]
    [InlineData("unreadable")]
    [InlineData("completed")]
    public void Every_Status_Emits_A_Status_Point(string expected)
    {
        using var recorder = new Recorder();
        var tool = $"status-probe-{expected}";
        var path = expected switch
        {
            "not_run" => TempPath("absent.sarif"),
            "unreadable" => WriteGarbage("bad.sarif"),
            _ => WriteSarif("ok.sarif"),
        };

        SecurityPipelineMetrics.Emit(SecurityPipelineMetrics.ReadSarifScan(path, tool, TextWriter.Null));

        var points = recorder.For("tamp.security.scan.status", "tool", tool);
        Assert.Single(points);
        Assert.Equal(1L, points[0].Value);
        Assert.Equal(expected, points[0].Tags["status"]);
    }

    [Fact]
    public void A_Skipped_Scan_Is_Now_Visible_In_The_Metrics_Instead_Of_Absent_From_Them()
    {
        using var recorder = new Recorder();

        // The legacy entry point, unchanged signature — existing callers get the fix for free.
        var buckets = SecurityPipelineMetrics.EmitFindingsFromSarif(TempPath("absent.sarif"), "legacy-probe");

        Assert.Equal(0, buckets);
        // findings_count still emits nothing, which is why it could never carry this signal...
        Assert.Empty(recorder.For("tamp.security.scan.findings_count", "tool", "legacy-probe"));
        // ...and status now does.
        Assert.Equal("not_run", Assert.Single(recorder.For("tamp.security.scan.status", "tool", "legacy-probe")).Tags["status"]);
    }

    [Fact]
    public void An_Unreliable_Scan_Emits_No_Findings_Counts()
    {
        using var recorder = new Recorder();

        SecurityPipelineMetrics.Emit(new SecurityScanReport
        {
            Tool = "unreliable-probe",
            Status = SecurityEvidenceStatus.Unreadable,
            Path = "x.sarif",
            Findings = 7,                                     // nonsense from an unreliable read
            FindingsBySeverity = new Dictionary<string, int> { ["error"] = 7 },
        });

        // Counts from a result that cannot be believed would be worse than no counts.
        Assert.Empty(recorder.For("tamp.security.scan.findings_count", "tool", "unreliable-probe"));
        Assert.Single(recorder.For("tamp.security.scan.status", "tool", "unreliable-probe"));
    }

    [Fact]
    public void The_Sbom_Gets_Its_Own_Status_Metric()
    {
        using var recorder = new Recorder();

        SecurityPipelineMetrics.Emit(SecurityPipelineMetrics.ReadBom(TempPath("absent.cdx.json"), "sbom-probe"));

        var point = Assert.Single(recorder.For("tamp.security.sbom.status", "producer", "sbom-probe"));
        Assert.Equal("sbom-probe", point.Tags["producer"]);
        Assert.Equal("not_run", point.Tags["status"]);
    }

    // ─── publication outcomes ─────────────────────────────────────────────

    [Theory]
    [InlineData(SecurityPublicationStatus.NotConfigured, true)]   // deliberately unset — a green outcome
    [InlineData(SecurityPublicationStatus.Completed, true)]
    [InlineData(SecurityPublicationStatus.Incomplete, false)]     // the unsettled DT analysis
    [InlineData(SecurityPublicationStatus.Failed, false)]
    public void Only_An_Attempted_And_Finished_Publication_Is_Reliable(SecurityPublicationStatus status, bool reliable)
        => Assert.Equal(reliable, new SecurityPublicationReport { Sink = "dependency-track", Status = status }.IsReliable);

    [Fact]
    public void An_Unsettled_Publication_Is_A_Reliability_Problem()
    {
        var summary = new SecurityEvidenceSummary
        {
            Publications =
            [
                new() { Sink = "dependency-track", Status = SecurityPublicationStatus.Incomplete, Detail = "did not settle" },
                new() { Sink = "defectdojo", Status = SecurityPublicationStatus.NotConfigured },
            ],
        };

        // Before #151 an unsettled analysis and a completed one both produced a green SecurityPush.
        Assert.Single(summary.UnreliablePublications);
        Assert.Contains(summary.ReliabilityProblems, p => p.Contains("dependency-track", StringComparison.Ordinal));
        Assert.DoesNotContain(summary.ReliabilityProblems, p => p.Contains("defectdojo", StringComparison.Ordinal));
    }

    // ─── the receipt ──────────────────────────────────────────────────────

    [Fact]
    public void The_Receipt_Writes_Null_Findings_For_A_Scan_That_Did_Not_Run()
    {
        var summary = new SecurityEvidenceSummary
        {
            Scans =
            [
                SecurityPipelineMetrics.ReadSarifScan(WriteSarif("ran.sarif", Finding(SarifLevel.Error)), "opengrep"),
                SecurityPipelineMetrics.ReadSarifScan(TempPath("absent.sarif"), "trivy"),
            ],
        };

        var receipt = JsonNode.Parse(summary.ToJson())!;
        var scans = (receipt["scans"] as JsonArray)!;

        Assert.Equal("tamp.security.receipt/1", (string?)receipt["schema"]);
        Assert.Equal(1, (int?)scans[0]!["findings"]);
        Assert.Equal("completed", (string?)scans[0]!["status"]);
        // null, not 0: a reader cannot mistake "no result" for "found nothing".
        Assert.Null((int?)scans[1]!["findings"]);
        Assert.Equal("not_run", (string?)scans[1]!["status"]);
        Assert.False((bool?)receipt["reliable"]);
        Assert.Single((receipt["reliabilityProblems"] as JsonArray)!);
    }

    [Fact]
    public void A_Complete_Receipt_Says_So()
    {
        var summary = new SecurityEvidenceSummary
        {
            Sbom = SecurityPipelineMetrics.ReadBom(WriteBomWith("ok.cdx.json", 2), "cyclonedx"),
            Scans = [SecurityPipelineMetrics.ReadSarifScan(WriteSarif("clean.sarif"), "opengrep")],
            Publications = [new() { Sink = "defectdojo", Status = SecurityPublicationStatus.NotConfigured }],
        };

        var receipt = JsonNode.Parse(summary.ToJson())!;

        Assert.True((bool?)receipt["reliable"]);
        Assert.Empty((receipt["reliabilityProblems"] as JsonArray)!);
        Assert.Equal(0, (int?)receipt["totalFindings"]);
        Assert.Equal(2, (int?)receipt["sbom"]!["components"]);
    }

    [Fact]
    public void Total_Findings_Counts_Only_Reliable_Scans()
    {
        var summary = new SecurityEvidenceSummary
        {
            Scans =
            [
                SecurityPipelineMetrics.ReadSarifScan(WriteSarif("a.sarif", Finding(SarifLevel.Error)), "opengrep"),
                SecurityPipelineMetrics.ReadSarifScan(TempPath("absent.sarif"), "trivy"),
            ],
        };

        Assert.Equal(1, summary.TotalFindings);
        Assert.Single(summary.UnreliableScans);
    }

    private AbsolutePath WriteGarbage(string name)
    {
        var path = TempPath(name);
        File.WriteAllText(path.Value, "{ not sarif");
        return path;
    }

    private AbsolutePath WriteBomWith(string name, int components)
    {
        var path = TempPath(name);
        SbomWriter.WriteToFile(
            new CycloneDxBom
            {
                Components = [.. Enumerable.Range(0, components).Select(i => new CycloneDxComponent { Name = $"pkg{i}", Type = "library" })],
            },
            path);
        return path;
    }
}
