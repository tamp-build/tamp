namespace Tamp;

/// <summary>
/// Canonical event vocabulary (`#0a`, ADR 0019). The single source of truth for
/// the <see cref="BuildEvent"/> stream that every consumer projects from — the
/// human console, the <see cref="IBuildReporter"/> surface (via
/// <c>ReporterProjectionSink</c>), the agent NDJSON sink (#9), and the ADR-0018
/// OpenTelemetry emission (folded into a projection in #0c).
/// </summary>
/// <remarks>
/// <para>
/// <b>Stability contract.</b> This vocabulary is governed like the ADR-0018 tag
/// keys: <b>additive-only</b>. Adding a new event <see cref="Types"/> value or a
/// new payload field is non-breaking; renaming or removing one is breaking and
/// requires an ADR amendment. <c>CanonicalEventSchemaTests</c> pins the set so an
/// accidental rename fails CI.
/// </para>
/// </remarks>
public static class BuildEventSchema
{
    /// <summary>Schema version stamped on every <see cref="BuildEvent.SchemaVersion"/>.</summary>
    public const string Version = "1.0";

    /// <summary>
    /// The pinned set of event types. Lifecycle (`#0a`) + the vocabulary expansion (#11):
    /// tool / secret / diagnostic / artifact / gate. Additive-only — new entries are fine,
    /// renames/removals require an ADR amendment (pinned by <c>CanonicalEventModelTests</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> Types = new[]
    {
        BuildEventTypes.BuildStarted,
        BuildEventTypes.BuildFinished,
        BuildEventTypes.TargetStarted,
        BuildEventTypes.TargetFinished,
        BuildEventTypes.ToolInvoked,
        BuildEventTypes.ToolExited,
        BuildEventTypes.SecretAccessRequested,
        BuildEventTypes.DiagnosticEmitted,
        BuildEventTypes.ArtifactProduced,
        BuildEventTypes.GateEvaluated,
    };
}

/// <summary>Canonical event <see cref="BuildEvent.Type"/> discriminators. See <see cref="BuildEventSchema"/>.</summary>
public static class BuildEventTypes
{
    public const string BuildStarted = "build.started";
    public const string BuildFinished = "build.finished";
    public const string TargetStarted = "target.started";
    public const string TargetFinished = "target.finished";

    // Vocabulary expansion (#11).
    public const string ToolInvoked = "tool.invoked";
    public const string ToolExited = "tool.exited";
    public const string SecretAccessRequested = "secret.access.requested";
    public const string DiagnosticEmitted = "diagnostic.emitted";
    public const string ArtifactProduced = "artifact.produced";
    public const string GateEvaluated = "gate.evaluated";
}

/// <summary>Pinned status vocabulary for <see cref="TargetFinishedPayload.Status"/> and <see cref="BuildFinishedPayload.Status"/>.</summary>
public static class BuildEventStatus
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Skipped = "skipped";
    public const string NotRun = "not_run";
}
