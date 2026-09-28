using System.Text.Json.Serialization;

namespace Tamp;

/// <summary>
/// The canonical build event envelope (`#0a`, ADR 0019). One structured stream is
/// the single source of truth for build emission; every consumer renders a
/// declared projection over it (human console, <see cref="IBuildReporter"/>, the
/// agent NDJSON sink, and the ADR-0018 OpenTelemetry projection).
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity.</b> <see cref="SpanId"/> / <see cref="ParentSpanId"/> live in the
/// envelope so the NDJSON and OTLP projections agree on span identity (and an
/// agent can correlate to a beacon trace). <see cref="TraceId"/> is the build's
/// <see cref="BuildId"/> reused (a 32-hex GUID), giving one trace per build; the
/// TAM-218 cross-batch reconcile still keys on <c>(project, traceId)</c>.
/// </para>
/// <para>
/// <b>Secrets.</b> Events carry counts and metadata only — never raw argv, env, or
/// stdin — matching ADR-0018's "deliberately not emitted" rule.
/// </para>
/// </remarks>
public sealed record BuildEvent
{
    /// <summary>Schema version; see <see cref="BuildEventSchema.Version"/>.</summary>
    public string SchemaVersion { get; init; } = BuildEventSchema.Version;

    /// <summary>Emission timestamp (UTC).</summary>
    public DateTimeOffset Ts { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Event type discriminator; one of <see cref="BuildEventTypes"/>. Mirrors the payload's polymorphic kind for top-level, no-descent switching by agents.</summary>
    public required string Type { get; init; }

    /// <summary>Logical build id — one per build-graph run. Also serves as <see cref="TraceId"/>.</summary>
    public required string BuildId { get; init; }

    /// <summary>Process-invocation id — one per <c>Execute&lt;T&gt;</c>.</summary>
    public required string RunId { get; init; }

    /// <summary>Target name for target-scoped events; <see langword="null"/> for build-level events.</summary>
    public string? TargetId { get; init; }

    /// <summary>OTLP trace id (32-hex); the build's <see cref="BuildId"/>. One trace per build.</summary>
    public required string TraceId { get; init; }

    /// <summary>This event's span id (16-hex).</summary>
    public required string SpanId { get; init; }

    /// <summary>Parent span id (16-hex); build span for targets, target span for commands. <see langword="null"/> for the root build span.</summary>
    public string? ParentSpanId { get; init; }

    /// <summary>Who produced this — <c>agent:&lt;id&gt;</c> or <c>human:&lt;login&gt;</c>. Minimal resolver in `#0a`; full order in #19.</summary>
    public required string WorkerId { get; init; }

    /// <summary>Monotonic sequence number within the run. Meaningful for the ordered agent projection (OTLP is unordered).</summary>
    public required long Seq { get; init; }

    /// <summary>The typed, polymorphic payload for this event.</summary>
    public required BuildEventPayload Payload { get; init; }
}

/// <summary>
/// Polymorphic base for <see cref="BuildEvent.Payload"/>. Derived records are keyed
/// on the same discriminator strings as <see cref="BuildEvent.Type"/> (surfaced as
/// <c>$type</c> inside the payload object; agents can switch on the top-level
/// <see cref="BuildEvent.Type"/> without descending). New payload types are added
/// additively (see <see cref="BuildEventSchema"/>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(BuildStartedPayload), BuildEventTypes.BuildStarted)]
[JsonDerivedType(typeof(BuildFinishedPayload), BuildEventTypes.BuildFinished)]
[JsonDerivedType(typeof(TargetStartedPayload), BuildEventTypes.TargetStarted)]
[JsonDerivedType(typeof(TargetFinishedPayload), BuildEventTypes.TargetFinished)]
[JsonDerivedType(typeof(ToolInvokedPayload), BuildEventTypes.ToolInvoked)]
[JsonDerivedType(typeof(ToolExitedPayload), BuildEventTypes.ToolExited)]
[JsonDerivedType(typeof(SecretAccessRequestedPayload), BuildEventTypes.SecretAccessRequested)]
[JsonDerivedType(typeof(DiagnosticEmittedPayload), BuildEventTypes.DiagnosticEmitted)]
[JsonDerivedType(typeof(ArtifactProducedPayload), BuildEventTypes.ArtifactProduced)]
[JsonDerivedType(typeof(GateEvaluatedPayload), BuildEventTypes.GateEvaluated)]
[JsonDerivedType(typeof(ConformanceEvaluatedPayload), BuildEventTypes.ConformanceEvaluated)]
public abstract record BuildEventPayload;

/// <summary><see cref="BuildEventTypes.BuildStarted"/> — emitted once at the top of a run.</summary>
public sealed record BuildStartedPayload : BuildEventPayload
{
    public required IReadOnlyList<string> RequestedTargets { get; init; }
    public required IReadOnlyList<string> ExecutionClosure { get; init; }
    public string? Worktree { get; init; }
    public string? EnforcementMode { get; init; }
}

/// <summary><see cref="BuildEventTypes.BuildFinished"/> — emitted once at the end of a run.</summary>
public sealed record BuildFinishedPayload : BuildEventPayload
{
    public required string Status { get; init; }
    public required double DurationMs { get; init; }
    public required int ExitCode { get; init; }
    public string? FirstFailedTarget { get; init; }
    public int TargetsTotal { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public int NotRun { get; init; }
    public int CommandsTotal { get; init; }
}

/// <summary><see cref="BuildEventTypes.TargetStarted"/> — emitted before a target's <c>Executes</c> block runs.</summary>
public sealed record TargetStartedPayload : BuildEventPayload
{
    public required string Target { get; init; }

    /// <summary>Reserved for #12 (input-hash observability); <see langword="null"/> in `#0a`.</summary>
    public string? InputsHash { get; init; }
}

/// <summary><see cref="BuildEventTypes.TargetFinished"/> — terminal state for a target.</summary>
public sealed record TargetFinishedPayload : BuildEventPayload
{
    public required string Target { get; init; }

    /// <summary>One of <see cref="BuildEventStatus"/>: success | failure | skipped | not_run.</summary>
    public required string Status { get; init; }

    /// <summary>Wall-clock duration; <see langword="null"/> for skipped / not-run targets that never ran.</summary>
    public double? DurationMs { get; init; }

    /// <summary>Skip reason or failure cause (<c>"exit {n}"</c>, exception text, unmet <c>Requires</c>).</summary>
    public string? Reason { get; init; }

    /// <summary>Last N lines of merged stdout+stderr before a failure (from the target output ring buffer). Empty otherwise.</summary>
    public IReadOnlyList<string>? OutputTail { get; init; }

    // ── #12 typed-result fields (framework-synthesized; additive). ──

    /// <summary>Produced artifacts, synthesized on success from the target's <c>Produces</c> globs (path + hash + kind + size). Null when the target declares none.</summary>
    public IReadOnlyList<ArtifactInfo>? Outputs { get; init; }

    /// <summary>The target's input hash when an <c>InputHash</c> producer is declared. Observability-only (no execution effect) — the migration primitive toward cache-aware execution (#17). Null otherwise.</summary>
    public string? InputsHash { get; init; }

    /// <summary>On failure, the structured remedy an agent can act on (reproduce command + hint + coarse class). Null on success.</summary>
    public TargetRemedy? Remedy { get; init; }

    /// <summary>
    /// #17 cache advisory: <see langword="true"/> when the target's inputs matched the last
    /// successful run (a cache would have skipped it — but it still ran). Null when the cache
    /// advisory is off or the target declares no <c>InputHash</c>.
    /// </summary>
    public bool? WouldSkip { get; init; }

    /// <summary>
    /// #23 agent economics: worktree-relative path to this target's captured log
    /// (<c>.tamp/logs/&lt;buildId&gt;/&lt;target&gt;.log</c>, redacted) when <c>--capture-logs</c>
    /// is on and the target ran. Null when capture is off or the target never ran. Lets an
    /// agent fetch the one failing log lazily instead of holding the whole output stream.
    /// </summary>
    public string? LogPath { get; init; }
}

/// <summary>A produced artifact (a plain nested record; mirrors <see cref="ArtifactProducedPayload"/>'s shape for the <see cref="TargetFinishedPayload.Outputs"/> list).</summary>
public sealed record ArtifactInfo
{
    public required string Path { get; init; }
    public string? Hash { get; init; }
    public string? Kind { get; init; }
    public long? SizeBytes { get; init; }
}

/// <summary>
/// Structured, agent-actionable remedy for a failed target (#12). <see cref="Reproduce"/>
/// and <see cref="Hint"/> are reliably synthesized; <see cref="Class"/> is a coarse
/// heuristic (<c>config</c> / <c>code</c>) refinable by wrapper-contributed hints later.
/// </summary>
public sealed record TargetRemedy
{
    /// <summary>Coarse class: <c>transient</c> | <c>config</c> | <c>code</c> | <c>policy</c>. Heuristic in #12.</summary>
    public required string Class { get; init; }

    /// <summary>A command that re-runs the failure, e.g. <c>tamp Compile</c>.</summary>
    public required string Reproduce { get; init; }

    /// <summary>Human/agent-readable hint (the failure reason).</summary>
    public string? Hint { get; init; }
}

// ── Vocabulary expansion (#11). tool.* + secret.access.requested are emitted by
// the executor at CommandPlan dispatch; diagnostic.emitted / artifact.produced /
// gate.evaluated are defined + pinned here and emitted by their owning tickets
// (#13 / #12 / #20).

/// <summary><see cref="BuildEventTypes.ToolInvoked"/> — a child process is about to be spawned for a CommandPlan.</summary>
public sealed record ToolInvokedPayload : BuildEventPayload
{
    public required string Tool { get; init; }

    /// <summary>The command's arguments with registered secret values scrubbed via the redaction table. Non-secret args (paths, flags) are included — the agent stream is intentionally richer than the coarse ADR-0018 telemetry.</summary>
    public required IReadOnlyList<string> ArgvRedacted { get; init; }

    public string? Cwd { get; init; }
}

/// <summary><see cref="BuildEventTypes.ToolExited"/> — a spawned child process has exited.</summary>
public sealed record ToolExitedPayload : BuildEventPayload
{
    public required string Tool { get; init; }
    public required int ExitCode { get; init; }
    public required double DurationMs { get; init; }
}

/// <summary><see cref="BuildEventTypes.SecretAccessRequested"/> — a CommandPlan declared a secret at dispatch. Carries the name + capability, NEVER the value.</summary>
public sealed record SecretAccessRequestedPayload : BuildEventPayload
{
    public required string Name { get; init; }

    /// <summary>The capability the access implies. `#11` emits <c>"secret.reveal"</c>; enriched by the capability model in #20.</summary>
    public string? Capability { get; init; }
}

/// <summary>A source location for <see cref="DiagnosticEmittedPayload"/>.</summary>
public sealed record DiagnosticLocation
{
    public required string File { get; init; }
    public int? Line { get; init; }
}

/// <summary><see cref="BuildEventTypes.DiagnosticEmitted"/> — a SARIF-normalized diagnostic. Producer: #13. Defined + pinned here.</summary>
public sealed record DiagnosticEmittedPayload : BuildEventPayload
{
    public required string RuleId { get; init; }

    /// <summary>SARIF level: <c>none</c> | <c>note</c> | <c>warning</c> | <c>error</c>.</summary>
    public required string Level { get; init; }

    public DiagnosticLocation? Location { get; init; }
    public required string Message { get; init; }
    public string? FixHint { get; init; }
}

/// <summary><see cref="BuildEventTypes.ArtifactProduced"/> — a target produced an output. Producer: #12 (from Produces globs + hashes). Defined + pinned here.</summary>
public sealed record ArtifactProducedPayload : BuildEventPayload
{
    public required string Path { get; init; }
    public string? Hash { get; init; }
    public string? Kind { get; init; }
    public long? SizeBytes { get; init; }
}

/// <summary><see cref="BuildEventTypes.GateEvaluated"/> — a release/capability gate was evaluated. Producer: #20. Defined + pinned here.</summary>
public sealed record GateEvaluatedPayload : BuildEventPayload
{
    public required string Gate { get; init; }

    /// <summary><c>pass</c> | <c>fail</c>.</summary>
    public required string Verdict { get; init; }

    public bool Blocks { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Reusable provenance envelope for evidence events (ADR 0023). The <see cref="BuildEvent.WorkerId"/>
/// envelope names the actor and <see cref="BuildEvent.Ts"/> the time; this adds the <i>what</i>: the
/// revision evaluated, which rule-set produced the verdict, and — for non-deterministic (model-produced)
/// verdicts — the model and the adversarial verify result. <c>CommitSha</c> + <c>RulesSha</c> are the
/// pair that make the verdict reconstructible at a point in time (the "snapshot the verdict" posture a
/// non-pure rule requires; see tamp-findings ADR 0001).
/// </summary>
public sealed record Provenance
{
    /// <summary>The revision under evaluation (from <c>GitRepository</c>).</summary>
    public string? CommitSha { get; init; }

    /// <summary>Content hash of the rule-set that produced the verdict (e.g. <c>AbsolutePath.Sha256Of</c>) — pins which interpretation was in force.</summary>
    public string? RulesSha { get; init; }

    /// <summary><c>deterministic</c> | <c>semantic</c> | <c>verify</c>; mirrors the payload's method for standalone consumption.</summary>
    public string? Method { get; init; }

    /// <summary>For <c>semantic</c>/<c>verify</c>: the model that produced the verdict.</summary>
    public string? ModelId { get; init; }

    /// <summary>The adversarial verify pass's result, when one ran.</summary>
    public string? VerifyVerdict { get; init; }
}

/// <summary>
/// <see cref="BuildEventTypes.ConformanceEvaluated"/> — an ADR-conformance verdict against a repo's
/// code (ADR 0023). Producers live in a satellite; the payload is the wire contract that downstream
/// <c>tamp-findings</c> ingests as attestation-grade evidence. <see cref="Verdict"/> is four-valued
/// (see <see cref="ConformanceVerdict"/>): <c>unknown</c> (a semantic check could not decide, or a
/// deterministic probe never ran) blocks with a different remedy than <c>fail</c> and is never a
/// silent pass. On <c>fail</c>, <see cref="AdrQuote"/> + <see cref="CodeEvidence"/> are the required
/// structured reason (a claim that cannot quote both sides is <c>unknown</c>, not <c>fail</c>).
/// </summary>
public sealed record ConformanceEvaluatedPayload : BuildEventPayload
{
    /// <summary>The ADR this verdict is against (e.g. <c>"0018"</c>, or a repo-qualified ref for cross-repo).</summary>
    public required string AdrRef { get; init; }

    /// <summary>The rule within that ADR's rule-set (e.g. <c>"0018-r1"</c>).</summary>
    public required string RuleId { get; init; }

    /// <summary>Four-valued; one of <see cref="ConformanceVerdict"/>.</summary>
    public required string Verdict { get; init; }

    /// <summary>The ADR text the rule encodes. Required on <c>fail</c>.</summary>
    public string? AdrQuote { get; init; }

    /// <summary>The code line/snippet that conflicts. Required on <c>fail</c>.</summary>
    public string? CodeEvidence { get; init; }

    public DiagnosticLocation? Location { get; init; }

    /// <summary><c>deterministic</c> | <c>semantic</c> | <c>verify</c> — how the verdict was reached.</summary>
    public required string Method { get; init; }

    /// <summary>Whether this verdict blocks; enforcement mode is resolved downstream (tamp-findings ADR 0004).</summary>
    public bool Blocks { get; init; }

    public Provenance? Provenance { get; init; }

    /// <summary>Control identifiers this finding is evidence for (e.g. <c>CM-6</c>, <c>SA-15</c>). The catalogue is consumer-side.</summary>
    public IReadOnlyList<string>? ControlRefs { get; init; }
}
