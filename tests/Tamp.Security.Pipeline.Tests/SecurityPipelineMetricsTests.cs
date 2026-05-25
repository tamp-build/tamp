using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using Tamp.Sarif;
using Tamp.Sbom;
using Tamp.Security.Pipeline;
using Xunit;

namespace Tamp.Security.Pipeline.Tests;

/// <summary>
/// Tests <see cref="SecurityPipelineMetrics"/> by attaching a <see cref="MeterListener"/>
/// to the Tamp.Security.Pipeline meter, exercising the emit helpers against synthetic
/// SARIF / BOM files, and asserting on the captured measurement records.
/// </summary>
public sealed class SecurityPipelineMetricsTests : IDisposable
{
    private readonly string _tempRoot;

    public SecurityPipelineMetricsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "tamp-sec-metrics-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
    }

    private AbsolutePath TempPath(string name) => AbsolutePath.Create(Path.Combine(_tempRoot, name));

    // ============== Recording listener ==============

    private sealed record Measurement(string InstrumentName, long Value, IReadOnlyDictionary<string, object?> Tags);

    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener;
        public List<Measurement> Records { get; } = new();
        private readonly object _gate = new();

        public Recorder()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == "Tamp.Security.Pipeline")
                        l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(OnLong);
            _listener.Start();
        }

        private void OnLong(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kv in tags) dict[kv.Key] = kv.Value;
            lock (_gate) Records.Add(new Measurement(instrument.Name, value, dict));
        }

        public IReadOnlyList<Measurement> ForInstrument(string name)
        {
            lock (_gate) return Records.Where(m => m.InstrumentName == name).ToList();
        }

        public void Dispose() => _listener.Dispose();
    }

    // ============== EmitFindingsFromSarif ==============

    [Fact]
    public void EmitFindingsFromSarif_Missing_File_Returns_Zero_No_Emit()
    {
        using var rec = new Recorder();
        var path = TempPath("does-not-exist.sarif");

        var emitted = SecurityPipelineMetrics.EmitFindingsFromSarif(path, "opengrep");

        Assert.Equal(0, emitted);
        Assert.Empty(rec.ForInstrument("tamp.security.scan.findings_count"));
    }

    [Fact]
    public void EmitFindingsFromSarif_Empty_Sarif_Emits_Zero_Buckets()
    {
        using var rec = new Recorder();
        var path = WriteSarif(TempPath("empty.sarif"), new SarifLog
        {
            Runs = new[] { new SarifRun { Tool = new(), Results = Array.Empty<SarifResult>() } }
        });

        var emitted = SecurityPipelineMetrics.EmitFindingsFromSarif(path, "opengrep");

        Assert.Equal(0, emitted);
        Assert.Empty(rec.ForInstrument("tamp.security.scan.findings_count"));
    }

    [Fact]
    public void EmitFindingsFromSarif_Single_Severity_Bucket_Emits_Count()
    {
        using var rec = new Recorder();
        var path = WriteSarif(TempPath("warnings.sarif"), new SarifLog
        {
            Runs = new[] { new SarifRun
            {
                Tool = new(),
                Results = new[]
                {
                    new SarifResult { RuleId = "rule1", Level = SarifLevel.Warning },
                    new SarifResult { RuleId = "rule2", Level = SarifLevel.Warning },
                    new SarifResult { RuleId = "rule3", Level = SarifLevel.Warning },
                }
            }}
        });

        var emitted = SecurityPipelineMetrics.EmitFindingsFromSarif(path, "opengrep");

        Assert.Equal(1, emitted);
        var meas = Assert.Single(rec.ForInstrument("tamp.security.scan.findings_count"));
        Assert.Equal(3, meas.Value);
        Assert.Equal("opengrep", meas.Tags["tool"]);
        Assert.Equal("warning", meas.Tags["severity"]);
    }

    [Fact]
    public void EmitFindingsFromSarif_Mixed_Severities_Each_Get_Their_Own_Bucket()
    {
        using var rec = new Recorder();
        var path = WriteSarif(TempPath("mixed.sarif"), new SarifLog
        {
            Runs = new[] { new SarifRun
            {
                Tool = new(),
                Results = new[]
                {
                    new SarifResult { Level = SarifLevel.Error },
                    new SarifResult { Level = SarifLevel.Error },
                    new SarifResult { Level = SarifLevel.Warning },
                    new SarifResult { Level = SarifLevel.Note },
                    new SarifResult { Level = SarifLevel.Note },
                    new SarifResult { Level = SarifLevel.Note },
                    new SarifResult { Level = SarifLevel.None },
                }
            }}
        });

        var emitted = SecurityPipelineMetrics.EmitFindingsFromSarif(path, "trivy");

        Assert.Equal(4, emitted);
        var ms = rec.ForInstrument("tamp.security.scan.findings_count").ToList();
        Assert.Equal(4, ms.Count);

        Assert.Equal(2, ms.Single(m => (string)m.Tags["severity"]! == "error").Value);
        Assert.Equal(1, ms.Single(m => (string)m.Tags["severity"]! == "warning").Value);
        Assert.Equal(3, ms.Single(m => (string)m.Tags["severity"]! == "note").Value);
        Assert.Equal(1, ms.Single(m => (string)m.Tags["severity"]! == "none").Value);

        Assert.All(ms, m => Assert.Equal("trivy", m.Tags["tool"]));
    }

    [Fact]
    public void EmitFindingsFromSarif_Multiple_Runs_Aggregate()
    {
        // SARIF can have multiple runs (multi-tool-invocation reports). Counter aggregates across runs.
        using var rec = new Recorder();
        var path = WriteSarif(TempPath("multi-run.sarif"), new SarifLog
        {
            Runs = new[]
            {
                new SarifRun { Tool = new(), Results = new[] { new SarifResult { Level = SarifLevel.Error }, new SarifResult { Level = SarifLevel.Error } } },
                new SarifRun { Tool = new(), Results = new[] { new SarifResult { Level = SarifLevel.Error } } },
            }
        });

        SecurityPipelineMetrics.EmitFindingsFromSarif(path, "osvscanner");

        var meas = Assert.Single(rec.ForInstrument("tamp.security.scan.findings_count"));
        Assert.Equal(3, meas.Value);
        Assert.Equal("osvscanner", meas.Tags["tool"]);
    }

    [Fact]
    public void EmitFindingsFromSarif_Run_With_Null_Results_Doesnt_Throw_Or_Emit()
    {
        using var rec = new Recorder();
        var path = WriteSarif(TempPath("null-results.sarif"), new SarifLog
        {
            Runs = new[] { new SarifRun { Tool = new(), Results = null } }
        });

        var emitted = SecurityPipelineMetrics.EmitFindingsFromSarif(path, "opengrep");

        Assert.Equal(0, emitted);
        Assert.Empty(rec.ForInstrument("tamp.security.scan.findings_count"));
    }

    [Fact]
    public void EmitFindingsFromSarif_Malformed_File_Logs_Warning_Returns_Zero()
    {
        using var rec = new Recorder();
        var warnLog = new StringWriter();

        var path = TempPath("malformed.sarif");
        File.WriteAllText(path, "{this is not valid json");

        var emitted = SecurityPipelineMetrics.EmitFindingsFromSarif(path, "opengrep", warnLog);

        Assert.Equal(0, emitted);
        Assert.Empty(rec.ForInstrument("tamp.security.scan.findings_count"));
        Assert.Contains("WARN", warnLog.ToString());
        Assert.Contains("opengrep", warnLog.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmitFindingsFromSarif_Empty_Tool_Tag_Throws(string? tool)
    {
        var path = TempPath("any.sarif");
        Assert.Throws<ArgumentException>(() => SecurityPipelineMetrics.EmitFindingsFromSarif(path, tool!));
    }

    // ============== EmitComponentsFromBom ==============

    [Fact]
    public void EmitComponentsFromBom_Missing_File_Returns_Zero_No_Emit()
    {
        using var rec = new Recorder();
        var path = TempPath("does-not-exist.cdx.json");

        var emitted = SecurityPipelineMetrics.EmitComponentsFromBom(path, "cyclonedx");

        Assert.Equal(0, emitted);
        Assert.Empty(rec.ForInstrument("tamp.security.sbom.components_count"));
    }

    [Fact]
    public void EmitComponentsFromBom_Empty_Components_Emits_Zero_Buckets()
    {
        using var rec = new Recorder();
        var path = WriteBom(TempPath("empty.cdx.json"), new CycloneDxBom { Components = Array.Empty<CycloneDxComponent>() });

        var emitted = SecurityPipelineMetrics.EmitComponentsFromBom(path, "cyclonedx");

        Assert.Equal(0, emitted);
        Assert.Empty(rec.ForInstrument("tamp.security.sbom.components_count"));
    }

    [Fact]
    public void EmitComponentsFromBom_Null_Components_Doesnt_Throw()
    {
        using var rec = new Recorder();
        var path = WriteBom(TempPath("null-components.cdx.json"), new CycloneDxBom { Components = null });

        var emitted = SecurityPipelineMetrics.EmitComponentsFromBom(path, "cyclonedx");

        Assert.Equal(0, emitted);
        Assert.Empty(rec.ForInstrument("tamp.security.sbom.components_count"));
    }

    [Fact]
    public void EmitComponentsFromBom_Single_Type_Bucket_Emits()
    {
        using var rec = new Recorder();
        var path = WriteBom(TempPath("libs-only.cdx.json"), new CycloneDxBom
        {
            Components = new[]
            {
                new CycloneDxComponent { Type = "library", Name = "a" },
                new CycloneDxComponent { Type = "library", Name = "b" },
                new CycloneDxComponent { Type = "library", Name = "c" },
            }
        });

        var emitted = SecurityPipelineMetrics.EmitComponentsFromBom(path, "cyclonedx");

        Assert.Equal(1, emitted);
        var meas = Assert.Single(rec.ForInstrument("tamp.security.sbom.components_count"));
        Assert.Equal(3, meas.Value);
        Assert.Equal("cyclonedx", meas.Tags["producer"]);
        Assert.Equal("library", meas.Tags["type"]);
    }

    [Fact]
    public void EmitComponentsFromBom_Mixed_Types_Each_Get_Their_Own_Bucket()
    {
        using var rec = new Recorder();
        var path = WriteBom(TempPath("mixed.cdx.json"), new CycloneDxBom
        {
            Components = new[]
            {
                new CycloneDxComponent { Type = "library", Name = "a" },
                new CycloneDxComponent { Type = "library", Name = "b" },
                new CycloneDxComponent { Type = "application", Name = "app" },
                new CycloneDxComponent { Type = "framework", Name = "f1" },
                new CycloneDxComponent { Type = "framework", Name = "f2" },
                new CycloneDxComponent { Type = "framework", Name = "f3" },
                new CycloneDxComponent { Type = "operating-system", Name = "linux" },
            }
        });

        var emitted = SecurityPipelineMetrics.EmitComponentsFromBom(path, "syft");

        Assert.Equal(4, emitted);
        var ms = rec.ForInstrument("tamp.security.sbom.components_count").ToList();
        Assert.Equal(2, ms.Single(m => (string)m.Tags["type"]! == "library").Value);
        Assert.Equal(1, ms.Single(m => (string)m.Tags["type"]! == "application").Value);
        Assert.Equal(3, ms.Single(m => (string)m.Tags["type"]! == "framework").Value);
        Assert.Equal(1, ms.Single(m => (string)m.Tags["type"]! == "operating-system").Value);
        Assert.All(ms, m => Assert.Equal("syft", m.Tags["producer"]));
    }

    [Fact]
    public void EmitComponentsFromBom_Empty_Type_Falls_Back_To_library_Default()
    {
        // CycloneDX 1.6 spec says type is required; some producers emit blank. Default to "library".
        using var rec = new Recorder();
        var path = WriteBom(TempPath("blank-types.cdx.json"), new CycloneDxBom
        {
            Components = new[]
            {
                new CycloneDxComponent { Type = "", Name = "a" },
                new CycloneDxComponent { Type = "library", Name = "b" },
            }
        });

        SecurityPipelineMetrics.EmitComponentsFromBom(path, "cyclonedx");

        var meas = Assert.Single(rec.ForInstrument("tamp.security.sbom.components_count"));
        Assert.Equal(2, meas.Value);
        Assert.Equal("library", meas.Tags["type"]);
    }

    [Fact]
    public void EmitComponentsFromBom_Unknown_Type_Passes_Through_Verbatim()
    {
        // Producers can emit custom types; consumers should accept them.
        using var rec = new Recorder();
        var path = WriteBom(TempPath("custom-types.cdx.json"), new CycloneDxBom
        {
            Components = new[]
            {
                new CycloneDxComponent { Type = "my-custom-type", Name = "x" },
            }
        });

        SecurityPipelineMetrics.EmitComponentsFromBom(path, "cyclonedx");

        var meas = Assert.Single(rec.ForInstrument("tamp.security.sbom.components_count"));
        Assert.Equal("my-custom-type", meas.Tags["type"]);
    }

    [Fact]
    public void EmitComponentsFromBom_Malformed_File_Logs_Warning_Returns_Zero()
    {
        using var rec = new Recorder();
        var warnLog = new StringWriter();
        var path = TempPath("malformed.cdx.json");
        File.WriteAllText(path, "{garbled json");

        var emitted = SecurityPipelineMetrics.EmitComponentsFromBom(path, "cyclonedx", warnLog);

        Assert.Equal(0, emitted);
        Assert.Empty(rec.ForInstrument("tamp.security.sbom.components_count"));
        Assert.Contains("WARN", warnLog.ToString());
        Assert.Contains("cyclonedx", warnLog.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmitComponentsFromBom_Empty_Producer_Tag_Throws(string? producer)
    {
        var path = TempPath("any.cdx.json");
        Assert.Throws<ArgumentException>(() => SecurityPipelineMetrics.EmitComponentsFromBom(path, producer!));
    }

    // ============== Cross-cutting ==============

    [Fact]
    public void Meter_Name_Is_Tamp_Security_Pipeline()
    {
        Assert.Equal("Tamp.Security.Pipeline", SecurityPipelineMetrics.Meter.Name);
    }

    [Fact]
    public void Counter_Names_And_Units_Match_The_Stated_Contract()
    {
        // Stability contract — adopters' OTel pipelines and dashboards depend on these names.
        Assert.Equal("tamp.security.scan.findings_count", SecurityPipelineMetrics.FindingsCount.Name);
        Assert.Equal("{findings}", SecurityPipelineMetrics.FindingsCount.Unit);
        Assert.Equal("tamp.security.sbom.components_count", SecurityPipelineMetrics.ComponentsCount.Name);
        Assert.Equal("{components}", SecurityPipelineMetrics.ComponentsCount.Unit);
    }

    // ============== File writers (helpers) ==============

    private static AbsolutePath WriteSarif(AbsolutePath path, SarifLog log)
    {
        SarifWriter.WriteToFile(log, path);
        return path;
    }

    private static AbsolutePath WriteBom(AbsolutePath path, CycloneDxBom bom)
    {
        SbomWriter.WriteToFile(bom, path);
        return path;
    }
}
