# ADR 0025: `Tamp.Security.Pipeline` — a curated one-import supply-chain security bundle

* Status: Accepted
* Date: 2026-09-29
* Deciders: scott
* Tracking: [#87](https://github.com/tamp-build/tamp/issues/87)

> Retroactive record. `Tamp.Security.Pipeline` shipped as a satellite but its composition decision was never captured as an ADR; the `tamp-conformance` reverse-examination dogfood flagged the bundled scanner set as an undocumented architectural decision. This ADR records the choice and its rationale.

## Context and Problem Statement

Supply-chain security for a .NET build is a *chain* of tools: generate an SBOM, scan it for known CVEs, run a SAST pass, run a filesystem/config scan, then push the results somewhere they can be tracked and gated. Each step has a Tamp wrapper (`Tamp.CycloneDx.V6`, `Tamp.OsvScanner.V2`, `Tamp.OpenGrep`, `Tamp.Trivy`, `Tamp.DependencyTrack.V1`, `Tamp.DefectDojo.V2`), but wiring them together — target order, shared file paths, SARIF/SBOM plumbing, conditional push — is real work every adopter would otherwise re-do (and re-do inconsistently).

We needed a way for a project to get a credible, opinionated supply-chain pipeline by adding *one* base class, while keeping every step overridable.

## Decision Drivers

* **Opinionated defaults over a parts bin.** The value is a *curated, wired* chain, not another list of wrappers an adopter must assemble. A default tool set that reflects current best practice is the product.
* **Uniformity across the fleet.** The ~60 satellites (and adopters) should run the *same* security shape, so results are comparable and improvements land by a package bump — the same argument as [ADR 0020](0020-tamp-components.md) (Tamp.Components) makes for the build shape.
* **Producer/push split.** Producing evidence (SBOM/scan) must always run; *pushing* it to an external tracker must be opt-in (an adopter without a Dependency-Track/DefectDojo instance still gets a green build).
* **Overridable everything.** A curated default is only acceptable if each step is `virtual` — adopters must be able to swap a tool, change scan scope, or re-order without abandoning the bundle.

## Considered Options

1. **Document the chain, let each adopter wire it.** Zero new code, but guarantees drift and re-work; the wiring *is* the hard part.
2. **A rigid all-in-one target with no overrides.** Easy to ship, impossible to adapt — an adopter with a different SAST tool or a non-.NET SBOM is stuck.
3. **A `SecurityPipelineBuild : TampBuild` base class with a curated default tool set and virtual steps (chosen).** One import, two required overrides, everything else swappable.

## Decision

`Tamp.Security.Pipeline` ships `SecurityPipelineBuild : TampBuild`. An adopter inherits it, sets `SecurityProductName` + `SecuritySolutionPath`, and runs `tamp Security`.

**Curated default tool set (v0, .NET-focused):**
* **SBOM** — `Tamp.CycloneDx.V6` (`dotnet-CycloneDX`), CycloneDX spec **1.6** (osv-scanner 2.x does not yet accept 1.7). Non-.NET adopters override `Sbom` to use `Tamp.Syft.V1`.
* **CVE scan** — `Tamp.OsvScanner.V2` against the generated SBOM.
* **SAST** — `Tamp.OpenGrep`.
* **Filesystem/config scan** — `Tamp.Trivy`.

**Producer half runs unconditionally** (`Sbom`, `SecurityScan`, `SecurityScanCveSbom`, `SecurityScanTrivy`). **Push half is env-var-gated:** supply `TAMP_DT_URL`/`API_KEY`/`PROJECT_UUID` for Dependency-Track and/or `TAMP_DD_URL`/`TOKEN`/`ENGAGEMENT_ID` for DefectDojo; absent those, `SecurityPush` logs clean skips and the build stays green.

Every target is `virtual` (scan directories, SBOM spec version, test-project exclusion, dependencies, tool choice) so an adopter can retune any single step without leaving the bundle.

## Consequences

* **Positive**: a project gets a credible supply-chain pipeline — SBOM + CVE + SAST + config scan, optionally pushed — from one base class and two overrides. The chain is wired once, correctly, and improves for everyone by a package bump.
* **Positive**: fleet uniformity — comparable security posture across satellites, and a natural producer for the `tamp-ingest-v1` / tamp.findings evidence path.
* **Positive**: green-by-default for adopters without a tracker; producing evidence never depends on having a push sink.
* **Negative**: the curated tool set is an opinion with a maintenance surface — each bundled wrapper is a dependency, and the default set will need revisiting as tools change (the `V6`/`V2` version pins are deliberate but will age). The `virtual` overrides are the pressure-relief valve.
* **Negative**: v0 is .NET-first (CycloneDX via dotnet-CycloneDX); non-.NET adopters must override `Sbom`. Accepted for v0.

## Notes

The bundle is a *composition* over existing single-tool wrappers, not a new scanner — each step remains an independently versioned satellite. It pairs naturally with `Tamp.Ingest.V1` (push the produced SBOM/SARIF to a `tamp-ingest-v1` sink) and with [ADR 0020](0020-tamp-components.md)'s uniform-topology goal. The metrics surface (`SecurityPipelineMetrics`) is out of scope for this ADR.
