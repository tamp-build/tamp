# ADR 0021: NUKE → Tamp assisted migration

**Status:** Proposed (2026-09-27). Tracking epic: [#58](https://github.com/tamp-build/tamp/issues/58). Depends on [ADR 0020](0020-tamp-components.md) (components) for the component-mapping piece.

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

## Open / deferred

- Machine-readable format for the reference (YAML vs JSON) and whether it ships in a package for the tool to consume.
- Whether `tamp migrate nuke` lives in `Tamp.Cli` or a separate `Tamp.Migrate` package.
- Handling of `[Partition]`/partitioned parallelism — flagged as `manual` until Tamp has an equivalent (separate ADR if we pursue it).
