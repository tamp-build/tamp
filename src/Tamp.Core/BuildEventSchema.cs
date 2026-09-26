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

    /// <summary>The pinned set of event types shipped in `#0a` (lifecycle only). #11 adds tool/diagnostic/artifact/gate/secret events additively.</summary>
    public static readonly IReadOnlyList<string> Types = new[]
    {
        BuildEventTypes.BuildStarted,
        BuildEventTypes.BuildFinished,
        BuildEventTypes.TargetStarted,
        BuildEventTypes.TargetFinished,
    };
}

/// <summary>Canonical event <see cref="BuildEvent.Type"/> discriminators. See <see cref="BuildEventSchema"/>.</summary>
public static class BuildEventTypes
{
    public const string BuildStarted = "build.started";
    public const string BuildFinished = "build.finished";
    public const string TargetStarted = "target.started";
    public const string TargetFinished = "target.finished";
}

/// <summary>Pinned status vocabulary for <see cref="TargetFinishedPayload.Status"/> and <see cref="BuildFinishedPayload.Status"/>.</summary>
public static class BuildEventStatus
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Skipped = "skipped";
    public const string NotRun = "not_run";
}
