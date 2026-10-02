# Architecture Decision Records

Decisions about Tamp's architecture, conventions, and governance live here. Each file is a single decision in [MADR](https://adr.github.io/madr/) format.

## Lifecycle

| Status       | Meaning                                                                                  |
|--------------|------------------------------------------------------------------------------------------|
| `Proposed`   | Drafted, open for discussion. Subject to change before any code depends on it.           |
| `Accepted`   | The decision is current. New code should follow it.                                      |
| `Superseded` | A later ADR overrides this one. Cross-link both ways.                                    |
| `Deprecated` | No longer applies and has no successor (rare — usually superseded instead).              |

ADRs are append-only. Don't edit an Accepted ADR's substance after the fact — write a new one that supersedes it. Typo fixes and link repairs are fine.

## Index

| #    | Title                                                                                  | Status   |
|------|----------------------------------------------------------------------------------------|----------|
| 0001 | [Small core, plugin-driven architecture](0001-small-core-plugin-architecture.md)       | Accepted |
| 0002 | [Package naming convention](0002-package-naming-convention.md)                         | Accepted |
| 0006 | [Repository layout: monorepo for core and first-party modules](0006-repo-layout-monorepo.md) | Accepted (partially superseded by 0026) |
| 0007 | [License — MIT](0007-license-mit.md)                                                   | Accepted |
| 0009 | [Governance and namespace policy](0009-governance-and-namespace-policy.md)             | Accepted |
| 0015 | [Target framework strategy](0015-target-framework-strategy.md)                         | Accepted |
| 0026 | [Satellite repositories for first-party tool wrappers](0026-satellite-repositories-for-wrappers.md) | Accepted |

ADR numbers are stable and gap-allowed — they correspond 1:1 with the YouTrack tracking issues (`TAM-N`), so a deferred ADR keeps its slot until written.

## Authoring a new ADR

1. Pick the next number from the YouTrack ADR list.
2. Copy an existing file as a starting shape; keep MADR section headings.
3. Open as `Proposed` until accepted; flip to `Accepted` and merge.
4. Update this index.
