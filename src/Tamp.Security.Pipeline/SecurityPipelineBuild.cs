using Tamp.CycloneDx.V6;
using Tamp.DefectDojo.V2;
using Tamp.DependencyTrack.V1;
using Tamp.NetCli.V10;
using Tamp.OpenGrep;
using Tamp.OsvScanner.V2;
using Tamp.Sarif;
using Tamp.Sbom;
using Tamp.Trivy;
// Class aliases — needed because we live in namespace Tamp.Security.Pipeline,
// which causes C# name lookup to resolve `CycloneDx`/`OpenGrep`/`OsvScanner`/`Trivy`
// to the namespace `Tamp.CycloneDx`/etc. before reaching the imported wrapper
// class of the same name. Unpinned satellites (OpenGrep, Trivy) and pinned
// (CycloneDx.V6, OsvScanner.V2) both need the alias for the same reason.
using CycloneDxCli = Tamp.CycloneDx.V6.CycloneDx;
using OpenGrepCli = Tamp.OpenGrep.OpenGrep;
using OsvScannerCli = Tamp.OsvScanner.V2.OsvScanner;
using TrivyCli = Tamp.Trivy.Trivy;

namespace Tamp.Security.Pipeline;

/// <summary>
/// One-import base class that wires the whole Wave 1+2 security chain.
/// Adopters inherit, override <see cref="SecurityProductName"/> +
/// <see cref="SecuritySolutionPath"/>, and run <c>tamp Security</c>.
/// </summary>
/// <remarks>
/// Producer half (Sbom + SecurityScan + SecurityScanCveSbom +
/// SecurityScanTrivy) runs unconditionally. Push half is env-var-gated:
/// supply <c>TAMP_DT_URL/API_KEY/PROJECT_UUID</c> for Dependency-Track
/// and/or <c>TAMP_DD_URL/TOKEN/ENGAGEMENT_ID</c> for DefectDojo. Without
/// them <see cref="SecurityPush"/> logs clean skips and the build stays
/// green.
///
/// v0 is .NET-focused: <see cref="Sbom"/> uses Tamp.CycloneDx.V6
/// (dotnet-CycloneDX). Non-.NET adopters override <see cref="Sbom"/> to
/// use Tamp.Syft.V1 instead.
///
/// Most targets are <see langword="virtual"/> so adopters can override
/// individual ones — e.g. add `.DependsOn(nameof(Compile))` to Sbom in
/// a project where Compile is the natural pre-req.
/// </remarks>
public abstract class SecurityPipelineBuild : TampBuild
{
    // ------------------------------------------------------------------
    // Required overrides
    // ------------------------------------------------------------------

    /// <summary>Product identity used in DefectDojo product/engagement names, SBOM metadata.component.name, and default file paths.</summary>
    protected abstract string SecurityProductName { get; }

    /// <summary>Path passed to <c>dotnet-CycloneDX</c> (a .sln/.slnx, .csproj, or directory).</summary>
    protected abstract string SecuritySolutionPath { get; }

    // ------------------------------------------------------------------
    // Optional overrides
    // ------------------------------------------------------------------

    /// <summary>Targets <see cref="Sbom"/> depends on. Default <c>["Restore"]</c> — most TampBuild adopters define a Restore target. Override to <c>[]</c> for non-.NET adopters.</summary>
    protected virtual string[] SbomDependencies { get; } = ["Restore"];

    /// <summary>CycloneDX spec version emitted by <see cref="Sbom"/>. Default 1.6 (osv-scanner 2.x doesn't yet accept 1.7).</summary>
    protected virtual string SecurityCycloneDxSpecVersion => "1.6";

    /// <summary>Skip test projects when generating the SBOM. Default true.</summary>
    protected virtual bool SecurityExcludeTestProjectsFromSbom => true;

    /// <summary>Source-tree directories OpenGrep, Trivy, and Roslyn-leg builds scan. Default <c>["src", "tests", "build"]</c> for the common Tamp layout.</summary>
    protected virtual string[] SecurityScanTargetDirectories { get; } = ["src", "tests", "build"];

    /// <summary>Path globs the secondary scanners skip. Default covers binaries, build outputs, and the artifacts tree.</summary>
    protected virtual string[] SecurityScanSkipDirs { get; } = ["artifacts", "**/bin/**", "**/obj/**"];

    /// <summary>DefectDojo engagement display name. Default <c>"{ProductName} CI"</c>.</summary>
    protected virtual string SecurityEngagementName => $"{SecurityProductName} CI";

    /// <summary>DefectDojo test title for the merged SAST findings.</summary>
    protected virtual string SecuritySastTestTitle => $"{SecurityProductName} SAST (OpenGrep + Roslyn)";

    /// <summary>DefectDojo test title for OSV-Scanner CVE findings.</summary>
    protected virtual string SecurityScaOsvTestTitle => $"{SecurityProductName} SCA (osv-scanner)";

    /// <summary>DefectDojo test title for Dependency-Track FPF passthrough findings.</summary>
    protected virtual string SecurityScaDtFpfTestTitle => $"{SecurityProductName} SCA (Dependency-Track FPF)";

    /// <summary>DefectDojo test title for Trivy secrets+misconfig findings.</summary>
    protected virtual string SecuritySecretsTestTitle => $"{SecurityProductName} Secrets+Misconfig (Trivy)";

    // ------------------------------------------------------------------
    // Derived paths (override only when default layout doesn't fit)
    // ------------------------------------------------------------------

    /// <summary>Where all security artifacts land. Default <c>{RootDirectory}/artifacts/security/</c>.</summary>
    protected virtual AbsolutePath SecurityArtifactsDir => RootDirectory / "artifacts" / "security";

    /// <summary>The CycloneDX SBOM. Filename uses the canonical <c>*.cdx.json</c> pattern osv-scanner's extractor requires.</summary>
    protected virtual AbsolutePath SecuritySbomFile => SecurityArtifactsDir / $"{SecurityProductName.ToLowerInvariant()}.cdx.json";

    protected virtual AbsolutePath SecuritySarifOpenGrepFile => SecurityArtifactsDir / "opengrep.sarif";
    protected virtual AbsolutePath SecuritySarifRoslynDir => SecurityArtifactsDir / "roslyn";
    protected virtual AbsolutePath SecuritySarifRoslynFile => SecurityArtifactsDir / "roslyn.sarif";
    protected virtual AbsolutePath SecuritySarifSastFile => SecurityArtifactsDir / "sast.sarif";
    protected virtual AbsolutePath SecuritySarifCveFile => SecurityArtifactsDir / "cve.sarif";
    protected virtual AbsolutePath SecuritySarifTrivyFile => SecurityArtifactsDir / "trivy.sarif";

    // ------------------------------------------------------------------
    // Env-var inputs (initialiser = null silences CS0649; bound by reflection)
    // ------------------------------------------------------------------

    [Parameter("Dependency-Track base URL.", EnvironmentVariable = "TAMP_DT_URL")]
    protected readonly string? SecurityDtUrl = null;

    [Secret("Dependency-Track API key.", EnvironmentVariable = "TAMP_DT_API_KEY")]
    protected readonly Secret? SecurityDtApiKey = null;

    [Parameter("Dependency-Track project UUID for this build.", EnvironmentVariable = "TAMP_DT_PROJECT_UUID")]
    protected readonly string? SecurityDtProjectUuid = null;

    [Parameter("DefectDojo base URL.", EnvironmentVariable = "TAMP_DD_URL")]
    protected readonly string? SecurityDdUrl = null;

    [Secret("DefectDojo API v2 token.", EnvironmentVariable = "TAMP_DD_TOKEN")]
    protected readonly Secret? SecurityDdToken = null;

    [Parameter("DefectDojo engagement id for this build.", EnvironmentVariable = "TAMP_DD_ENGAGEMENT_ID")]
    protected readonly int? SecurityDdEngagementId = null;

    // ------------------------------------------------------------------
    // Targets
    // ------------------------------------------------------------------

    protected virtual Target Sbom => _ => _
        .Description($"Generate a CycloneDX SBOM (spec {SecurityCycloneDxSpecVersion}) into {nameof(SecuritySbomFile)}.")
        .DependsOn(SbomDependencies)
        .Executes(() =>
        {
            SecurityArtifactsDir.CreateDirectory();
            return CycloneDxCli.Generate(s => s
                .SetPath(SecuritySolutionPath)
                .SetOutputDirectory(SecurityArtifactsDir)
                .SetFilename(System.IO.Path.GetFileName(SecuritySbomFile.Value))
                .SetFormat(CycloneDxFormat.Json)
                .SetSpecVersion(SecurityCycloneDxSpecVersion)
                .SetRecursive(true)
                .SetExcludeTestProjects(SecurityExcludeTestProjectsFromSbom)
                .SetMetadataComponentName(SecurityProductName)
                .SetWorkingDirectory(RootDirectory));
        });

    protected virtual Target SecurityScanOpenGrep => _ => _
        .Description("Pattern-based SAST via OpenGrep.")
        .Executes(() =>
        {
            SecurityArtifactsDir.CreateDirectory();
            return OpenGrepCli.Scan(s =>
            {
                foreach (var dir in SecurityScanTargetDirectories) s.AddTarget(dir);
                foreach (var skip in SecurityScanSkipDirs) s.AddExclude(skip);
                s.AddConfig("auto")
                 .SetOutputFile(SecuritySarifOpenGrepFile)
                 .SetQuiet(true)
                 .SetWorkingDirectory(RootDirectory);
            });
        });

    protected virtual Target SecurityScanRoslyn => _ => _
        .Description("Semantic SAST via SonarAnalyzer + Roslynator + NetAnalyzers, captured to per-(project,TFM) SARIFs via MSBuild /p:ErrorLog. Requires Directory.Build.props in the adopter repo to conditionally include the analyzer PackageReferences when IncludeSecurityAnalyzers=true (see Tamp's own Directory.Build.props for the canonical wiring).")
        .DependsOn(SbomDependencies)
        .Executes(() =>
        {
            SecurityArtifactsDir.CreateDirectory();
            SecuritySarifRoslynDir.CreateDirectory();
            foreach (var f in SecuritySarifRoslynDir.GlobFiles("*.sarif")) f.Delete();

            return DotNet.Build(s => s
                .SetProject(SecuritySolutionPath)
                .SetProperty("IncludeSecurityAnalyzers", "true")
                .SetProperty("TreatWarningsAsErrors", "false")
                .SetNoIncremental(true));
        });

    protected virtual Target SecurityScan => _ => _
        .Description($"Combine OpenGrep SARIF + per-(project, TFM) Roslyn SARIFs into {nameof(SecuritySarifSastFile)} via SarifMerge.CombineDistinct.")
        .DependsOn(nameof(SecurityScanOpenGrep), nameof(SecurityScanRoslyn))
        .Executes(() =>
        {
            var logs = new List<SarifLog>();

            if (File.Exists(SecuritySarifOpenGrepFile))
                logs.Add(SarifReader.LoadFromFile(SecuritySarifOpenGrepFile));

            var roslynSarifs = SecuritySarifRoslynDir.GlobFiles("*.sarif").ToList();
            foreach (var sarif in roslynSarifs)
                logs.Add(SarifReader.LoadFromFile(sarif));

            if (roslynSarifs.Count > 0)
            {
                var roslynOnly = SarifMerge.CombineDistinct(roslynSarifs.Select(f => SarifReader.LoadFromFile(f)));
                SarifWriter.WriteToFile(roslynOnly, SecuritySarifRoslynFile);
            }

            var merged = SarifMerge.CombineDistinct(logs);
            SarifWriter.WriteToFile(merged, SecuritySarifSastFile);

            var totalResults = merged.Runs.Sum(r => r.Results?.Count ?? 0);
            Console.WriteLine($"[security] Merged {logs.Count} SARIF source(s) → {SecuritySarifSastFile.Value} ({merged.Runs.Count} runs, {totalResults} distinct findings)");
        });

    protected virtual Target SecurityScanCveSbom => _ => _
        .Description($"Cross-ecosystem SCA: osv-scanner against {nameof(SecuritySbomFile)} → {nameof(SecuritySarifCveFile)}.")
        .DependsOn(nameof(Sbom))
        .Executes(() =>
        {
            SecurityArtifactsDir.CreateDirectory();
            return OsvScannerCli.ScanSource(s => s
                .SetSbomFile(SecuritySbomFile)
                .SetOutputFile(SecuritySarifCveFile)
                .SetFormat(OsvScannerFormat.Sarif)
                .SetAllowNoLockfiles(true)
                .SetWorkingDirectory(RootDirectory));
        });

    protected virtual Target SecurityScanTrivy => _ => _
        .Description($"Trivy fs scan: secrets + IaC misconfig → {nameof(SecuritySarifTrivyFile)}. Vuln scanner deliberately OFF — osv-scanner is the canonical SCA path.")
        .Executes(() =>
        {
            SecurityArtifactsDir.CreateDirectory();
            return TrivyCli.ScanFilesystem(s =>
            {
                s.SetPath(".")
                 .AddScanner(TrivyScanner.Secret)
                 .AddScanner(TrivyScanner.Misconfig)
                 .SetOutputFile(SecuritySarifTrivyFile)
                 .SetQuiet(true)
                 .SetNoProgress(true)
                 .SetWorkingDirectory(RootDirectory);
                foreach (var skip in SecurityScanSkipDirs) s.AddSkipDir(skip);
            });
        });

    protected virtual Target SecurityPush => _ => _
        .Description("Push SBOM to Dependency-Track + merged SAST + CVE + Trivy SARIF + DT FPF to DefectDojo. Both legs are env-var-gated and no-op cleanly when unset.")
        .DependsOn(nameof(Sbom), nameof(SecurityScan), nameof(SecurityScanCveSbom), nameof(SecurityScanTrivy))
        .Executes(async () =>
        {
            string? exportedFpf = null;

            if (!string.IsNullOrEmpty(SecurityDtUrl) && SecurityDtApiKey is not null && !string.IsNullOrEmpty(SecurityDtProjectUuid))
            {
                Console.WriteLine($"[security] Uploading SBOM to Dependency-Track at {SecurityDtUrl} (project {SecurityDtProjectUuid})…");
                var dtSettings = new DependencyTrackSettings { BaseUrl = new Uri(SecurityDtUrl), ApiKey = SecurityDtApiKey };
                using var dt = new DependencyTrackClient(dtSettings);
                var bom = SbomReader.LoadFromFile(SecuritySbomFile);
                var upload = await dt.UploadBomAsync(Guid.Parse(SecurityDtProjectUuid), bom);
                Console.WriteLine($"[security] DT upload token: {upload.Token}; waiting for async analysis…");
                var settled = await dt.WaitForAnalysisCompleteAsync(upload.Token, dtSettings.DefaultAnalysisTimeout, Backoff.Constant(TimeSpan.FromSeconds(3)));
                if (!settled)
                {
                    Console.WriteLine("[security] DT analysis didn't settle within budget; skipping findings export.");
                }
                else
                {
                    exportedFpf = await dt.ExportFindingsAsync(Guid.Parse(SecurityDtProjectUuid));
                    Console.WriteLine($"[security] DT findings exported ({exportedFpf.Length} bytes of FPF JSON).");
                }
            }
            else
            {
                Console.WriteLine("[security] TAMP_DT_URL / TAMP_DT_API_KEY / TAMP_DT_PROJECT_UUID not all set — skipping Dependency-Track push.");
            }

            if (!string.IsNullOrEmpty(SecurityDdUrl) && SecurityDdToken is not null && SecurityDdEngagementId is { } engagementId)
            {
                Console.WriteLine($"[security] Pushing findings to DefectDojo at {SecurityDdUrl} (engagement {engagementId})…");
                var ddSettings = new DefectDojoSettings { BaseUrl = new Uri(SecurityDdUrl), Token = SecurityDdToken };
                using var dd = new DefectDojoClient(ddSettings);

                var sastOptions = new DefectDojoImportOptions
                {
                    ProductName = SecurityProductName,
                    EngagementName = SecurityEngagementName,
                    TestTitle = SecuritySastTestTitle,
                };
                var cveOptions = sastOptions with { TestTitle = SecurityScaOsvTestTitle };
                var trivyOptions = sastOptions with { TestTitle = SecuritySecretsTestTitle };
                var fpfOptions = sastOptions with { TestTitle = SecurityScaDtFpfTestTitle };

                if (File.Exists(SecuritySarifSastFile))
                {
                    var log = SarifReader.LoadFromFile(SecuritySarifSastFile);
                    var r = await dd.ReimportSarifAsync(engagementId, log, sastOptions);
                    Console.WriteLine($"[security] DD SAST SARIF reimport → test {r.TestId}");
                }
                if (File.Exists(SecuritySarifCveFile))
                {
                    var log = SarifReader.LoadFromFile(SecuritySarifCveFile);
                    var r = await dd.ReimportSarifAsync(engagementId, log, cveOptions);
                    Console.WriteLine($"[security] DD CVE SARIF reimport → test {r.TestId}");
                }
                if (File.Exists(SecuritySarifTrivyFile))
                {
                    var log = SarifReader.LoadFromFile(SecuritySarifTrivyFile);
                    var r = await dd.ReimportSarifAsync(engagementId, log, trivyOptions);
                    Console.WriteLine($"[security] DD Trivy SARIF reimport → test {r.TestId}");
                }
                if (exportedFpf is not null)
                {
                    var r = await dd.ReimportScanAsync(DefectDojoScanType.DependencyTrackFpf, engagementId, exportedFpf, fpfOptions);
                    Console.WriteLine($"[security] DD FPF reimport → test {r.TestId}");
                }
            }
            else
            {
                Console.WriteLine("[security] TAMP_DD_URL / TAMP_DD_TOKEN / TAMP_DD_ENGAGEMENT_ID not all set — skipping DefectDojo push.");
            }
        });

    /// <summary>
    /// Read the produced SBOM + per-scanner SARIF files and emit OTel metrics via
    /// <see cref="SecurityPipelineMetrics"/>: <c>tamp.security.sbom.components_count</c>
    /// tagged by producer + component-type, and <c>tamp.security.scan.findings_count</c>
    /// tagged by tool + severity. Files that don't exist (e.g. a scan target was skipped)
    /// are silently no-op'd; files that fail to parse log a stderr warning.
    /// </summary>
    /// <remarks>
    /// Override <see cref="SecurityMetricsToolTagForRoslynSarif"/> /
    /// <see cref="SecurityMetricsProducerTagForSbom"/> if the adopter has substituted
    /// the upstream tool (e.g. swapped CycloneDX → Syft on the Sbom target — set
    /// <see cref="SecurityMetricsProducerTagForSbom"/> to <c>"syft"</c>).
    /// </remarks>
    protected virtual Target SecurityMetrics => _ => _
        .Description("Emit tamp.security.sbom.components_count + tamp.security.scan.findings_count OTel metrics from the produced SBOM + SARIF files.")
        .DependsOn(nameof(Sbom), nameof(SecurityScanOpenGrep), nameof(SecurityScanRoslyn), nameof(SecurityScanCveSbom), nameof(SecurityScanTrivy))
        .Executes(() =>
        {
            // SBOM → components_count tagged by (producer, type).
            SecurityPipelineMetrics.EmitComponentsFromBom(SecuritySbomFile, SecurityMetricsProducerTagForSbom);

            // Per-scanner SARIF → findings_count tagged by (tool, severity).
            SecurityPipelineMetrics.EmitFindingsFromSarif(SecuritySarifOpenGrepFile, "opengrep");
            SecurityPipelineMetrics.EmitFindingsFromSarif(SecuritySarifCveFile, "osvscanner");
            SecurityPipelineMetrics.EmitFindingsFromSarif(SecuritySarifTrivyFile, "trivy");

            // Roslyn ships per-(project, TFM) SARIFs in the dir. Emit one batched count per file —
            // the (project, TFM) detail is intentionally NOT a metric tag (high-cardinality cost);
            // every roslyn SARIF in the dir aggregates under tool="roslyn".
            if (SecuritySarifRoslynDir.DirectoryExists())
            {
                foreach (var sarif in SecuritySarifRoslynDir.GlobFiles("*.sarif"))
                {
                    SecurityPipelineMetrics.EmitFindingsFromSarif(sarif, SecurityMetricsToolTagForRoslynSarif);
                }
            }
        });

    /// <summary>Tool tag emitted for each Roslyn per-(project,TFM) SARIF. Default <c>"roslyn"</c>.</summary>
    protected virtual string SecurityMetricsToolTagForRoslynSarif => "roslyn";

    /// <summary>Producer tag emitted for the SBOM. Default <c>"cyclonedx"</c> (matches the dotnet-CycloneDX path). Override to <c>"syft"</c> when <see cref="Sbom"/> has been swapped to Syft.</summary>
    protected virtual string SecurityMetricsProducerTagForSbom => "cyclonedx";

    protected virtual Target Security => _ => _
        .Description("End-to-end security chain: Sbom + SAST merge + SCA (osv) + Trivy secrets/misconfig + metrics emission + (env-gated) DT/DD push.")
        .DependsOn(nameof(Sbom), nameof(SecurityScan), nameof(SecurityScanCveSbom), nameof(SecurityScanTrivy), nameof(SecurityMetrics), nameof(SecurityPush));
}
