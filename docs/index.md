---
title: Tamp
description: Pack the build down tight.
---

# Tamp

> Pack the build down tight.

A small-core, plugin-driven build automation framework for **.NET 8, 9, and 10**. Cross-platform. Honest about resources. Forkable.

Tamp is the architectural rethink of the .NET build-tool ecosystem's monolith problem: every tool wrapper ships as an independently-versioned NuGet package, the host environment is a first-class concept, and the architecture is the resilience strategy.

## Try it

```bash
dotnet tool install -g Tamp.Cli         # then: tamp ci
# or
dotnet tool install -g dotnet-tamp      # then: dotnet tamp ci
```

→ **[5-minute Getting Started ↗](https://github.com/tamp-build/tamp/wiki/Getting-Started)**

## Documentation

The reference and guides live in the [Wiki ↗](https://github.com/tamp-build/tamp/wiki). Quick links:

- [Build Script Authoring](https://github.com/tamp-build/tamp/wiki/Build-Script-Authoring) — the fluent target DSL
- [Parameter & Secret Injection](https://github.com/tamp-build/tamp/wiki/Parameter-And-Secret-Injection)
- [Module Catalog](https://github.com/tamp-build/tamp/wiki/Module-Catalog) — what we ship today
- [Failure Handling](https://github.com/tamp-build/tamp/wiki/Failure-Handling)
- [CI Host Integrations](https://github.com/tamp-build/tamp/wiki/CI-Host-Integrations) — GitHub Actions, Azure DevOps, TeamCity
- [Migrating from NUKE](https://github.com/tamp-build/tamp/wiki/Migrating-From-NUKE) · [Migrating from Cake](https://github.com/tamp-build/tamp/wiki/Migrating-From-Cake)

## Security & compliance chain

SBOM → SAST → SCA → secrets/misconfig → Dependency-Track / DefectDojo, in one import via `Tamp.Security.Pipeline`.

- [Security chain](security-chain) — narrative, adopter recipe, locked decisions
- [Security env vars](security-env-vars) — the `TAMP_<TOOL>_<FIELD>` contract

## Architecture decisions

Every load-bearing design choice is recorded as an ADR. Read these before proposing a change to a corresponding area:

- [ADR 0001 — Small core, plugin-driven architecture](adr/0001-small-core-plugin-architecture)
- [ADR 0002 — Package naming convention](adr/0002-package-naming-convention)
- [ADR 0003 — Build scripts are .NET console projects](adr/0003-build-scripts-net-console-projects)
- [ADR 0004 — CommandPlan as universal wrapper output](adr/0004-commandplan-universal-output)
- [ADR 0005 — Secret as a distinct type, not a `[Sensitive]` attribute](adr/0005-secret-distinct-type)
- [ADR 0006 — Repository layout: monorepo for core and first-party modules](adr/0006-repo-layout-monorepo)
- [ADR 0007 — License (MIT)](adr/0007-license-mit)
- [ADR 0008 — SemVer policy for `Tamp.Core` and modules](adr/0008-semver-policy)
- [ADR 0009 — Governance and namespace policy](adr/0009-governance-and-namespace-policy)
- [ADR 0010 — HostProfile is built once at startup and frozen](adr/0010-hostprofile-frozen)
- [ADR 0011 — Target authoring via fluent property DSL (NUKE-shaped)](adr/0011-target-fluent-dsl)
- [ADR 0012 — Declarative resource consumption (Shared / Exclusive)](adr/0012-declarative-resources)
- [ADR 0013 — Schema-driven wrapper generation as the canonical workflow](adr/0013-schema-driven-wrappers)
- [ADR 0014 — Shell strategy: `pwsh` when shell is required, never assume `bash`/`cmd`](adr/0014-shell-strategy-pwsh)
- [ADR 0015 — Target framework strategy](adr/0015-target-framework-strategy)
- [ADR 0016 — Decision-maker silence within scope timeframe forfeits the right to weigh in](adr/0016-decision-silence-forfeits)
- [ADR 0017 — Pull-request staleness: auto-close after author silence on review feedback](adr/0017-pr-staleness-autoclose)
- [ADR 0018 — Diagnostics emission contract](adr/0018-diagnostics-emission-contract)

→ [Full ADR index](adr/)

**Proposing a change?** Anyone may open an ADR via PR with `Status: Proposed` — see [ADR 0009 §3.1](adr/0009-governance-and-namespace-policy). No CLA.

## Releases

`Tamp.Core 1.13.0` is live, the `Tamp.*` NuGet prefix is reserved to the project, and 70+ first-party packages are shipping. See the [changelog ↗](https://github.com/tamp-build/tamp/blob/main/CHANGELOG.md) and the [main README ↗](https://github.com/tamp-build/tamp#readme) for the current package matrix.

Tamp is actively maintained and in daily production use across several private client projects. Current focus: **reporting and attestation**.

## Project meta

- [Repository ↗](https://github.com/tamp-build/tamp)
- [NuGet packages ↗](https://www.nuget.org/profiles/tamp)
- [Maintainers ↗](https://github.com/tamp-build/tamp/blob/main/MAINTAINERS.md) · [Contributing ↗](https://github.com/tamp-build/tamp/blob/main/CONTRIBUTING.md) · [Code of Conduct ↗](https://github.com/tamp-build/tamp/blob/main/CODE_OF_CONDUCT.md) · [Security ↗](https://github.com/tamp-build/tamp/blob/main/SECURITY.md)
- License: [MIT](https://github.com/tamp-build/tamp/blob/main/LICENSE)
