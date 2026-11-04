using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Tamp.Diagnostics;

/// <summary>
/// Native <see cref="ActivitySource"/> + <see cref="Meter"/> instrumentation
/// for Tamp builds. Zero-overhead when no listener is attached (the .NET
/// <see cref="ActivitySource"/> infrastructure short-circuits to null on
/// <c>StartActivity</c>). Activities emit on three namespaced sources so
/// consumers can selectively subscribe.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Stability contract.</strong> Source names, meter name, span
/// shapes (kind + canonical name), and tag keys form a public contract
/// once shipped — they're what consumers' OTel pipelines, dashboards,
/// and alerts depend on. Changes are breaking and require an ADR.
/// Recorded in <c>docs/adr/0018-diagnostics-emission-contract.md</c>.
/// </para>
/// <para>
/// <strong>Sources.</strong>
/// <list type="bullet">
///   <item><c>Tamp.Build</c> — top-level build lifecycle (one span per <c>Execute&lt;T&gt;</c> invocation).</item>
///   <item><c>Tamp.Build.Targets</c> — one span per executed target.</item>
///   <item><c>Tamp.Build.Commands</c> — one span per dispatched <see cref="CommandPlan"/>.</item>
/// </list>
/// Wildcard subscription via <c>AddSource("Tamp.Build*")</c> picks up all three;
/// selective subscription via the exact source names is supported.
/// </para>
/// <para>
/// <strong>Meter.</strong> Single meter <c>Tamp.Build</c> exposes counters and
/// histograms documented inline below.
/// </para>
/// <para>
/// <strong>Secrets.</strong> Tag values never include command arguments, env-var
/// values, or stdin payloads. The redaction table runs upstream of this; this
/// module is the second line of defense — by construction, only counts and
/// metadata flow through, never user data.
/// </para>
/// </remarks>
public static class TampDiagnostics
{
    /// <summary>Stable version-tag for span/meter declarations. Derived from <see cref="TampBuild"/>'s assembly version.</summary>
    private static readonly string Version =
        typeof(TampBuild).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Top-level build lifecycle span source. One span per <see cref="TampBuild.Execute{T}"/>.</summary>
    public static readonly ActivitySource BuildSource = new("Tamp.Build", Version);

    /// <summary>Per-target span source. One span per target in the executed plan.</summary>
    public static readonly ActivitySource TargetsSource = new("Tamp.Build.Targets", Version);

    /// <summary>Per-CommandPlan span source. One span per <see cref="ProcessRunner.Execute"/> dispatch.</summary>
    public static readonly ActivitySource CommandsSource = new("Tamp.Build.Commands", Version);

    /// <summary>Shared meter for counters and histograms.</summary>
    public static readonly Meter Meter = new("Tamp.Build", Version);

    /// <summary>Counter: total builds invoked. Tag: <c>outcome</c> (success / failure).</summary>
    public static readonly Counter<long> BuildsTotal =
        Meter.CreateCounter<long>("tamp.builds.total", unit: "{builds}", description: "Total builds invoked, tagged by outcome.");

    /// <summary>Counter: total targets executed. Tags: <c>target.name</c>, <c>outcome</c>.</summary>
    public static readonly Counter<long> TargetsExecuted =
        Meter.CreateCounter<long>("tamp.targets.executed", unit: "{targets}", description: "Total targets executed, tagged by name + outcome.");

    /// <summary>Counter: total command plans dispatched. Tags: <c>executable</c>, <c>outcome</c>.</summary>
    public static readonly Counter<long> CommandsExecuted =
        Meter.CreateCounter<long>("tamp.commands.executed", unit: "{commands}", description: "Total CommandPlan dispatches, tagged by executable + outcome.");

    /// <summary>Histogram: build wall-clock duration. Tag: <c>outcome</c>.</summary>
    public static readonly Histogram<double> BuildDurationMs =
        Meter.CreateHistogram<double>("tamp.builds.duration", unit: "ms", description: "Build wall-clock duration in milliseconds.");

    /// <summary>Histogram: per-target wall-clock duration. Tags: <c>target.name</c>, <c>outcome</c>.</summary>
    public static readonly Histogram<double> TargetDurationMs =
        Meter.CreateHistogram<double>("tamp.targets.duration", unit: "ms", description: "Target wall-clock duration in milliseconds, tagged by name + outcome.");

    /// <summary>Histogram: per-command wall-clock duration. Tags: <c>executable</c>, <c>outcome</c>.</summary>
    public static readonly Histogram<double> CommandDurationMs =
        Meter.CreateHistogram<double>("tamp.commands.duration", unit: "ms", description: "Per-CommandPlan wall-clock duration in milliseconds, tagged by executable + outcome.");

    /// <summary>Histogram: peak working-set bytes for the tamp process over the full build. Tag: <c>outcome</c>.</summary>
    public static readonly Histogram<long> BuildPeakMemoryBytes =
        Meter.CreateHistogram<long>("tamp.builds.peak_memory", unit: "By", description: "Peak working-set RSS of the tamp process over the build, in bytes.");

    /// <summary>Histogram: bytes allocated by the managed heap during a target's execution. Tags: <c>target.name</c>, <c>outcome</c>.</summary>
    public static readonly Histogram<long> TargetGcAllocatedBytes =
        Meter.CreateHistogram<long>("tamp.targets.memory.allocated", unit: "By", description: "Managed-heap bytes allocated during a target's execution (start→end delta of GC.GetTotalAllocatedBytes).");

    /// <summary>Histogram: peak working-set bytes of a child process spawned by a CommandPlan. Tags: <c>executable</c>, <c>outcome</c>.</summary>
    public static readonly Histogram<long> CommandPeakMemoryBytes =
        Meter.CreateHistogram<long>("tamp.commands.memory.peak", unit: "By", description: "Peak working-set RSS of the spawned child process at exit, in bytes.");

    /// <summary>Conventional tag-key strings. Pinned. Changes require an ADR.</summary>
    public static class Tags
    {
        public const string BuildTargets = "tamp.build.targets";
        public const string BuildInvocation = "tamp.build.invocation";
        public const string BuildSolution = "tamp.build.solution";
        public const string BuildProjectName = "tamp.build.project.name";                 // [BuildProject] or fallback
        public const string BuildProjectArea = "tamp.build.project.area";                 // [BuildProject(Area = ...)]; nullable
        public const string BuildProjectNameSource = "tamp.build.project.name_source";    // attribute / solution / repo_directory / default
        public const string BuildExitCode = "tamp.build.exit_code";
        public const string BuildCliVersion = "tamp.build.cli_version";
        public const string BuildDurationNs = "tamp.build.duration_ns";                  // high-res, complements span duration
        public const string BuildPeakWorkingSetBytes = "tamp.build.peak_working_set_bytes";
        public const string BuildTargetsTotal = "tamp.build.targets.total";
        public const string BuildTargetsSucceeded = "tamp.build.targets.succeeded";
        public const string BuildTargetsFailed = "tamp.build.targets.failed";
        public const string BuildTargetsSkipped = "tamp.build.targets.skipped";
        public const string BuildTargetsNotRun = "tamp.build.targets.not_run";
        public const string BuildCommandsTotal = "tamp.build.commands.total";
        public const string BuildFailureTarget = "tamp.build.failure.target";
        public const string BuildFailureExitCode = "tamp.build.failure.exit_code";
        public const string BuildFailureHandlersInvoked = "tamp.build.failure_handlers_invoked";

        public const string HostOs = "tamp.host.os";
        public const string HostOsVersion = "tamp.host.os.version";
        public const string HostArch = "tamp.host.arch";
        public const string HostCpuCount = "tamp.host.cpu_count";
        public const string HostTotalMemoryBytes = "tamp.host.total_memory_bytes";
        public const string DotnetRuntimeDescription = "tamp.dotnet.runtime_description";
        public const string CiIsCi = "tamp.ci.is_ci";
        public const string CiVendor = "tamp.ci.vendor";

        public const string TargetName = "tamp.target.name";
        public const string TargetPhase = "tamp.target.phase";
        public const string TargetStatus = "tamp.target.status";
        public const string TargetSkipReason = "tamp.target.skip_reason";
        public const string TargetDependsOn = "tamp.target.depends_on";
        public const string TargetIsAssuredAfterFailure = "tamp.target.assured_after_failure";
        public const string TargetDurationNs = "tamp.target.duration_ns";                // nanosecond-precision; Stopwatch-backed
        public const string TargetStartWorkingSetBytes = "tamp.target.start_working_set_bytes";
        public const string TargetEndWorkingSetBytes = "tamp.target.end_working_set_bytes";
        public const string TargetGcAllocatedBytes = "tamp.target.gc_allocated_bytes";   // delta over the target's run
        public const string TargetGcGen0Collections = "tamp.target.gc.gen0.collections";
        public const string TargetGcGen1Collections = "tamp.target.gc.gen1.collections";
        public const string TargetGcGen2Collections = "tamp.target.gc.gen2.collections";
        public const string TargetCpuTimeMs = "tamp.target.cpu_time_ms";                 // Process.TotalProcessorTime delta
        public const string TargetActionsCount = "tamp.target.actions.count";
        public const string TargetCommandsCount = "tamp.target.commands.count";
        public const string TargetFailureMode = "tamp.target.failure_mode";              // configured (Fatal/Continue/Retry)
        public const string TargetAttempt = "tamp.target.attempt";                       // 1-based, current attempt
        public const string TargetAttemptsTotal = "tamp.target.attempts_total";          // final count at terminal state

        public const string CmdExecutable = "tamp.cmd.executable";
        public const string CmdArgsCount = "tamp.cmd.args.count";
        public const string CmdWorkingDirectory = "tamp.cmd.working_directory";
        public const string CmdExitCode = "tamp.cmd.exit_code";
        public const string CmdHadStdin = "tamp.cmd.had_stdin";
        public const string CmdHadSecrets = "tamp.cmd.had_secrets";
        public const string CmdSourceTarget = "tamp.cmd.source_target";
        public const string CmdDurationNs = "tamp.cmd.duration_ns";                       // high-res
        public const string CmdChildPeakWorkingSetBytes = "tamp.cmd.child.peak_working_set_bytes";
        public const string CmdChildPrivateMemoryBytes = "tamp.cmd.child.private_memory_bytes";
        public const string CmdChildVirtualMemoryBytes = "tamp.cmd.child.virtual_memory_bytes";
        public const string CmdChildCpuTimeUserMs = "tamp.cmd.child.cpu_time.user_ms";
        public const string CmdChildCpuTimeSystemMs = "tamp.cmd.child.cpu_time.system_ms";
        public const string CmdChildCpuTimeTotalMs = "tamp.cmd.child.cpu_time.total_ms";
        public const string CmdChildThreadCount = "tamp.cmd.child.thread_count";
        public const string CmdChildHandleCount = "tamp.cmd.child.handle_count";          // Windows-only; 0 elsewhere
        public const string CmdStdoutBytes = "tamp.cmd.stdout_bytes";
        public const string CmdStderrBytes = "tamp.cmd.stderr_bytes";

        // Outcome values — pinned vocabulary so dashboards group consistently.
        public const string OutcomeKey = "outcome";
        public const string OutcomeSuccess = "success";
        public const string OutcomeFailure = "failure";
        public const string OutcomeSkipped = "skipped";
        public const string OutcomeNotRun = "not_run";
        public const string OutcomeCancelled = "cancelled";
    }

    /// <summary>
    /// Detect the running CI vendor from conventional env vars. Returns
    /// <c>"local"</c> when nothing matches. Vendor list intentionally short;
    /// add via PR + ADR amendment if other vendors come up.
    /// </summary>
    internal static string DetectCiVendor()
    {
        if (string.Equals(System.Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", System.StringComparison.OrdinalIgnoreCase))
            return "github-actions";
        if (string.Equals(System.Environment.GetEnvironmentVariable("TF_BUILD"), "True", System.StringComparison.OrdinalIgnoreCase))
            return "azure-devops";
        if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("TEAMCITY_VERSION")))
            return "teamcity";
        if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("GITLAB_CI")))
            return "gitlab-ci";
        if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("CIRCLECI")))
            return "circleci";
        if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("BUILDKITE")))
            return "buildkite";
        if (string.Equals(System.Environment.GetEnvironmentVariable("CI"), "true", System.StringComparison.OrdinalIgnoreCase))
            return "generic";
        return "local";
    }
}
