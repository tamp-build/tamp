using System.Threading.Tasks;
using Tamp;
using Tamp.Security.Pipeline;
using Xunit;

namespace Tamp.Security.Pipeline.Tests;

/// <summary>
/// Tests <see cref="SecurityPipelineBuild"/> — the one-import base that wires the
/// Wave 1+2 security chain. Because it's a Nuke <c>TampBuild</c> of pure target
/// definitions, we cover it two ways:
///   1. Materialise each <c>Target</c> against a recording <see cref="ITargetDefinition"/>
///      to assert descriptions + dependency wiring.
///   2. Invoke the runnable <c>.Executes</c> bodies (plan-builders return a CommandPlan
///      without spawning a process; the file-merge + metrics + env-gated push bodies run
///      against a temp artifacts dir) to cover their configuration + control flow.
/// The external scanners themselves are integration concerns and are NOT run here.
/// </summary>
public sealed class SecurityPipelineBuildTests : IDisposable
{
    private readonly string _tempRoot;

    public SecurityPipelineBuildTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "tamp-sec-build-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
    }

    // ============================================================
    // Test subclasses
    // ============================================================

    /// <summary>Minimal concrete build using every default. Exposes the protected surface.</summary>
    private sealed class DefaultBuild : SecurityPipelineBuild
    {
        protected override string SecurityProductName => "MyProduct";
        protected override string SecuritySolutionPath => "MyProduct.slnx";

        public string ProductName => SecurityProductName;
        public string SolutionPath => SecuritySolutionPath;
        public string[] SbomDeps => SbomDependencies;
        public string SpecVersion => SecurityCycloneDxSpecVersion;
        public bool ExcludeTests => SecurityExcludeTestProjectsFromSbom;
        public string[] ScanDirs => SecurityScanTargetDirectories;
        public string[] SkipDirs => SecurityScanSkipDirs;
        public string EngagementName => SecurityEngagementName;
        public string SastTitle => SecuritySastTestTitle;
        public string OsvTitle => SecurityScaOsvTestTitle;
        public string DtFpfTitle => SecurityScaDtFpfTestTitle;
        public string SecretsTitle => SecuritySecretsTestTitle;
        public string RoslynToolTag => SecurityMetricsToolTagForRoslynSarif;
        public string SbomProducerTag => SecurityMetricsProducerTagForSbom;

        public AbsolutePath ArtifactsDir => SecurityArtifactsDir;
        public AbsolutePath SbomFile => SecuritySbomFile;
        public AbsolutePath OpenGrepSarif => SecuritySarifOpenGrepFile;
        public AbsolutePath RoslynDir => SecuritySarifRoslynDir;
        public AbsolutePath RoslynSarif => SecuritySarifRoslynFile;
        public AbsolutePath SastSarif => SecuritySarifSastFile;
        public AbsolutePath CveSarif => SecuritySarifCveFile;
        public AbsolutePath TrivySarif => SecuritySarifTrivyFile;

        // Env fields (default null when unset).
        public string? DtUrl => SecurityDtUrl;
        public Secret? DtApiKey => SecurityDtApiKey;
        public string? DtProjectUuid => SecurityDtProjectUuid;
        public string? DdUrl => SecurityDdUrl;
        public Secret? DdToken => SecurityDdToken;
        public int? DdEngagementId => SecurityDdEngagementId;

        // Targets exposed for materialisation.
        public Target T_Sbom => Sbom;
        public Target T_OpenGrep => SecurityScanOpenGrep;
        public Target T_Roslyn => SecurityScanRoslyn;
        public Target T_Scan => SecurityScan;
        public Target T_Cve => SecurityScanCveSbom;
        public Target T_Trivy => SecurityScanTrivy;
        public Target T_Push => SecurityPush;
        public Target T_Metrics => SecurityMetrics;
        public Target T_Collect => SecurityCollect;
        public Target T_Gate => SecurityGate;
        public Target T_Security => Security;
        public bool RequireReliableEvidence => SecurityRequireReliableEvidence;
        public AbsolutePath ReceiptFile => SecurityReceiptFile;
        public IReadOnlyList<SecurityScanSource> ExpectedScans => SecurityExpectedScans;
        public SecurityEvidenceSummary ReadEvidence() => ReadSecurityEvidence();
    }

    /// <summary>Build that overrides every optional knob + points artifacts at a temp dir.</summary>
    private sealed class OverrideBuild : SecurityPipelineBuild
    {
        private readonly AbsolutePath _art;
        public OverrideBuild(string tempRoot) => _art = AbsolutePath.Create(tempRoot);

        protected override string SecurityProductName => "OverRidden"; // mixed case → ToLowerInvariant path
        protected override string SecuritySolutionPath => "sln/App.csproj";
        protected override string[] SbomDependencies => [];
        protected override string SecurityCycloneDxSpecVersion => "1.5";
        protected override bool SecurityExcludeTestProjectsFromSbom => false;
        protected override string[] SecurityScanTargetDirectories => ["lib"];
        protected override string[] SecurityScanSkipDirs => ["out"];
        protected override string SecurityEngagementName => "Custom Engagement";
        protected override string SecuritySastTestTitle => "Custom SAST";
        protected override string SecurityMetricsToolTagForRoslynSarif => "roslynator";
        protected override string SecurityMetricsProducerTagForSbom => "syft";
        protected override AbsolutePath SecurityArtifactsDir => _art;

        public string[] SbomDeps => SbomDependencies;
        public string SpecVersion => SecurityCycloneDxSpecVersion;
        public bool ExcludeTests => SecurityExcludeTestProjectsFromSbom;
        public string[] ScanDirs => SecurityScanTargetDirectories;
        public string EngagementName => SecurityEngagementName;
        public string SastTitle => SecuritySastTestTitle;
        public string RoslynToolTag => SecurityMetricsToolTagForRoslynSarif;
        public string SbomProducerTag => SecurityMetricsProducerTagForSbom;
        public AbsolutePath SbomFile => SecuritySbomFile;
        public AbsolutePath ArtifactsDir => SecurityArtifactsDir;

        public Target T_Sbom => Sbom;
        public Target T_OpenGrep => SecurityScanOpenGrep;
        public Target T_Roslyn => SecurityScanRoslyn;
        public Target T_Scan => SecurityScan;
        public Target T_Cve => SecurityScanCveSbom;
        public Target T_Trivy => SecurityScanTrivy;
        public Target T_Push => SecurityPush;
        public Target T_Metrics => SecurityMetrics;
        public Target T_Collect => SecurityCollect;
        public Target T_Gate => SecurityGate;
        public AbsolutePath ReceiptFile => SecurityReceiptFile;
        public IReadOnlyList<SecurityScanSource> ExpectedScans => SecurityExpectedScans;
        public SecurityEvidenceSummary ReadEvidence() => ReadSecurityEvidence();
    }

    // ============================================================
    // Recording ITargetDefinition — captures Description/DependsOn + Executes bodies
    // ============================================================

    private static RecordingDef Materialize(Target target)
    {
        var def = new RecordingDef();
        target(def);
        return def;
    }

    // RecordingDef lives in RecordingDef.cs so the gate tests (#151) can use the same fake.

    // ============================================================
    // Property / default coverage
    // ============================================================

    [Fact]
    public void Defaults_Are_As_Documented()
    {
        var b = new DefaultBuild();
        Assert.Equal(["Restore"], b.SbomDeps);
        Assert.Equal("1.6", b.SpecVersion);
        Assert.True(b.ExcludeTests);
        Assert.Equal(["src", "tests", "build"], b.ScanDirs);
        Assert.Equal(["artifacts", "**/bin/**", "**/obj/**"], b.SkipDirs);
        Assert.Equal("roslyn", b.RoslynToolTag);
        Assert.Equal("cyclonedx", b.SbomProducerTag);
    }

    [Fact]
    public void Default_Titles_Derive_From_Product_Name()
    {
        var b = new DefaultBuild();
        Assert.Equal("MyProduct CI", b.EngagementName);
        Assert.Equal("MyProduct SAST (OpenGrep + Roslyn)", b.SastTitle);
        Assert.Equal("MyProduct SCA (osv-scanner)", b.OsvTitle);
        Assert.Equal("MyProduct SCA (Dependency-Track FPF)", b.DtFpfTitle);
        Assert.Equal("MyProduct Secrets+Misconfig (Trivy)", b.SecretsTitle);
    }

    [Fact]
    public void Derived_Paths_Follow_The_Artifacts_Security_Layout()
    {
        var b = new DefaultBuild();
        Assert.EndsWith(Path.Combine("artifacts", "security"), b.ArtifactsDir.ToString());
        // SBOM filename is lower-cased product name + .cdx.json (osv-scanner extractor requirement).
        Assert.EndsWith("myproduct.cdx.json", b.SbomFile.ToString());
        Assert.EndsWith("opengrep.sarif", b.OpenGrepSarif.ToString());
        Assert.EndsWith("roslyn.sarif", b.RoslynSarif.ToString());
        Assert.EndsWith(Path.Combine("security", "roslyn"), b.RoslynDir.ToString());
        Assert.EndsWith("sast.sarif", b.SastSarif.ToString());
        Assert.EndsWith("cve.sarif", b.CveSarif.ToString());
        Assert.EndsWith("trivy.sarif", b.TrivySarif.ToString());
    }

    [Fact]
    public void Env_Var_Inputs_Default_To_Null_When_Unset()
    {
        var b = new DefaultBuild();
        Assert.Null(b.DtUrl);
        Assert.Null(b.DtApiKey);
        Assert.Null(b.DtProjectUuid);
        Assert.Null(b.DdUrl);
        Assert.Null(b.DdToken);
        Assert.Null(b.DdEngagementId);
    }

    [Fact]
    public void Overrides_Take_Effect()
    {
        var b = new OverrideBuild(_tempRoot);
        Assert.Empty(b.SbomDeps);
        Assert.Equal("1.5", b.SpecVersion);
        Assert.False(b.ExcludeTests);
        Assert.Equal(["lib"], b.ScanDirs);
        Assert.Equal("Custom Engagement", b.EngagementName);
        Assert.Equal("Custom SAST", b.SastTitle);
        Assert.Equal("roslynator", b.RoslynToolTag);
        Assert.Equal("syft", b.SbomProducerTag);
        Assert.EndsWith("overridden.cdx.json", b.SbomFile.ToString());
        Assert.Equal(_tempRoot, b.ArtifactsDir.ToString());
    }

    // ============================================================
    // Target definition wiring
    // ============================================================

    [Fact]
    public void Every_Target_Has_A_Description()
    {
        var b = new DefaultBuild();
        foreach (var t in new[] { b.T_Sbom, b.T_OpenGrep, b.T_Roslyn, b.T_Scan, b.T_Cve, b.T_Trivy, b.T_Collect, b.T_Push, b.T_Metrics, b.T_Gate, b.T_Security })
            Assert.False(string.IsNullOrWhiteSpace(Materialize(t).RecordedDescription));
    }

    [Fact]
    public void Sbom_Depends_On_Restore_By_Default()
        => Assert.Equal(["Restore"], Materialize(new DefaultBuild().T_Sbom).RecordedDependencies);

    [Fact]
    public void Sbom_Dependencies_Are_Overridable_To_Empty()
        => Assert.Empty(Materialize(new OverrideBuild(_tempRoot).T_Sbom).RecordedDependencies);

    [Fact]
    public void SecurityScan_Merges_OpenGrep_And_Roslyn()
        => Assert.Equal(["SecurityScanOpenGrep", "SecurityScanRoslyn"], Materialize(new DefaultBuild().T_Scan).RecordedDependencies);

    [Fact]
    public void SecurityScanCveSbom_Depends_On_Sbom()
        => Assert.Equal(["Sbom"], Materialize(new DefaultBuild().T_Cve).RecordedDependencies);

    // ---- #151: collect -> publish -> enforce, as three ordered stages -------------------
    //
    // The wiring IS the fix, so it is asserted rather than left implicit. Enforcing by failing a
    // scan target would skip SecurityPush and SecurityMetrics in exactly the runs where findings
    // exist, so evidence would arrive reliably only when there was nothing to report.

    [Fact]
    public void The_Collect_Stage_Depends_On_Every_Producer()
        => Assert.Equal(["Sbom", "SecurityScan", "SecurityScanCveSbom", "SecurityScanTrivy"],
            Materialize(new DefaultBuild().T_Collect).RecordedDependencies);

    [Fact]
    public void Both_Publish_Targets_Depend_Only_On_The_Collect_Stage()
    {
        // One edge each, to the stage — not to the individual scanners. That is what lets the
        // publish stage be reached when one scanner failed.
        Assert.Equal(["SecurityCollect"], Materialize(new DefaultBuild().T_Push).RecordedDependencies);
        Assert.Equal(["SecurityCollect"], Materialize(new DefaultBuild().T_Metrics).RecordedDependencies);
    }

    [Fact]
    public void The_Gate_Runs_After_Both_Publish_Targets()
        => Assert.Equal(["SecurityMetrics", "SecurityPush"],
            Materialize(new DefaultBuild().T_Gate).RecordedDependencies);

    [Fact]
    public void Security_Aggregate_Depends_On_The_Whole_Chain()
        => Assert.Equal(
            ["SecurityCollect", "SecurityMetrics", "SecurityPush", "SecurityGate"],
            Materialize(new DefaultBuild().T_Security).RecordedDependencies);

    [Fact]
    public void Collect_Publish_And_Enforce_Are_All_Assured_After_Failure()
    {
        // Without this, a single scanner crash suppresses every downstream target: no metrics, no
        // push, no receipt — precisely in the run where something went wrong.
        var build = new DefaultBuild();
        foreach (var (name, target) in new (string, Tamp.Target)[]
        {
            ("Sbom", build.T_Sbom),
            ("SecurityScanOpenGrep", build.T_OpenGrep),
            ("SecurityScanRoslyn", build.T_Roslyn),
            ("SecurityScan", build.T_Scan),
            ("SecurityScanCveSbom", build.T_Cve),
            ("SecurityScanTrivy", build.T_Trivy),
            ("SecurityCollect", build.T_Collect),
            ("SecurityMetrics", build.T_Metrics),
            ("SecurityPush", build.T_Push),
            ("SecurityGate", build.T_Gate),
        })
        {
            Assert.True(Materialize(target).RecordedAssuredAfterFailure,
                $"{name} must be assured-after-failure so evidence survives an upstream failure");
        }
    }

    // ============================================================
    // Runnable .Executes bodies (plan builders / file IO / env-gated skips)
    // ============================================================

    [Fact]
    public void Sbom_Body_Builds_A_Plan_Without_Spawning()
    {
        var def = Materialize(new OverrideBuild(_tempRoot).T_Sbom);
        Assert.True(def.HasExecutes);
        Assert.NotEmpty(def.RunExecutes());           // one CycloneDX plan
    }

    [Theory]
    [InlineData("OpenGrep")]
    [InlineData("Roslyn")]
    [InlineData("Cve")]
    [InlineData("Trivy")]
    public void Scanner_Bodies_Build_Plans(string which)
    {
        var b = new OverrideBuild(_tempRoot);
        var t = which switch
        {
            "OpenGrep" => b.T_OpenGrep,
            "Roslyn" => b.T_Roslyn,
            "Cve" => b.T_Cve,
            _ => b.T_Trivy,
        };
        var def = Materialize(t);
        Assert.True(def.HasExecutes);
        Assert.NotEmpty(def.RunExecutes());
    }

    [Fact]
    public void SecurityScan_Body_Handles_Zero_Source_Sarifs()
    {
        // No opengrep/roslyn SARIFs present → merges nothing, still writes an (empty) merged SAST file.
        var b = new OverrideBuild(_tempRoot);
        var def = Materialize(b.T_Scan);
        def.RunExecutes();
        Assert.True(File.Exists(Path.Combine(_tempRoot, "sast.sarif")));
    }

    [Fact]
    public void SecurityMetrics_Body_NoOps_On_Missing_Files()
    {
        // No SBOM/SARIF produced yet → every Emit is a silent no-op; body must not throw.
        var b = new OverrideBuild(_tempRoot);
        var def = Materialize(b.T_Metrics);

        var thrown = Record.Exception(() => def.RunExecutes());

        Assert.Null(thrown);
        Assert.True(def.HasExecutes);
        // A metrics pass reads evidence; it must not create any (no placeholder SBOM / SARIF).
        Assert.Empty(Directory.EnumerateFileSystemEntries(_tempRoot));
    }

    [Fact]
    public void SecurityPush_Body_Skips_Cleanly_When_Env_Unset()
    {
        // Neither DT nor DD env vars are set → both push legs log a clean skip and return.
        var b = new OverrideBuild(_tempRoot);
        var def = Materialize(b.T_Push);
        Assert.True(def.HasExecutes);
        def.RunExecutes();   // must not throw, no network
    }
}
