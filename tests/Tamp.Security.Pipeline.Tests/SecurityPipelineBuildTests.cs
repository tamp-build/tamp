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
        public Target T_Security => Security;
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

    private sealed class RecordingDef : ITargetDefinition
    {
        public string? RecordedDescription { get; private set; }
        public List<string> RecordedDependencies { get; } = new();

        private Action? _action;
        private Func<CommandPlan>? _plan;
        private Func<IEnumerable<CommandPlan>>? _plans;
        private Func<Task>? _asyncAction;
        private Func<Task<CommandPlan>>? _asyncPlan;
        private Func<Task<IEnumerable<CommandPlan>>>? _asyncPlans;

        public bool HasExecutes => _action is not null || _plan is not null || _plans is not null
            || _asyncAction is not null || _asyncPlan is not null || _asyncPlans is not null;

        /// <summary>Run whatever the target registered; returns produced plans (may be empty).</summary>
        public IReadOnlyList<CommandPlan> RunExecutes()
        {
            var produced = new List<CommandPlan>();
            _action?.Invoke();
            if (_plan is not null) produced.Add(_plan());
            if (_plans is not null) produced.AddRange(_plans());
            if (_asyncAction is not null) _asyncAction().GetAwaiter().GetResult();
            if (_asyncPlan is not null) produced.Add(_asyncPlan().GetAwaiter().GetResult());
            if (_asyncPlans is not null) produced.AddRange(_asyncPlans().GetAwaiter().GetResult());
            return produced;
        }

        public ITargetDefinition Description(string description) { RecordedDescription = description; return this; }
        public ITargetDefinition DependsOn(params string[] targetNames) { RecordedDependencies.AddRange(targetNames); return this; }
        public ITargetDefinition DependsOn(Target target, string? name = null) { if (name is not null) RecordedDependencies.Add(name); return this; }
        public ITargetDefinition DependsOn(Target t1, Target t2, params Target[] more) => this;

        public ITargetDefinition Executes(Action action) { _action = action; return this; }
        public ITargetDefinition Executes(Func<CommandPlan> planFactory) { _plan = planFactory; return this; }
        public ITargetDefinition Executes(Func<IEnumerable<CommandPlan>> planFactory) { _plans = planFactory; return this; }
        public ITargetDefinition Executes(Func<Task> asyncAction) { _asyncAction = asyncAction; return this; }
        public ITargetDefinition Executes(Func<Task<CommandPlan>> asyncPlanFactory) { _asyncPlan = asyncPlanFactory; return this; }
        public ITargetDefinition Executes(Func<Task<IEnumerable<CommandPlan>>> asyncPlanFactory) { _asyncPlans = asyncPlanFactory; return this; }

        // Everything else is a no-op recorder returning this.
        public ITargetDefinition Phase(Phase phase) => this;
        public ITargetDefinition Phase(Phase phase, PhaseDescriptor descriptor) => this;
        public ITargetDefinition Tag(params string[] tags) => this;
        public ITargetDefinition TopLevel() => this;
        public ITargetDefinition Internal() => this;
        public ITargetDefinition Default() => this;
        public ITargetDefinition After(params string[] targetNames) => this;
        public ITargetDefinition After(Target target, string? name = null) => this;
        public ITargetDefinition After(Target t1, Target t2, params Target[] more) => this;
        public ITargetDefinition Before(params string[] targetNames) => this;
        public ITargetDefinition Before(Target target, string? name = null) => this;
        public ITargetDefinition Before(Target t1, Target t2, params Target[] more) => this;
        public ITargetDefinition Triggers(params string[] targetNames) => this;
        public ITargetDefinition Triggers(Target target, string? name = null) => this;
        public ITargetDefinition Triggers(Target t1, Target t2, params Target[] more) => this;
        public ITargetDefinition TriggeredBy(params string[] targetNames) => this;
        public ITargetDefinition TriggeredBy(Target target, string? name = null) => this;
        public ITargetDefinition TriggeredBy(Target t1, Target t2, params Target[] more) => this;
        public ITargetDefinition OnFailureOf(params string[] targetNames) => this;
        public ITargetDefinition OnFailureOf(Target target, string? name = null) => this;
        public ITargetDefinition OnFailureOf(Target t1, Target t2, params Target[] more) => this;
        public ITargetDefinition OnlyWhen(Func<bool> condition, string expressionText = "") => this;
        public ITargetDefinition Requires(Func<bool> condition, string expressionText = "") => this;
        public ITargetDefinition AssuredAfterFailure() => this;
        public ITargetDefinition Consumes(Resource resource, ConsumeMode mode) => this;
        public ITargetDefinition RequiresNetwork() => this;
        public ITargetDefinition RequiresDocker() => this;
        public ITargetDefinition RequiresAdmin() => this;
        public ITargetDefinition Capability(CapabilityTier tier) => this;
        public ITargetDefinition RequiresTool(string toolName, string? minVersion = null) => this;
        public ITargetDefinition Timeout(TimeSpan timeout) => this;
        public ITargetDefinition ExpectedDuration(TimeSpan expected) => this;
        public ITargetDefinition MemoryBudget(int megabytes) => this;
        public ITargetDefinition MemoryHardLimit(int megabytes) => this;
        public ITargetDefinition MaxParallelism(int copies) => this;
        public ITargetDefinition MaxHostParallelism(int copies) => this;
        public ITargetDefinition Idempotent() => this;
        public ITargetDefinition InputHash(Func<string> hashProducer) => this;
        public ITargetDefinition Produces(string globPattern) => this;
        public ITargetDefinition RunMode(RunMode mode) => this;
        public ITargetDefinition FailureMode(FailureMode mode) => this;
        public ITargetDefinition Retry(int count, Backoff backoff, params int[] retryableExitCodes) => this;
    }

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
        foreach (var t in new[] { b.T_Sbom, b.T_OpenGrep, b.T_Roslyn, b.T_Scan, b.T_Cve, b.T_Trivy, b.T_Push, b.T_Metrics, b.T_Security })
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

    [Fact]
    public void SecurityPush_Depends_On_All_Producers()
        => Assert.Equal(["Sbom", "SecurityScan", "SecurityScanCveSbom", "SecurityScanTrivy"],
            Materialize(new DefaultBuild().T_Push).RecordedDependencies);

    [Fact]
    public void SecurityMetrics_Depends_On_The_Five_Producers()
        => Assert.Equal(["Sbom", "SecurityScanOpenGrep", "SecurityScanRoslyn", "SecurityScanCveSbom", "SecurityScanTrivy"],
            Materialize(new DefaultBuild().T_Metrics).RecordedDependencies);

    [Fact]
    public void Security_Aggregate_Depends_On_The_Whole_Chain()
        => Assert.Equal(
            ["Sbom", "SecurityScan", "SecurityScanCveSbom", "SecurityScanTrivy", "SecurityMetrics", "SecurityPush"],
            Materialize(new DefaultBuild().T_Security).RecordedDependencies);

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
        def.RunExecutes();   // must not throw
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
