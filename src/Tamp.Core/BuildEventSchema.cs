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
        BuildEventTypes.ConformanceEvaluated,
        BuildEventTypes.ServiceReady,
        BuildEventTypes.ServiceStopped,
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

    /// <summary>Attestation evidence contract (ADR 0023): an ADR-conformance verdict.</summary>
    public const string ConformanceEvaluated = "conformance.evaluated";

    /// <summary>A long-running service a target started (a dev server, an emulator) is ready to use (#124).</summary>
    public const string ServiceReady = "service.ready";

    /// <summary>A long-running service stopped, with the reason (#124).</summary>
    public const string ServiceStopped = "service.stopped";
}

/// <summary>
/// Pinned reason vocabulary for <see cref="ServiceStoppedPayload.Reason"/> (#124). Lets a consumer tell a
/// deliberate shutdown from a failure without reading the exit code: a service that is stopped on purpose is
/// usually killed, so its <c>tool.exited</c> code is non-zero (137 on Linux) even though nothing went wrong.
/// </summary>
public static class ServiceStopReason
{
    /// <summary>The build stopped it on purpose (the owning handle was disposed, the target finished).</summary>
    public const string Requested = "requested";

    /// <summary>Stopped because the build received a termination signal (Ctrl+C, SIGTERM, SIGHUP).</summary>
    public const string Signal = "signal";

    /// <summary>The service's process exited on its own: a crash, or a failure before it became ready.</summary>
    public const string Exited = "exited";

    /// <summary>It never became ready within its readiness timeout and was stopped.</summary>
    public const string Timeout = "timeout";

    /// <summary>The pinned set, in declaration order.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Requested, Signal, Exited, Timeout };
}

/// <summary>Pinned status vocabulary for <see cref="TargetFinishedPayload.Status"/> and <see cref="BuildFinishedPayload.Status"/>.</summary>
public static class BuildEventStatus
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Skipped = "skipped";
    public const string NotRun = "not_run";

    /// <summary>
    /// The build was interrupted (Ctrl+C, SIGTERM, SIGHUP) while this target ran, or, on
    /// <see cref="BuildFinishedPayload.Status"/>, the build as a whole was (#125). Distinct from
    /// <see cref="Failure"/>: nothing the target did failed. The reason names the signal.
    /// </summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// Pinned four-valued verdict vocabulary for <see cref="ConformanceEvaluatedPayload.Verdict"/>
/// (ADR 0023), matching the downstream tamp-findings model (ADR 0001). <see cref="Unknown"/> and
/// <see cref="Error"/> both block — but with different remedies than <see cref="Fail"/> — and neither
/// is ever a silent pass.
/// </summary>
public static class ConformanceVerdict
{
    /// <summary>Code honors the rule.</summary>
    public const string Pass = "pass";

    /// <summary>Violation, carrying the ADR quote + code evidence.</summary>
    public const string Fail = "fail";

    /// <summary>A semantic check could not decide, or a deterministic probe never ran. Blocks; "your check didn't answer."</summary>
    public const string Unknown = "unknown";

    /// <summary>Evaluation itself broke. Blocks; alert an operator.</summary>
    public const string Error = "error";
}

/// <summary>Pinned method vocabulary for <see cref="ConformanceEvaluatedPayload.Method"/> / <see cref="Provenance.Method"/> (ADR 0023).</summary>
public static class ConformanceMethod
{
    /// <summary>A pure predicate over the finding vocabulary (grep/AST); reproducible.</summary>
    public const string Deterministic = "deterministic";

    /// <summary>A model-produced judgement; non-pure — its verdict is snapshot, not recomputed.</summary>
    public const string Semantic = "semantic";

    /// <summary>The adversarial verify pass that decides fail-vs-unknown for a semantic claim.</summary>
    public const string Verify = "verify";
}
