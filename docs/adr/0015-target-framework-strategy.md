# ADR 0015: Target framework strategy

* Status: Accepted — **amended 2026-11-20** (see [Amendment](#amendment--2026-11-20-net-11-added-net-8--net-9-dropped))
* Date: 2026-05-09
* Deciders: scott
* Tracking: TAM-21 (ADR), TAM-32 (work item)
* Amended by: tamp#166 / Tamp 1.21.0 — the first calendar turn executed under this rule

> **Note (2026-11-20):** the rule below has now fired for the first time. .NET 11
> shipped on 2026-11-10 and .NET 8 and .NET 9 reached EOL the same day, so the TFM
> list is **`net10.0;net11.0`** as of Tamp **1.21.0**, and `Tamp.NetCli.V11` /
> `Tamp.Components.NetCli.V11` exist. The body below is the 2026-05-09 record and
> still states the old list; see the [Amendment](#amendment--2026-11-20-net-11-added-net-8--net-9-dropped) for what shipped and for
> the one consequence the original write-up got wrong.

## Context and Problem Statement

Tamp ships first-party assemblies (`Tamp.Core`, `Tamp.Cli`, `Tamp.NetCli.V{N}`, future modules) as NuGet packages consumed from build scripts that are themselves .NET console projects. Two questions, one ADR:

1. **Which .NET runtimes do Tamp's own assemblies have to run on?** — i.e., what TFMs do `Tamp.Core` and friends list in `<TargetFrameworks>`?
2. **What's the lifecycle policy** — when do new TFMs get added, when do old ones get dropped?

The answers are surprisingly load-bearing. Tamp's value proposition includes consumers in regulated and locked-down environments — federal contracts where the dev fleet is fixed on .NET 8, client VDIs that haven't approved newer SDKs, partner orgs that intentionally adopted .NET 9 STS to surface early features and plan to roll forward to the next LTS before ship. If Tamp targets only the latest, those consumers can't use it. The architecture is the resilience strategy, but it doesn't help if half the audience can't load the assembly.

The original deferred ADR for this decision said *"`net10.0` only for v0; multi-targeting rules"*. Both halves of that proved wrong on first contact: V8/V9 retroactive support is a v0 must-have, not a future enhancement, and "rules" need to be one rule, not several.

## Decision Drivers

* **Locked-down consumer environments.** Federal, regulated, and corporate-IT-managed dev shops frequently cannot install newer SDKs. The user's `Build.csproj` may be pinned to `net8.0` because that's what the host machine actually has. Every package they reference must support `net8.0` for Tamp to be usable at all.
* **STS adopters are not a fringe case.** Long-term projects routinely start on STS to surface early features, planning to land on the next LTS before shipping to production. Dismissing STS support breaks that workflow and excludes a real consumer cohort.
* **Microsoft owns the calendar.** Whether a TFM is in or out of support is decided by Microsoft, not by us. Inventing our own definition of "supported" is unnecessary and would drift from the ecosystem's actual reality.
* **TFM ≠ module version.** Which .NET runtime a Tamp assembly *runs on* is a separate question from which version of the wrapped tool a *module* targets. `Tamp.NetCli.V8` wraps the `dotnet 8` CLI; the wrapper assembly itself can run on net10. Conflating these is a category error that leads to bad rules.
* **One rule, no judgement calls.** Maintainer overhead must stay weekend-scale (ADR 0009). Whether a TFM is in or out should be answered by checking a Microsoft web page, not by deliberating.

## Considered Options

1. **Single TFM (`net10.0` only).** Smallest support burden; excludes the entire net8 / net9 consumer base.
2. **LTS-only (`net8.0;net10.0`).** Skips STS releases by policy. Simplifies CI matrix slightly. Excludes consumers deliberately targeting STS for early-feature access.
3. **All Microsoft-supported releases (`net8.0;net9.0;net10.0` today, adjusts automatically with the calendar).** ← chosen.
4. **All Microsoft-supported plus N years past EOL.** Extended-support model; we explicitly rejected this — supporting EOL'd runtimes is the caller's problem (e.g., Microsoft's commercial paid-support tier) and not something a small-core project takes on.

## Decision Outcome

**Chosen: Option 3 — track Microsoft's official .NET release calendar exactly.**

The policy is one rule:

> **Tamp's first-party assemblies multi-target every .NET release that Microsoft considers "in support" — both LTS and STS. When Microsoft's calendar moves, we move with it: we add a TFM the day a new release ships and drop a TFM the day Microsoft ends support for it.**

That's it. No judgement calls. No "we decided to skip net N." No version we added because we like it and now have to drag along after EOL.

### Specifics as of this ADR (2026-05-09)

Per [Microsoft's .NET support policy page](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core):

| Version  | Type | GA            | End of Support  |
|----------|------|---------------|------------------|
| .NET 8   | LTS  | 2023-11-14    | 2026-11-10       |
| .NET 9   | STS  | 2024-11-12    | 2026-11-10       |
| .NET 10  | LTS  | 2025-11-11    | 2028-11-14       |

(Note: .NET 8 and .NET 9 happen to EOL on the same day. STS support is 24 months, LTS is 36; the .NET 9 STS clock and the .NET 8 LTS clock both expire 2026-11-10.)

Therefore today, every first-party Tamp assembly multi-targets:

```xml
<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
```

When .NET 11 STS ships in November 2026 (per Microsoft's cadence), `net11.0` is added on the same day. When .NET 8 and .NET 9 reach EOL on 2026-11-10, both are dropped from `<TargetFrameworks>` in the next Tamp minor release.

### TFM lifecycle vs. module lifecycle

These are independent. Specifically:

* **TFM lifecycle** — *which runtimes Tamp's own DLLs load on* — follows the rule above strictly.
* **Module lifecycle** — *which version of a wrapped tool's CLI a module targets* — is a per-module decision driven by whether anyone actually still uses that tool version. A `Tamp.NetCli.V8` package can continue to exist (and be useful) for as long as any consumer still has the `dotnet 8` CLI installed, even if `net8.0` has been dropped from the wrapper assembly's TFM list.

Concretely: when .NET 8 reaches EOL, `Tamp.NetCli.V8` does *not* automatically get retired. The package's `<TargetFrameworks>` drops `net8.0` per the rule above (so the wrapper itself runs on supported runtimes), but the package continues to wrap `dotnet 8` invocations because plenty of locked-down environments will still have the `dotnet 8` SDK installed for years past Microsoft's support window. Module retirement is a separate decision, made per module, based on consumer demand — and is properly the subject of a future ADR if and when retirement becomes a meaningful question.

### What "in support" means precisely

The rule says "Microsoft considers in support." We define that, for purposes of this ADR, as: a release is in support if it appears as supported (either Active or Maintenance phase) on Microsoft's `dotnet/core` support page on the day we cut a Tamp release. That's an unambiguous, externally-verifiable test. We don't track Microsoft's preliminary announcements, blog posts, or community speculation — only the support page state at release-cut time.

## Consequences

### Positive

* **Consumer reach is maximised within reason.** Federal, regulated, locked-down VDI, and STS-adopter consumers can all install Tamp and use it. No artificial gates.
* **The rule is mechanical.** Whether a TFM is in or out is a one-page check, not a deliberation. Maintainer overhead is bounded.
* **No drift from the ecosystem.** Tamp's notion of "supported" is identical to Microsoft's. We don't have to defend a Tamp-specific definition.
* **Safe under Microsoft cadence shifts.** If Microsoft changes STS to 18 months, or extends LTS, or invents a new tier, the rule keeps working — we read the calendar, we adjust.

### Negative

* **CI matrix grows linearly with supported releases.** Today: 3 TFMs × N projects. When net11 ships in late 2026, momentarily 4 (until 8/9 drop the same week). Acceptable; matrix builds are cheap and selective-build optimisations are a known technique.
* **Multi-targeting requires occasional `#if NET10_0_OR_GREATER` guards** when Tamp wants to use a newer-runtime API that doesn't exist on net8.0. We accept this. Where the API surface diverges meaningfully, the guard makes the divergence explicit, which is better than pretending it doesn't exist.
* **Brief "ghost" windows around Microsoft's EOL dates.** A consumer running an EOL'd runtime can't pull the latest Tamp build that drops their TFM. They have to either upgrade their runtime or pin to the last Tamp release that still included it. We document this in the README and call it out in release notes; we do not extend support past Microsoft's window to smooth the transition.

### Neutral / future-facing

* **STS support adds CI surface but no real maintenance burden** in the common case, because the .NET CLI surface across STS and LTS is largely additive. Where it isn't, the wrapper code branches — a normal day's work, not a structural problem.
* **`Tamp.NetCli.V11` (and onward) appears the day .NET 11 ships.** Per ADR 0002 it gets its own package; per this ADR its TFM list mirrors Core's at that moment.
* **Retirement of a `Tamp.NetCli.V{N}` module is a separate ADR** when it ever happens. This ADR explicitly does not commit to or against retiring modules; it only commits to TFM lifecycle.

## Pros and Cons of the Options

### Option 1 — `net10.0` only

* Pro: smallest CI matrix; smallest support footprint for the maintainer team.
* Con: excludes net8 LTS (still in support, large enterprise base) and net9 STS (intentional STS adopters).
* Con: contradicts Tamp's "build framework that meets you where you are" positioning.

### Option 2 — LTS-only (`net8.0;net10.0`)

* Pro: still excludes the smallest population (STS adopters).
* Pro: avoids the "STS is a shit show" rep some shops have.
* Con: relies on a Tamp-specific rule ("we don't do STS") that drifts from Microsoft's calendar.
* Con: penalises the legitimate workflow of starting a long project on STS to surface early features.
* Con: contributors and consumers have to remember our rule on top of Microsoft's.

### Option 3 — Microsoft's calendar (chosen)

* Pro: zero judgement calls; check the page.
* Pro: maximum consumer reach within Microsoft's stated support boundary.
* Pro: rule is stable under Microsoft policy changes.
* Con: CI matrix tracks Microsoft's release cadence — not really a downside, just reality.

### Option 4 — extended support past EOL

* Con: every month past EOL is uncompensated maintenance work for the maintainer team.
* Con: the consumer running an EOL runtime is, by Microsoft's definition, accepting unsupported risk; we should not be the safety net.
* Con: nobody asked for this.

## Amendment — 2026-11-20 (tamp#166): .NET 11 added, .NET 8 + .NET 9 dropped

The first execution of this ADR's rule, and the first chance to check it against
reality. Recorded here because one consequence in the original write-up turned out
to be wrong in a way that matters.

### What shipped (Tamp 1.21.0)

* Every first-party assembly now multi-targets **`net10.0;net11.0`**. The
  three-TFM line in the body above is historical.
* `global.json` pins SDK **11.0.100**. This is load-bearing, not cosmetic: SDK 10
  cannot target `net11.0`, and `rollForward: latestFeature` will not cross a major
  version, so an unpinned or 10-pinned environment fails the build outright rather
  than quietly producing a net10-only package.
* **`Tamp.NetCli.V11`** and **`Tamp.Components.NetCli.V11`** were added, per the
  "appears the day .NET 11 ships" consequence. The second one is not mandated by
  this ADR but follows from it: a consumer whose build script targets `net11.0`
  and composes via `Tamp.Components` needs a body that drives the `dotnet 11`
  CLI, and `Tamp.Components.NetCli.V10` drives SDK 10, which cannot build
  `net11.0` projects.
* **`Tamp.NetCli.V8` and `Tamp.NetCli.V9` were retargeted, not retired** — exactly
  as the TFM-lifecycle-vs-module-lifecycle section requires. Both packages still
  wrap the `dotnet 8` / `dotnet 9` CLI; their own assemblies simply load on
  `net10.0` / `net11.0` now. Nothing about EOL changed which CLI they drive.
* Tamp's own `build/Build.csproj` stays single-target `net10.0` (the LTS runtime)
  but now references `Tamp.NetCli.V11`, because the SDK in `global.json` is the
  one it actually drives.

### How much of this was mechanical

Worth recording, because it validates the "zero judgement calls" claim in Option 3.
Across the whole ecosystem at the moment of the turn: **167 projects** carrying
exactly one TFM string with no deviants, **69 identical `global.json` pins**, and
**zero** `#if NET8_0`-style conditionals or TFM-conditioned `PackageReference`
entries. The `#if NET10_0_OR_GREATER` guard the Consequences section budgeted for
was never actually needed. Central package management (ADR 0006) is why.

### The consequence this ADR got wrong

The Consequences section describes a **"ghost window"** — a consumer stranded on an
EOL runtime pins an older Tamp release and carries on — and treats that as an
acceptable, essentially safe outcome. It is not safe, and the migration proved it
by accident.

.NET 11's `System.Text.Json` source generator changed one behaviour: a non-nullable
property with an initializer is only honoured if the serializer leaves absent JSON
members alone. net11 leaves them alone. **Every earlier runtime overwrites the
member with `default(T)`.** `Tamp.Sarif`'s schema records relied on those
initializers, and `default(SarifLevel)` is `None` — the *least* severe value, where
the declared default was `Warning`. So on net8 / net9 / net10, a SARIF result whose
scanner legally omitted `level` (SARIF 2.1.0 §3.27.10 defers to the rule's
`defaultConfiguration`, which itself defaults to `warning`) was read as `None` and
**written back into the evidence file as `"level": "none"`** — unknown severity
recorded as benign, below any "warning and above" gate threshold. Eleven other
non-nullable properties could come back null the same way. See tamp#166.

The fix is in 1.21.0, which net8/net9 consumers cannot install. The lesson for this
ADR is narrow and worth stating: **a ghost-window pin freezes the consumer's bugs
along with their runtime.** Runtime-dependent behaviour means the last release a
stranded cohort can install is also the release whose defects they keep forever.
Dropping an EOL TFM is still right; assuming the stranded consumer is therefore
*fine* is not. When a TFM drop coincides with a correctness fix that the dropped
cohort cannot receive, the drop release should say so explicitly in its CHANGELOG,
and a patch on the last supporting line should at least be considered on the merits
rather than ruled out by Option 4 — Option 4 rejects *extended support*, which is
an ongoing commitment, not a single correctness patch.

## Links

* Tracker: TAM-21 (ADR), TAM-32 (work item).
* Microsoft .NET support policy: `https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core` (canonical source for the calendar).
* First calendar turn under this rule: [tamp#166](https://github.com/tamp-build/tamp/issues/166) (Tamp 1.21.0) — see the Amendment above.
* Package naming: ADR 0002 — separately governs `Tamp.NetCli.V{N}` package boundaries.
* Repo layout: ADR 0006 — central package management already in place to make TFM additions a one-file edit.
