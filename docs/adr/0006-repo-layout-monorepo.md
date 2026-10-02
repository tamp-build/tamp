# ADR 0006: Repository layout — monorepo for core and first-party modules

* Status: Accepted
* Date: 2026-05-09
* Deciders: scott
* Tracking: TAM-12
* Partially superseded by: [ADR 0026](0026-satellite-repositories-for-wrappers.md) (2026-10-02, TAM-254) — first-party tool wrappers now ship as independent `tamp-<tool>` satellite repositories, not monorepo sources. The core (`Tamp.Core` + tightly-coupled siblings) remains a monorepo as decided here; only the "all first-party module sources" clause is superseded.

## Context and Problem Statement

Tamp ships as a small core (`Tamp.Core`, `Tamp.Cli`) plus a growing set of independently-versioned first-party tool wrappers (`Tamp.NetCli.V{N}`, `Tamp.Docker.V{N}`, `Tamp.SonarQube.V{N}`, `Tamp.Yarn`, `Tamp.Turbo.V{N}`, `Tamp.Pac`, `Tamp.Kubectl`, …). Independent versioning is non-negotiable — it's the headline architectural improvement over NUKE, codified in ADR 0002.

Open question: where does the *source* live? One repo? One repo per package? A hybrid? This decision shapes every PR, every CI pipeline, every contributor's onboarding, and how aggressively Core's API can evolve.

The decision must reconcile two pressures that pull in opposite directions:

1. **Independent versioning per package.** A consumer on `Tamp.NetCli.V10@1.4.2` should not be jostled because someone shipped `Tamp.Docker.V27@2.0.0`.
2. **Core API evolution costs.** When `Tamp.Core` adds a new wrapper-facing surface (e.g., a new lifecycle hook on `CommandPlan`), the change is only complete when at least one wrapper exercises it. In a multi-repo world, that's an N-repo coordinated PR dance.

## Decision Drivers

* **Atomic Core-plus-module changes.** Evolving `Tamp.Core`'s wrapper-facing API in lockstep with at least one consuming wrapper must be a single PR, not a choreographed cascade.
* **Single contributor onboarding.** `git clone && dotnet build` should produce a working tree of every first-party project at once. The bar for casual contribution must be low.
* **One CI pipeline.** Build, test, and pack run together; cross-cutting refactors are caught immediately rather than discovered three weeks later when a downstream module rebuilds.
* **Independent NuGet versions are still required.** The decision affects *source layout*, not *publication coupling*. Per-project versioning is a tooling concern, not a repo-boundary one.
* **Forkability.** Anyone forking Tamp must get a working development environment from the single repo. The architecture must not depend on a private mesh of inter-repo links.
* **Community modules are explicitly out of scope here.** Third-party modules live in third-party repos. This ADR is only about first-party (`tamp-build/tamp`-published) source.

## Considered Options

1. **Multi-repo: one repo per package.** `tamp-core`, `tamp-cli`, `tamp-netcli-v10`, `tamp-docker-v27`, …
2. **Hybrid: core in one repo, first-party modules in a second `tamp-modules` repo.**
3. **Monorepo with one solution, independently versioned NuGet packages, single CI pipeline (chosen).**
4. **Monorepo with one solution per project.** Same source tree, separate `.slnx` per project.

## Decision Outcome

**Chosen: Option 3 — single repository (`tamp-build/tamp`) containing core, CLI, all first-party module sources, and tests, organised under one `Tamp.slnx`. Each project publishes its own NuGet package on its own version cadence.**

Layout:

```
tamp/
├── README.md
├── LICENSE
├── CONTRIBUTING.md
├── Tamp.slnx
├── Directory.Build.props          # shared MSBuild settings
├── Directory.Packages.props       # central package management
├── docs/
│   └── adr/
├── src/
│   ├── Tamp.Core/
│   ├── Tamp.Cli/
│   └── Tamp.NetCli.V10/
├── tests/
│   ├── Tamp.Core.Tests/
│   ├── Tamp.Cli.Tests/
│   └── Tamp.NetCli.V10.Tests/
└── eng/
    └── ...                        # build scripts, release tooling
```

A few specific shapes follow from this layout:

* **`.slnx`, not `.sln`.** Tamp targets .NET 10, where SLNX is the canonical solution format.
* **Central package management** via `Directory.Packages.props`. NuGet versions for non-Tamp dependencies are unified across projects; intra-Tamp project references are `<ProjectReference>`, not `<PackageReference>`.
* **Per-project `.csproj` controls its own published NuGet metadata** (`PackageId`, `Version`, `PackageReleaseNotes`). Versions are *not* repo-global. A change to one project does not bump every other project.
* **Tests sit in a parallel `tests/` tree**, one project per source project, named `<Project>.Tests`. Test projects are never packed.
* **Release tooling lives in `eng/`** and is allowed to be Tamp-flavoured eventually (Tamp self-hosting). For v0 it can be plain `dotnet pack` invocations; the seam is reserved.

## Consequences

### Positive

* A `Tamp.Core` API change can land alongside the wrapper change that exercises it in one PR. CI fails immediately if anything breaks, instead of weeks later.
* New contributors clone one repo and have everything. No README sentence saying "and don't forget to also clone five sibling repos."
* Refactors that touch shared abstractions (e.g., renaming a record on `CommandPlan`) are mechanical, not coordinated multi-repo events.
* Cross-project search, rename, and review all work through standard tooling without ceremony.
* CI pipeline is one pipeline. Caching, secrets, and matrix configuration are defined once.

### Negative

* `git log` covers everything. Filtering history per-project requires path filters; not painful, but a cost.
* Per-project versioning has to be enforced by tooling (changelog automation, conventional-commit scoping, or release scripts that read project-specific tags). Multi-repo gets this for free; we accept the tooling burden.
* The repo grows monotonically. We accept that — the alternative (split repos) doesn't actually shrink the total volume of code, it just spreads it out.
* PR review can be tempted to bundle unrelated changes across projects. Style guidance: keep PRs scoped to one project's surface plus the Core changes that justify it. CI gates can enforce this if it becomes a real problem.

### Neutral / future-facing

* This ADR does not commit to monorepo *forever* across all phases. If first-party module count grows past a clear threshold (say, 20+ modules with materially different release cadences), Option 2 (hybrid) becomes worth re-evaluating. A successor ADR would supersede this one then.
* Community / third-party modules are explicitly out of scope. They live wherever their authors host them. The package naming convention (ADR 0002) is what makes the namespace self-organising — repository hosting is unrelated to it.
* Tamp self-hosting (using Tamp to build Tamp) is a future goal, not a v0 requirement. The `eng/` seam is reserved for it.

## Pros and Cons of the Options

### Option 1 — Multi-repo, one repo per package

* Pro: hard package boundaries; impossible to accidentally couple Core to a module's internals.
* Pro: per-package CI is trivially scoped.
* Con: every cross-cutting change becomes an N-repo PR dance with manual ordering. This is the dominant cost.
* Con: contributor onboarding is "clone all of these, then…".
* Con: dependency-version drift between repos is hard to audit.

### Option 2 — Hybrid (core repo + modules repo)

* Pro: separates Core's lifecycle from module churn, in principle.
* Pro: still allows first-party modules to share CI.
* Con: every Core API change still costs at least one cross-repo PR.
* Con: invites scope arguments ("does this go in core or modules?") that don't exist with Option 3.

### Option 3 — Single monorepo with independent NuGet versions (chosen)

* Pro: optimises the case we know we'll be in constantly — Core API changes that need a module to exercise them.
* Pro: one onboarding path; one CI; one search tree.
* Pro: per-project versioning is well-understood; tooling exists.
* Con: requires versioning discipline by tooling rather than by repo boundary.
* Con: CI runs the full suite on every change unless we're careful with selective builds. We can optimise later.

### Option 4 — Monorepo with one solution per project

* Pro: smaller solution-load times in IDE.
* Con: solution sprawl (you must remember which `.slnx` to open for which work).
* Con: cross-project refactors don't see all consumers in one go.
* Con: no real win over Option 3 once .NET 10's SLNX format is the baseline (it loads faster than .sln).

## Links

* Tracker: TAM-12.
* Independent versioning rationale: ADR 0002 (Package naming convention).
* Governance and namespace policy: ADR 0009 (deferred — covers community modules, which are explicitly out of scope here).
