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

**`Tamp.Core` 1.11.1** is the current public API; the `Tamp.*` NuGet prefix is reserved to the project ([nuget.org/profiles/tamp](https://www.nuget.org/profiles/tamp)). **50+ first-party satellite packages** are live and pin against core via standard `PackageReference`. The dogfood Release pipeline (3-OS × multi-TFM matrix, refuses to publish if the commit's CI hasn't passed) ships every satellite end-to-end through Tamp itself.

Latest surface additions worth knowing: full TAMP001–TAMP006 analyzer family bundled with `Tamp.Core` (1.9.0+); `Tamp.Polling.Until` async helper (1.10.0+); native filesystem surface on `AbsolutePath` (1.8.0+); `Secret.Reveal()` public + TAMP004-gated (1.6.0+); async `Executes(Func<Task>)` overloads (1.5.0+); `tamp init` scaffolder with `minimal` / `library` / `monorepo` templates; object-init overloads on every wrapper; ADR-0018 diagnostics emission contract (three `ActivitySource`s + the `Tamp.Build` `Meter`) feeding into `tamp-beacon` and downstream dashboards.

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

Full rationale: [ADR 0002 — Package naming and versioning](docs/adr/0002-package-naming.md).

---

## Ecosystem — 50+ first-party satellites

The wiki's **[Module Catalog](https://github.com/tamp-build/tamp/wiki/Module-Catalog)** is the canonical reference for every published `Tamp.*` package — what it wraps, latest version, and source repo. Categories include:

- **.NET toolchain** — `Tamp.NetCli.V8/9/10`, `Tamp.EFCore.V8/9/10`, `Tamp.Coverlet.V6`, `Tamp.ReportGenerator.V5`, `Tamp.DotNetCoverage.V18`, `Tamp.GitVersion.V6`
- **Containers + cluster ops** — `Tamp.Docker.V27`, `Tamp.Helm.V3`, `Tamp.Sccache`, `Tamp.AdjacentContainer(.Provisioning)`, `Tamp.Testcontainers.V4`
- **JavaScript / TypeScript** — `Tamp.Yarn.V4`, `Tamp.Npm.V10`, `Tamp.Turbo.V2`, `Tamp.Vite.V5`, `Tamp.Playwright.V1`, `Tamp.GraphQLCodegen.V5`, `Tamp.Eslint.V9`
- **Rust + desktop ship chain** — `Tamp.Cargo`, `Tamp.Tauri.V2`, `Tamp.Msix`, `Tamp.MicrosoftStoreCli`
- **Azure + ADO** — `Tamp.AzureCli.V2`, `Tamp.Bicep`, `Tamp.AzureAppService`, `Tamp.Kudu`, `Tamp.PostgresFlex`, `Tamp.AzureFunctionsCoreTools.V4`, `Tamp.AzureStaticWebApps.V2`, `Tamp.ServiceBus.V7/8`, `Tamp.AdoGit`, `Tamp.AdoRest.V7`, `Tamp.AdoServiceConnection.V1`
- **Database deploy** — `Tamp.SqlPackage`, `Tamp.SqlCmd`, `Tamp.MSBuildClassic`, `Tamp.MsDeploy`
- **Supply-chain security** — `Tamp.TruffleHog.V3`, `Tamp.CodeQL.V2`, `Tamp.Syft`, `Tamp.Grype`, `Tamp.SonarScanner.V10`, `Tamp.OpenGrep`, `Tamp.OsvScanner.V2`, `Tamp.Trivy`, `Tamp.CycloneDx.V6`, `Tamp.DependencyTrack.V1`, `Tamp.DefectDojo.V2`, `Tamp.Security.Pipeline`
- **Source control + tracking** — `Tamp.GitHubCli.V2`, `Tamp.YouTrack`
- **Foundation** — `Tamp.Http`, `Tamp.Sarif`, `Tamp.Sbom`, `Tamp.Templates.AspNet`
- **Editor integration** — **[Tamp for VS Code](https://github.com/tamp-build/tamp-vscode)** (`.vsix` sideload from the repo's GitHub Releases; activity-bar targets tree, Run / Dry Run / View Plan, CodeLens, hover docs, run history)

All satellites ship through Tamp itself — `dotnet tamp Ci && dotnet tamp Push` running in the satellite repo's CI, dogfooding the framework end-to-end. See any satellite's `build/Build.cs` and `.github/workflows/release.yml` for the pattern.

---

## Downstream consumers — dashboards + observability

Tamp builds emit a frozen diagnostics contract (ADR-0018): three `ActivitySource`s (`Tamp.Build` / `Tamp.Build.Targets` / `Tamp.Build.Commands`), one `Meter` (`Tamp.Build`) with counters and histograms covering builds / targets / commands / memory / outcomes. Any OTel-compatible receiver picks it up — and two first-party consumers ship as self-hostable images so you can stand up the whole observability + evidence stack without an SaaS dependency:

- **[`tamp-beacon`](https://github.com/tamp-build/tamp-beacon)** — self-hosted OTLP receiver + dashboard. Ingests the ADR-0018 contract as-is, persists builds / targets / commands to bundled Postgres, surfaces a browser dashboard with queryable history, and pushes Web Push notifications on failure. Single Docker image, multi-arch (`linux/amd64` + `linux/arm64`). Just point your build's `OTEL_EXPORTER_OTLP_ENDPOINT` at it. Repo: [`tamp-build/tamp-beacon`](https://github.com/tamp-build/tamp-beacon).
- **[`tamp-findings`](https://github.com/tamp-build/tamp-findings)** — security / quality dashboard with federal-readiness evidence. Ingests SARIF (via `/ingest/findings`), CycloneDX SBOMs, coverage reports, test results, SLSA / in-toto / DSSE provenance, and per-scanner receipts; scores each build against a configurable risk policy; produces CISA SSDF attestations, VEX, POA&M, KEV exposure tracking, and VDP metadata. Built on `Tamp.Security.Pipeline` and dogfooded on itself. Token-based ingest (`cli_` / `prj_` bearer tokens, SHA-256 hashed). The egress contract is published as the [**tamp-ingest-v1**](https://github.com/tamp-build/tamp-findings#tamp-ingest-v1) spec so other sinks can implement against the same shape. Repo: [`tamp-build/tamp-findings`](https://github.com/tamp-build/tamp-findings).

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

**v1.x — Ecosystem fill (current).** ADR backfill, per-satellite wiki pages, additional wrappers as adopters ask.

**v2 — Adoption.** Schema-driven wrapper generation with AI-assisted bootstrapping from `--help` output. JetBrains Fleet extension (`tamp-fleet`) — sibling to the VS Code extension, surfaces the same target tree + run actions inside Fleet. MCP server mode (`tamp :mcp-server`) exposing targets as callable tools. Migration guides from NUKE and Cake. Community module template.

**Explicitly out of scope:** distributed builds (Bazel-style remote execution is a different project). Build script DSLs (Tamp builds are .NET console projects, period).

---

## Governance

Community-maintained. Contributions from humans and AI agents welcome.

The package convention is the contract: anyone can publish `Tamp.{ToolFamily}.V{N}` packages without coordinating with core maintainers. Core maintainers reserve the right to bless packages as "official first-party" but do not gatekeep what can exist.

License: MIT — see [LICENSE](LICENSE) and [ADR 0007](docs/adr/0007-license-mit.md) for the rationale.
