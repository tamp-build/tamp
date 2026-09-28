# ADR 0023: Attestation evidence contract — conformance events, provenance, control mapping

**Status:** Accepted (2026-09-28); targets Tamp.Core 1.17.0.

**Context.** The agent-first event stream (ADR 0019) and the diagnostics emission contract (ADR 0018) already give Tamp.Core a mature, attestation-adjacent surface: every `BuildEvent` carries `WorkerId` (the producing actor), a UTC timestamp, `BuildId`/`TraceId`/`SpanId` correlation, and serializes to the pinned `tamp-ingest-v1` shape. The vocabulary already includes SARIF-normalized `diagnostic.emitted`, `artifact.produced` (with content hashes), and `gate.evaluated`. The schema is governed **additive-only** (`BuildEventSchema`, pinned by `CanonicalEventSchemaTests`): adding an event type or payload field is non-breaking; renaming or removing one requires an ADR amendment.

Downstream, `tamp-findings` is becoming an evidence-of-record system for compliance regimes (FedRAMP / GovRAMP and commercial equivalents). Its own ADRs establish a **four-valued verdict** model — `Pass | Fail | Unknown | Error`, where "the check never ran" is `Unknown` and blocks with a different remedy (tamp-findings ADR 0001) — plus **advisory/enforcing enforcement with a locked instance floor** (tamp-findings ADR 0004) and a `Suppression`/VEX shape for dispositioned deviations.

A new capability — **agentic ADR-conformance review** — will compare a repo's code against its Architecture Decision Records and emit conformance verdicts as evidence. The review tooling itself lives in a **satellite** (out of scope here). What core owns is the **wire contract** that satellite emits and `tamp-findings` ingests. This ADR defines that contract.

**Problem.** The current event surface cannot express, in an attestation-grade way:

1. A **four-valued verdict**. `gate.evaluated`'s `Verdict` is `pass | fail` only — it cannot say "could not reach a verdict," which is the load-bearing state of the downstream model.
2. **Evidence provenance beyond the actor.** `WorkerId` names *who*; attestation also needs *from what* — the commit under evaluation, which rule (and rule version) produced the finding, by what method, and — for non-deterministic checks — which model and whether an adversarial verify pass confirmed it.
3. A **control mapping**. Nothing ties a finding to the control it is evidence for (NIST 800-53 CM-3/6, SA-15, …) — the join key between evidence and an SSP.
4. A **conformance verdict with a required structured reason** — the ADR text and the code line that conflict — which is more than `diagnostic.emitted` carries.

## Decision Drivers

* **Additive-only.** Do not break the pinned schema (ADR 0018 / `BuildEventSchema`). Every change here is a new type or a new optional field; nothing is renamed or removed.
* **Self-attesting reproducibility.** tamp-findings ADR 0001 requires a signed attestation to be reproducible later, and rules that are non-pure (an LLM-backed conformance check *is* non-pure) resolve this by **storing the verdict rather than recomputing it**. The event must therefore carry enough provenance to stand on its own as frozen evidence.
* **Small core.** The producers (rule generation, examination, reverse examination) live in a satellite. Core adds the *contract*, not the tooling.
* **Language-agnostic.** Conformance evidence is not .NET-specific; the payload names an ADR and a source location, nothing stack-bound (consistent with ADR 0018).
* **One event stream.** Conformance is not a side-channel; it is another projection over the canonical `BuildEvent` stream, like diagnostics and gates.

## Decision

### 1. A new `conformance.evaluated` event type (additive)

Add `BuildEventTypes.ConformanceEvaluated = "conformance.evaluated"` to `BuildEventSchema.Types`, with a pinned `ConformanceEvaluatedPayload`:

| Field | Meaning |
|---|---|
| `AdrRef` | the ADR this verdict is against (e.g. `"0018"`, or a repo-qualified ref for cross-repo) |
| `RuleId` | the rule within that ADR's rule-set (e.g. `"0018-r1"`) |
| `Verdict` | four-valued — see §2 |
| `AdrQuote` | the ADR text the rule encodes (required on `fail`) |
| `CodeEvidence` | the code line/snippet that conflicts (required on `fail`) |
| `Location` | reuse `DiagnosticLocation` (`File`, optional `Line`) |
| `Method` | `deterministic` \| `semantic` \| `verify` — how the verdict was reached |
| `Blocks` | whether this verdict blocks, mirroring `gate.evaluated` (enforcement decided downstream) |
| `Provenance` | §3 |
| `ControlRefs` | §4 |

`AdrQuote` + `CodeEvidence` make the structured reason a *required* output on a `fail`, matching tamp-findings ADR 0001's "a structured reason is required on both paths." A claimed violation that cannot quote both sides is not a `fail` — it is `unknown` (see §2).

### 2. A pinned four-valued conformance verdict

Add `ConformanceVerdict` = `pass | fail | unknown | error`, matching tamp-findings ADR 0001 exactly:

| Verdict | Meaning | Downstream |
|---|---|---|
| `pass` | code honors the rule | ship |
| `fail` | violation, with `AdrQuote` + `CodeEvidence` | block, "fix the code" |
| `unknown` | semantic check could not decide, or a deterministic probe did not run | block, "your check didn't answer" |
| `error` | evaluation itself broke | block, alert operator |

The adversarial verify pass (`Method = verify`) is the `fail`-vs-`unknown` decider: an unquotable claim resolves to `unknown`, never `fail`.

**This ADR does not mutate `gate.evaluated`.** Its `Verdict` stays `pass | fail`. Aligning it to four values is a reasonable future amendment but is deliberately *not* decided here, to keep this change strictly additive. The gap between the two verdict vocabularies is noted below.

### 3. A reusable `Provenance` sub-record (additive, optional)

A nested record carried on `conformance.evaluated` (and offered as an optional field on `diagnostic.emitted`, so any evidence source can be attestation-grade):

| Field | Meaning |
|---|---|
| `CommitSha` | the revision evaluated (`GitRepository` already resolves it) |
| `RulesSha` | content hash of the rule-set that produced the verdict (`AbsolutePath.Sha256Of`) — pins *which* interpretation was in force |
| `Method` | mirrors the payload's method for standalone consumption |
| `ModelId` | for `semantic`/`verify`: the model that produced the verdict |
| `VerifyVerdict` | the adversarial pass's result, when one ran |

The envelope's `WorkerId` remains the actor and `Ts` the time; `Provenance` adds the *what*. `CommitSha` + `RulesSha` are the pair that make the evidence reconstructible at a point in time and satisfy the ADR 0001 "snapshot the verdict" requirement — the frozen event *is* the reproducible record for a non-pure check.

### 4. Optional `ControlRefs` on evidence payloads

Add `ControlRefs` (`IReadOnlyList<string>?`) to `conformance.evaluated` (and optionally `diagnostic.emitted`): control identifiers this finding is evidence for (`"CM-6"`, `"SA-15"`, …). Core defines the *field*, not a control catalogue — the catalogue and the SSP mapping belong to the consumer.

### 5. Schema stays 1.0

Per the ADR 0018 / `BuildEventSchema` additive-only contract, adding a type and optional fields is non-breaking; `BuildEventSchema.Version` stays `"1.0"` (as it did through the #11 vocabulary expansion). Register the new type in `BuildEventSchema.Types` and add the `JsonDerivedType`; `CanonicalEventSchemaTests` pins it so an accidental rename fails CI.

## What this ADR does not decide (scope)

* **The conformance tooling.** Rule generation / examination / reverse examination live in a satellite, its own ADR and cadence.
* **The rules artifact.** `adr-rules.json` lives in the *governed repo's* git (versioned intent), not in core and not on the wire.
* **Enforcement.** Advisory/enforcing modes and the locked floor are owned by tamp-findings ADR 0004. Core only emits `Verdict` + `Blocks`, as `gate.evaluated` already does.
* **The control catalogue** and SSP mapping — consumer-side.
* **Cryptographic provenance** (DSSE signing, verified inbound attestation). That is tamp-findings' long-term provenance track; `Provenance` here is the structured evidence it will later sign, not the signature.

## Consequences

**Positive**

* Conformance evidence becomes attestation-grade and self-describing: verdict, reason, provenance, and control mapping travel together on the canonical stream.
* Entirely additive — no existing consumer breaks; the change is a new type plus optional fields.
* `Provenance` and `ControlRefs` are reusable by *any* evidence source (diagnostics too), not just ADR conformance.
* Reproducibility is satisfied structurally: a non-pure (LLM) verdict is frozen with its `CommitSha` + `RulesSha` + `ModelId`, which is exactly the "snapshot the verdict" posture tamp-findings ADR 0001 requires.

**Negative / accepted**

* Two verdict vocabularies now coexist — four-valued `conformance.evaluated` vs two-valued `gate.evaluated` — until a later amendment aligns them. Accepted to keep this change additive; the divergence is documented, not silent.
* More schema surface to maintain and pin.

**Neutral**

* Cryptographic signing is out of scope and sequenced on tamp-findings' clock; the shared `Provenance` shape is designed once and serves that track when it lands.

## Notes

* Composes with tamp-findings ADR 0001 (four-valued verdicts; non-pure rules snapshot their verdict) and ADR 0004 (advisory/enforcing + locked floor). This ADR owns *what a conformance event on the wire looks like*; those own *what a rule is* and *how a verdict is enforced*.
* Follows ADR 0018's additive tag/vocabulary discipline and ADR 0019's single-stream principle.
* The `gate.evaluated` `pass|fail` vs four-valued gap (§2) is itself an example of the code/decision drift the conformance tooling is built to catch — a fitting first finding once the pipeline runs against core.
