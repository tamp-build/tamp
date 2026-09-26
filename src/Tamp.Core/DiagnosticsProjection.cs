using System.Diagnostics;
using Tamp.Diagnostics;

namespace Tamp;

/// <summary>
/// The ADR-0018 diagnostics projection (`#0c`, ADR 0019). The executor computes
/// each scope's facts once and co-renders two projections from them: the lean
/// canonical <see cref="BuildEvent"/> stream (agent-facing) and — here — the rich
/// ADR-0018 <see cref="ActivitySource"/> spans + <see cref="System.Diagnostics.Metrics.Meter"/>
/// records (beacon-facing). This keeps the two contracts from drifting without
/// forcing beacon's per-target resource metrics onto the agent event stream (D1).
/// </summary>
/// <remarks>
/// Moved verbatim out of the executor's hot loop; the frozen ADR-0018 contract
/// (source names, span shapes, tag keys, the summary event) is unchanged and pinned
/// by <c>TampDiagnosticsEmissionTests</c>. The command span (<c>Tamp.Build.Commands</c>)
/// is emitted separately in <see cref="ProcessRunner"/> and is not part of this module.
/// </remarks>
internal static class DiagnosticsProjection
{
    /// <summary>Populate the root build span's start-time tags (host / CI / project facets).</summary>
    public static void AnnotateBuildStart(Activity? span, IReadOnlyList<string> targetNames, BuildProjectInfo? projectInfo)
    {
        if (span is null) return;

        span.SetTag(TampDiagnostics.Tags.BuildTargets, string.Join(",", targetNames));
        span.SetTag(TampDiagnostics.Tags.BuildCliVersion, typeof(TampBuild).Assembly.GetName().Version?.ToString());
        // Host facets — set once per build.
        span.SetTag(TampDiagnostics.Tags.HostOs, System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        span.SetTag(TampDiagnostics.Tags.HostOsVersion, System.Environment.OSVersion.VersionString);
        span.SetTag(TampDiagnostics.Tags.HostArch, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString());
        span.SetTag(TampDiagnostics.Tags.HostCpuCount, System.Environment.ProcessorCount);
        try { span.SetTag(TampDiagnostics.Tags.HostTotalMemoryBytes, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes); } catch { /* shrug */ }
        span.SetTag(TampDiagnostics.Tags.DotnetRuntimeDescription, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        var ciVendor = TampDiagnostics.DetectCiVendor();
        span.SetTag(TampDiagnostics.Tags.CiVendor, ciVendor);
        span.SetTag(TampDiagnostics.Tags.CiIsCi, ciVendor != "local");
        if (projectInfo is not null)
        {
            span.SetTag(TampDiagnostics.Tags.BuildProjectName, projectInfo.Name);
            if (projectInfo.Area is not null) span.SetTag(TampDiagnostics.Tags.BuildProjectArea, projectInfo.Area);
            span.SetTag(TampDiagnostics.Tags.BuildProjectNameSource, projectInfo.NameSource.ToString().ToLowerInvariant());
        }
    }

    /// <summary>Close out the root build span (terminal tags + summary event) and record the build-level meters.</summary>
    public static void AnnotateBuildEnd(Activity? span, in BuildEndTelemetry t)
    {
        if (span is not null)
        {
            span.SetTag(TampDiagnostics.Tags.BuildExitCode, t.ExitCode);
            span.SetTag(TampDiagnostics.Tags.OutcomeKey, t.Outcome);
            span.SetTag(TampDiagnostics.Tags.BuildDurationNs, t.DurationNs);
            span.SetTag(TampDiagnostics.Tags.BuildPeakWorkingSetBytes, t.PeakWorkingSetBytes);
            span.SetTag(TampDiagnostics.Tags.BuildTargetsTotal, t.TargetsTotal);
            span.SetTag(TampDiagnostics.Tags.BuildTargetsSucceeded, t.Succeeded);
            span.SetTag(TampDiagnostics.Tags.BuildTargetsFailed, t.Failed);
            span.SetTag(TampDiagnostics.Tags.BuildTargetsSkipped, t.Skipped);
            span.SetTag(TampDiagnostics.Tags.BuildTargetsNotRun, t.NotRun);
            span.SetTag(TampDiagnostics.Tags.BuildCommandsTotal, t.CommandsTotal);
            if (t.FailureTarget is not null)
            {
                span.SetTag(TampDiagnostics.Tags.BuildFailureTarget, t.FailureTarget);
                span.SetTag(TampDiagnostics.Tags.BuildFailureExitCode, t.FailureExitCode);
                span.SetStatus(ActivityStatusCode.Error, $"failed at: {t.FailureTarget}");
            }
            else
            {
                span.SetStatus(ActivityStatusCode.Ok);
            }
            if (t.HandlersInvoked.Count > 0)
                span.SetTag(TampDiagnostics.Tags.BuildFailureHandlersInvoked, string.Join(",", t.HandlersInvoked));

            // Structured snapshot — single event, easy to grep / dashboard.
            span.AddEvent(new ActivityEvent("tamp.build.summary", tags: new ActivityTagsCollection
            {
                [TampDiagnostics.Tags.BuildTargetsTotal] = t.TargetsTotal,
                [TampDiagnostics.Tags.BuildTargetsSucceeded] = t.Succeeded,
                [TampDiagnostics.Tags.BuildTargetsFailed] = t.Failed,
                [TampDiagnostics.Tags.BuildTargetsSkipped] = t.Skipped,
                [TampDiagnostics.Tags.BuildTargetsNotRun] = t.NotRun,
                [TampDiagnostics.Tags.BuildCommandsTotal] = t.CommandsTotal,
                [TampDiagnostics.Tags.BuildExitCode] = t.ExitCode,
                [TampDiagnostics.Tags.OutcomeKey] = t.Outcome,
            }));
        }

        TampDiagnostics.BuildsTotal.Add(1, new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, t.Outcome));
        TampDiagnostics.BuildDurationMs.Record(t.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, t.Outcome));
        TampDiagnostics.BuildPeakMemoryBytes.Record(t.PeakWorkingSetBytes, new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, t.Outcome));
    }

    /// <summary>Populate a per-target span's start-time tags.</summary>
    public static void AnnotateTargetStart(Activity? span, TargetSpec spec, long startWorkingSetBytes, int actionsCount)
    {
        if (span is null) return;
        span.SetTag(TampDiagnostics.Tags.TargetName, spec.Name);
        span.SetTag(TampDiagnostics.Tags.TargetPhase, spec.Phase.ToString());
        if (spec.Dependencies.Count > 0) span.SetTag(TampDiagnostics.Tags.TargetDependsOn, string.Join(",", spec.Dependencies));
        if (spec.AssuredAfterFailure) span.SetTag(TampDiagnostics.Tags.TargetIsAssuredAfterFailure, true);
        span.SetTag(TampDiagnostics.Tags.TargetStartWorkingSetBytes, startWorkingSetBytes);
        span.SetTag(TampDiagnostics.Tags.TargetFailureMode, spec.FailureMode.ToString());
        span.SetTag(TampDiagnostics.Tags.TargetAttempt, 1);                  // reserved; bumps when retry mode lands
        span.SetTag(TampDiagnostics.Tags.TargetActionsCount, actionsCount);
    }

    /// <summary>
    /// Finalize a per-target span and record the matching meters. High-res timing
    /// (Stopwatch ticks → ns), memory deltas, per-target GC-collection counts, CPU
    /// time, and command counts. Success / failure / continue-on-failure emit identically.
    /// </summary>
    public static void AnnotateTargetTerminal(Activity? span, TargetSpec spec, in TargetTerminalTelemetry t)
    {
        var endTicks = Stopwatch.GetTimestamp();
        var durationNs = (long)((endTicks - t.SwStartTicks) * 1_000_000_000.0 / Stopwatch.Frequency);
        var allocDelta = System.Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - t.AllocAtStart);
        var gen0Delta = GC.CollectionCount(0) - t.Gen0AtStart;
        var gen1Delta = GC.CollectionCount(1) - t.Gen1AtStart;
        var gen2Delta = GC.CollectionCount(2) - t.Gen2AtStart;
        long workingSetAtEnd;
        double cpuDeltaMs;
        try
        {
            using var p = Process.GetCurrentProcess();
            workingSetAtEnd = p.WorkingSet64;
            cpuDeltaMs = (p.TotalProcessorTime - t.CpuAtStart).TotalMilliseconds;
        }
        catch { workingSetAtEnd = 0; cpuDeltaMs = 0; }

        if (span is not null)
        {
            span.SetTag(TampDiagnostics.Tags.TargetStatus, t.Outcome);
            span.SetTag(TampDiagnostics.Tags.TargetDurationNs, durationNs);
            span.SetTag(TampDiagnostics.Tags.TargetEndWorkingSetBytes, workingSetAtEnd);
            span.SetTag(TampDiagnostics.Tags.TargetGcAllocatedBytes, allocDelta);
            span.SetTag(TampDiagnostics.Tags.TargetGcGen0Collections, gen0Delta);
            span.SetTag(TampDiagnostics.Tags.TargetGcGen1Collections, gen1Delta);
            span.SetTag(TampDiagnostics.Tags.TargetGcGen2Collections, gen2Delta);
            span.SetTag(TampDiagnostics.Tags.TargetCpuTimeMs, cpuDeltaMs);
            span.SetTag(TampDiagnostics.Tags.TargetCommandsCount, t.CommandsCount);
            span.SetTag(TampDiagnostics.Tags.TargetAttemptsTotal, 1);                       // reserved; retry-mode bumps later
            span.SetStatus(t.Outcome == TampDiagnostics.Tags.OutcomeSuccess ? ActivityStatusCode.Ok : ActivityStatusCode.Error, t.ErrorMessage);
        }
        TampDiagnostics.TargetsExecuted.Add(1,
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.TargetName, spec.Name),
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, t.Outcome));
        TampDiagnostics.TargetDurationMs.Record(t.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.TargetName, spec.Name),
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, t.Outcome));
        TampDiagnostics.TargetGcAllocatedBytes.Record(allocDelta,
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.TargetName, spec.Name),
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, t.Outcome));
    }

    /// <summary>Emit a zero-duration "skipped" activity so dashboards see every plan-position, not just executed ones.</summary>
    public static void EmitSkipped(TargetSpec spec, string reason)
    {
        using var span = TampDiagnostics.TargetsSource.StartActivity($"target:{spec.Name}", ActivityKind.Internal);
        if (span is not null)
        {
            span.SetTag(TampDiagnostics.Tags.TargetName, spec.Name);
            span.SetTag(TampDiagnostics.Tags.TargetPhase, spec.Phase.ToString());
            span.SetTag(TampDiagnostics.Tags.TargetStatus, TampDiagnostics.Tags.OutcomeSkipped);
            span.SetTag(TampDiagnostics.Tags.TargetSkipReason, reason);
            span.SetStatus(ActivityStatusCode.Ok);
        }
        TampDiagnostics.TargetsExecuted.Add(1,
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.TargetName, spec.Name),
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, TampDiagnostics.Tags.OutcomeSkipped));
    }
}

/// <summary>Terminal telemetry for a target's ADR-0018 span (`#0c`). Start-time snapshots + outcome; deltas computed at render.</summary>
internal readonly record struct TargetTerminalTelemetry(
    TimeSpan Elapsed,
    long SwStartTicks,
    long AllocAtStart,
    long WorkingSetAtStart,
    int Gen0AtStart,
    int Gen1AtStart,
    int Gen2AtStart,
    TimeSpan CpuAtStart,
    int CommandsCount,
    string Outcome,
    string? ErrorMessage);

/// <summary>Build-level telemetry for the ADR-0018 root span close-out (`#0c`).</summary>
internal readonly record struct BuildEndTelemetry(
    int ExitCode,
    string Outcome,
    long DurationNs,
    long PeakWorkingSetBytes,
    TimeSpan Elapsed,
    int TargetsTotal,
    int Succeeded,
    int Failed,
    int Skipped,
    int NotRun,
    int CommandsTotal,
    string? FailureTarget,
    int? FailureExitCode,
    IReadOnlyList<string> HandlersInvoked);
