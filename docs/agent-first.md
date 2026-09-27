---
title: Agent-first
description: Tamp's machine output channel and control surface for agentic development.
---

# Agent-first — the machine output channel

Tamp was designed for a world that arrived while it was being built: one where the thing
reading your build output is increasingly **not a human scrolling a terminal, but an agent
deciding what to do next.**

When Tamp started, a build tool had one audience. It printed to a console, a person read the
console, and the person decided whether the build was good. Every build tool in .NET —
MSBuild, Cake, NUKE — is shaped by that assumption: text first, structure bolted on afterward
(MSBuild's binlog is structured, but it is a post-hoc artifact, not the primary channel).

A year of building software alongside coding agents changed the assumption. The author's own
work is now agentic across several projects at once — N agents in N git worktrees, each
editing, building, and retrying constantly. In that world the build tool has a **second
first-class consumer**, and that consumer has different needs than a human:

- It reads **structure**, not prose. A regex over console text is a fork waiting to break.
- It works in **parallel** with other agents and with you — so the build must be collision-free
  by construction, and every result must say **who** produced it.
- It pays for its context **by the token**, so terse, addressable output beats a firehose.
- It shouldn't be able to **deploy to production by accident** while iterating on a test.
- It wants to **call the build like an API** — list targets, plan a slice, run one, read the
  typed result — not screen-scrape a CLI.

Tamp's answer is one rule, applied everywhere: **emit structure first, render text second.**
The human console is a *view* of a single canonical event stream — never a separate code path.
This is the whole of [ADR 0019](adr/0019-agent-first-toolchain.md); what follows is the surface
it produces. Everything here is **additive and opt-in** — the human console is still the
default, and every existing consumer keeps working untouched.

---

## The canonical event stream

Every build fact — build/target lifecycle, each tool invocation and exit, diagnostics,
produced artifacts, capability gates, secret-access requests — is emitted **once** as a typed
`BuildEvent`, then projected to each consumer. The human console, the `IBuildReporter` surface,
and the ADR-0018 OpenTelemetry spans/meters are all *projections* of that one stream, not
parallel emitters. (Removing the last hand-synced dual-path — the old `TampDiagnostics`
emission — was the unnumbered prerequisite for the whole initiative.)

Turn on the agent channel with a flag or an env var; it writes newline-delimited JSON
(NDJSON, one event per line, LF, UTF-8, flushed per line) to a separate sink so the human
console is untouched:

```bash
dotnet tamp Ci --events build.ndjson       # or: TAMP_EVENTS=build.ndjson dotnet tamp Ci
dotnet tamp Ci --reporter=json             # quiet-JSON: NDJSON to stdout, banner suppressed
```

Each event carries a stable envelope: `type`, `buildId` / `runId`, `traceId` / `spanId` /
`parentSpanId` (so the stream reconstructs into a span tree), a monotonic `seq`, a `ts`, the
resolved `workerId` (see [Attribution](#attribution-by-construction)), and a typed payload.

## Typed target results + remedies

A finished target doesn't just say pass/fail. `target.finished` carries the structured result
an agent can act on without parsing logs:

- **`outputs`** — the artifacts the target produced (path + `sha256` + kind + size), synthesized
  from its declared `Produces` globs, with one `artifact.produced` event per file.
- **`inputsHash`** — the target's declared input hash (observability today; the migration
  primitive toward cache-aware execution).
- **`remedy`** — on failure, a `{ class, reproduce, hint }`: a coarse class (`config` / `code` /
  …), a **command that re-runs just this failure** (e.g. `tamp Compile --rule CS0246`), and the
  reason. The agent gets a next action, not a stack trace to mine.

## `tamp mcp` — the control surface

`tamp mcp` runs Tamp as a [Model Context Protocol](https://modelcontextprotocol.io) server over
stdio, exposing the build graph as callable tools so an agent drives the build like an API
instead of screen-scraping the CLI:

| Tool | What it does |
|------|--------------|
| `list_targets` | the catalog — targets with deps, produced artifacts, capability tier |
| `describe_target` | one target's inputs/outputs/deps/capability |
| `plan` | the resolved execution order (DAG slice) for a target, without running anything |
| `run_target` | run a target **under agent capability enforcement** (see below) |
| `get_result` | the typed result of the last run (status, outputs, inputsHash, remedy) |
| `get_log` | the addressable per-target log from the last run |

Read tools need no elevation; `run_target` executes under default-deny side-effect
enforcement unless explicitly elevated.

## Capability tiers — correct-by-default

An agent iterating on a test target should not be able to publish a package or deploy a service
because a dependency happened to be side-effectful. Targets and commands carry a capability tier
(`Safe` / `Grey` / `SideEffectful`; revealing a `Secret` implies side-effectful). Under agent
enforcement, side-effectful work is **denied by default** and each decision is emitted as a
`gate.evaluated` event:

```bash
dotnet tamp Deploy --enforce=agent                        # side effects blocked, gate.evaluated emitted
dotnet tamp Deploy --enforce=agent --allow-side-effects   # explicit elevation
```

Off by default — humans are unaffected. This is the same idiom as the caching dial and slice
scoping: **correct-by-default, fast-by-explicit-scope, fail-closed.**

## Slice-running — fast-by-explicit-scope

`--from <target>` runs a target and everything **downstream** of it (its transitive dependents),
treating upstream targets as already-satisfied — and **fail-closed**: if an assumed-satisfied
upstream target's declared `Produces` artifacts are missing, the build fails rather than running
on stale or absent inputs. An agent that just changed one file reruns exactly the affected slice.

## Agent economics

Agents pay per token of context, so Tamp is frugal by default and lets an agent fetch detail
lazily:

- **Compact failure summary** — after the summary table, a terse `FAILED (N):` block lists each
  failed target with its `remedy.reproduce`. The actionable bit, without scrolling.
- **Addressable per-target logs** — `--capture-logs` tees each target's output to a redacted
  `.tamp/logs/<buildId>/<target>.log`; `target.finished.logPath` carries the path, and MCP
  `get_log` reads the one failing log instead of holding the whole stream. Secrets are redacted
  on the way to disk.

## Attribution by construction

Every event and artifact carries a resolved `workerId` — who produced this build, an agent or a
human. Resolution order mirrors the host layering: `TAMP_WORKER_ID` (explicit; agent ids look
like `agent:pool/3`) → CI actor → git author → `human:<login>`. That id flows onto the ingest
wire as an `actor { id, kind }` ([`tamp-ingest-v1`](https://github.com/tamp-build/tamp-findings#tamp-ingest-v1)
v1.3) and is persisted by `tamp-findings`, so every finding, artifact, and build is attributable
end-to-end without anyone bolting attribution on afterward.

## Parallel-safety

N agents in N worktrees must not collide on a container name, a host port, or a shared cache.
The satellites that spin up local resources namespace them per-worker by default (correct-by-
default, opt out for intentional sharing): per-worker sccache directories and shared-backend key
prefixes, per-worker compose project names and host-port bands, per-worker container labels for
attribution and cleanup. Two agents can run the same container-spinning target concurrently with
no collision.

---

## See also

- [ADR 0019 — Agent-first toolchain](adr/0019-agent-first-toolchain.md) — the five invariants,
  nine pillars, and two load-bearing design decisions behind everything above.
- [ADR 0018 — Diagnostics emission contract](adr/0018-diagnostics-emission-contract.md) — the
  OpenTelemetry projection the agent stream shares a source of truth with.
