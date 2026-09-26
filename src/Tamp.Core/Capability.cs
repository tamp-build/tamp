namespace Tamp;

/// <summary>
/// The capability tier a piece of work requires (#20, ADR 0019 Pillar 7).
/// Ordered so the effective tier of a target is the <em>max</em> over its work.
/// </summary>
public enum CapabilityTier
{
    /// <summary>Produce / read; worktree-scoped; no external mutation. Never gated.</summary>
    Safe = 0,

    /// <summary>A worktree-scoped resource (e.g. a spun-up container) — allowed, but expected to be isolated (per-worker names/ports). Not gated.</summary>
    Grey = 1,

    /// <summary>External mutation (publish / deploy / registry) or a secret reveal. Gated in agent mode.</summary>
    SideEffectful = 2,
}

/// <summary>
/// How the executor enforces <see cref="CapabilityTier"/> (#20). <see cref="Off"/> is the
/// default — humans are unaffected. Agents opt into <see cref="Agent"/> (default-deny on
/// side effects unless elevated).
/// </summary>
public enum CapabilityMode
{
    /// <summary>No enforcement (default). All tiers run.</summary>
    Off = 0,

    /// <summary>Default-deny: <see cref="CapabilityTier.SideEffectful"/> work is blocked unless explicitly elevated.</summary>
    Agent = 1,
}
