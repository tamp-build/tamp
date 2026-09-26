# ADR 0019: Agent-first toolchain — dual-channel emission, typed results, parallel-safety, capability tiers

**Status:** Proposed (2026-09-26). Tracking epic: [#7](https://github.com/tamp-build/tamp/issues/7).

**Context.** The largest consumer of Tamp today is its own author, and that work is already agentic across several projects — N agents in N worktrees, each editing, building, and retrying constantly. Nuke, Cake, and MSBuild are human-first frameworks with machine output bolted on afterward (MSBuild's binlog is structured but not agent-shaped). Nobody in .NET treats an agent as a *peer worker* from the ground up. That is a real, currently-empty position — and, more importantly, it pays off on the dogfood alone regardless of whether any external adopter ever wants it.

This is **not** "machine mode vs human mode." It is **one source of truth rendered to two audiences.** The build stays human-authored C# (keep the no-DSL, no-manifest ethos — it is a strength). What becomes dual-channel is the **output** (results, diagnostics, events) and the **control surface** (introspection, planning, invocation). Emit structure first; render text second. The human console is a *view* of the structured stream.

The failure mode to avoid above all: **forking into a human codebase and an agent codebase.** The day that happens they drift and the value is gone. Every decision below is the same move — structure first, text second — applied in a different place.

There is a concrete, present-day version of that risk in the code. `Executor.cs` already maintains **two** hand-synced emission paths in the same methods: the `IBuildReporter` callbacks and the `TampDiagnostics` (ADR-0018) spans/metrics. That is a proto-fork. Adding an agent NDJSON channel as a *third* hand-synced emitter would violate this ADR's central invariant on day one. So consolidation onto one canonical event model is the unnumbered prerequisite (ticket `#0`), not an afterthought.

## Decision

Adopt the agent-first direction under five invariants, nine pillars, and two load-bearing design decisions recorded below. Everything is **additive and opt-in**: the human console stays the default, structured output is a second channel, and every existing consumer — the 70+ satellites, `tamp-beacon`, `tamp-findings` — keeps working untouched.

### Invariants — do not violate

1. **One source of truth.** Human text is derived from structured output, not maintained beside it.
2. **Human-authored, no DSL.** The build script stays plain C# referencing `Tamp.Core`. No manifest format to serve agents.
3. **Additive and opt-in.** Structured output is a second channel; the human console is the default.
4. **Attributable by construction.** Every action, event, and artifact carries a worker id — agent or human.
5. **Least privilege for agents.** Safe targets by default; side-effectful targets gated behind explicit capability.

### Pillars (condensed)

1. **Dual-channel output** — every target emits a versioned NDJSON event stream alongside the human console, on a separate sink so they never interleave. Everything else hangs off this.
2. **Diagnostics as one shape** — every diagnostic-emitting surface (compiler, TAMP001-006 analyzers, test failures, scanner findings) normalizes to `Tamp.Sarif`. An agent learns one diagnostic schema and can act on the whole toolchain.
3. **Typed target results** — a target yields a result object (inputs hash, outputs+hashes, SARIF-normalized diagnostics, timing, and a **remedy**: class + reproduce command + hint), not just an exit code. This is what powers agent self-remediation.
4. **Parallel-worker model** — collision-free by construction across worktrees: no absolute state outside the worktree, per-run temp, per-worker caches, container name/port isolation, single-target/slice runs, cheap retries.
5. **Worker identity** — threaded through every event, artifact, and ingest. Solved once at the build layer; flows downstream (closes the same actor gap that bit `tamp-findings`).
6. **Emission-contract backbone** — one structured run stream feeds both the human dashboards and the agents (see D1).
7. **Capability tiers** — targets classified safe / side-effectful / grey; an agent context is scoped to the safe tier; publish/deploy/secret-reveal require elevated capability. The build-graph version of findings' "an agent can't mint AcceptRisk."
8. **Control surface (tamp-build MCP)** — `list_targets`, `describe_target`, `plan --json`, `run_target`, `get_result`, `get_log`. Read/drive split; side-effectful runs behind elevated capability. (Already on the v2 roadmap as `tamp :mcp-server`.)
9. **Agent economics** — quiet-JSON, compact failure summaries, and addressable per-target logs pulled on demand, because the consumer is billed per token of context.

### D1 — One canonical event stream, one projection per consumer

Not same-bytes, not two contracts. **One producer, one schema registry, one `schemaVersion`.** A consumer projection is a *declared subset of the vocabulary rendered to that consumer's shape*, never a parallel producer.

- **Why not same-bytes:** beacon wants coarse events (build/target status, failure alerts); agents want the firehose (`tool.invoked`, `diagnostic.emitted`, typed result + remedy). Forcing beacon to eat the fine stream bloats its model; forcing agents onto beacon's coarse contract starves the remediation loop. This is the same call already made when `tamp-findings` was split from `tamp-beacon`.
- **Projections have two parts.** The agent projection is a filter → NDJSON. The beacon projection is (a) event-subset selection **+** (b) an OTLP span/metric rendering with `trace_id`/`parentSpanId` and Meter instruments, preserving the TAM-218 cross-batch reconcile. The registry governs (a); (b) is a compat-bearing adapter that pins ADR-0018's ~80 frozen tag keys as a one-place translation table. `#0c` re-expresses an emitter, not a `.Where()`.
- **Span identity in the envelope.** The canonical envelope carries a stable `spanId`/`parentSpanId` alongside logical ids (`buildId`/`runId`/`targetId`) so the NDJSON and OTLP projections agree on identity, and an agent can later deep-link into beacon's view. `seq` (monotonic per run) is meaningful for the ordered agent projection; OTLP is unordered by nature.

**Staging** (so the beacon migration never gates agent value):

- **`#0a`** — define the canonical event model + schema registry (source of truth), including correlation ids and `spanId`.
- **`#0b`** — agent NDJSON sink consumes it. Greenfield, zero regression, ships value immediately.
- **`#0c`** (committed fast-follow, not a blocker) — re-express ADR-0018/beacon emission as a coarse projection over the canonical stream and **delete** the legacy `TampDiagnostics` dual path.

The staging accepts a short, deliberate double-emit during the `#0a→#0c` window (canonical + legacy-frozen). That window reintroduces the proto-fork only if `#0c` slips, so `#0c` is guarded: it deletes the legacy emitter at its end, and a skipped tracking test ("beacon still on legacy emitter — remove after #0c") keeps the temporary from silently becoming permanent.

### D2 — Slice-running first; cache-aware execution deferred behind an advisory phase

Slice-running (`--only`/`--from` a target + its dirty dependents) is enough to make agent retries cheap and is the correct first cut. Cache-aware execution is a correctness surface whose blast radius stays closed for now.

- **Why defer the cache:** a false cache hit ships wrong bytes silently — in a gating/defense context, a stale artifact sailing through the gates is precisely the nightmare this work exists to prevent. The hard part is not the cache; it is *hermeticity* (declaring every input incl. env, tool versions, hidden deps). Bad risk/reward for cut one.
- **Why slicing is enough:** the agent already knows what it changed; it fixed target X and runs X plus X's dirty dependents. "Dirty" here means **graph-downstream** (transitive `DependsOn(X)` closure over `TargetGraph`), **caller-owned, zero hashing** — the moment "dirty" means "a hash says so," the deferred cache surface is back.
- **Fail-closed on missing inputs.** `--only`/`--from X` means "run X against its declared upstream artifacts already on disk, and **error** if a required one is absent," not "run against whatever happens to be there." This is fail-closed *with respect to declared* artifacts (dependencies' `Produces` globs), not true inputs — acceptable because the scope is caller-owned and the full `tamp` build remains the correctness authority.
- **Flag axes are distinct.** Target *selection* (which targets: default = X+upstream; `--skip-deps` = just X; `--from X` = X+downstream) is a separate axis from an in-target *rule filter* (`--rule CS1002`, which powers `remedy.reproduce`). Do not overload one flag across both, or `remedy.reproduce` strings become ambiguous to the agent meant to run them verbatim.
- **Migration primitive.** Emit `inputsHash` in the typed result **now**, observability-only, no execution effect. Later add a would-skip **advisory** phase (log "inputs unchanged, would skip" but still run) to measure false-hit/false-miss against reality; only once hashes prove trustworthy flip to actually skipping. Advisory-then-enforcing, same discipline as the gating dial. Note: `InputHashProducer` is author-supplied and optional today, so the advisory dataset is sparse until (approximate) hermeticity is invested in — un-deferring the cache is not free later.

### One safety idiom, three places

Capability tiers (default-deny side effects, explicit elevation), the caching dial (advisory-then-enforcing), and slice scoping (`--only`/`--from`, fail-closed) are the **same** governance pattern: **correct-by-default, fast-by-explicit-scope, fail-closed when the assumption breaks.** Stating it as a named principle makes the consistency deliberate — and it is the thing Nuke/Cake/binlog structurally lack.

## Consequences

**Positive.** A single structured run stream that both humans and agents consume; agents that read structure not logs, act on one diagnostic shape, self-remediate from typed remedies, run collision-free per worktree, and are gated out of anything that mutates the world without authorization. Humans lose nothing — they keep the pretty console. The dogfood improves immediately at `#0b`, independent of beacon and of any external adopter.

**Negative / cost.** Ticket `#0` is real consolidation work touching the executor's emission core. The `#0a→#0c` window carries a guarded, time-boxed double-emit. Capability tiering requires `CommandPlan` to carry a required-capability facet. Full addressable per-target logs need a persistence design (`get_log`). Worker identity is coupled to a `tamp-ingest-v1` actor-field bump (a downstream contract change) before attribution flows all the way to findings.

**Neutral.** The event schema is governed like ADR-0018 from line one: additive-only, ADR-amended for any rename/removal. Sub-decisions (the event schema v1 vocabulary, the capability model) may earn their own follow-up ADRs; this ADR sets the direction and pins the two load-bearing decisions.

## Related

- ADR 0004 — CommandPlan as universal wrapper output. The dispatch seam where `secret.access.requested` and per-plan capability are emitted.
- ADR 0005 — Secret as a distinct type. Reinforces why event payloads carry access-requested, never the value.
- ADR 0009 — Governance. This ADR is proposed under §3.1 (PR + `Status: Proposed`, lazy consensus).
- ADR 0012 — Declarative resource consumption. Slice-running and per-worker resource isolation extend the "declare in the model now, enforce later" pattern.
- ADR 0018 — Diagnostics emission contract. Becomes the first *projection* over the canonical stream (D1), rather than a parallel producer.
