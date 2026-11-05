---
title: NUKE → Tamp mapping reference
description: Canonical API mapping for migrating a NUKE build to Tamp. Consumed by humans, the `tamp migrate nuke` tool, and agent-guided migration.
---

# NUKE → Tamp mapping reference

The single source of truth for converting a NUKE `Build.cs` to Tamp ([ADR 0021](../adr/0021-nuke-to-tamp-migration.md)). It is **assisted, not automatic** — coverage is bounded by this table and by tool parity.

**Confidence tiers** (per row):

- **`auto`** — deterministic 1:1; a rewriter can apply it mechanically.
- **`assisted`** — mostly mechanical but needs light judgment (a renamed-with-different-semantics method, an optional arg).
- **`manual`** — no clean mapping; must be hand-resolved. The tool flags these with `// TAMP-MIGRATE:` and the agent/human resolves them.

> Scope: **NUKE 8+**. Every row here has a rule in [`mapping.yaml`](mapping.yaml) (schema 0.2), with a stable id that `tamp migrate nuke` reports and cites in its `// TAMP-MIGRATE(<id>):` markers. The Tamp side of every row backed by an in-tree package (Tamp.Core, Tamp.NetCli, Tamp.Components) is checked against the real API by `tests/Tamp.Migrate.Tests`; satellite wrappers' verbs are checked against their source by hand.

---

## 1. Boilerplate & entrypoint

| NUKE | Tamp | Tier | Notes |
|------|------|------|-------|
| `class Build : NukeBuild` | `class Build : TampBuild` | auto | |
| `public static int Main() => Execute<Build>(x => x.Default);` | `public static int Main(string[] args) => Execute<Build>(args);` | auto | NUKE picks the default target via the `Execute` arg; Tamp designates it with `.Default()` on the target (see §4). |
| `using Nuke.Common;` / `Nuke.Common.*` | `using Tamp;` (+ per-tool `using Tamp.NetCli.V10;` etc.) | assisted | Drop `Nuke.Common.*`; add the Tamp namespaces for the wrappers actually used. |
| `build.sh` / `build.cmd` / `.nuke/` | `tamp.sh` / `tamp.cmd` (from `tamp init`) | assisted | Regenerate the bootstrap via `dotnet tamp init`; don't port NUKE's. |
| `class Configuration : Enumeration` (Debug/Release, from `nuke :setup`) | Tamp's built-in `Configuration` | assisted | The template class is removed (and its file, if that leaves it empty); each `Configuration` member is marked, because Tamp's is an enum: write `Configuration.ToString()` where it was used as a string. Any other `Enumeration` is marked: use an enum or a string. |
| `protected override void OnBuildInitialized()` and the other `OnBuild*` / `OnTarget*` hooks | — | manual | `TampBuild` has no lifecycle hooks: move the code into a target that runs first, or the constructor. |
| `Configure<DotNetPackSettings> X => _ => _...` | — | manual | Inline the setters into the `DotNet.X(s => ...)` call. |
| `build/_build.csproj` (`Nuke.Common`, `PackageDownload`) | the same project, referencing `Tamp.Core` + the wrappers used | assisted | `tamp migrate nuke` rewrites it in place. A `PackageDownload` tool becomes a `[NuGetPackage("<id>", Version = "<v>")] Tool` field. |
| `[UnsetVisualStudioEnvironmentVariables]` and other build-attributes | (drop) | manual | No Tamp equivalent; usually unnecessary. |
| `[GitHubActions(...)]` / `[AzurePipelines(...)]` / other CI-generation attributes | — | manual | Tamp doesn't generate CI configuration. Keep the existing pipeline file, point it at `./tamp.sh`, and delete the attribute. |

## 2. Injection attributes

| NUKE | Tamp | Tier | Notes |
|------|------|------|-------|
| `[Parameter] readonly string Foo;` | `[Parameter] readonly string Foo;` | auto | Same attribute name + semantics. |
| `[Solution] readonly Solution Solution;` | `[Solution] readonly Solution Solution;` | auto | `Solution.Path` in both. |
| `[Secret] [Parameter] readonly string Token;` | `[Secret] readonly Secret Token;` | assisted | Tamp models secrets as a distinct `Secret` type (ADR 0005), not a string; call sites use `Token` / `Token.Reveal()`. |
| `[PathVariable]` / `[PathExecutable] readonly Tool Git;` | `[FromPath("git")] readonly Tool Git;` | auto | Both resolve a tool from PATH into a `Tool`. |
| `Git("status --short")` (calling that field) | `Git.Plan("status", "--short")` | assisted | A NUKE `Tool` is a delegate that parses one argument string; a Tamp `Tool` builds a `CommandPlan` from one argument per element, which the target must return or run (TAMP001). |
| `[GitVersion] readonly GitVersion GitVersion;` | `Tamp.GitVersion.V6` wrapper | assisted | Add `Tamp.GitVersion.V6`; call its wrapper rather than an injected field. |
| `[GitRepository] readonly GitRepository Repo;` | `[GitRepository] readonly GitRepository Repo;` | assisted | Tamp's has `Branch`, `Commit`, `RemoteUrl`, `IsDetachedHead`; NUKE's `Tags`, `IsOnMainOrMasterBranch()`, `GetGitHubOwner()` aren't there (read them with git). |
| `Solution.AllProjects` | `Solution.Projects` | auto | Every project, solution folders included. |
| `IsLocalBuild` / `IsServerBuild` | `IsLocalBuild` / `IsServerBuild` | auto | Both exist on `TampBuild`. |
| `Host is GitHubActions` / `GitHubActions.Instance` | `CiHost.Detect()` | assisted | Returns the detected CI host, or null for a local build. |

## 3. Paths & filesystem (near 1:1 — mostly `auto`)

NUKE 8 names (`AbsolutePath` extension methods). Tamp's are instance methods on `AbsolutePath`.

| NUKE | Tamp | Tier | Notes |
|------|------|------|-------|
| `RootDirectory` / `TemporaryDirectory` | `RootDirectory` / `TemporaryDirectory` | auto | |
| `AbsolutePath`, `dir / "sub" / "file.txt"` | `AbsolutePath`, `dir / "sub" / "file.txt"` | auto | |
| `path.GlobFiles(...)` / `GlobDirectories(...)` | `path.GlobFiles(...)` / `GlobDirectories(...)` | auto | |
| `path.CreateDirectory()` | `path.CreateDirectory()` | auto | |
| `path.CreateOrCleanDirectory()` | `path.DeleteDirectory().CreateDirectory()` | auto | `DeleteDirectory` is a no-op when the directory doesn't exist. |
| `path.DeleteDirectory()` / `DeleteFile()` | `path.DeleteDirectory()` / `DeleteFile()` | auto | |
| `path.TouchFile()` | `path.Touch()` | auto | |
| `path.ReadAllText()` / `WriteAllText(...)` (and the `Lines` / `Bytes` variants) | the same | auto | |
| `path.Copy(target, policy)` / `Move(target, policy)` | `path.CopyTo(target, overwrite)` / `MoveTo(target, overwrite)` | assisted | NUKE's `ExistsPolicy` becomes a `bool overwrite` (default false). |
| `file.CopyToDirectory(dir, policy)` | `file.CopyToDirectory(dir, overwrite)` | assisted | Tamp's copies files only; `overwrite` defaults to true. |

## 4. Target DSL

| NUKE | Tamp | Tier | Notes |
|------|------|------|-------|
| `Target Foo => _ => _ …` | `Target Foo => _ => _ …` | auto | Identical property/lambda shape. |
| `.DependsOn(Bar)` | `.DependsOn(Bar)` | auto | Both take target refs (and `params`). |
| `.Before(X)` / `.After(X)` | `.Before(X)` / `.After(X)` | auto | |
| `.Triggers(X)` / `.TriggeredBy(X)` | `.Triggers(X)` / `.TriggeredBy(X)` | auto | |
| default target (via `Execute<Build>(x => x.Default)`) | `.Default()` on the target | auto | Move the designation onto the target. |
| `.Executes(() => { … })` | `.Executes(() => { … })` | auto | Body carries over; NUKE-API calls inside still need mapping. |
| `.Description("...")` | `.Description("...")` | auto | |
| `.Produces("artifacts/*.nupkg")` | `.Produces("artifacts/*.nupkg")` | auto | Tamp uses this for typed outputs + fail-closed slices. One glob per call. |
| `.Consumes(X)` | `.DependsOn(X)` (+ `.Produces` on X) | assisted | ⚠ Tamp's own `Consumes(Resource, ConsumeMode)` is a concurrency resource, not an artifact dependency, so the call can't be kept by name. |
| `.OnlyWhenStatic(() => cond)` / `.OnlyWhenDynamic(...)` | `.OnlyWhen(() => cond)` | assisted | Tamp has one `OnlyWhen`; static/dynamic distinction collapses. |
| `.Requires(() => Foo)` | `.Requires(() => Foo != null)` | assisted | NUKE's form means "Foo is set"; Tamp's `Requires` takes a bool. |
| `.Unlisted()` | `.Internal()` | assisted | `Internal` also blocks invoking the target directly; `Unlisted` only hid it. |
| `.AssuredAfterFailure()` | `.AssuredAfterFailure()` | auto | |
| `.ProceedAfterFailure()` | `.FailureMode(FailureMode.Continue)` | assisted | ⚠ Tamp's `Continue` runs remaining work but still **fails the build** (#4) — matches "proceed but report", not "swallow". |
| `[Partition]` / partitioned parallelism | — | manual | **No Tamp equivalent.** Flag; run serially or redesign. |
| `.WhenSkipped(...)` / `.DependentFor(...)` / `.TryDependsOn(...)` / `.TryBefore(...)` / `.TryAfter(...)` / `.TryTriggers(...)` | — | manual | Restate with `DependsOn` / `Before` / `After` / `TriggeredBy`. |

## 5. Logging

Tamp has no logger for build code: it captures each target's console output (and shows the tail when a target fails).

| NUKE (Serilog) | Tamp | Tier | Notes |
|------|------|------|-------|
| `Log.Information("Built {Project}", p)` / `Log.Debug` / `Log.Verbose` | `Console.WriteLine($"Built {p}")` | assisted | Rewrite the message template as an interpolated string. |
| `Log.Warning(...)` / `Log.Error(...)` / `Log.Fatal(...)` | `Console.Error.WriteLine(...)` | assisted | |

## 6. Components (per [ADR 0020](../adr/0020-tamp-components.md))

| NUKE | Tamp | Tier | Notes |
|------|------|------|-------|
| `class Build : NukeBuild, ICompile, ITest, IPack` | `class Build : TampBuild, ICompile, ITest, IPack` | assisted | `Tamp.Components` (shipped in 1.17) has `IHazSolution`, `IHazConfiguration`, `IHazArtifacts`, `IRestore`, `ICompile`, `ITest`, `IPack`; check each overridden or configured member. |
| `Nuke.Components.IHazSolution` etc. | `Tamp.Components.IHazSolution` etc. | assisted | Same `IHaz*` mental model; the build satisfies the abstract property. |
| component target override (reimplement member) | declare the same-named `Target` on the class | assisted | Tamp uses class-precedence override (analyzer-guarded). |
| `IPublish`, `IReportCoverage`, `ISignPackages`, `IHazGitVersion`, `IHazGitRepository`, `IHazChangelog`, … | — | manual | Not in `Tamp.Components`; write the targets out on the build class. |

## 7. Tool tasks — DotNet (the template for every tool family)

NUKE `DotNetTasks.DotNetXyz(s => s.…)` → Tamp `DotNet.Xyz(s => s.…)` (from `Tamp.NetCli.V{N}`). The **verb** maps 1:1; the **settings methods** are the per-call surface:

| NUKE | Tamp | Tier |
|------|------|------|
| `DotNetTasks.DotNetRestore(s => …)` | `DotNet.Restore(s => …)` | auto |
| `DotNetTasks.DotNetBuild(s => …)` | `DotNet.Build(s => …)` | auto |
| `DotNetTasks.DotNetTest(s => …)` | `DotNet.Test(s => …)` | auto |
| `DotNetTasks.DotNetPack(s => …)` | `DotNet.Pack(s => …)` | auto |
| `DotNetTasks.DotNetPublish(s => …)` | `DotNet.Publish(s => …)` | auto |
| `DotNetTasks.DotNetNuGetPush(s => …)` | `DotNet.NuGetPush(s => …)` | auto |
| `DotNetTasks.DotNetClean(s => …)` / `DotNetFormat(s => …)` | `DotNet.Clean(s => …)` / `DotNet.Format(s => …)` | auto |

Settings method renames (the common ones):

| NUKE setting | Tamp setting | Tier |
|------|------|------|
| `.SetProjectFile(x)` | `.SetProject(x)` (a `Solution` converts to its path, as in NUKE) | auto |
| `.SetConfiguration(x)` | `.SetConfiguration(x)` | auto |
| `.SetNoRestore(true)` / `.EnableNoRestore()` | `.SetNoRestore(true)` | auto |
| `.SetNoBuild(true)` / `.EnableNoBuild()` | `.SetNoBuild(true)` | auto |
| `.SetOutputDirectory(x)` / `.SetOutput(x)` | `.SetOutput(x)` | auto |
| `.SetFramework(x)` | `.SetFramework(x)` | auto |
| `.SetVerbosity(DotNetVerbosity.minimal)` | `.SetVerbosity(DotNetVerbosity.Minimal)` | assisted |
| `.AddLoggers("trx")` | `.AddLogger("trx")` | assisted |
| `.SetProperty("K","V")` / `.AddProperty(...)` | `.SetProperty("K","V")` | auto |
| `.SetVersion(v)` / `.SetAssemblyVersion(v)` / `.SetFileVersion(v)` / `.SetInformationalVersion(v)` | `.SetProperty("Version", v)` (etc.) | auto |
| `.SetFilter` / `.SetResultsDirectory` / `.SetRuntime` / `.SetVersionSuffix` | the same | auto |
| `.EnableSelfContained()` / `.EnablePublishSingleFile()` / `.EnablePublishTrimmed()` / `.EnablePublishReadyToRun()` / `.EnableIncludeSymbols()` / `.EnableSkipDuplicate()` / `.EnableNoLogo()` | `.Set{X}(true)` | auto |
| `.SetTargetPath(x)` (NuGet push) | `.SetPackagePath(x)` | auto |
| `.SetDataCollector("XPlat Code Coverage")` | `.AddDataCollector(...)` | auto |
| `.SetSource(x)` / `.SetApiKey(x)` | `.SetSource(x)` / `.SetApiKey(x)` | assisted |

> NUKE's `Enable{X}()`/`Disable{X}()` boolean pairs generally map to Tamp's `Set{X}(bool)`.

**How to read the rest of §7:** NUKE codegens every tool as `{Tool}Tasks.{Tool}{Verb}(s => s.Set…)`; Tamp exposes `{Tool}.{Verb}(s => s.Set…)` from `Tamp.{Family}`. Verb rows below are **verified against Tamp's wrapper source**; the NUKE side follows that regular convention. Verb rows are `assisted` by default (the verb maps 1:1 but settings method names/args differ per call — apply the `Set*`/`Enable*` convention and verify), and gaps are marked `manual`.

### 7.2 Docker (`Tamp.Docker.V27`)

NUKE `DockerTasks.Docker{Verb}` → `Docker.{Verb}`. Verified Tamp verbs: `Build` `Buildx.Build` `LegacyBuild` `Run` `Pull` `Push` `Tag` `Login` `Logout` `Ps` `Ls` `Inspect` `Logs` `Exec` `Start` `Stop` `Restart` `Kill` `Pause` `Unpause` `Rm` `Prune` `Create` `Version` `Bake`, plus Compose: `Up` `Down` `Config` `Use`.

| NUKE | Tamp | Tier | Notes |
|------|------|------|-------|
| `DockerTasks.DockerBuild(s => …)` | `Docker.Build(s => …)` | assisted | BuildKit is `Docker.Buildx.Build`; the classic backend is `Docker.LegacyBuild`. |
| `DockerTasks.DockerRun/Push/Pull/Tag/Login/…` | `Docker.Run/Push/Pull/Tag/Login/…` | assisted | 1:1 verbs. |
| `DockerTasks.DockerComposeUp/Down` | `Docker.Up/Down` (Compose surface) | assisted | Lifecycle compose verbs are fluent-only. |

### 7.3 EF Core (`Tamp.EFCore.V{N}`)

NUKE `EntityFrameworkTasks.EntityFramework{Verb}` → `EFCore.{Verb}` (async; `RunAsync`/`RunAndThrowOnFailureAsync`). Verified Tamp verbs: `MigrationsAdd` `MigrationsRemove` `MigrationsList` `MigrationsScript` `MigrationsBundle` `MigrationsHasPendingModelChanges` `DatabaseUpdate` `DatabaseDrop` `DbContextInfo` `DbContextList` `DbContextScaffold` `DbContextScript` `DbContextOptimize`.

| NUKE | Tamp | Tier |
|------|------|------|
| `EntityFrameworkMigrationsAdd(s => …)` | `EFCore.MigrationsAdd(s => …)` | assisted |
| `EntityFrameworkDatabaseUpdate(s => …)` | `EFCore.DatabaseUpdate(s => …)` | assisted |
| `EntityFrameworkMigrationsScript(s => …)` | `EFCore.MigrationsScript(s => …)` | assisted |

### 7.4 Coverlet (`Tamp.Coverlet.V6`)

NUKE runs coverage via `DotNetTest(... .SetDataCollector("XPlat Code Coverage"))` or `CoverletTasks.Coverlet`. Tamp integrates coverage as an **extension on the dotnet-test settings**:

| NUKE | Tamp | Tier |
|------|------|------|
| `DotNetTest(s => s.SetDataCollector("XPlat Code Coverage"))` | `DotNet.Test(s => s.WithCoverlet(c => …))` | assisted |

### 7.5 ReportGenerator (`Tamp.ReportGenerator.V5`)

| NUKE | Tamp | Tier |
|------|------|------|
| `ReportGeneratorTasks.ReportGenerator(s => …)` | `ReportGenerator.Run(s => …)` | assisted |

### 7.6 SonarScanner (`Tamp.SonarScanner.V10` / `Cli.V6`)

| NUKE | Tamp | Tier | Notes |
|------|------|------|-------|
| `SonarScannerTasks.SonarScannerBegin(s => …)` | `Sonar.Begin(s => …)` | assisted | Wrap the build between Begin/End. |
| `SonarScannerTasks.SonarScannerEnd(s => …)` | `Sonar.End(s => …)` | assisted | |

### 7.7 GitVersion (`Tamp.GitVersion.V6`)

| NUKE | Tamp | Tier | Notes |
|------|------|------|-------|
| `[GitVersion] readonly GitVersion Gv;` | `GitVersion.Run(s => …)` | assisted | NUKE prefers the injected attribute; Tamp calls the wrapper and reads its result. |
| `GitVersionTasks.GitVersion(s => …)` | `GitVersion.Run(s => …)` | assisted | |

### 7.8 npm (`Tamp.Npm.V10`)

NUKE `NpmTasks.Npm{Verb}` → `Npm.{Verb}`. Verified Tamp verbs: `Install` `Ci` `Run` `Publish` `Audit` `SetVersion` `Raw`.

| NUKE | Tamp | Tier |
|------|------|------|
| `NpmTasks.NpmInstall/NpmCi/NpmRun/NpmPublish(s => …)` | `Npm.Install/Ci/Run/Publish(s => …)` | assisted |

### 7.9 Yarn (`Tamp.Yarn.V4`)

Verified Tamp verbs: `Install` `Run` `Exec` `Dlx` `Pack` `Publish` `Dedupe` `Focus` `Foreach` `List` `TagAdd` `TagRemove` `Whoami` `Raw`.

| NUKE | Tamp | Tier |
|------|------|------|
| `YarnTasks.YarnInstall/YarnRun(s => …)` | `Yarn.Install/Run(s => …)` | assisted |

### 7.10 Helm (`Tamp.Helm.V3`)

Verified Tamp verbs: `Lint` `Package` `Push` `Template` `Upgrade`.

| NUKE | Tamp | Tier | Notes |
|------|------|------|-------|
| `HelmTasks.HelmLint/HelmPackage/HelmUpgrade(s => …)` | `Helm.Lint/Package/Upgrade(s => …)` | assisted | NUKE verbs beyond these (`HelmInstall`, etc.) → `manual` (no Tamp verb yet). |

### 7.11 Kubernetes / kubectl (`Tamp.Kubectl`)

NUKE `KubernetesTasks.Kubernetes{Verb}` → `Kubectl.{Verb}`. Verified Tamp verbs: `Apply` `Delete` `Get` `Describe` `Logs` `Exec` `Scale` `PortForward` `RolloutRestart` `RolloutStatus` `RolloutUndo`.

| NUKE | Tamp | Tier |
|------|------|------|
| `KubernetesTasks.KubernetesApply/Delete/Get(s => …)` | `Kubectl.Apply/Delete/Get(s => …)` | assisted |

### 7.12 MSBuild — classic (`Tamp.MSBuildClassic`)

Verified Tamp verbs: `Build` `Clean` `Restore` (classic `msbuild.exe`, not `dotnet build`).

| NUKE | Tamp | Tier | Notes |
|------|------|------|-------|
| `MSBuildTasks.MSBuild(s => s.SetTargets("Build"))` | `MSBuildClassic.Build(s => …)` | assisted | For SDK-style projects prefer `DotNet.Build` (§7.1); use classic only for full-framework/`.sln` needing `msbuild.exe`. |

### 7.13 Azure CLI (`Tamp.AzureCli.V2`)

Verified Tamp verbs: `Login` `Logout` `Show` `List` `Create` `Delete` `Set` `Rest` `GetAccessToken` `GetAccessTokenAsSecret` `Version` `Raw`.

| NUKE | Tamp | Tier | Notes |
|------|------|------|-------|
| `AzureTasks.AzureCli(...)` / raw `az` | `AzureCli.Raw(...)` / typed verbs above | assisted | Tamp exposes typed verbs plus `Raw` for arbitrary `az` invocations. |

## 8. Coverage gaps — what Tamp can't convert (be up front)

The sections above are what Tamp *has*. This section is the honest inverse: **NUKE surface with no Tamp equivalent.** A migration must surface these to the adopter before it starts — they are the parts the tooling flags `manual` and cannot auto-convert. This list doubles as the **wrapper backlog** (priority = how often a real NUKE build hits it).

> Scoped against NUKE's `Nuke.Common.Tools.*` set; **validate against the current NUKE release** when the tooling is built (NUKE's tool list moves). "No wrapper" = no `Tamp.{Family}` package today; the workaround is always a raw `ProcessRunner.Execute` / `[FromPath("x")] Tool` call, or write the wrapper.

### 8.1 NUKE tools with **no Tamp wrapper** (can't auto-convert → `manual`)

| NUKE tool | Tamp status | Migration workaround | Backlog priority |
|-----------|-------------|----------------------|------------------|
| `PowerShellTasks` / `PowerShell` / `Pwsh` | none | `[FromPath("pwsh")] Tool` / `ProcessRunner` | **high** (common glue) |
| `GitTasks` (generic `git` CLI) | none (Tamp has `gh`, not raw `git`) | `[FromPath("git")] Tool`; some facts via `Tamp.GitHubCli.V2` | **high** |
| `PaketTasks` (F# package manager) | none | raw `Tool` | med (F# shops) |
| `GitReleaseManagerTasks` | none | raw `Tool` / `gh` release | med |
| ReSharper CLT: `InspectCodeTasks` / `CleanupCodeTasks` / `dupFinder` | none (Tamp has Sonar/OpenGrep/analyzers) | raw `Tool`, or lean on `Tamp.SonarScanner` | med |
| `DotCoverTasks` (JetBrains coverage) | none (Tamp has Coverlet + DotNetCoverage) | reshape onto `Tamp.Coverlet.V6` | low |
| `ChocolateyTasks` | none | raw `Tool` | low |
| `InnoSetupTasks` | none (Tamp has MSIX / MS Store) | raw `Tool` | low |
| `ILRepackTasks` / `ILMergeTasks` | none | raw `Tool` | low |
| `OctopusDeployTasks` | none | raw `Tool` / az/gh deploy | low |
| `CodecovTasks` / `CoverallsNetTasks` (coverage upload) | none (Tamp ingests via `tamp-findings`) | reshape to `Tamp.Ingest.V1`, or raw `Tool` | low |
| `SlackTasks` notifications | none (Tamp ships **Telegram**; Slack/Discord are `IBuildReporter` siblings, not yet published) | custom `IBuildReporter`, or raw webhook | med (notifications are common) |
| `AzureKeyVaultTasks` | partial (Tamp has `Secret` + `OsSecretStore` + `Tamp.AzureCli.V2`) | reshape onto Secret injection / `AzureCli` | med |

### 8.2 NUKE tools **covered differently** (convertible, but reshape — not 1:1)

| NUKE | Tamp equivalent | Note |
|------|-----------------|------|
| `Xunit2Tasks` / `NUnitTasks` / `MSTestTasks` / `VSTestTasks` (console runners) | `DotNet.Test(...)` | Tamp runs tests through `dotnet test`, not per-framework console runners. Reshape the invocation. |
| `NuGetTasks` (classic `nuget.exe` pack/push/restore) | `DotNet.Pack` / `DotNet.NuGetPush` / `DotNet.Restore` | Use the SDK path unless you specifically need `nuget.exe`. |
| `MSBuildTasks` (SDK-style projects) | `DotNet.Build` (§7.1) | Prefer `DotNet.Build`; `Tamp.MSBuildClassic` (§7.12) only for full-framework/`msbuild.exe`. |

### 8.3 NUKE **framework features** Tamp lacks (not tools)

| NUKE feature | Tamp status | Migration guidance |
|--------------|-------------|--------------------|
| `Nuke.Components` (interface-mixin targets) | **covered** — `Tamp.Components` (ADR 0020, 1.17) | The core components map near-1:1 (§6); the others are written out. |
| CI-configuration generation (`[GitHubActions]` etc.) | none | Keep the existing pipeline file and point it at `./tamp.sh` (§1). |
| `[Partition]` / partitioned parallelism | **no equivalent** | `manual` — run serially, or shard outside Tamp. The single biggest hard gap. |
| `[GitRepository]` injection | **covered** — `[GitRepository]` (`Branch`, `Commit`, `RemoteUrl`) | NUKE's richer members (`Tags`, `IsOnMainOrMasterBranch()`) aren't there; read them with `git` (§2). |
| Serilog-based `Log.*` | per-target captured console output | Rewrite as `Console.WriteLine` (§5). |
| `nuke :setup` interactive wizard | `tamp init` (non-interactive scaffold) | Re-scaffold; don't port the bootstrap. |
| ReSharper / Rider IDE plugin | VS Code extension (Fleet planned) | Different IDE surface; not a build-file concern. |

## 9. Custom code / catch-all

- **NUKE verbs beyond a Tamp wrapper's surface** — e.g. a Docker/Helm/kubectl verb Tamp hasn't added → `manual` (raw call, or extend the wrapper).
- **Arbitrary custom C# in `Executes` bodies** — carries over unchanged *except* calls into NUKE APIs (covered above); anything not in this table is `manual`.

---

*This reference is the durable asset ([ADR 0021](../adr/0021-nuke-to-tamp-migration.md)); the tooling (`tamp migrate nuke` + agent-guided migration) applies its machine-readable projection, [`mapping.yaml`](mapping.yaml). §8 is the honest coverage boundary — a migration reports these gaps up front rather than failing on them mid-convert. Change a row here and its rule in `mapping.yaml` together.*
