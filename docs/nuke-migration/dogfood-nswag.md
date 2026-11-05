---
title: "Dogfood: migrating NSwag's NUKE build"
description: ADR 0021 Phase 2 step 6. What `tamp migrate nuke` did to a real public NUKE build, what it got wrong, and what changed as a result.
---

# Dogfood: migrating NSwag's NUKE build

[ADR 0021](../adr/0021-nuke-to-tamp-migration.md)'s last delivery step is to migrate a real public NUKE build and write it up. This is the first run. It was done before `Tamp.Migrate` was made packable, and it shaped that release.

**Build:** [RicoSuter/NSwag](https://github.com/RicoSuter/NSwag) at `63daf8f`, `build/` only. It is NUKE 10.1 with five C# files: `Build.cs`, `Build.Pack.cs`, `Build.Publish.cs`, `Build.CI.GitHubActions.cs` (a custom CI-generation attribute) and the `nuke :setup` template `Configuration.cs`. It uses DotNet, Npm, NuGet, Chocolatey, GitVersion-style versioning, `[GitRepository]`, `[Secret]`, Serilog and the MSBuild locator.

**Method:**
1. `tamp-migrate nuke` on a clean checkout.
2. Compile the migrated `build/*.cs` in-process with Roslyn against the in-tree Tamp.Core, Tamp.NetCli.V10 and Tamp.Components. Nothing from NSwag's build ran.
3. Count the compiler errors, and separate the ones inside a statement or member that carries a `TAMP-MIGRATE` marker from the ones that don't.

An error under a marker is the tool working as designed: it flagged the site and left it for a person or the agent path. An **unflagged** error is a silent failure, where the tool produced broken code without saying so. That's the number that matters, because "flagged, never guessed" is the contract.

## Results

| | First run | After the fixes below |
|---|---|---|
| Sites: applied / assisted / manual / unmapped | 72 / 22 / 12 / 19 | 74 / 25 / 36 / 19 |
| Compiler errors | 124 | 118 |
| Errors under a marker | 62 | 63 |
| **Unflagged** | 62 | 55 |
| Unflagged outside `Build.CI.GitHubActions.cs` | **17** | **10** |

Raw error counts barely move, and that's expected. A marker flags code; it doesn't make it compile. The 16 fully qualified `Serilog.Log.*` calls, for example, went from unflagged to flagged and still don't compile.

`Build.CI.GitHubActions.cs` (45 unflagged errors) is NUKE CI generation, a subclass of NUKE's `GitHubActionsAttribute`. Tamp doesn't generate CI. The `boilerplate.ci-generation` marker on the `[CustomGitHubActions]` usage and the report both say to delete the file. A whole-file rule would flag every line in it, but that's one decision rather than 45, so it's left to the report.

Each of the 10 remaining unflagged errors depends on something flagged at its **declaration**:

| Errors | Cause | Where it's flagged |
|---|---|---|
| 6 | `Microsoft.Build` (`MSBuildLocator`, `ProjectCollection`) came in transitively through Nuke.Common | the rewritten `_build.csproj` (`boilerplate.build-project`) |
| 1 | `GitRepository.Tags` | the `[GitRepository]` field (`injection.git-repository`) |
| 1 | `RootDirectory / Configuration`: Tamp's `Configuration` is an enum | the `Configuration` member (`boilerplate.configuration-class`) |
| 1 | a `Secret` passed where a `string` is expected | the `[Secret]` field (`injection.secret`) |
| 1 | NUKE's `ForEach` collection extension | the kept `using Nuke.Common.Utilities.Collections` |

## What the run changed

The run found real gaps. Each fix below has tests (`tests/Tamp.Migrate.Tests`).

- **The `nuke :setup` `Configuration : Enumeration` class.** Every template build has it. Left as it was, it doesn't compile, and because it's declared in the build it hides Tamp's own `Configuration`. The new rule `boilerplate.configuration-class` (assisted) removes the Debug/Release template and deletes the file if that empties it. It then marks each `Configuration`-typed member, because Tamp's is an enum: write `.ToString()` where it was used as a string. A `Configuration` with other values, or any other `Enumeration`, is marked and kept.
- **Fully qualified `Serilog.Log.Information(..)`.** It accounted for 16 silent errors. A rule's receiver now matches on its last name.
- **NUKE settings types used directly.** In `DotNetBuildSettings BuildDefaults(DotNetBuildSettings s)`, the same class name exists in Tamp, so after the usings change it silently named Tamp's type, whose setters differ. These are now marked. The settings type names are derived from each tool family's verbs in `mapping.yaml`.
- **`Configure<T>` settings delegates** (`boilerplate.configure-delegate`) and **NUKE lifecycle hooks** such as `OnBuildInitialized` (`boilerplate.lifecycle-hooks`) are marked. Tamp has neither.
- **`Solution.AllProjects` → `Solution.Projects`** (auto). This needed a new `member` match kind, because member names were invisible to the existing kinds.
- **The `[GitRepository]` row was wrong.** It said Tamp has no injected `GitRepository`, but Tamp does: its own build uses one. It's now `assisted`: kept, with a marker listing the NUKE members that aren't there.
- **A rule bug:** an `assisted` rule that keeps an attribute as written recorded "applied" and left no marker. It now leaves one.
- **The build project** marker now warns that packages NUKE brought in transitively are gone.

## Not NUKE: Quartz.NET

[quartznet/quartznet](https://github.com/quartznet/quartznet) looked like a second candidate, but its build uses **Fallout**, a NUKE fork with its own `Fallout.*` namespaces. Nothing in it is recognised, and that's correct for a NUKE 8+ tool. The tool now warns when it finds no `NukeBuild` class and no `Nuke.*` usings, instead of reporting a near-empty run without comment.

## Verdict

The tool is good enough for a **preview**. On a real, non-trivial build it applied 74 mappings, and everything it couldn't convert was flagged either at the site or at the declaration the site depends on. What's left is the long tail ADR 0021 assigns to the agent path: unconverted tool calls, unmapped NUKE APIs, CI generation. Before the tool loses the preview label:
- The `compile_diagnostics` / `migration_status` MCP tools and the agent playbook (delivery step 5), so that tail has a loop.
- A `--verify` option that runs this compile check and adds unflagged errors to the report.
- A second dogfood build that uses `Nuke.Components`.
