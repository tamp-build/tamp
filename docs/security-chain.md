# Tamp Security & Compliance Chain (Wave 1)

The Wave 1 security chain wires six packages into a single end-to-end
flow: build → SBOM → SAST → Dependency-Track → DefectDojo. Every step
produces a standards-shaped artifact (CycloneDX or SARIF) that survives
tool replacement.

The chain is **opt-in**: adopters wire it into their `TampBuild` via the
satellites below. Tamp itself dogfoods the chain — see
[`build/Build.cs`](../build/Build.cs) `Sbom` / `SecurityScan` /
`SecurityPush` / `Security` targets for a reference recipe.

## The packages

| Package | Role | What it emits |
|---|---|---|
| [`Tamp.Sarif`](../src/Tamp.Sarif) | Contract package | Typed `SarifLog` record + `IFindingSource` interface (SARIF 2.1.0) |
| [`Tamp.Sbom`](../src/Tamp.Sbom) | Contract package | Typed `CycloneDxBom` record + `ISbomSource`/`ISbomSink` (CycloneDX 1.6/1.7 with first-class VEX) |
| [`Tamp.CycloneDx.V6`](../src/Tamp.CycloneDx.V6) | SBOM producer (.NET) | CommandPlan wrapping `dotnet-CycloneDX 6.x`; emits a CycloneDX JSON BOM. Best signal for managed .NET projects (just the NuGet graph; ~18 components on Tamp). |
| [`Tamp.Syft.V1`](../src/Tamp.Syft.V1) | SBOM producer (universal) | CommandPlan wrapping Anchore `syft 1.x`. Three subcommands — `ScanDirectory` / `ScanImage` / `ScanArchive`. Catalogs every ecosystem syft knows (npm / PyPI / Cargo / Go / Maven / NuGet / Gem / Composer / Pub / Conan / etc.) plus file-level cataloging. The right SBOM producer for non-.NET source trees and for container images. Wave 2. |
| [`Tamp.OpenGrep`](../src/Tamp.OpenGrep) | SAST producer (pattern) | CommandPlan wrapping `opengrep 1.x`; emits SARIF findings. See the package README for the GitHub-Releases install path (no winget / scoop / brew / PyPI / NuGet distribution). |
| Roslyn analyzers (built-in path) | SAST producer (semantic) | `SonarAnalyzer.CSharp` + `Roslynator.Analyzers` + `Microsoft.CodeAnalysis.NetAnalyzers` activated via `/p:IncludeSecurityAnalyzers=true`; per-(project, TFM) SARIF via MSBuild's `/p:ErrorLog`. No separate satellite — see TAM-248. |
| [`Tamp.OsvScanner.V2`](../src/Tamp.OsvScanner.V2) | SCA producer (cross-ecosystem) | CommandPlan wrapping `osv-scanner 2.x`; reads the CycloneDX SBOM and queries OSV.dev (npm / PyPI / Cargo / Go / Maven / NuGet / Packagist / Pub). SARIF output, slots into the chain like the SAST sources but kept as a separate file (tamp-cve.sarif) so DefectDojo can route SAST and SCA to different triage queues. Wave 2 — pulled forward for non-.NET coverage. |
| [`Tamp.DependencyTrack.V1`](../src/Tamp.DependencyTrack.V1) | SBOM/CVE/VEX hub | REST client for OWASP Dependency-Track v4.x (upload BOM, wait for analysis, export FPF) |
| [`Tamp.DefectDojo.V2`](../src/Tamp.DefectDojo.V2) | Findings sink | REST client for DefectDojo v2 (import / reimport SARIF or FPF) |
| [`Tamp.Security.Pipeline`](../src/Tamp.Security.Pipeline) | One-import meta-package | Abstract `SecurityPipelineBuild : TampBuild`. Adopters inherit, override `SecurityProductName` + `SecuritySolutionPath`, and get every Sbom / SecurityScan* / SecurityPush / Security target above for free. The recipe below is what the base class implements. Wave 3. |

Plus one Core addition:

| Helper | Role |
|---|---|
| [`Tamp.Polling.Until`](../src/Tamp.Core/Polling.cs) | Generic poll-until-condition helper. First consumer is the DT analysis wait; designed to also fit Azure deployments and NuGet index propagation. |

## Quick adoption — `SecurityPipelineBuild`

Drop the meta-package PackageReference into your build script and
inherit. Override two properties. Done.

```csharp
using Tamp;
using Tamp.NetCli.V10;
using Tamp.Security.Pipeline;

class Build : SecurityPipelineBuild
{
    public static int Main(string[] args) => Execute<Build>(args);

    [Solution] readonly Solution Solution = null!;
    [Parameter] Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    protected override string SecurityProductName  => "HoldFast";   // or "Strata", etc.
    protected override string SecuritySolutionPath => Solution.Path;

    Target Restore => _ => _.Executes(() => DotNet.Restore(s => s.SetProject(Solution.Path)));
    Target Compile => _ => _.DependsOn(nameof(Restore))
                            .Executes(() => DotNet.Build(s => s.SetProject(Solution.Path)
                                                              .SetConfiguration(Configuration)));
    // … your other targets stay as-is
}
```

`tamp Security` now runs the whole chain. Set
`TAMP_DT_URL/TAMP_DT_API_KEY/TAMP_DT_PROJECT_UUID` to push to
Dependency-Track and/or `TAMP_DD_URL/TAMP_DD_TOKEN/TAMP_DD_ENGAGEMENT_ID`
to push to DefectDojo. Without them `SecurityPush` logs a clean skip
and the producer half still runs.

For the Roslyn semantic-SAST leg you also need a `Directory.Build.props`
that conditionally includes the analyzer PackageReferences when
`IncludeSecurityAnalyzers=true` — see Tamp's own
[`Directory.Build.props`](../Directory.Build.props) for the canonical
wiring (SonarAnalyzer.CSharp + Roslynator.Analyzers, scoped to non-test
projects, with `<ErrorLog>` set to a per-(project, TFM) SARIF path).

Every inherited target is `virtual` — override `Sbom`,
`SecurityScanTrivy`, etc. when defaults don't fit your repo layout
(e.g. non-.NET adopters swap `Sbom` to use `Tamp.Syft.V1`).

## Three stages: collect, publish, enforce

`tamp Security` runs three ordered stages, and the ordering is the contract.

```
SecurityCollect   Sbom + SecurityScan(OpenGrep, Roslyn) + SecurityScanCveSbom + SecurityScanTrivy
      |           every scanner runs; nothing is enforced
      v
SecurityMetrics   metrics emitted          }  publish stage, unconditional:
SecurityPush      SBOM + SARIF pushed      }  never contingent on what the findings say
      |
      v
SecurityGate      read the evidence, write the receipt, fail on policy
```

**Why publication comes before enforcement.** The obvious way to enforce a policy is to fail a scan
target when findings exceed a threshold. Do that and both publishing targets are skipped, so the SBOM
is not pushed, the SARIF is not uploaded and no metrics are emitted — **precisely in the runs where
findings exist.** Evidence then arrives reliably when there is nothing to report and unreliably when
there is. Every stage from `SecurityCollect` onward is declared `AssuredAfterFailure()`, so failing
the gate costs no evidence, and one scanner crashing does not suppress the others' output.

### Three outcomes per scanner, not two

| State | Meaning | How it is reported |
| --- | --- | --- |
| **Completed clean** | the scanner ran and found nothing — a positive result | `status{status="completed"}`, no findings points, `IsClean` true |
| **Completed with findings** | the scanner ran and found something; policy decides whether that blocks | `status{status="completed"}` plus `findings_count` points |
| **Not run / unreadable** | no reliable result exists | `status{status="not_run"}` or `"unreadable"`, **no** findings points |

Before this, a missing SARIF emitted nothing at all and `findings_count` reported the same
observation — zero — for "this scanner ran and found nothing" and "this scanner never ran". The absent
case is the one that should raise an alarm, so it is now the one that is loudest.

`tamp.security.scan.status` and `tamp.security.sbom.status` are what carry it. **Alert on
`status="not_run"`**: `findings_count` alone cannot distinguish a clean build from an absent scan,
because a clean scan emits no findings point either.

### Writing a policy

Thresholds belong to the adopter, so there are none built in. Override `EnforceSecurityPolicy` and
throw:

```csharp
protected override void EnforceSecurityPolicy(SecurityEvidenceSummary evidence)
{
    var blocking = evidence.FindingsAtOrAbove("error");
    if (blocking > 0)
        throw new InvalidOperationException($"{blocking} error-level finding(s) exceed policy.");
}
```

It runs in `SecurityGate`, after both publishing targets, so throwing here loses nothing that was
already published. `evidence` carries per-scanner counts, the SBOM, the publication outcomes and
`ReliabilityProblems`.

### Evidence integrity is enforced; severity is not

`SecurityGate` fails by default when the evidence cannot be relied on — an expected scan that did not
run, an unreadable output, an absent or empty SBOM, or a publication that was attempted and did not
land. That is not a severity policy; it is the refusal to treat an unknown outcome as a good one.

> **This is the one change that can fail a build that used to pass.** A scanner that is not installed
> previously produced a skipped target, zero findings and a green build. There are three ways to go
> green again, and the first two are the honest ones:
>
> 1. install the scanner;
> 2. remove it from `SecurityExpectedScans`, which states that the surface is deliberately not
>    covered;
> 3. set `SecurityRequireReliableEvidence` to `false`, which keeps the receipt and the `status`
>    metrics so the gap stays visible even though it no longer blocks.

An empty SBOM counts as unreliable for a reason worth stating: every SCA result derived from a
component-less SBOM is *vacuously* clean, which is the most convincing kind of false negative.

### The receipt

`SecurityGate` writes `artifacts/security/security-receipt.json` — what ran, what each scanner found,
and what could not be believed. It is written **before** the gate throws, because the run where
evidence matters most is the run where something went wrong.

```json
{
  "schema": "tamp.security.receipt/1",
  "scans": [
    { "tool": "opengrep", "status": "completed", "findings": 3, "reliable": true },
    { "tool": "trivy",    "status": "not_run",   "findings": null, "reliable": false,
      "detail": "no SARIF at this path: the scan was skipped, or the tool died before writing" }
  ],
  "reliable": false,
  "reliabilityProblems": ["trivy: not_run — no SARIF at this path: …"]
}
```

`findings` is `null`, not `0`, for a scan with no result — a reader cannot mistake "no result" for
"found nothing". Publish the receipt: a green publishing job is weak evidence that a consumer accepted
anything, because most report ingestion is asynchronous. The receipt is what gives a *missing* report
a single meaning.

## End-to-end recipe (manual wiring)

This is the canonical flow the meta-package implements. Use this if
you can't (or won't) inherit `SecurityPipelineBuild`.

```csharp
using Tamp;
using Tamp.Sarif;
using Tamp.Sbom;
using Tamp.CycloneDx.V6;
using Tamp.OpenGrep;
using Tamp.DependencyTrack.V1;
using Tamp.DefectDojo.V2;

class Build : TampBuild
{
    AbsolutePath SecurityDir => RootDirectory / "artifacts" / "security";
    AbsolutePath SbomFile    => SecurityDir / "bom.json";
    AbsolutePath SarifFile   => SecurityDir / "scan.sarif";

    [Parameter(EnvironmentVariable = "TAMP_DT_URL")]          readonly string? DtUrl = null;
    [Secret   (EnvironmentVariable = "TAMP_DT_API_KEY")]      readonly Secret? DtApiKey = null;
    [Parameter(EnvironmentVariable = "TAMP_DT_PROJECT_UUID")] readonly string? DtProjectUuid = null;
    [Parameter(EnvironmentVariable = "TAMP_DD_URL")]          readonly string? DdUrl = null;
    [Secret   (EnvironmentVariable = "TAMP_DD_TOKEN")]        readonly Secret? DdToken = null;
    [Parameter(EnvironmentVariable = "TAMP_DD_ENGAGEMENT_ID")] readonly int? DdEngagementId = null;

    Target Sbom => _ => _
        .Executes(() => CycloneDx.Generate(s => s
            .SetPath(Solution.Path)
            .SetOutputDirectory(SecurityDir)
            .SetFilename("bom.json")
            .SetFormat(CycloneDxFormat.Json)
            .SetRecursive(true)
            .SetExcludeTestProjects(true)));

    Target SecurityScan => _ => _
        .Executes(() => OpenGrep.Scan(s => s
            .AddTarget("src").AddTarget("tests")
            .AddConfig("auto")
            .SetOutputFile(SarifFile)
            .SetQuiet(true)));

    Target SecurityPush => _ => _
        .DependsOn(nameof(Sbom), nameof(SecurityScan))
        .Executes(async () =>
        {
            string? fpf = null;

            if (DtUrl is not null && DtApiKey is not null && DtProjectUuid is not null)
            {
                using var dt = new DependencyTrackClient(new()
                {
                    BaseUrl = new Uri(DtUrl),
                    ApiKey  = DtApiKey,
                });
                var upload  = await dt.UploadBomAsync(Guid.Parse(DtProjectUuid), SbomReader.LoadFromFile(SbomFile));
                var settled = await dt.WaitForAnalysisCompleteAsync(upload.Token, TimeSpan.FromMinutes(5));
                if (settled) fpf = await dt.ExportFindingsAsync(Guid.Parse(DtProjectUuid));
            }

            if (DdUrl is not null && DdToken is not null && DdEngagementId is { } eid)
            {
                using var dd = new DefectDojoClient(new() { BaseUrl = new Uri(DdUrl), Token = DdToken });
                if (File.Exists(SarifFile))
                    await dd.ReimportSarifAsync(eid, SarifReader.LoadFromFile(SarifFile));
                if (fpf is not null)
                    await dd.ReimportScanAsync(DefectDojoScanType.DependencyTrackFpf, eid, fpf);
            }
        });

    Target Security => _ => _.DependsOn(nameof(Sbom), nameof(SecurityScan), nameof(SecurityPush));
}
```

Run it:

```sh
# Producer-only (no DT/DD needed; artifacts land in ./artifacts/security/):
tamp Security

# Full chain (set the env vars then re-run):
export TAMP_DT_URL=https://dt.example.com
export TAMP_DT_API_KEY=<key>
export TAMP_DT_PROJECT_UUID=<uuid>
export TAMP_DD_URL=https://dd.example.com
export TAMP_DD_TOKEN=<token>
export TAMP_DD_ENGAGEMENT_ID=42
tamp Security
```

When the DT or DD env vars aren't set, `SecurityPush` logs a clean skip
and returns success — the producer half stays green even before the
hubs are reachable.

## Is Dependency-Track required?

**No.** The chain works with just OSV-Scanner → DefectDojo for SCA.
Tamp.DependencyTrack.V1 is opt-in: when the `TAMP_DT_*` env vars are
unset, `SecurityPush` skips the DT leg cleanly and only pushes the
OSV-Scanner CVE SARIF to DefectDojo.

DT is the right choice when you need any of:
- **Continuous monitoring** — alerts when a newly-published CVE matches
  a package already in an uploaded SBOM (without re-running the chain).
- **Portfolio-wide queries** — "which of our N projects use vulnerable
  log4j 2.x?" answered in one call.
- **Persistent VEX state** — mark a CVE not-exploitable once; survives
  every re-upload of the SBOM, so noise stays suppressed across builds.
- **Multi-source CVE reconciliation** — DT pulls NVD + GHSA + OSV + OSS
  Index + (optional) VulnDB in one feed.
- **Federal compliance evidence trail** — auditor-grade record of
  what-was-known-when, paired with VEX justifications.

For single-repo or laptop-scale evaluation, OSV-Scanner + DefectDojo
covers ~80% of DT's value at zero operational cost. Stand up DT when
the portfolio or compliance story is real.

## Why two SAST sources

OpenGrep is a pattern-matcher. The C# ruleset on Semgrep Registry is
~30 rules vs. SonarQube's ~700 — structurally thin coverage for C#.
The chain pairs it with Roslyn analyzers (SonarAnalyzer.CSharp +
Roslynator + NetAnalyzers) emitted as SARIF via MSBuild's `/p:ErrorLog`.

| Source | What it catches | What it misses |
|---|---|---|
| OpenGrep | Codified policies, hardcoded secrets, generic OWASP patterns, multi-language sweeps | Deep C# semantics — dataflow, taint, type-aware checks |
| Roslyn analyzers (Sonar + Roslynator + NetAnalyzers) | Dataflow-aware C# issues (`S6966 await WriteLineAsync`, `CA1510 ArgumentNullException.ThrowIfNull`, `S127 stop-condition mutation`, ~1400 findings on a clean Tamp tree) | Cross-language patterns; codified non-C# policy rules |

Both emit SARIF. `SecurityScan` merges them via
`Tamp.Sarif.SarifMerge.CombineDistinct` — runs are preserved separately, so a consumer still sees
every tool that ran, and duplicate results are collapsed. That dedup is the point here: the Roslyn
leg emits one copy of each source-level finding per target framework via `/p:ErrorLog`, and
`CombineDistinct` folds those back to one entry.

> **Only code-scanner SARIF goes through this merge** ([#155](https://github.com/tamp-build/tamp/issues/155)).
> The dedup key is `(ruleId, URI, line, column, logical fullyQualifiedName)` — it has no component or
> version term of its own. A dependency scanner reports the advisory as `ruleId`, the lockfile as the
> URI, the same line for every result, and the affected version only in the message text, so
> **distinct affected versions of one package collapse into one result** — measured on grype
> 0.112.0's native SARIF, 299 results become 268. That is why the CVE and Trivy SARIFs are published
> separately rather than merged into `sast.sarif`, and why adding them to this merge would silently
> under-report.
>
> A producer that puts component identity in `logicalLocations.fullyQualifiedName` is safe under the
> default key — `Tamp.Grype` 0.2.0 puts the purl there, and the same document passes through 299 →
> 299. Otherwise use `SarifMerge.Combine`, or pass a key selector to the `CombineDistinct` overload.

Note also that `CombineDistinct` collapses duplicates *across* runs, so a finding two tools both
reported is attributed to whichever run came first in input order. For deliberately overlapping
scanners, where agreement between two independent tools is itself a signal, use `Combine`.

## Locked decisions

These are settled and not revisited without a TAM ticket pinning the
reversal rationale:

| Decision | Why |
|---|---|
| **OpenGrep over Semgrep** | License stability — multi-vendor governance, no proprietary Pro tier that can paywall rules. The OSS engine is the same fork point; SARIF output is identical. |
| **CycloneDX over SPDX** | First-class inline VEX (`vulnerability.analysis.state` / `.justification` / `.response`). SPDX needs a sidecar file for the same thing. |
| **FPF flows raw end-to-end** | DT exports its Finding Packaging Format; DefectDojo ingests FPF as a known `scan_type`. No SARIF normalisation in the middle — preserves DT-specific suppression rationale. |
| **`reimport-scan` is the default after the first push** | DefectDojo reconciles against the prior scan in the engagement: marks closed CVEs inactive, reactivates regressions, preserves triage notes. `import-scan` loses history. |
| **DT analysis-complete uses `Polling.Until`, not webhooks** | Webhooks aren't testable from build runners. Polling is honest, observable, and bounded. |
| **Beacon emits per-scan counts + durations only** | No per-finding events. High-cardinality cost; per-scan tags (tool, severity) are enough for trend dashboards. (Implementation pending — TAM-247.) |

## Environment variables

See [`docs/security-env-vars.md`](security-env-vars.md) for the
canonical contract: `TAMP_<TOOL>_<FIELD>` naming, required-vs-optional,
defaults.

## Format choices

| Artifact | Format | Where |
|---|---|---|
| Findings (SAST / IaC / secrets / container) | **SARIF 2.1.0** | OASIS-standard; ingested by every aggregator worth using |
| SBOM | **CycloneDX 1.6+** | OWASP-standard; chosen over SPDX for inline VEX |

Tooling rule: **if a tool can't emit SARIF (for findings) or CycloneDX
(for SBOM), it doesn't join the chain.** That's the adoption gate.

## Wave 2 / Wave 3 (preview)

| Wave | Adds |
|---|---|
| 2 | `Tamp.Syft` (non-.NET SBOM), `Tamp.Trivy` (container + IaC + secrets), `Tamp.Gitleaks` (specialised secrets), `Tamp.Checkov` (specialised IaC) |
| 3 | `Tamp.Cosign` (artifact signing), `Tamp.Slsa` (provenance), `Tamp.Security.Pipeline` (curated meta-package wiring the whole chain) |

Tracked under TAM-235 (Wave 2) and TAM-236 (Wave 3). Both are blocked
by Wave 1 (TAM-234) shipping to nuget.
