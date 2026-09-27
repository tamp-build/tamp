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

> Status: **initial cut** — boilerplate, injection, paths, target DSL, logging, components, and the **DotNet** tool family are covered. Remaining tool families (Docker, Sonar, npm/yarn, …) are filled in per-tool under the [migration epic](../adr/0021-nuke-to-tamp-migration.md); each maps `XyzTasks.*` → `Tamp.Xyz` the same way DotNet does.

---

## 1. Boilerplate & entrypoint

| NUKE | Tamp | Tier | Notes |
|------|------|------|-------|
| `class Build : NukeBuild` | `class Build : TampBuild` | auto | |
| `public static int Main() => Execute<Build>(x => x.Default);` | `public static int Main(string[] args) => Execute<Build>(args);` | auto | NUKE picks the default target via the `Execute` arg; Tamp designates it with `.Default()` on the target (see §4). |
| `using Nuke.Common;` / `Nuke.Common.*` | `using Tamp;` (+ per-tool `using Tamp.NetCli.V10;` etc.) | assisted | Drop `Nuke.Common.*`; add the Tamp namespaces for the wrappers actually used. |
| `build.sh` / `build.cmd` / `.nuke/` | `tamp.sh` / `tamp.cmd` (from `tamp init`) | assisted | Regenerate the bootstrap via `dotnet tamp init`; don't port NUKE's. |
| `[UnsetVisualStudioEnvironmentVariables]` and other build-attributes | (drop) | manual | No Tamp equivalent; usually unnecessary. |

## 2. Injection attributes

| NUKE | Tamp | Tier | Notes |
|------|------|------|-------|
| `[Parameter] readonly string Foo;` | `[Parameter] readonly string Foo;` | auto | Same attribute name + semantics. |
| `[Solution] readonly Solution Solution;` | `[Solution] readonly Solution Solution;` | auto | `Solution.Path` in both. |
| `[Secret] [Parameter] readonly string Token;` | `[Secret] readonly Secret Token;` | assisted | Tamp models secrets as a distinct `Secret` type (ADR 0005), not a string; call sites use `Token` / `Token.Reveal()`. |
| `[PathVariable]` / `[PathExecutable] readonly Tool Git;` | `[FromPath("git")] readonly Tool Git;` | auto | Both resolve a tool from PATH into a `Tool`. |
| `[GitVersion] readonly GitVersion GitVersion;` | `Tamp.GitVersion.V6` wrapper | assisted | Add `Tamp.GitVersion.V6`; call its wrapper rather than an injected field. |
| `[GitRepository] readonly GitRepository Repo;` | (git via `Tamp.GitHubCli.V2` / shell) | manual | No injected `GitRepository`; obtain repo facts via a git wrapper. |
| `IsLocalBuild` / `IsServerBuild` | `IsLocalBuild` / host detection (`HostProfile` / `CiHost.Detect`) | assisted | `IsLocalBuild` exists; richer CI facts come from host detection rather than a static flag. |

## 3. Paths & filesystem (near 1:1 — mostly `auto`)

| NUKE | Tamp | Tier |
|------|------|------|
| `RootDirectory` | `RootDirectory` | auto |
| `AbsolutePath` | `AbsolutePath` | auto |
| `dir / "sub" / "file.txt"` | `dir / "sub" / "file.txt"` | auto |
| `TemporaryDirectory` | `TemporaryDirectory` | auto |
| `AbsolutePath.GlobFiles(...)` / `GlobDirectories(...)` | `RootDirectory.GlobFiles(...)` / `GlobDirectories(...)` | auto |
| `EnsureExistingDirectory` / `EnsureCleanDirectory` | `CreateDirectory()` / `CreateOrCleanDirectory()` | auto |
| `CopyFile` / `MoveFile` / `DeleteDirectory` | `CopyFile` / `MoveFile` / `DeleteDirectory` | assisted | Same intent; verify overload names. |

## 4. Target DSL

| NUKE | Tamp | Tier | Notes |
|------|------|------|-------|
| `Target Foo => _ => _ …` | `Target Foo => _ => _ …` | auto | Identical property/lambda shape. |
| `.DependsOn(Bar)` | `.DependsOn(Bar)` | auto | Both take target refs (and `params`). |
| `.Before(X)` / `.After(X)` | `.Before(X)` / `.After(X)` | auto | |
| `.Triggers(X)` / `.TriggeredBy(X)` | `.Triggers(X)` / `.TriggeredBy(X)` | auto | |
| default target (via `Execute<Build>(x => x.Default)`) | `.Default()` on the target | auto | Move the designation onto the target. |
| `.Executes(() => { … })` | `.Executes(() => { … })` | auto | Body carries over; NUKE-API calls inside still need mapping. |
| `.Produces("artifacts/*.nupkg")` | `.Produces("artifacts/*.nupkg")` | auto | Tamp uses this for typed outputs + fail-closed slices. |
| `.Consumes(X)` | `.DependsOn(X)` (+ `.Produces` on X) | assisted | Tamp models input/output through Produces + deps, not a distinct Consumes. |
| `.OnlyWhenStatic(() => cond)` / `.OnlyWhenDynamic(...)` | `.OnlyWhen(() => cond)` | assisted | Tamp has one `OnlyWhen`; static/dynamic distinction collapses. |
| `.Requires(() => Foo)` | `.Requires(() => cond)` | assisted | Tamp's `Requires` is a boolean precondition. |
| `.AssuredAfterFailure()` | `.AssuredAfterFailure()` | auto | |
| `.ProceedAfterFailure()` | `.FailureMode(FailureMode.Continue)` | assisted | ⚠ Tamp's `Continue` runs remaining work but still **fails the build** (#4) — matches "proceed but report", not "swallow". |
| `[Partition]` / partitioned parallelism | — | manual | **No Tamp equivalent.** Flag; run serially or redesign. |

## 5. Logging

| NUKE (Serilog) | Tamp | Tier |
|------|------|------|
| `Serilog.Log.Information("...")` | `Log.Info("...")` (build `Logger`) | assisted |
| `Log.Warning` / `Log.Error` / `Log.Debug` | `Log.Warn` / `Log.Error` / `Log.Debug` | assisted |

## 6. Components (per [ADR 0020](../adr/0020-tamp-components.md))

| NUKE | Tamp | Tier | Notes |
|------|------|------|-------|
| `class Build : NukeBuild, ICompile, ITest, IPack` | `class Build : TampBuild, ICompile, ITest, IPack` | assisted | Requires `Tamp.Components` (0020). Near-1:1 once shipped. |
| `Nuke.Components.IHazSolution` etc. | `Tamp.Components.IHazSolution` etc. | assisted | Same `IHaz*` mental model; the build satisfies the abstract property. |
| component target override (reimplement member) | declare the same-named `Target` on the class | assisted | Tamp uses class-precedence override (analyzer-guarded). |

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

Settings method renames (the common ones):

| NUKE setting | Tamp setting | Tier |
|------|------|------|
| `.SetProjectFile(x)` | `.SetProject(x)` | auto |
| `.SetConfiguration(x)` | `.SetConfiguration(x)` | auto |
| `.SetNoRestore(true)` / `.EnableNoRestore()` | `.SetNoRestore(true)` | assisted |
| `.SetNoBuild(true)` / `.EnableNoBuild()` | `.SetNoBuild(true)` | assisted |
| `.SetOutputDirectory(x)` / `.SetOutput(x)` | `.SetOutput(x)` | assisted |
| `.SetFramework(x)` | `.SetFramework(x)` | auto |
| `.SetVerbosity(DotNetVerbosity.X)` | `.SetVerbosity(DotNetVerbosity.X)` | assisted |
| `.AddLoggers("trx")` | `.AddLogger("trx")` | assisted |
| `.SetProperty("K","V")` / `.AddProperty(...)` | `.SetProperty("K","V")` | assisted |

> NUKE's `Enable{X}()`/`Disable{X}()` boolean pairs generally map to Tamp's `Set{X}(bool)`.

## 8. Not yet mapped / known manual

- **Tool families beyond DotNet** — Docker, Sonar, npm/yarn, etc.: same `XyzTasks.* → Tamp.Xyz` pattern, filled per-tool under the epic. A NUKE build using a tool **Tamp doesn't wrap** is `manual` (find a Tamp wrapper or keep a raw `ProcessRunner`/`Tool` call).
- **`[Partition]` / partitioned parallelism** — `manual`, no equivalent.
- **Arbitrary custom C# in `Executes` bodies** — carries over unchanged *except* calls into NUKE APIs (covered above); anything not in this table is `manual`.

---

*This reference is the durable asset ([ADR 0021](../adr/0021-nuke-to-tamp-migration.md)); tooling (`tamp migrate nuke` + agent-guided migration) is planned once it is complete. A machine-readable (YAML/JSON) projection is derived from these tables for tool consumption.*
