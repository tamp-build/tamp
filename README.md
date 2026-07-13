# Tamp

> Pack the build down tight.

A small-core, plugin-driven build automation framework for .NET 10 and beyond. Cross-platform. Honest about resources. Forkable.

---

## Where Tamp came from, and why

NUKE was the right idea executed in a way that didn't survive its maintainer. Every tool wrapper lived in the framework's main assembly, every release was bottlenecked on one person's evenings, and every breaking change in `dotnet`, `docker`, or `sonar-scanner` waited for an upstream cut. When NUKE's lifecycle stalled, the .NET community had no fallback that wasn't also a lifecycle bet.

Tamp fixes the architecture, not the personality. Core stays small. Tool wrappers ship as independently-versioned NuGet packages from independent satellite repos. The host environment — Windows + Defender, Linux in a cgroup-limited pod, macOS with sandbox quirks — is a first-class concept rather than something the framework pretends doesn't exist. Builds run identically on a developer's laptop and in a runner pod, with the framework adapting to what it finds rather than assuming uniformity.

This is a pragmatic project. It does not aspire to be everything. It aspires to be the thing that's still working in five years when the next NUKE has gone quiet.

### Attribution

Tamp draws design lessons from:

- **NUKE** — target authoring style, IDE integration goals, the operator-pain documented in [NUKE Discussion #1564](https://github.com/nuke-build/nuke/discussions/1564) (the governance lessons learned the hard way).
- **Bullseye** — small-core philosophy, target DAG executor.
- **Bazel** — declarative resource consumption, dependency-driven scheduling.
- **Cake** — addin-versioning lessons (good) and DSL-script lessons (avoided).

What's different: Tamp's *architecture* is the resilience strategy. One small core; satellite packages versioned to the tools they wrap; no single bottleneck for the ecosystem.

---

## Status

**`Tamp.Core` 1.13.0** is the current public API; the `Tamp.*` NuGet prefix is reserved to the project ([nuget.org/profiles/tamp](https://www.nuget.org/profiles/tamp)). **70+ first-party packages** are live and pin against core via standard `PackageReference`. The dogfood Release pipeline (3-OS × multi-TFM matrix, refuses to publish if the commit's CI hasn't passed) ships every satellite end-to-end through Tamp itself.

Tamp is actively maintained and in daily production use — it drives dev / test / prod pipelines for several private client projects. Public releases track that work: the security chain (1.11.0) came straight out of an adopter's compliance requirement. **Current focus: reporting and attestation** — see [`tamp-findings`](#downstream-consumers--dashboards--observability) below.

Latest surface additions worth knowing: `Tamp.Security.Pipeline` OTel metrics — findings + component counts (1.13.0); Wave 1+2 security wrappers split out to their own satellite repos (1.12.0); the SBOM → SAST → SCA → secrets → Dependency-Track / DefectDojo chain behind one import (1.11.0, [`docs/security-chain.md`](docs/security-chain.md)); adopter `IBuildReporter` plug-in via `[BuildReporter]` (1.10.0 — **breaking for `IBuildReporter` implementers**: `OnTargetFailed` now takes a `TargetFailureDetail` record carrying the failing target's output tail); `--list --format=json` + NDJSON `--reporter=json` (1.9.0); full TAMP001–TAMP006 analyzer family bundled with `Tamp.Core` (1.9.0+); `Tamp.Polling.Until` async helper (1.11.0+); native filesystem surface on `AbsolutePath` (1.8.0+); `Secret.Reveal()` public + TAMP004-gated (1.6.0+); async `Executes(Func<Task>)` overloads (1.5.0+); ADR-0018 diagnostics emission contract (three `ActivitySource`s + the `Tamp.Build` `Meter`) feeding `tamp-beacon` and downstream dashboards.

> **Deprecated:** `Tamp.Syft.V1` and `Tamp.OpenGrep.V1` (both 1.11.0) were reconciled away in 1.11.1. Use **`Tamp.Syft`** and **`Tamp.OpenGrep`** instead. The old IDs still resolve on nuget.org but receive no further updates.

---

## On-ramp — 30 seconds

```bash
dotnet tool install -g dotnet-tamp
cd your-repo
dotnet tamp init                          # writes build/Build.cs, build/Build.csproj, .config/dotnet-tools.json, tamp.sh/.cmd
dotnet tool restore && dotnet tamp Test
```

`tamp init` works offline (template embedded in the CLI) and won't overwrite an existing scaffold. The global tool ships as two NuGet packages — `Tamp.Cli` (bare command: `tamp ci`) and `dotnet-tamp` (verb form: `dotnet tamp ci`) — same code, pick whichever you prefer.

---

## What a build looks like

A Tamp build is a regular .NET console project that references `Tamp.Core` and the tool modules it needs. Standard C#, standard NuGet, no DSL, no manifest format.

```csharp
using Tamp;
using Tamp.NetCli.V10;
using Tamp.Docker.V27;

class Build : TampBuild
{
    public static int Main(string[] args) => Execute<Build>(args);

    [Parameter] Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    [Secret] readonly Secret RegistryPassword = null!;
    [Solution] readonly Solution Solution = null!;
    [FromPath("docker")] readonly Tool DockerBin = null!;

    Target Clean => _ => _.Executes(() => CleanArtifacts());

    Target Compile => _ => _
        .Default()
        .DependsOn(Clean)
        .Executes(() => DotNet.Build(s => s.SetProject(Solution.Path).SetConfiguration(Configuration)));

    Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() => DotNet.Test(s => s.SetProject(Solution.Path).SetNoBuild(true)));

    Target Pack => _ => _
        .DependsOn(Test)
        .Produces("artifacts/*.nupkg")
        .Executes(() => DotNet.Pack(s => s.SetProject(Solution.Path).SetOutput("artifacts")));

    Target Ci => _ => _.DependsOn(Pack);
}
```

```bash
dotnet tamp ci                        # full pipeline
dotnet tamp pack -c Release           # one target + parameter
dotnet tamp ci --dry-run              # show what would happen, run nothing
dotnet tamp --list-tree               # targets + their dependencies
```

For deeper authoring patterns (target shapes, `[FromPath]` conventions, dependency declaration, `.Before(...)` ordering, the indirect-config pattern, cross-platform path helpers), see the wiki: **[Build Script Authoring](https://github.com/tamp-build/tamp/wiki/Build-Script-Authoring)** and **[Pitfalls](https://github.com/tamp-build/tamp/wiki/Pitfalls)**.

---

## Design philosophy

- **Core stays small.** `Tamp.Core` contains the target dependency graph executor, parameter injection, path utilities, process invocation, host detection, secret handling, and dry-run support. Nothing else. No tool knowledge, no CI YAML generation, no Sonar integration.
- **Modules are independently versioned.** Each tool wrapper is its own NuGet package, on its own release cadence. New `dotnet` SDK → new wrapper. Old wrapper keeps working. No forced flag day.
- **The host is real.** Tamp detects OS, container status, cgroup limits, CI vendor, tool availability. Targets declare what they need; Tamp warns or fails fast when the host can't deliver.
- **Dry runs are mandatory.** Every wrapper produces a `CommandPlan` — a typed description of what would run. The runner either dispatches the plan or prints it. Dry-run output is exactly what would execute.
- **Secrets stay secret.** Sensitive parameters are typed differently from regular parameters. The runner redacts them in logs, dry-run output, error messages, and stack traces. The type system makes leaks hard; the runtime makes them harder.
- **Forkable by default.** Core is small enough that one person can maintain it on weekends. Modules are decoupled enough that abandoning one doesn't break the rest. The architecture is the resilience strategy.

---

## Package convention

```
Tamp.{ToolFamily}.{TargetVersion?}
```

- **`Tamp.`** — fixed brand prefix; reserved on NuGet.
- **`{ToolFamily}`** — what's wrapped: `Core`, `NetCli`, `Docker`, `Sonar`, `Yarn`, `Kubectl`, ...
- **`{TargetVersion}`** — `V{major}` of the wrapped tool, ONLY when the tool's CLI surface breaks across majors (`Tamp.NetCli.V10`, `Tamp.Docker.V27`). Stable-surface tools ship without a version suffix (`Tamp.Yarn`, `Tamp.Kubectl`).

The NuGet semver field tracks the *plugin's own* evolution within that line — so `Tamp.NetCli.V10` 1.0.0 → 1.0.1 → 1.1.0 → 2.0.0 are all wrappers for .NET 10; when .NET 11 ships, a new package `Tamp.NetCli.V11` starts at its own 1.0.0.

Full rationale: [ADR 0002 — Package naming convention](docs/adr/0002-package-naming-convention.md).

---

## Ecosystem — 60+ first-party satellites

The wiki's **[Module Catalog](https://github.com/tamp-build/tamp/wiki/Module-Catalog)** is the canonical reference for every published `Tamp.*` package — what it wraps, latest version, and source repo. Categories include:

- **.NET toolchain** — `Tamp.NetCli.V8/9/10`, `Tamp.EFCore.V8/9/10`, `Tamp.Coverlet.V6`, `Tamp.ReportGenerator.V5`, `Tamp.DotNetCoverage.V18`, `Tamp.GitVersion.V6`
- **Containers + cluster ops** — `Tamp.Docker.V27`, `Tamp.Helm.V3`, `Tamp.Kubectl`, `Tamp.Sccache`, `Tamp.AdjacentContainer(.Local/.Provisioning)`, `Tamp.Testcontainers.V4`
- **JavaScript / TypeScript** — `Tamp.Yarn.V4`, `Tamp.Npm.V10`, `Tamp.Turbo.V2`, `Tamp.Vite.V5`, `Tamp.Playwright.V1`, `Tamp.GraphQLCodegen.V5`, `Tamp.Eslint.V9`
- **Rust + desktop ship chain** — `Tamp.Cargo`, `Tamp.Tauri.V2`, `Tamp.Msix`, `Tamp.MicrosoftStoreCli`
- **Azure + ADO** — `Tamp.AzureCli.V2`, `Tamp.Bicep`, `Tamp.AzureAppService`, `Tamp.Kudu`, `Tamp.PostgresFlex`, `Tamp.AzureFunctionsCoreTools.V4`, `Tamp.AzureStaticWebApps.V2`, `Tamp.ServiceBus.V7/8`, `Tamp.AdoGit`, `Tamp.AdoRest.V7`, `Tamp.AdoServiceConnection.V1`
- **Database deploy** — `Tamp.SqlPackage`, `Tamp.SqlCmd`, `Tamp.MSBuildClassic`, `Tamp.MsDeploy`
- **Supply-chain security** — `Tamp.TruffleHog.V3`, `Tamp.CodeQL.V2`, `Tamp.Syft`, `Tamp.Grype`, `Tamp.SonarScanner.V10/Cli.V6`, `Tamp.OpenGrep`, `Tamp.OsvScanner.V2`, `Tamp.Trivy`, `Tamp.CycloneDx.V6`, `Tamp.DependencyTrack.V1`, `Tamp.DefectDojo.V2`, `Tamp.Security.Pipeline`
- **Accessibility** — `Tamp.AxeCore` (axe-core scan + SARIF emit, feeds the same security pipeline)
- **Attestation + provenance** — `Tamp.GitHubAttest` (`gh attestation` + cosign keyless artifact signing / verification)
- **Observability + notifications** — `Tamp.Telemetry` (OTel emit-side bridge), `Tamp.Telegram` (`IBuildReporter` → Telegram Bot API; Slack + Discord siblings planned), `Tamp.Ingest.V1` (typed client for the `tamp-ingest-v1` egress contract)
- **Source control + tracking** — `Tamp.GitHubCli.V2`, `Tamp.YouTrack`
- **Foundation** — `Tamp.Http`, `Tamp.Sarif`, `Tamp.Sbom`, `Tamp.Templates.AspNet`
- **Editor integration** — **[Tamp for VS Code](https://github.com/tamp-build/tamp-vscode)** (`.vsix` sideload from the repo's GitHub Releases; activity-bar targets tree, Run / Dry Run / View Plan, CodeLens, hover docs, run history)

Only `Tamp.Core`, `Tamp.Cli` / `dotnet-tamp`, `Tamp.NetCli.V8/9/10`, `Tamp.DotNetCoverage.V18`, `Tamp.Sarif`, `Tamp.Sbom`, and `Tamp.Security.Pipeline` ship from this repo. Everything else lives in its own `tamp-build/tamp-*` satellite repo on its own release cadence — including the security wrappers, which moved out in 1.12.0.

All satellites ship through Tamp itself — `dotnet tamp Ci && dotnet tamp Push` running in the satellite repo's CI, dogfooding the framework end-to-end. See any satellite's `build/Build.cs` and `.github/workflows/release.yml` for the pattern.

---

## Downstream consumers — dashboards + observability

Tamp builds emit a frozen diagnostics contract (ADR-0018): three `ActivitySource`s (`Tamp.Build` / `Tamp.Build.Targets` / `Tamp.Build.Commands`), one `Meter` (`Tamp.Build`) with counters and histograms covering builds / targets / commands / memory / outcomes. Any OTel-compatible receiver picks it up — and two first-party consumers ship as self-hostable images so you can stand up the whole observability + evidence stack without an SaaS dependency:

- **[`tamp-beacon`](https://github.com/tamp-build/tamp-beacon)** — self-hosted OTLP receiver + dashboard. Ingests the ADR-0018 contract as-is, persists builds / targets / commands to bundled Postgres, surfaces a browser dashboard with queryable history, and pushes Web Push notifications on failure. Single Docker image, multi-arch (`linux/amd64` + `linux/arm64`). Just point your build's `OTEL_EXPORTER_OTLP_ENDPOINT` at it. Repo: [`tamp-build/tamp-beacon`](https://github.com/tamp-build/tamp-beacon).
- **[`tamp-findings`](https://github.com/tamp-build/tamp-findings)** — security / quality dashboard with federal-readiness evidence ([dashboard overview](https://github.com/tamp-build/tamp-findings/blob/main/docs/dashboard-overview.png)). Ingests SARIF (via `/ingest/findings`), CycloneDX SBOMs, coverage reports, test results, SLSA / in-toto / DSSE provenance, and per-scanner receipts; scores each build against a configurable risk policy; produces CISA SSDF attestations, VEX, POA&M, KEV exposure tracking, and VDP metadata. Built on `Tamp.Security.Pipeline` and dogfooded on itself. Token-based ingest (`cli_` / `prj_` bearer tokens, SHA-256 hashed). The egress contract is published as the [**tamp-ingest-v1**](https://github.com/tamp-build/tamp-findings#tamp-ingest-v1) spec so other sinks can implement against the same shape. Repo: [`tamp-build/tamp-findings`](https://github.com/tamp-build/tamp-findings).

If you build a different consumer (a defect tracker, an evidence vault, a CI insights surface) and want it to receive the same wrapper output without sink-specific glue, implement against the `tamp-ingest-v1` contract — the shape is stable and the producer side already exists.

---

## Documentation

- **[Wiki](https://github.com/tamp-build/tamp/wiki)** — task-shaped guides: Build Script Authoring, Module Catalog, Parameter & Secret Injection, Paths & Filesystem, Failure Handling, Logging & Verbosity, CI Host integrations (GitHub Actions / Azure DevOps / TeamCity), Pitfalls, FAQ, Migrating from NUKE, Migrating from Cake.
- **[ADRs](docs/adr/)** — architectural decision records. The "why" behind every load-bearing choice.
- **[Security chain](docs/security-chain.md)** — the SBOM + SAST + SCA + secrets + sink chain assembled from supply-chain satellites.
- **Per-satellite READMEs** — each `tamp-build/tamp-*` repo ships its own README + CHANGELOG.

---

## Supported .NET versions

Tamp's first-party assemblies (`Tamp.Core`, `Tamp.Cli`, every `Tamp.NetCli.V{N}`, every satellite) multi-target every .NET release Microsoft considers in support — both LTS and STS. We track [Microsoft's support calendar](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) exactly: a TFM gets added the day a new release ships, dropped the day Microsoft EOLs it.

Today: `net8.0;net9.0;net10.0`. Full rationale incl. the federal / regulated VDI consumer cohort: [ADR 0015 — Target framework strategy](docs/adr/0015-target-framework-strategy.md).

---

## Roadmap

**v0 — Walking skeleton.** Shipped 2026-05. Executor, parameter injection, `CommandPlan` dry-run, `Secret` type, host detection, `Tamp.Cli` + `dotnet-tamp`, `Tamp.NetCli.V8/V9/V10`. Tamp self-hosts.

**v1 — Real-world coverage.** Shipped 2026-05. `Tamp.Core` API frozen for satellite pinning. `Tamp.*` NuGet prefix confirmed reserved. Tier-1 .NET + container + JS + supply-chain satellites all live. 3-OS × 3-TFM CI matrix on every satellite. Release workflow refuses to publish if the commit's CI hasn't passed.

**v1.x — Ecosystem fill.** Shipped. ADR backfill (0001–0018), per-satellite wiki pages, [migration guides from NUKE and Cake](https://github.com/tamp-build/tamp/wiki/Migrating-From-NUKE), the security chain, the VS Code extension, `tamp-beacon` + `tamp-findings`. Additional wrappers continue to land as adopters ask.

**v2 — Adoption (current).** Reporting + attestation is the active workstream (`tamp-findings`, `Tamp.GitHubAttest`, SLSA / in-toto / DSSE provenance, CISA SSDF evidence). Also queued: schema-driven wrapper generation with AI-assisted bootstrapping from `--help` output ([ADR 0013](docs/adr/0013-schema-driven-wrappers.md)); JetBrains Fleet extension (`tamp-fleet`), sibling to the VS Code one; MCP server mode (`tamp :mcp-server`) exposing targets as callable tools; `Tamp.Components`; community module template.

**Explicitly out of scope:** distributed builds (Bazel-style remote execution is a different project). Build script DSLs (Tamp builds are .NET console projects, period). CI YAML generation — most teams treat CI config as the source of truth for *when* things run and the build script as the source of truth for *what* runs; Tamp owns the latter and stays out of the former.

---

## Known gaps — read before you migrate

Honest limits, so you find them here rather than halfway through a port:

- **No `Tamp.Components`.** NUKE has `Nuke.Components` (`IRestore`, `ICompile`, `ITest`, `IPack`, `IHazSolution` — interface mixins that hand you pre-composed *targets*). Tamp has **no equivalent**. It's named as a candidate in [ADR 0001](docs/adr/0001-small-core-plugin-architecture.md) and it's on the v2 list, but it is not designed, not built, and not scheduled. If your NUKE build leans on components, you'll write those targets out by hand for now. This is the largest single gap for a NUKE migration — and the item **most worth an outside contributor's ADR** (see [Governance](#governance)).
- **No automated NUKE → Tamp converter.** The [migration guide](https://github.com/tamp-build/tamp/wiki/Migrating-From-NUKE) is thorough, but conversion is manual. A tool has never been scoped.
- **Wrapper codegen isn't built.** Wrappers are hand-authored, which is why `NetCli.V8/V9/V10` carry real duplication. Deferred in [ADR 0013](docs/adr/0013-schema-driven-wrappers.md); tractable by hand at current scale.
- **Logging is in-house, not Serilog.** `Tamp.Core` ships a minimal `Logger` with no external dependencies, routed through a `RedactingTextWriter` so registered secrets are scrubbed. A `Tamp.Logging.Serilog` adapter would be a welcome satellite; nobody has written one.
- **The community module registry is empty.** [`docs/community-modules.md`](docs/community-modules.md) exists and the inclusion criteria are written, but no third-party module is listed yet. Be the first.

---

## Governance

Community-maintained. Contributions from humans and AI agents welcome, and there is **no CLA**. Full rules: [ADR 0009 — Governance and namespace policy](docs/adr/0009-governance-and-namespace-policy.md).

The package convention is the contract: anyone can publish `Tamp.{ToolFamily}.V{N}` packages without coordinating with core maintainers. Core maintainers reserve the right to bless packages as "official first-party" but do not gatekeep what can exist, and will not pursue takedowns on naming grounds alone. Forkability without gatekeeping is the point.

How to engage, by size of change:

- **Typo** → open a PR.
- **Bug fix / new wrapper verb** → [open an issue](https://github.com/tamp-build/tamp/issues) first, then a PR. See [CONTRIBUTING.md](CONTRIBUTING.md).
- **Wrap a new tool** → just publish it as `Tamp.{Tool}`. No permission needed. Then send a PR adding it to [`docs/community-modules.md`](docs/community-modules.md).
- **Change a load-bearing design decision** (e.g. *design `Tamp.Components`*) → open a PR adding an ADR with `Status: Proposed`. Anyone may propose one (ADR 0009 §3.1); lazy consensus carries it after 7 days without objection.

Decision-making is currently BDFL with a single maintainer ([MAINTAINERS.md](MAINTAINERS.md)); ADR 0009 §2.5 retires that rule automatically once the team reaches four maintainers. Two process ADRs bound the review loop in both directions: [0016](docs/adr/0016-decision-silence-forfeits.md) (maintainer silence forfeits the right to weigh in) and [0017](docs/adr/0017-pr-staleness-autoclose.md) (contributor silence on review feedback auto-closes the PR — reopenable freely).

Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md); vulnerabilities go through [SECURITY.md](SECURITY.md).

License: MIT — see [LICENSE](LICENSE) and [ADR 0007](docs/adr/0007-license-mit.md) for the rationale.
