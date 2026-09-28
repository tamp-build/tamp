# ADR 0020: Tamp.Components — reusable targets via interface mixins

**Status:** Accepted (2026-09-28) — **shipped in Tamp.Core 1.17.0** (Phases 1–3). Per the versioning policy in [ADR 0022](0022-versioning-policy.md), **1.16 was skipped**: Components was complete and unreleased, so it bundled into 1.17.0 alongside the attestation contract ([ADR 0023](0023-attestation-evidence-contract.md)) rather than forcing a redundant release (see [ROADMAP](../../ROADMAP.md)). Tracking epic: [#57](https://github.com/tamp-build/tamp/issues/57).

## Context

The tamp-build org runs ~60 satellites, and **every one hand-writes essentially the same build** — `Restore → Compile → Test → Pack → Ci`. That is ~60 copies of the same targets, which (a) drift (different flags, target names, failure handling), and (b) don't pick up improvements without editing all 60. When this cycle shipped `--blame-crash` ([#5](https://github.com/tamp-build/tamp/pull/49)), addressable per-target logs ([#23](https://github.com/tamp-build/tamp/pull/44)), and the `FailureMode.Continue` correctness fix ([#4](https://github.com/tamp-build/tamp/pull/48)), each satellite's build only benefits if someone hand-edits it. That is the problem `Tamp.Components` solves.

Two payoffs, both dogfood-real and independent of any external adopter:

1. **Reuse + fix-once propagation.** A shared component set collapses 60 bespoke builds to `class Build : TampBuild, ICompile, ITest, IPack`. Improving the standard build then propagates by a **package bump**, not 60 edits — the same compounding argument that already justifies the tool wrappers, applied to the build shape.
2. **Uniform, agent-facing target topology.** This cycle made the build a first-class agent surface ([ADR 0019](0019-agent-first-toolchain.md): `tamp mcp`, typed results, capability tiers). Components extend that to the *topology*: every tamp-built repo exposes the **same target names, shapes, and capability tiers**, so an agent driving any repo via `tamp mcp` finds a predictable `Compile`/`Test`/`Pack`/`Ci` instead of rediscovering each repo's bespoke targets. Uniform build topology across the ecosystem is a direct agent-first payoff.

> NUKE's `Nuke.Components` (`IRestore`/`ICompile`/… interface mixins) is the prior art and a migration bonus, but it is **not** the justification — the dogfood reuse and agent-topology cases above stand on their own.

Two facts about Tamp's internals set the constraints:

1. **Target discovery is class-only.** `TampBuild.CollectTargets` calls `build.GetType().GetProperties(...)`, which returns class + base-class members but **not** default-implemented members on **interfaces**. A `Target` on an interface is invisible today. Extending this is the one load-bearing change.
2. **Injection binds to instance members on the Build.** `[Parameter]`/`[Solution]`/`[Secret]`/`[FromPath]` populate members on the concrete build. Interfaces can't declare instance fields, so a component's target body has no direct route to `Solution`, a tool, or a parameter. "Scan interfaces too" is necessary but not sufficient — this is the crux.

The ethos holds: builds stay human-authored C# (no DSL, no manifest). Components are plain C# interfaces with default members, discovered like class targets — additive, opt-in, invisible to a build that implements none.

## Decision

Adopt interface-based components. Part 1 (discovery) lands in **`Tamp.Core` 1.16**; Parts 2–3 ship the standard set as the **`Tamp.Components`** package family. **Gated on committing to migrate the satellites onto it** — the value is maintenance leverage over many builds, so shipping it without adopting it would be unused core surface. That migration is part of the 1.16 plan.

### 1. Interface target discovery (core)

Extend `CollectTargets` to also walk `build.GetType().GetInterfaces()`, collect their `Target`-typed properties (default interface members), and invoke each via the interface `PropertyInfo` against the build (reflection dispatches to the DIM, or to the class's reimplementation when present).

- **Class-precedence override.** Targets are keyed by name; a `Target` on the build class (or a base class) **wins** over a same-named component target — that is the override mechanism.
- **Ambiguity is fail-closed.** Two *different* interfaces contributing the same target name with no class override is an error at collection time, naming both sources.
- **Method map.** Interface target lambdas join the method map that powers the `params Target[]` overloads, so a component can `.DependsOn(nameof(ICompile.Compile))` and resolve across components.

### 2. Injection into components — the `IHaz*` pattern (decided)

A component that needs an injected value declares it as an **abstract interface property**; the concrete build satisfies it from its injected member. Explicit, type-safe, analyzer-visible; the crux is solved by C#'s type system, not a service locator.

```csharp
public interface IHazSolution : ITampComponent { Solution Solution { get; } }

public interface ICompile : IHazSolution
{
    Target Compile => _ => _.Executes(() => DotNet.Build(s => s.SetProject(Solution.Path)));
}

class Build : TampBuild, ICompile
{
    [Solution] readonly Solution _solution = null!;
    public Solution Solution => _solution;      // satisfy the component's requirement
    public static int Main(string[] a) => Execute<Build>(a);
}
```

*Rejected:* an ambient `this.Require<T>()` service locator — less boilerplate but implicit and analyzer-hostile; it hides the component↔build contract that `IHaz*` makes explicit. A thin `this.From<TComponent>()` readability helper is allowed, but the `IHaz*` property is the contract.

### 3. Component parameterization — same pattern (decided)

Component knobs (e.g., an `ITest` coverage toggle) are expressed with the **same mechanism**: an `IHaz*` abstract property the build provides, or a `virtual` default member the build overrides. No second configuration system.

### 4. Override ergonomics — implicit override + analyzer (decided)

Declaring a same-named target on the build class is the override. A new/extended analyzer (in the TAMP005/006 shadowing family) **distinguishes an intended override from an accidental shadow**, so a name collision with a component is never silent.

### 5. Packaging — split (decided)

- **`Tamp.Components`** — tool-agnostic contracts: `IHazSolution` / `IHazConfiguration` / `IHazArtifacts` and the target-shape interfaces `IRestore` / `ICompile` / `ITest` / `IPack`. Depends only on `Tamp.Core`; defines target names + dependency wiring, leaving the tool call abstract where it must be.
- **`Tamp.Components.NetCli.V{N}`** — the concrete dotnet `.Executes(() => DotNet.…)` bodies, SDK-version-coupled like the wrappers (depends on `Tamp.Components` + `Tamp.NetCli.V{N}`). Keeps the SDK coupling out of the reusable contracts and follows `Tamp.{Family}.V{N}` ([ADR 0002](0002-package-naming-convention.md)); leaves room for non-dotnet component sets later.

A build composes them — `class Build : TampBuild, ICompile, ITest, IPack` wires `Compile → Test → Pack`, overriding any by declaring it on the class.

## Consequences

- **No breaking change.** Discovery is additive; a build implementing no component interfaces behaves exactly as today. Ships as a `Tamp.Core` **minor** (1.16).
- **New surface to maintain** — the discovery path, the analyzer, and the `Tamp.Components(.NetCli.V{N})` packages — which cuts against "core stays small." Justified only by adopting it across the satellites (the gate above); the standard component targets bundle current best practice (`--capture-logs`, `--blame-crash`, correct `FailureMode` handling) so satellites inherit it by a bump.
- **Migration bonus:** NUKE builds get a near-1:1 target for `Nuke.Components`; the migration guide gains a components section.

## Phasing for 1.16

1. **Core discovery + override + method-map + analyzer** — the load-bearing, testable core.
2. **`Tamp.Components`** — tool-agnostic contracts + `IHaz*`.
3. **`Tamp.Components.NetCli.V10`** — first concrete set (Restore/Compile/Test/Pack), dogfooded by converting a satellite's `Build.cs`.
4. **Satellite migration wave** — convert the satellites onto the component set (the commitment that justifies the feature).

## Deferred implementation details

- Exact `IHaz*` surface for the first component set (which knobs `ITest`/`IPack` expose) — settled during Phase 2 against a real satellite.
- Whether `Tamp.Core` gains a small `ITampComponent` marker interface or discovery keys purely off `Target`-typed interface members.
- Analyzer diagnostic id + message for the override-vs-shadow distinction (extends TAMP006).
