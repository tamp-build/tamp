using System.Text.Json.Nodes;
using Tamp.Sarif;
using Tamp.Sbom;
using Tamp.Security.Pipeline;
using Xunit;

namespace Tamp.Security.Pipeline.Tests;

/// <summary>
/// #151 part 2 — the enforcement stage. Enforcing by failing a scan target skipped
/// <c>SecurityPush</c> and <c>SecurityMetrics</c>, so evidence was published reliably when there was
/// nothing to report and unreliably when findings existed. The gate runs last and is assured, so
/// failing it costs no evidence.
/// </summary>
public sealed class SecurityGateTests : IDisposable
{
    private readonly string _tempRoot;

    public SecurityGateTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "tamp-sec-gate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
    }

    // ============================================================
    // A build whose artifacts live in a temp dir, with the scanners' outputs
    // faked by writing the files they would have written.
    // ============================================================

    private class GateBuild : SecurityPipelineBuild
    {
        private readonly AbsolutePath _artifacts;

        public GateBuild(string root) => _artifacts = AbsolutePath.Create(Path.Combine(root, "artifacts", "security"));

        protected override string SecurityProductName => "GateProduct";
        protected override string SecuritySolutionPath => "GateProduct.slnx";
        protected override AbsolutePath SecurityArtifactsDir => _artifacts;

        public Target T_Gate => SecurityGate;
        public AbsolutePath ReceiptFile => SecurityReceiptFile;
        public AbsolutePath ArtifactsDir => SecurityArtifactsDir;
        public AbsolutePath SbomFile => SecuritySbomFile;
        public AbsolutePath OpenGrepFile => SecuritySarifOpenGrepFile;
        public AbsolutePath CveFile => SecuritySarifCveFile;
        public AbsolutePath TrivyFile => SecuritySarifTrivyFile;
        public AbsolutePath RoslynDir => SecuritySarifRoslynDir;
        public SecurityEvidenceSummary Evidence() => ReadSecurityEvidence();
        public void Publish(SecurityPublicationReport report) => RecordSecurityPublication(report);

        /// <summary>What <see cref="EnforceSecurityPolicy"/> was handed, for assertions.</summary>
        public SecurityEvidenceSummary? PolicySaw { get; private set; }

        protected override void EnforceSecurityPolicy(SecurityEvidenceSummary evidence)
        {
            PolicySaw = evidence;
            base.EnforceSecurityPolicy(evidence);
        }
    }

    /// <summary>A build that tolerates incomplete evidence rather than failing on it.</summary>
    private sealed class TolerantGateBuild(string root) : GateBuild(root)
    {
        protected override bool SecurityRequireReliableEvidence => false;
    }

    /// <summary>A build with an adopter policy that fails on any error-level finding.</summary>
    private sealed class PolicyGateBuild(string root) : GateBuild(root)
    {
        public int Blocking { get; private set; }

        protected override void EnforceSecurityPolicy(SecurityEvidenceSummary evidence)
        {
            base.EnforceSecurityPolicy(evidence);
            Blocking = evidence.FindingsAtOrAbove("error");
            if (Blocking > 0) throw new InvalidOperationException($"{Blocking} error-level finding(s) exceed policy.");
        }
    }

    // ============================================================
    // Fixture helpers: write the files the collect stage would have written.
    // ============================================================

    private static void WriteSarif(AbsolutePath path, params SarifResult[] results)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path.Value)!);
        SarifWriter.WriteToFile(new SarifLog { Runs = [new SarifRun { Tool = new(), Results = results }] }, path);
    }

    private static SarifResult Finding(SarifLevel level) =>
        new() { RuleId = "X1", Level = level, Message = new SarifMessage { Text = "finding" } };

    /// <summary>Every expected scan present and clean, plus a non-empty SBOM: a complete evidence set.</summary>
    private static GateBuild CompleteEvidence(GateBuild build)
    {
        Directory.CreateDirectory(build.ArtifactsDir.Value);
        SbomWriter.WriteToFile(
            new CycloneDxBom { Components = [new CycloneDxComponent { Name = "pkg", Type = "library" }] },
            build.SbomFile);
        WriteSarif(build.OpenGrepFile);
        WriteSarif(build.CveFile);
        WriteSarif(build.TrivyFile);
        Directory.CreateDirectory(build.RoslynDir.Value);
        WriteSarif(AbsolutePath.Create(Path.Combine(build.RoslynDir.Value, "proj.net10.0.sarif")));
        return build;
    }

    private static RecordingDef Materialize(Target target)
    {
        var definition = new RecordingDef();
        target(definition);
        return definition;
    }

    // ============================================================
    // The gate
    // ============================================================

    [Fact]
    public void Complete_Evidence_Passes_And_Writes_A_Receipt()
    {
        var build = CompleteEvidence(new GateBuild(_tempRoot));

        Materialize(build.T_Gate).RunExecutes();

        Assert.True(File.Exists(build.ReceiptFile.Value));
        var receipt = JsonNode.Parse(File.ReadAllText(build.ReceiptFile.Value))!;
        Assert.True((bool?)receipt["reliable"]);
        Assert.Equal(4, (receipt["scans"] as JsonArray)!.Count);
        Assert.NotNull(build.PolicySaw);
    }

    [Fact]
    public void A_Scan_That_Did_Not_Run_Fails_The_Gate()
    {
        var build = CompleteEvidence(new GateBuild(_tempRoot));
        File.Delete(build.TrivyFile.Value);          // the scanner was never installed

        var thrown = Assert.Throws<InvalidOperationException>(() => Materialize(build.T_Gate).RunExecutes());

        // The message has to say what is unknown, not just that something failed.
        Assert.Contains("trivy", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("not_run", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("not the same as having good news", thrown.Message, StringComparison.Ordinal);
        // And it has to say how to resolve it honestly.
        Assert.Contains("SecurityExpectedScans", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Receipt_Is_Written_Before_The_Gate_Fails()
    {
        // The run where evidence matters most is the run where something went wrong, so the receipt
        // must survive the throw.
        var build = CompleteEvidence(new GateBuild(_tempRoot));
        File.Delete(build.OpenGrepFile.Value);

        Assert.Throws<InvalidOperationException>(() => Materialize(build.T_Gate).RunExecutes());

        Assert.True(File.Exists(build.ReceiptFile.Value));
        var receipt = JsonNode.Parse(File.ReadAllText(build.ReceiptFile.Value))!;
        Assert.False((bool?)receipt["reliable"]);
        Assert.Contains("opengrep", File.ReadAllText(build.ReceiptFile.Value), StringComparison.Ordinal);
    }

    [Fact]
    public void An_Absent_Sbom_Fails_The_Gate()
    {
        var build = CompleteEvidence(new GateBuild(_tempRoot));
        File.Delete(build.SbomFile.Value);

        var thrown = Assert.Throws<InvalidOperationException>(() => Materialize(build.T_Gate).RunExecutes());
        Assert.Contains("sbom", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_Empty_Sbom_Fails_The_Gate()
    {
        // 0 components means every SCA result derived from it is vacuously clean — the most
        // convincing kind of false negative.
        var build = CompleteEvidence(new GateBuild(_tempRoot));
        SbomWriter.WriteToFile(new CycloneDxBom { Components = [] }, build.SbomFile);

        var thrown = Assert.Throws<InvalidOperationException>(() => Materialize(build.T_Gate).RunExecutes());
        Assert.Contains("vacuously clean", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Incomplete_Publication_Fails_The_Gate()
    {
        var build = CompleteEvidence(new GateBuild(_tempRoot));
        build.Publish(new SecurityPublicationReport
        {
            Sink = "dependency-track",
            Status = SecurityPublicationStatus.Incomplete,
            Detail = "analysis did not settle within 120s",
        });

        var thrown = Assert.Throws<InvalidOperationException>(() => Materialize(build.T_Gate).RunExecutes());
        Assert.Contains("dependency-track", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Unconfigured_Publication_Does_Not_Fail_The_Gate()
    {
        // Publication must stay no-op-clean when its credentials are unset.
        var build = CompleteEvidence(new GateBuild(_tempRoot));
        build.Publish(new SecurityPublicationReport { Sink = "defectdojo", Status = SecurityPublicationStatus.NotConfigured });

        Materialize(build.T_Gate).RunExecutes();

        Assert.True((bool?)JsonNode.Parse(File.ReadAllText(build.ReceiptFile.Value))!["reliable"]);
    }

    [Fact]
    public void Findings_Alone_Do_Not_Fail_The_Gate()
    {
        // Policy belongs to the adopter: core reports findings and enforces nothing about their count.
        var build = CompleteEvidence(new GateBuild(_tempRoot));
        WriteSarif(build.OpenGrepFile, Finding(SarifLevel.Error), Finding(SarifLevel.Error));

        Materialize(build.T_Gate).RunExecutes();

        Assert.Equal(2, build.PolicySaw!.TotalFindings);
        Assert.Equal(2, build.PolicySaw.FindingsAtOrAbove("error"));
        Assert.Empty(build.PolicySaw.ReliabilityProblems);
    }

    [Fact]
    public void An_Adopter_Policy_Can_Fail_The_Build_On_Findings()
    {
        var build = CompleteEvidence(new PolicyGateBuild(_tempRoot));
        WriteSarif(build.OpenGrepFile, Finding(SarifLevel.Error));

        var thrown = Assert.Throws<InvalidOperationException>(() => Materialize(build.T_Gate).RunExecutes());

        Assert.Contains("exceed policy", thrown.Message, StringComparison.Ordinal);
        // And the receipt still exists: the whole reason the gate is last.
        Assert.True(File.Exists(build.ReceiptFile.Value));
    }

    [Fact]
    public void Incomplete_Evidence_Can_Be_Downgraded_To_A_Warning()
    {
        var build = CompleteEvidence(new TolerantGateBuild(_tempRoot));
        File.Delete(build.TrivyFile.Value);

        Materialize(build.T_Gate).RunExecutes();      // does not throw

        // The gap stays visible in the receipt even though it no longer blocks.
        var receipt = JsonNode.Parse(File.ReadAllText(build.ReceiptFile.Value))!;
        Assert.False((bool?)receipt["reliable"]);
        Assert.Contains("trivy", File.ReadAllText(build.ReceiptFile.Value), StringComparison.Ordinal);
        // Policy still runs — tolerating missing evidence is not skipping enforcement.
        Assert.NotNull(build.PolicySaw);
    }

    [Fact]
    public void The_Default_Policy_Enforces_Nothing()
    {
        // Keeping policy out of core: the base implementation must be a no-op, so an adopter who
        // never overrides it gets evidence integrity and no opinion about severity.
        var build = CompleteEvidence(new GateBuild(_tempRoot));
        WriteSarif(build.CveFile, Finding(SarifLevel.Error), Finding(SarifLevel.Warning));

        Materialize(build.T_Gate).RunExecutes();

        Assert.Equal(3, build.PolicySaw!.Scans.Count(s => s.IsClean));
        Assert.Equal(2, build.PolicySaw.TotalFindings);
    }

    [Fact]
    public void The_Expected_Scan_Set_Is_What_Makes_A_Gap_Answerable()
    {
        var build = CompleteEvidence(new GateBuild(_tempRoot));

        var evidence = build.Evidence();

        Assert.Equal(["opengrep", "roslyn", "osvscanner", "trivy"], evidence.Scans.Select(s => s.Tool));
        // The merged sast.sarif is deliberately not in the set — it is derived from opengrep and
        // roslyn, and counting it too would double every SAST finding.
        Assert.DoesNotContain("sast", evidence.Scans.Select(s => s.Tool));
    }
}
