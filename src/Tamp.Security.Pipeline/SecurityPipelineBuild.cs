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
        // #151 collect stage: assured so one scanner's failure cannot suppress the others'
        // evidence, nor the publish stage downstream. The target still reports failed and the
        // build still fails — what changes is that the remaining evidence is produced anyway.
        .AssuredAfterFailure()
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
        // #151 collect stage: assured so one scanner's failure cannot suppress the others'
        // evidence, nor the publish stage downstream. The target still reports failed and the
        // build still fails — what changes is that the remaining evidence is produced anyway.
        .AssuredAfterFailure()
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
        // #151 collect stage: assured so one scanner's failure cannot suppress the others'
        // evidence, nor the publish stage downstream. The target still reports failed and the
        // build still fails — what changes is that the remaining evidence is produced anyway.
        .AssuredAfterFailure()
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
        // #151 collect stage: assured so one scanner's failure cannot suppress the others'
        // evidence, nor the publish stage downstream. The target still reports failed and the
        // build still fails — what changes is that the remaining evidence is produced anyway.
        .AssuredAfterFailure()
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
        // #151 collect stage: assured so one scanner's failure cannot suppress the others'
        // evidence, nor the publish stage downstream. The target still reports failed and the
        // build still fails — what changes is that the remaining evidence is produced anyway.
        .AssuredAfterFailure()
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
        // #151 collect stage: assured so one scanner's failure cannot suppress the others'
        // evidence, nor the publish stage downstream. The target still reports failed and the
        // build still fails — what changes is that the remaining evidence is produced anyway.
        .AssuredAfterFailure()
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
        .DependsOn(nameof(SecurityCollect))
        // #151 publish stage: assured, so evidence is published even when a scanner failed or a
        // policy gate is about to fail the build. Publication must never be contingent on the
        // content of the findings, or evidence arrives reliably only when there is nothing to report.
        .AssuredAfterFailure()
        .Executes(async () =>
        {
            string? exportedFpf = null;
            _securityPublications.Clear();

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
                    // #151: this used to log and continue, so an unsettled analysis and a completed
                    // one both produced a green SecurityPush. The upload may have landed and the
                    // analysis may still finish later, so this is Incomplete rather than Failed —
                    // but it is not success, and the gate sees it.
                    Console.WriteLine("[security] DT analysis didn't settle within budget; no findings export. Recorded as an incomplete publication.");
                    RecordSecurityPublication(new SecurityPublicationReport
                    {
                        Sink = "dependency-track",
                        Status = SecurityPublicationStatus.Incomplete,
                        Detail = $"SBOM uploaded (token {upload.Token}) but analysis did not settle within "
                               + $"{dtSettings.DefaultAnalysisTimeout.TotalSeconds:0}s, so findings were not exported",
                    });
                }
                else
                {
                    exportedFpf = await dt.ExportFindingsAsync(Guid.Parse(SecurityDtProjectUuid));
                    Console.WriteLine($"[security] DT findings exported ({exportedFpf.Length} bytes of FPF JSON).");
                    RecordSecurityPublication(new SecurityPublicationReport
                    {
                        Sink = "dependency-track",
                        Status = SecurityPublicationStatus.Completed,
                        Detail = $"SBOM uploaded and analysis settled; {exportedFpf.Length} bytes of FPF exported",
                    });
                }
            }
            else
            {
                Console.WriteLine("[security] TAMP_DT_URL / TAMP_DT_API_KEY / TAMP_DT_PROJECT_UUID not all set — skipping Dependency-Track push.");
                RecordSecurityPublication(new SecurityPublicationReport
                {
                    Sink = "dependency-track",
                    Status = SecurityPublicationStatus.NotConfigured,
                    Detail = "TAMP_DT_URL / TAMP_DT_API_KEY / TAMP_DT_PROJECT_UUID not all set",
                });
            }

            if (!string.IsNullOrEmpty(SecurityDdUrl) && SecurityDdToken is not null && SecurityDdEngagementId is { } engagementId)
            {
                Console.WriteLine($"[security] Pushing findings to DefectDojo at {SecurityDdUrl} (engagement {engagementId})…");
                var ddSettings = new DefectDojoSettings { BaseUrl = new Uri(SecurityDdUrl), Token = SecurityDdToken };
                using var dd = new DefectDojoClient(ddSettings);
                var imported = 0;

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
                    imported++;
                }
                if (File.Exists(SecuritySarifCveFile))
                {
                    var log = SarifReader.LoadFromFile(SecuritySarifCveFile);
                    var r = await dd.ReimportSarifAsync(engagementId, log, cveOptions);
                    Console.WriteLine($"[security] DD CVE SARIF reimport → test {r.TestId}");
                    imported++;
                }
                if (File.Exists(SecuritySarifTrivyFile))
                {
                    var log = SarifReader.LoadFromFile(SecuritySarifTrivyFile);
                    var r = await dd.ReimportSarifAsync(engagementId, log, trivyOptions);
                    Console.WriteLine($"[security] DD Trivy SARIF reimport → test {r.TestId}");
                    imported++;
                }
                if (exportedFpf is not null)
                {
                    var r = await dd.ReimportScanAsync(DefectDojoScanType.DependencyTrackFpf, engagementId, exportedFpf, fpfOptions);
                    Console.WriteLine($"[security] DD FPF reimport → test {r.TestId}");
                    imported++;
                }

                // Imported is not the same as ingested: DefectDojo's dedupe and the consumer's own
                // processing are asynchronous. This records that the import call returned, which is
                // the strongest claim this build can honestly make.
                RecordSecurityPublication(new SecurityPublicationReport
                {
                    Sink = "defectdojo",
                    Status = SecurityPublicationStatus.Completed,
                    Detail = $"{imported} report(s) reimported into engagement {engagementId}",
                });
            }
            else
            {
                Console.WriteLine("[security] TAMP_DD_URL / TAMP_DD_TOKEN / TAMP_DD_ENGAGEMENT_ID not all set — skipping DefectDojo push.");
                RecordSecurityPublication(new SecurityPublicationReport
                {
                    Sink = "defectdojo",
                    Status = SecurityPublicationStatus.NotConfigured,
                    Detail = "TAMP_DD_URL / TAMP_DD_TOKEN / TAMP_DD_ENGAGEMENT_ID not all set",
                });
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
        .DependsOn(nameof(SecurityCollect))
        // #151 publish stage: see SecurityPush.
        .AssuredAfterFailure()
        .Executes(() =>
        {
            // Read the whole expected set, then emit. Reading through SecurityPipelineMetrics'
            // readers is what makes an absent scan emit status="not_run" instead of emitting
            // nothing at all, which is indistinguishable from a clean scan.
            var evidence = ReadSecurityEvidence();

            if (evidence.Sbom is { } sbom) SecurityPipelineMetrics.Emit(sbom);
            foreach (var scan in evidence.Scans) SecurityPipelineMetrics.Emit(scan);

            Console.WriteLine("[security] evidence:");
            Console.WriteLine(evidence.ToString());
        });

    /// <summary>
    /// The scan outputs this pipeline expects to exist, and the tool tag each is reported under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This list is what turns "did this scan run?" into an answerable question. A scanner that was
    /// never wired and a scanner that crashed are otherwise both simply absent, and no consumer can
    /// tell them apart.
    /// </para>
    /// <para>
    /// <b>An adopter who deliberately drops a scanner should remove it here</b> — that is an explicit
    /// statement that the surface is not covered, rather than a silent gap that the gate will report
    /// as missing evidence. Likewise, an adopter who adds a scanner should add it, or its findings
    /// will never reach the metrics or the gate.
    /// </para>
    /// <para>
    /// The merged <c>sast.sarif</c> is deliberately absent: it is derived from the OpenGrep and
    /// Roslyn outputs, which are both here, and counting it too would double every SAST finding.
    /// </para>
    /// </remarks>
    protected virtual IReadOnlyList<SecurityScanSource> SecurityExpectedScans =>
    [
        new("opengrep", SecuritySarifOpenGrepFile),
        new(SecurityMetricsToolTagForRoslynSarif, SecuritySarifRoslynDir, IsDirectory: true),
        new("osvscanner", SecuritySarifCveFile),
        new("trivy", SecuritySarifTrivyFile),
    ];

    /// <summary>
    /// Read every expected scan plus the SBOM, and fold in the publication outcomes recorded by
    /// <see cref="SecurityPush"/>. Pure: no metrics are emitted and nothing is written.
    /// </summary>
    /// <remarks>
    /// Both the metrics target and the gate call this. It re-reads rather than caching, so the gate's
    /// verdict never depends on whether the metrics target happened to run first.
    /// </remarks>
    protected SecurityEvidenceSummary ReadSecurityEvidence() => new()
    {
        Sbom = SecurityPipelineMetrics.ReadBom(SecuritySbomFile, SecurityMetricsProducerTagForSbom),
        Scans = [.. SecurityExpectedScans.Select(source => SecurityPipelineMetrics.ReadScan(source))],
        Publications = _securityPublications,
    };

    /// <summary>Tool tag emitted for each Roslyn per-(project,TFM) SARIF. Default <c>"roslyn"</c>.</summary>
    protected virtual string SecurityMetricsToolTagForRoslynSarif => "roslyn";

    /// <summary>Producer tag emitted for the SBOM. Default <c>"cyclonedx"</c> (matches the dotnet-CycloneDX path). Override to <c>"syft"</c> when <see cref="Sbom"/> has been swapped to Syft.</summary>
    protected virtual string SecurityMetricsProducerTagForSbom => "cyclonedx";

    /// <summary>
    /// The collect stage, as a named target: every scanner has run and written whatever it produced.
    /// Depending on this rather than on the individual scanners is what lets the publish stage be a
    /// single edge in the graph.
    /// </summary>
    protected virtual Target SecurityCollect => _ => _
        .Description("Collect stage: run every scanner and the SBOM generator to completion. Produces evidence; enforces nothing.")
        .DependsOn(nameof(Sbom), nameof(SecurityScan), nameof(SecurityScanCveSbom), nameof(SecurityScanTrivy))
        // Assured for the same reason as its dependencies: the stage completes so the publish stage
        // downstream is reached even when a scanner failed.
        .AssuredAfterFailure()
        .Executes(() => { });

    /// <summary>
    /// The enforcement stage: read the collected evidence, write the receipt, and apply policy. Runs
    /// <b>after</b> publication, so failing it costs no evidence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two separate things happen here, and only the first is built in.
    /// </para>
    /// <para>
    /// <b>Evidence integrity</b> — when <see cref="SecurityRequireReliableEvidence"/> is set (the
    /// default), the gate fails if any expected scan did not run or could not be parsed, if the SBOM
    /// is absent, unreadable or empty, or if a publication was attempted and did not land. This is
    /// not a severity policy: it is the refusal to treat an unknown outcome as a good one, which is
    /// the failure direction a security control must never take.
    /// </para>
    /// <para>
    /// <b>Policy</b> — thresholds belong to the adopter, so there are none here. Override
    /// <see cref="EnforceSecurityPolicy"/> and throw to fail the build on whatever the policy is. The
    /// summary handed to it carries per-scanner counts and <see cref="SecurityEvidenceSummary.FindingsAtOrAbove"/>.
    /// </para>
    /// </remarks>
    protected virtual Target SecurityGate => _ => _
        .Description($"Enforce stage: read the collected evidence, write {nameof(SecurityReceiptFile)}, fail on unreliable evidence, then apply the adopter's policy via EnforceSecurityPolicy.")
        .DependsOn(nameof(SecurityMetrics), nameof(SecurityPush))
        // Assured so the receipt is written and the verdict reported even when a scanner failed
        // upstream — the run where the evidence matters most is the run where something went wrong.
        .AssuredAfterFailure()
        .Executes(() =>
        {
            var evidence = ReadSecurityEvidence();

            SecurityArtifactsDir.CreateDirectory();
            File.WriteAllText(SecurityReceiptFile.Value, evidence.ToJson());
            Console.WriteLine($"[security] receipt → {SecurityReceiptFile.Value}");
            Console.WriteLine(evidence.ToString());

            var problems = evidence.ReliabilityProblems;
            if (problems.Count > 0)
            {
                Console.WriteLine($"[security] {problems.Count} reliability problem(s):");
                foreach (var problem in problems) Console.WriteLine($"  - {problem}");

                if (SecurityRequireReliableEvidence)
                    throw new InvalidOperationException(
                        $"Security evidence is incomplete, so this build has no information about {problems.Count} "
                        + "thing(s) it is supposed to check — which is not the same as having good news. "
                        + $"See {SecurityReceiptFile.Value}. Problems: {string.Join("; ", problems)}. "
                        + $"Set {nameof(SecurityRequireReliableEvidence)} to false to downgrade this to a warning, "
                        + $"or drop the scanner from {nameof(SecurityExpectedScans)} if it is deliberately not run.");

                Console.WriteLine($"[security] {nameof(SecurityRequireReliableEvidence)} is false — continuing with incomplete evidence.");
            }
            else
            {
                Console.WriteLine($"[security] evidence is complete: {evidence.Scans.Count} scan(s) ran, {evidence.TotalFindings} finding(s) total.");
            }

            EnforceSecurityPolicy(evidence);
        });

    /// <summary>
    /// The policy seam. Default implementation does nothing: a severity threshold is the adopter's
    /// decision, not core's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Throw to fail the build. Because this runs in the enforce stage — after
    /// <see cref="SecurityMetrics"/> and <see cref="SecurityPush"/>, both of which are assured —
    /// failing here costs no evidence: the SBOM is pushed, the SARIFs are uploaded, the metrics are
    /// emitted and the receipt is written before this is called.
    /// </para>
    /// <para>
    /// That ordering is the point. Enforcing by failing a scan target instead would skip publication
    /// in exactly the runs where findings exist, so evidence would arrive reliably only when there
    /// was nothing to report.
    /// </para>
    /// <example>
    /// <code>
    /// protected override void EnforceSecurityPolicy(SecurityEvidenceSummary evidence)
    /// {
    ///     var blocking = evidence.FindingsAtOrAbove("error");
    ///     if (blocking > 0)
    ///         throw new InvalidOperationException($"{blocking} error-level finding(s) exceed policy.");
    /// }
    /// </code>
    /// </example>
    /// </remarks>
    /// <param name="evidence">Everything the collect and publish stages produced.</param>
    protected virtual void EnforceSecurityPolicy(SecurityEvidenceSummary evidence) { }

    /// <summary>
    /// Fail <see cref="SecurityGate"/> when the evidence cannot be relied on — an expected scan that
    /// did not run, an unreadable output, an absent or empty SBOM, or a publication that did not
    /// land. Default <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the one behavioural change in #151 that can fail a build that used to pass.</b> A
    /// scanner that is not installed previously produced a skipped target, zero findings and a green
    /// build; it now fails the gate, because a pipeline that silently does not scan a surface is the
    /// bug being fixed.
    /// </para>
    /// <para>
    /// Two honest ways to go green again: install the scanner, or remove it from
    /// <see cref="SecurityExpectedScans"/> to state that the surface is deliberately not covered.
    /// Setting this to <see langword="false"/> is the third, and it keeps the receipt and the
    /// <c>status</c> metrics, so the gap stays visible even though it no longer blocks.
    /// </para>
    /// </remarks>
    protected virtual bool SecurityRequireReliableEvidence => true;

    /// <summary>
    /// Where <see cref="SecurityGate"/> writes the evidence receipt. Default
    /// <c>{SecurityArtifactsDir}/security-receipt.json</c>.
    /// </summary>
    /// <remarks>
    /// A positive artifact recording what ran, what each scanner found, and what could not be
    /// believed. Publish it: a green publishing job is weak evidence that a consumer accepted
    /// anything, because most report ingestion is asynchronous. The receipt is what gives a missing
    /// report a single meaning.
    /// </remarks>
    protected virtual AbsolutePath SecurityReceiptFile => SecurityArtifactsDir / "security-receipt.json";

    /// <summary>Publication outcomes recorded by <see cref="SecurityPush"/> for the gate to read.</summary>
    private readonly List<SecurityPublicationReport> _securityPublications = [];

    /// <summary>Record one publication leg's outcome. Called by <see cref="SecurityPush"/>.</summary>
    protected void RecordSecurityPublication(SecurityPublicationReport report)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));
        Console.WriteLine($"[security] publication — {report}");
        _securityPublications.Add(report);
    }

    /// <summary>
    /// End-to-end security chain, in three ordered stages: <b>collect</b> (every scanner runs),
    /// <b>publish</b> (evidence and metrics go out unconditionally), <b>enforce</b> (the gate reads
    /// the evidence and applies policy).
    /// </summary>
    /// <remarks>
    /// The ordering is the contract. Publication never depends on what the findings say, so evidence
    /// arrives on the runs that matter; enforcement happens last, so failing the build costs nothing
    /// that was already published.
    /// </remarks>
    protected virtual Target Security => _ => _
        .Description("End-to-end security chain in three stages: collect (scanners + SBOM) → publish (metrics + env-gated DT/DD push, unconditional) → enforce (gate reads the evidence and applies policy).")
        .DependsOn(nameof(SecurityCollect), nameof(SecurityMetrics), nameof(SecurityPush), nameof(SecurityGate));
}
