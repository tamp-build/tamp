# ADR 0022: Versioning policy — milestone minors, compatibility-first patches

**Status:** Accepted (2026-09-27). Supersedes the informal "breaking → minor" habit and the stale
"pre-1.0 may break freely" note in the changelog (Tamp is at 1.x).

## Context

We need to **pin future feature sets to versions and have that hold** — to say "Components is the
next minor" and not have it invalidated a day later. Under strict SemVer we cannot: SemVer defines
the minor by *whether something broke*, so a breaking bug fix from adopter friction consumes the
minor we reserved. The version number is an *output* of the diff, and you cannot pin an output.

Two facts about this ecosystem change the trade-off:

1. **Every consumer pins Tamp packages exactly.** All ~60 satellites and the downstream apps use
   Central Package Management with exact versions (e.g. `<PackageVersion Include="Tamp.Core"
   Version="1.15.0" />`) — no floating `1.15.*` ranges anywhere. So **no release ever
   auto-propagates**; an upgrade is always a deliberate pin bump. The SemVer guarantee "a patch is
   always safe to take blindly" is one nobody here mechanically relies on.
2. **The audience is first-party.** Consumers are our own satellites and agent adopters that track
   the changelog and the interagent bus. The at-a-glance "did the number break?" signal is worth
   less here than a **dependable roadmap**.

Given that, we trade the SemVer "patch = never breaks" guarantee (unused here) for what we actually
need (pinnable feature milestones), and communicate breakage through the changelog instead of the
number.

## Decision

`MAJOR.MINOR.PATCH`, redefined:

- **MINOR = a planned feature milestone.** Minors are named on the [roadmap](../../ROADMAP.md) *in
  advance* and are **firm** — routine fixes never consume a reserved minor. A minor ships when its
  milestone is ready.
- **PATCH = everything shipped between milestones** — bug fixes, docs, and small additions, **plus
  the occasional breaking change** when adopter friction forces one. Every breaking change in a
  patch is called out under a **`### Breaking`** heading in that patch's changelog entry.
- **MAJOR = a deliberate, batched re-architecture** (the eventual 2.0). Routine breaking fixes do
  **not** trigger a major — that is the point of allowing them in patches. Major is reserved so it
  stays meaningful.

Supporting rules:

- **Compatibility-first is the default.** Prefer non-breaking mechanisms — a new opt-in flag,
  deprecate-then-remove, warn-then-error — so most fixes are genuinely compatible and breaking
  patches stay rare and deliberate. A break is a conscious call, not a shortcut.
- **The roadmap is the source of truth for what's coming**, in [`ROADMAP.md`](../../ROADMAP.md).
  ADRs and epics may name their target minor; because minors are now pinnable, that target is a real
  commitment.
- **Changelog carries the compatibility signal**, not the number. Any release with breaking changes
  says so prominently under `### Breaking`, with the migration.

## Consequences

- **We can pin feature sets.** "1.17 = Components + attestation contract, 1.18 = migration tooling"
  holds regardless of how many fix-patches land first. (1.16 was skipped: Components was complete but
  unreleased, so it bundled into 1.17 rather than forcing a redundant release — the pin *moves* with a
  deliberate roadmap decision, it does not drift.) This is the requirement that drove the ADR.
- **A patch may contain a breaking change.** This deviates from strict SemVer. It is safe here
  *only because consumers pin exactly* (no auto-propagation) and read the changelog. If we ever
  publish for consumers who float version ranges, revisit this ADR.
- **Majors become rare and meaningful.** 2.0 is an intentional redesign, not the accumulation of
  small breaks.
- **Discipline required.** "Compatibility-first" is a habit, not something the tooling enforces; a
  lazy breaking change that could have been an opt-in flag is a review smell.

## Alternatives considered

- **Strict SemVer (breaking → minor, or → major post-1.0).** Rejected: makes feature milestones
  un-pinnable, which is the problem we set out to solve. Post-1.0-strict (breaking → major) would
  also churn the major on routine fixes.
- **Two-track versioning** (a pinned product/marketing version + a separate SemVer package version).
  Rejected: two numbers to reason about for a one-number-per-package ecosystem; more machinery than
  the pinning problem warrants.
- **CalVer / release trains** (e.g. `2026.11`). Rejected: pins features to dates well, but throws
  away the "is this a breaking upgrade?" signal entirely, and we still want that signal in the
  changelog-linked number for the occasional consumer who reads it.
