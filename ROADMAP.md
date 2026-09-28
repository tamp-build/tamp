# Tamp roadmap

What's planned, and in which release. This is a **living document** — it changes as releases ship
and priorities move. For what has already shipped, see [CHANGELOG.md](CHANGELOG.md).

Versioning follows [ADR 0022](docs/adr/0022-versioning-policy.md): **a minor is a planned feature
milestone you can pin** (the slots below are firm — routine bug fixes do not consume them); a
**patch** carries everything shipped between milestones, including the occasional breaking fix,
which is called out in a **Breaking** section of that patch's changelog entry. This is safe because
every consumer pins Tamp packages exactly (Central Package Management), so no release ever
auto-propagates — upgrades are always deliberate.

## Now

### 1.15.x — adopter-friction hardening *(current line)*

Stabilization from the first wave of real 1.15 agent-first adoption (two dogfood adopters). Fixes
and small additions land here as patches; see the changelog for each.

- **1.15.1** — Produces coverage advisory (#63); imperative tool dispatch now on the canonical
  event stream + `ProcessRunner.Run` (#66); `--reporter=json` emits the canonical envelope
  **(breaking)** (#68); argument parsing fails closed + `--help` **(breaking)** (#70); `tamp mcp`
  entrypoint guidance + capability fluent-API docs (#71); `--verify-redaction` self-test (#72);
  exit-code / Produces-glob docs (#62, #64).

## Next

### 1.17.0 — Tamp.Components + Attestation evidence contract

*(1.16 skipped — Components was complete and unreleased; bundled into 1.17 with the attestation
contract to avoid a redundant release push.)*

**Tamp.Components.** Reusable targets via interface mixins — `class Build : TampBuild, ICompile, ITest, IPack` — so the
~60 satellites stop hand-copying the same `Restore → Compile → Test → Pack → Ci` shape and instead
pick up improvements by a package bump. Also gives every tamp-built repo a **uniform, agent-facing
target topology** (predictable `Compile`/`Test`/`Pack`/`Ci` under `tamp mcp`).

- Plan of record: [ADR 0020](docs/adr/0020-tamp-components.md) · Tracking: epic
  [#57](https://github.com/tamp-build/tamp/issues/57)

**Attestation evidence contract.** Additive `BuildEvent` surface for compliance evidence: a
`conformance.evaluated` event (four-valued verdict + structured reason), a reusable `Provenance`
sub-record, and optional `ControlRefs` (control mapping). The wire contract that downstream
`tamp-findings` ingests as attestation-grade evidence; the ADR-conformance review tooling that
produces it lives in a separate satellite.

- Plan of record: [ADR 0023](docs/adr/0023-attestation-evidence-contract.md)

### 1.18.0 — NUKE → Tamp assisted migration

Tooling to convert an existing NUKE build to Tamp: the mapping reference (shipped) plus a converter
**and** an agentic guided migration that walks a repo through the move, using the coverage/gap
inventory to be honest about what can and can't be auto-converted.

- Plan of record: [ADR 0021](docs/adr/0021-nuke-to-tamp-migration.md) · Tracking: epic
  [#58](https://github.com/tamp-build/tamp/issues/58)

## Backlog *(planned, not yet scheduled to a minor)*

Real work with owners/ADRs, awaiting a slot:

- **Schema-driven wrapper generation** — bootstrap a tool wrapper from its `--help` output, AI-assisted
  ([ADR 0013](docs/adr/0013-schema-driven-wrappers.md)).
- **JetBrains Fleet extension** (`tamp-fleet`) — sibling to [Tamp for VS Code](https://github.com/tamp-build/tamp-vscode).
- **Community module template** — a scaffold for third-party satellites.

## Someday

- **2.0** — reserved for a deliberate, batched breaking re-architecture. No date. It exists so routine
  breaking fixes never need a major bump — they ride patches (per ADR 0022) and 2.0 stays clean for an
  intentional redesign.

---

*Slot contents are commitments to a **milestone**, not hard dates. A slot's number is firm once
listed here; its ship date is not. Questions about direction:
[#2](https://github.com/tamp-build/tamp/issues/2).*
