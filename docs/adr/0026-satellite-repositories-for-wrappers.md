# ADR 0026: Satellite repositories for first-party tool wrappers

**Status:** Accepted (2026-10-02). Partially supersedes [ADR 0006](0006-repo-layout-monorepo.md) — the "all first-party module sources in the monorepo" clause. Tracking: TAM-254.

**Context.** [ADR 0006](0006-repo-layout-monorepo.md) chose a single monorepo holding core, CLI, and **all** first-party module sources under one `Tamp.slnx`, each project publishing its own NuGet package. That held while the module set was small. It no longer matches reality: the first-party tool wrappers have been extracted into ~60 independent `tamp-<tool>` satellite repositories (TAM-254), each publishing `Tamp.<Family>.V<major>?` on its own cadence, pinned to a published `Tamp.Core` via `PackageReference`. The monorepo (`tamp-build/tamp`) now ships only the core line: `Tamp.Core`, `Tamp.Cli`/`dotnet-tamp`, `Tamp.NetCli.V{8,9,10}`, `Tamp.DotNetCoverage.V18`, `Tamp.Analyzers`, `Tamp.Sarif`, `Tamp.Sbom`, `Tamp.Security.Pipeline`.

This drift is also a correctness problem for the conformance system: ADR-0006-derived rules flag legitimate satellite `PackageReference`s (e.g. `Tamp.Security.Pipeline` → `Tamp.CycloneDx.V6`) as monorepo-layout violations, because the recorded decision contradicts the shipped architecture. Recording the decision that was actually made resolves that.

The forces that overrode ADR 0006's "atomic core-plus-module PR" benefit:

- **Independent versioning at scale.** [ADR 0002](0002-package-naming-convention.md)'s forkable `Tamp.<X>.V<N>` namespace and [ADR 0001](0001-small-core-plugin-architecture.md)'s small-core/plugin thesis both treat modules as independent units. At ~60 modules, one solution + one CI pipeline became the inverse of ADR 0006's fear: every core PR rebuilding and retesting 60 wrappers.
- **Resilience is the architecture.** Tamp's origin thesis — NUKE stalled on a single maintainer — is that a small core plus independently-shipped satellites survives where a monolith does not. Satellites make each wrapper independently ownable, releasable, and (community-)forkable; the namespace self-organizes per ADR 0002.
- **Agent-first topology** ([ADR 0019](0019-agent-first-toolchain.md) / [ADR 0020](0020-tamp-components.md)). Every satellite exposes the same `Tamp.Components` build surface, so an agent drives any repo identically. The uniformity lives in the shared packages, not a shared tree.
- **Blast radius.** A broken wrapper release cannot break the core's CI or other wrappers.

**What ADR 0006 still gets right (NOT superseded):** the **core** is a monorepo. `Tamp.Core` and its tightly-coupled siblings (`Cli`, `NetCli.V*`, `Sarif`, `Sbom`, `Security.Pipeline`, `Analyzers`, `DotNetCoverage`) stay in one tree under one `Tamp.slnx` with intra-tree `ProjectReference`s, because they co-evolve and ADR 0006's "atomic Core-API-plus-first-consumer PR" argument holds for them. The split applies to the tool-wrapper layer, not the core.

## Decision

First-party tool wrappers live in **independent `tamp-<tool>` satellite repositories** under the `tamp-build` org, not in the core monorepo.

1. **Monorepo scope** (`tamp-build/tamp`) is the core line only: `Tamp.Core`, `Tamp.Cli` (dotnet-tamp), `Tamp.NetCli.V{8,9,10}`, `Tamp.DotNetCoverage.V18`, `Tamp.Analyzers`, `Tamp.Sarif`, `Tamp.Sbom`, `Tamp.Security.Pipeline` — one `Tamp.slnx`, intra-tree `ProjectReference`s. ADR 0006's monorepo rationale is retained for this set.
2. **Each wrapper is its own repo** `tamp-<tool>`, publishing `Tamp.<Family>.V<major>?` ([ADR 0002](0002-package-naming-convention.md)) on its own cadence, pinned to a published `Tamp.Core` via `PackageReference`. Satellites carry a dual-mode core reference: `TampCoreMode=project` (sibling-clone `ProjectReference`, inner-loop dev) vs `package` (CI/release).
3. **A monorepo project MAY `PackageReference` a first-party satellite** where it orchestrates them — e.g. `Tamp.Security.Pipeline` references `Tamp.CycloneDx.V6` / `Tamp.OpenGrep` / `Tamp.OsvScanner.V2` / `Tamp.Trivy`. This meta-package pattern is expressly allowed and is **not** a layering violation.
4. **Satellites dogfood the core**: each ships via `dotnet tamp Ci && dotnet tamp Push`, adopts `Tamp.Components` ([ADR 0020](0020-tamp-components.md)), and is CI-gated on the 3-OS matrix.
5. **Dashboards / downstream** (`tamp-beacon`, `tamp-findings`, `tamp-ingest-v1`, `tamp-vscode`) are their own repos and are **not** wrapper satellites (non-standard build shapes); they are out of scope for the wrapper rules.

## Consequences

### Positive

- Independent release cadence and blast-radius isolation per wrapper; a core change no longer rebuilds 60 wrappers.
- The namespace self-organizes (ADR 0002); community wrappers sit beside first-party ones with identical topology.
- Uniform agent-facing surface via shared `Tamp.Components`, not a shared source tree.
- Conformance rules derived from the ADR corpus now match the shipped architecture (no false monorepo-violation flags on satellite references).

### Negative / cost

- The "atomic Core-API-plus-consumer in one PR" benefit (ADR 0006) is lost for wrappers: a `Tamp.Core` wrapper-facing change now needs a core publish plus a satellite bump. Mitigated by the `TampCoreMode=project` sibling-clone inner loop and fleet-wide bump tooling (cf. the Components Phase 4 migration wave).
- Sweeping changes become cross-repo coordination (the 60-satellite migration wave is the worked example).
- `git clone && dotnet build` no longer yields every wrapper; a contributor clones the satellite they work on (plus, optionally, a core sibling-clone).

## Related

- **Partially supersedes** [ADR 0006](0006-repo-layout-monorepo.md) (first-party wrappers move to satellite repos; the core stays a monorepo).
- [ADR 0001](0001-small-core-plugin-architecture.md) (small core / plugins), [ADR 0002](0002-package-naming-convention.md) (naming + forkability), [ADR 0019](0019-agent-first-toolchain.md) (agent-first topology), [ADR 0020](0020-tamp-components.md) (Tamp.Components).
- Proposed under [ADR 0009](0009-governance-and-namespace-policy.md) §3.1 (PR + `Status: Proposed`, lazy consensus). Tracking: TAM-254.
