# ADR 0021: NUKE → Tamp assisted migration

**Status:** Accepted (2026-11-04; proposed 2026-09-27). Target: **1.19** (moved from 1.17 when Components + the attestation contract took that slot, then from 1.18 when long-running services took it; see [ROADMAP](../../ROADMAP.md) / [ADR 0022](0022-versioning-policy.md)). Tracking epic: [#58](https://github.com/tamp-build/tamp/issues/58). Depends on [ADR 0020](0020-tamp-components.md) (components, shipped in 1.17) for the component-mapping piece.

## Context

The [known-gaps](../../README.md#known-gaps--read-before-you-migrate) list two NUKE items: no `Tamp.Components` (now ADR 0020) and **no automated NUKE → Tamp converter** — "a tool has never been scoped." This ADR scopes it.

It is more tractable than a general transpile because **Tamp deliberately mirrored NUKE**:

- **Target authoring is nearly identical** — `Target Foo => _ => _.DependsOn(Bar).Executes(() => …)`, same `Before`/`After`/`Triggers`/`Produces`.
- **Path helpers are ~1:1** — `RootDirectory`, `AbsolutePath`, the `/` operator.
- **Injection overlaps** — `[Parameter]`, `[Solution]`, `IsLocalBuild`/`IsServerBuild`.

So migration is a **targeted delta**, not a language port. Two forces make it viable *now*: ADR 0020 makes `Nuke.Components` builds map near-1:1, and the agent-first surface (ADR 0019: `tamp mcp`, typed results, capabilities) makes a "convert → build → read failure → fix → repeat" loop reliable for the parts a rewriter can't mechanically handle.

**Framing, non-negotiable: this is *assisted* migration, never "one-click."** Coverage is bounded by the mapping table and by tool parity; a NUKE build using a tool Tamp doesn't wrap, or `[Partition]` parallelism (no Tamp equivalent), converts partially and **flags the gap**. Promising "automatic" would set a false expectation and produce silently-wrong builds. Assisted-with-honest-gaps is the contract.

## Decision

Two phases, in order. **The reference mapping is built first and is the single source of truth** that every downstream tool and agent consumes.

### Phase 1 — the mapping reference (do this first, fully)

A canonical NUKE-API → Tamp-API mapping ([`docs/nuke-migration/mapping-reference.md`](../nuke-migration/mapping-reference.md)), organized by category:

- **Boilerplate** — base class, `Main`/`Execute` shape, default-target designation, `build.sh`/`.cmd` → `tamp.sh`/`.cmd`.
- **Injection attributes** — `[Parameter]`, `[Solution]`, `[Secret]`, `[PathExecutable]` → `[FromPath]`, `[GitVersion]`/`[GitRepository]`, CI detection.
- **Paths & IO** — the near-1:1 `AbsolutePath`/glob/filesystem surface.
- **Target DSL** — `DependsOn`/`Triggers`/`Before`/`After`/`OnlyWhen*`/`Requires`/`Produces`/`Consumes`/`Proceed`/failure modes.
- **Tool task families** — per tool: NUKE `XyzTasks.*` + settings methods → Tamp `Xyz.*` wrapper + settings, starting with **DotNet** (covers most builds), then Docker, and outward.
- **Components** — `Nuke.Components.*` → `Tamp.Components.*` (per ADR 0020).
- **Logging / misc** — Serilog `Log.*` → Tamp `Logger`.

Each entry carries a **confidence tier**: `auto` (deterministic 1:1), `assisted` (needs light judgment), `manual` (no clean mapping — flag). A machine-readable projection (YAML/JSON) is derived from it so tooling doesn't re-parse prose. **The reference is valuable on its own** — a human or an agent can migrate against it with no dedicated tool.

### Phase 2 — tooling (planned after the reference exists)

Once the reference is real, build **both**, sharing it:

1. **`tamp migrate nuke`** — a deterministic Roslyn rewriter that applies the `auto`-tier mappings (base class, usings, target shape, default target, covered task calls), and for everything else emits `// TAMP-MIGRATE:` markers plus a coverage report. Handles the mechanical bulk with zero ambiguity.
2. **Agent-guided migration** — an agent consumes the same reference and drives the tail (marker resolution, unmapped tools, custom code) through a `tamp mcp` build-to-green loop, reasoning about intent where syntax mapping stops. This is where ADR 0013's "AI-assisted bootstrapping" direction pays off.

The split is deliberate: the deterministic tool gives repeatable, reviewable bulk conversion; the agent covers the long tail that a rewriter can't. Neither alone is sufficient; the reference makes both cheap.

## Consequences

- **Lowers the single biggest NUKE-migration cost** without over-promising.
- **The reference is the durable asset** — it survives tool rewrites, doubles as migration documentation, and grows per-tool as wrappers are mapped.
- **New surface:** the reference (ongoing curation), the `tamp migrate nuke` command, and the agent playbook.
- **Coverage is explicit, not hidden** — the confidence tiers + coverage report tell an adopter exactly what converted and what needs hands.

## Phasing

1. **Phase 1** — reference mapping: core categories + the DotNet family first, then broaden per tool. (Groundwork starts now.)
2. **Phase 2a** — `tamp migrate nuke` Roslyn rewriter over the `auto` tier + markers + report.
3. **Phase 2b** — agent-guided migration playbook + a real dogfood conversion of a public NUKE build.

## Phase 2 design (decided 2026-11-04)

The Phase 1 open questions are settled, and Phase 2 has a design ([plan on #58](https://github.com/tamp-build/tamp/issues/58#issuecomment-6633517100)).

**Settled:**

- **Format: YAML, embedded in the tool.** [`mapping.yaml`](../nuke-migration/mapping.yaml) (schema 0.2) gives every rule a stable `id`, a `match` from a closed vocabulary (`base_type`, `attribute`, `invocation`, `identifier`, `using`, `entrypoint`, `field_invocation`, `file`), a `rewrite` (`keep`, `rename`, `template`, `remove`, `handler`), a `marker` for anything not `auto`, and `verify`: the Tamp-side symbols it relies on. `Tamp.Migrate` embeds the file, so a given tool version always applies the mapping it was tested with. `tests/Tamp.Migrate.Tests` checks the file's schema and tier consistency, and checks every in-tree Tamp symbol (Tamp.Core, Tamp.NetCli, Tamp.Components) by reflection, so the mapping can't drift from the API it targets. Satellite wrappers' verbs are not verified in-tree.
- **Packaging: a separate `Tamp.Migrate` dotnet tool, in this monorepo.** `tamp migrate nuke …` in `Tamp.Cli` forwards to it (`dotnet tool exec Tamp.Migrate@<same version>`), so the `tamp` tool every CI run installs doesn't carry Roslyn for a one-off job. It lives in the core repo, not a satellite, because its rules co-evolve with the core API and the mapping lives here ([ADR 0026](0026-satellite-repositories-for-wrappers.md)'s monorepo list includes it).
- **`[Partition]` stays `manual`** until Tamp has partitioned parallelism (a separate ADR if pursued).
- **Scope: NUKE 8+** (the Serilog era). Older builds upgrade NUKE first.

**Design:**

1. **The rewrite is syntactic only and never compiles the NUKE build.** Roslyn parses the build with no NUKE restore, so it works where the old build no longer restores. NUKE calls are recognized by their usings (`using static Nuke.Common.Tools.DotNet.DotNetTasks`) and names. Every edit is traced (file, span, rule id, tier). An `assisted` or `manual` match keeps its code under a `// TAMP-MIGRATE(<rule-id>): <marker>` comment. The tool also generates `build/Build.csproj` (Tamp.Core plus one reference per wrapper family the build uses, versions via `TampVersionResolver`) and the `tamp init` bootstrap scripts.
2. **It rewrites in place, on a clean git working tree**, so review is `git diff`. NUKE's scripts and `.nuke/` are left for the user to delete. `--dry-run` produces the inventory and report only; `--verify` runs `dotnet build` on the result and adds compiler errors to the report.
3. **The report is `tamp-migration.json` plus a Markdown summary.** It lists what converted (by tier), every marker, the gaps found (`gaps:`), and **the NUKE target graph, extracted statically**. That graph is the parity check: after migration, `plan` / `list_targets` must show the same targets and edges.
4. **The agent path uses the same report**, plus two `tamp mcp` tools: `compile_diagnostics` (build the build project; structured compiler errors) and `migration_status` (the report, the markers left, the graph-parity diff). The playbook (`docs/nuke-migration/agent-playbook.md`) loops: resolve a marker, compile, check `plan` parity, repeat until there are no markers, the build compiles, and the graph matches.
5. **Tests use a corpus of before/after files**: small NUKE builds with their expected Tamp output. DotNet-family output is also compiled against the in-tree Tamp.Core and NetCli.

**Delivery, each step useful on its own:**

1. Decisions and data: this section, `mapping.yaml` 0.2 and its tests.
2. `Tamp.Migrate --dry-run`: inventory and report.
3. The `auto`-tier rewrite.
4. Tool calls and project generation, plus `--verify`.
5. The agent path: two MCP tools and the playbook.
6. Dogfood: a real public NUKE build migrated to green and written up.
