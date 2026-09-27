using System.Reflection;

namespace Tamp;

/// <summary>
/// Base class for a Tamp build script. Authors derive from this, declare
/// <see cref="Target"/>-typed properties for each target, and call
/// <see cref="Execute{T}"/> from <c>Main</c>.
/// </summary>
/// <remarks>
/// At execution time, the framework reflects over the build class, finds
/// every <see cref="Target"/>-typed property, invokes each property's
/// delegate against a fresh <see cref="ITargetDefinition"/>, and assembles
/// the resulting <see cref="TargetSpec"/>s into the build's target graph.
/// </remarks>
public abstract partial class TampBuild
{
    /// <summary>
    /// Whether the current build invocation is local (developer machine)
    /// rather than CI. Build scripts use this for picking sensible defaults
    /// — e.g., Debug locally, Release in CI.
    /// </summary>
    protected static bool IsLocalBuild => HostProfileBuilder.Build().Ci is null;

    /// <summary>
    /// Whether the current build invocation is in a known CI environment.
    /// </summary>
    protected static bool IsServerBuild => !IsLocalBuild;

    private static IReadOnlyList<string> _ruleFilter = Array.Empty<string>();

    /// <summary>
    /// The active <c>--rule</c> filters (#16). Build/target code reads these to scope its tool to
    /// specific diagnostic rules where the tool supports it (degrade gracefully otherwise); empty
    /// when no <c>--rule</c> was passed. Orthogonal to target selection.
    /// </summary>
    public static IReadOnlyList<string> RuleFilter => _ruleFilter;

    private static AbsolutePath? _rootDirectoryCache;

    /// <summary>
    /// The root of the consumer's repository. Computed by walking up from
    /// the build assembly's location and stopping at the first directory
    /// containing any of: <c>.git</c>, a <c>.slnx</c> or <c>.sln</c> file,
    /// or a <c>.tamp</c> subdirectory. Cached after first access.
    /// </summary>
    public static AbsolutePath RootDirectory
    {
        get
        {
            if (_rootDirectoryCache is not null) return _rootDirectoryCache;
            var found = LocateRootDirectory(AppContext.BaseDirectory);
            if (found is null)
                throw new InvalidOperationException(
                    $"Could not locate repository root — no .git, .slnx/.sln, or .tamp directory found above '{AppContext.BaseDirectory}'.");
            return _rootDirectoryCache = AbsolutePath.Create(found);
        }
    }

    /// <summary>
    /// Tamp's per-build scratch directory. Lives at
    /// <c>RootDirectory / ".tamp" / "temp"</c>; created on first access.
    /// </summary>
    public static AbsolutePath TemporaryDirectory => (RootDirectory / ".tamp" / "temp").EnsureDirectoryExists();

    /// <summary>
    /// Build-instance tracking for <see cref="Scratch"/>-allocated temp dirs.
    /// Cleaned up at end of <see cref="Execute{T}"/> (success or failure)
    /// unless the <c>TAMP_KEEP_SCRATCH</c> env var is set.
    /// </summary>
    private readonly List<AbsolutePath> _scratchDirs = new();

    /// <summary>
    /// Allocate a uniquely-named scratch directory tied to this build's
    /// lifetime. The directory exists on disk when this method returns; at
    /// end of <see cref="Execute{T}"/> it is deleted recursively (success or
    /// failure paths both clean up).
    /// </summary>
    /// <param name="namePrefix">
    /// Optional prefix for the directory name. Defaults to <c>tamp-scratch</c>.
    /// Useful for grepping <c>.tamp/temp</c> when <c>TAMP_KEEP_SCRATCH=1</c> is set.
    /// </param>
    /// <remarks>
    /// Set <c>TAMP_KEEP_SCRATCH=1</c> in the environment to preserve scratch
    /// directories after the build exits — useful for post-mortem inspection
    /// of intermediate artifacts. Default is "delete, including on failure"
    /// so a flaky build doesn't slowly fill <c>.tamp/temp</c>.
    /// </remarks>
    protected AbsolutePath Scratch(string? namePrefix = null)
    {
        var prefix = string.IsNullOrWhiteSpace(namePrefix) ? "tamp-scratch" : namePrefix.Trim();
        // TAM-#14: scratch lives UNDER the worktree (RootDirectory/.tamp/temp), not the
        // shared OS temp root — so parallel workers in separate worktrees never share a
        // scratch root, and per-build cleanup stays inside the repo. The GUID keeps
        // concurrent allocations (even within one worktree / run) unique. Adopters who
        // genuinely want the OS temp root can call AbsolutePath.CreateTempDirectory.
        var dir = (TemporaryDirectory / $"{prefix}-{Guid.NewGuid():N}").EnsureDirectoryExists();
        lock (_scratchDirs) _scratchDirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// Delete every scratch directory allocated via <see cref="Scratch"/>
    /// during this build, unless <c>TAMP_KEEP_SCRATCH</c> is set. Safe to call
    /// multiple times; subsequent calls are no-ops because the tracking list
    /// is cleared on first call.
    /// </summary>
    /// <remarks>
    /// Delete errors are swallowed — the cleanup is best-effort. If a child
    /// process holds an open handle to a scratch file at exit, Windows will
    /// refuse the delete; the user can recover via <c>TAMP_KEEP_SCRATCH=1</c>
    /// and a manual <c>rm -rf</c>, but the build's exit code is not affected
    /// either way.
    /// </remarks>
    internal void CleanUpScratchDirs()
    {
        var keep = Environment.GetEnvironmentVariable("TAMP_KEEP_SCRATCH");
        if (!string.IsNullOrEmpty(keep)
            && !keep.Equals("0", StringComparison.Ordinal)
            && !keep.Equals("false", StringComparison.OrdinalIgnoreCase))
            return;

        List<AbsolutePath> snapshot;
        lock (_scratchDirs)
        {
            snapshot = new List<AbsolutePath>(_scratchDirs);
            _scratchDirs.Clear();
        }
        foreach (var dir in snapshot)
        {
            try { if (dir.DirectoryExists()) Directory.Delete(dir.Value, recursive: true); }
            catch { /* best effort — see remarks */ }
        }
    }

    /// <summary>Test-only access to the scratch-dir tracking list.</summary>
    internal IReadOnlyList<AbsolutePath> ScratchDirsSnapshot()
    {
        lock (_scratchDirs) return _scratchDirs.ToList();
    }

    private static CiHost? _ciHostCache;
    private static bool _ciHostResolved;

    /// <summary>
    /// The active CI host (GitHub Actions, Azure DevOps, TeamCity, …) when
    /// running under a recognised CI environment, or null otherwise.
    /// Cached after first access; build scripts on a developer machine see
    /// null and can branch on that.
    /// </summary>
    public static CiHost? CiHost
    {
        get
        {
            if (_ciHostResolved) return _ciHostCache;
            _ciHostCache = Tamp.CiHost.Detect();
            _ciHostResolved = true;
            return _ciHostCache;
        }
    }

    /// <summary>
    /// Cheap pre-scan over <paramref name="args"/> looking for the
    /// <c>--list</c> / <c>--list-tree</c> flags. Used before
    /// <see cref="ParseInvocation"/> runs so <see cref="ParameterBinder.Bind"/>
    /// can decide whether to tolerate <c>[FromPath]</c> resolution failures.
    /// HoldFast friction #20 — target introspection should not require
    /// every tool to already be on PATH.
    /// </summary>
    internal static bool IsListOnlyInvocation(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg == "--list" || arg == "--list-tree") return true;
            // Handle inline-equals form (--list=json) — anything that begins
            // with one of the list flags followed by '=' is still list mode.
            if (arg.StartsWith("--list=", StringComparison.Ordinal)) return true;
            if (arg.StartsWith("--list-tree=", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Reset the cached <see cref="RootDirectory"/>. Test-only.</summary>
    internal static void ResetCachedDirectories()
    {
        _rootDirectoryCache = null;
        _ciHostCache = null;
        _ciHostResolved = false;
    }

    private static string? LocateRootDirectory(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git"))) return current.FullName;
            if (Directory.Exists(Path.Combine(current.FullName, ".tamp"))) return current.FullName;
            if (Directory.GetFiles(current.FullName, "*.slnx", SearchOption.TopDirectoryOnly).Length > 0)
                return current.FullName;
            if (Directory.GetFiles(current.FullName, "*.sln", SearchOption.TopDirectoryOnly).Length > 0)
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// Print the framework banner (ASCII logo + version + URL) plus a
    /// host-info panel (OS, arch, CPU/memory, runtime, CI vendor, cgroup).
    /// Always shown, regardless of verbosity — useful for after-the-fact
    /// debugging of runner-specific build issues where the runner config
    /// isn't otherwise visible in the log.
    /// </summary>
    public static void PrintBanner(TextWriter writer)
    {
        // Prefer the InformationalVersion (carries pre-release suffixes like
        // "0.0.1-alpha") over Assembly.Version (which is a 3-part numeric
        // and drops everything after the patch).
        var asm = typeof(TampBuild).Assembly;
        var infoVersion = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // SourceLink appends "+<commit>" to InformationalVersion; trim that
        // for display since the host panel below shows commit info already.
        if (infoVersion is not null)
        {
            var plus = infoVersion.IndexOf('+');
            if (plus > 0) infoVersion = infoVersion[..plus];
        }
        var version = infoVersion ?? asm.GetName().Version?.ToString(3) ?? "0.0.0";
        writer.WriteLine();
        writer.WriteLine(@"  ████████╗ █████╗ ███╗   ███╗██████╗ ");
        writer.WriteLine(@"  ╚══██╔══╝██╔══██╗████╗ ████║██╔══██╗");
        writer.WriteLine(@"     ██║   ███████║██╔████╔██║██████╔╝");
        writer.WriteLine(@"     ██║   ██╔══██║██║╚██╔╝██║██╔═══╝ ");
        writer.WriteLine(@"     ██║   ██║  ██║██║ ╚═╝ ██║██║     ");
        writer.WriteLine(@"     ╚═╝   ╚═╝  ╚═╝╚═╝     ╚═╝╚═╝     ");
        writer.WriteLine();
        writer.WriteLine($"  Tamp {version}  ·  https://github.com/tamp-build/tamp");
        writer.WriteLine();

        var host = HostProfileBuilder.Build();
        var ci = Tamp.CiHost.Detect();

        var os = host.Os switch
        {
            OSFamily.Windows => "Windows",
            OSFamily.Linux => host.InWsl ? "Linux (WSL)" : "Linux",
            OSFamily.MacOs => "macOS",
            _ => "Unknown",
        };
        var arch = host.Arch switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            _ => host.Arch.ToString().ToLowerInvariant(),
        };
        var totalGb = host.TotalMemoryBytes / 1024.0 / 1024.0 / 1024.0;
        var freeGb = host.AvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0;
        var runtime = $".NET {Environment.Version}";
        var runner = ci is not null
            ? FormatCiVendor(ci.Vendor)
            : (host.InContainer ? "container (local)" : "local");

        writer.WriteLine($"  Host:     {os} {arch}  ·  {host.LogicalCpuCount} core{(host.LogicalCpuCount == 1 ? "" : "s")}  ·  {totalGb:F1} GB total / {freeGb:F1} GB free");
        writer.WriteLine($"  Runtime:  {runtime}  ·  Runner: {runner}");

        if (host.Cgroup is { } cg)
        {
            var memLimit = cg.MemoryLimitBytes is { } mb
                ? $"{mb / 1024.0 / 1024.0 / 1024.0:F1} GB memory limit"
                : "no memory limit";
            var cpuQuota = cg.CpuQuota is { } cq
                ? $"{cq:F2} cpu quota"
                : "no cpu quota";
            writer.WriteLine($"  Cgroup:   v{cg.Version}  ·  {cpuQuota}  ·  {memLimit}");
        }

        writer.WriteLine();
    }

    private static string FormatCiVendor(CiVendor v) => v switch
    {
        CiVendor.GitHubActions => "GitHub Actions",
        CiVendor.AzureDevOps => "Azure DevOps",
        CiVendor.GitLabCi => "GitLab CI",
        CiVendor.AppVeyor => "AppVeyor",
        CiVendor.TeamCity => "TeamCity",
        CiVendor.Jenkins => "Jenkins",
        CiVendor.CircleCI => "CircleCI",
        CiVendor.Buildkite => "Buildkite",
        CiVendor.Travis => "Travis CI",
        CiVendor.Unknown => "CI (vendor unknown)",
        _ => "local",
    };

    /// <summary>
    /// Emit a CI-vendor-specific masking instruction when a <see cref="Secret"/>
    /// resolves. The instruction tells the CI runner to scrub the value
    /// from subsequent log lines, even ones produced by child processes
    /// the wrapper spawns. Tamp's own <c>RedactingTextWriter</c> handles
    /// in-process scrubbing; this adds vendor-side defense in depth.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><b>GitHub Actions</b> — emits <c>::add-mask::&lt;value&gt;</c>.
    ///         Subsequent occurrences in stdout / stderr are replaced
    ///         with <c>***</c> by the runner.</item>
    ///   <item><b>Azure DevOps</b> — emits
    ///         <c>##vso[task.setvariable variable=...;issecret=true]&lt;value&gt;</c>
    ///         which registers the value with the Azure Pipelines
    ///         secret store + masking.</item>
    ///   <item><b>TeamCity / Jenkins / GitLab / Travis / others</b> —
    ///         no portable equivalent; relies on Tamp's in-process
    ///         redaction only.</item>
    ///   <item><b>Local</b> — no-op; nothing reads the instruction.</item>
    /// </list>
    /// </remarks>
    private static void RegisterSecretForCiMasking(Secret secret)
    {
        var host = HostProfileBuilder.Build();
        if (host.Ci is null) return;

        var value = secret.Reveal();
        switch (host.Ci)
        {
            case CiVendor.GitHubActions:
                Console.WriteLine($"::add-mask::{value}");
                break;
            case CiVendor.AzureDevOps:
                // The variable name is just the secret's label; the
                // important bit is issecret=true which registers the
                // value with the pipeline's masking store.
                Console.WriteLine($"##vso[task.setvariable variable=TAMP_SECRET_{secret.Name};issecret=true]{value}");
                break;
            // Other vendors: no portable instruction. Rely on in-process redaction.
        }
    }

    /// <summary>Top-level build entry point. Pass <c>args</c> from <c>Main</c>.</summary>
    public static int Execute<T>(string[] args) where T : TampBuild, new()
    {
        T? build = null;
        NdjsonEventSink? eventSink = null;
        NdjsonEventSink? stdoutEventSink = null;   // --reporter=json: canonical stream to stdout
        TextWriter? savedConsoleOut = null;        // real stdout, restored in the finally
        try
        {
            build = new T();

            // List-only (and --help) invocations skip value-injection failures so adopters
            // can `tamp --list` / `tamp --help` without every [FromPath] tool being installed
            // on the runner — HoldFast friction #20. Cheap pre-scan; the
            // authoritative flag parse happens later in ParseInvocation.
            var listOnly = IsListOnlyInvocation(args) || IsHelpInvocation(args);

            // #16: resolve --rule filters early so target-authoring code can read RuleFilter.
            _ruleFilter = ResolveRuleFilters(args);

            // Parameter binding happens before target discovery so that any
            // [Parameter] reads inside a target's authoring lambda see the
            // resolved values.
            ParameterBinder.Bind(build, args, Environment.GetEnvironmentVariable,
                tolerateInjectionFailures: listOnly);

            // Secret binding: env-var leg (TAM-78). The onResolved callback
            // emits CI-vendor masking instructions (e.g. ::add-mask:: on
            // GitHub Actions) so subsequent log lines don't leak the value.
            // EnsureResolved (interactive prompt + future legs) runs later,
            // just before a target that .Requires() the secret executes.
            SecretBinder.Bind(build, Environment.GetEnvironmentVariable, RegisterSecretForCiMasking);

            var targets = CollectTargets(build);

            // #70: --help / -h prints usage + the target list and runs nothing. Handled before the
            // default-target / unknown-flag machinery so it always works, even alongside a typo.
            if (IsHelpInvocation(args))
            {
                PrintHelp(targets);
                return 0;
            }

            // #71: `mcp` / `init` are verbs of the global `tamp` tool, not of a consumer build exe.
            // The build doesn't host the MCP server itself (that would pull the MCP SDK + Hosting
            // into Tamp.Core and onto every satellite); the global tool locates and drives this build
            // for you. Point the user there instead of failing with a confusing "unknown target".
            if (args.Length > 0 && (args[0] == "mcp" || args[0] == "init") && !targets.ContainsKey(args[0]))
            {
                var verb = args[0];
                Console.Error.WriteLine($"tamp: `{verb}` is a command of the global `tamp` tool, not of this build.");
                if (verb == "mcp")
                {
                    Console.Error.WriteLine("      Start the MCP control surface with:  tamp mcp   (run from this repo)");
                    Console.Error.WriteLine("      It locates and drives this build project (list_targets / plan / run_target / get_result / get_log).");
                }
                else
                {
                    Console.Error.WriteLine("      Scaffold a new build with:  tamp init");
                }
                return 2;
            }

            // Single-default invariant: at most one target across the entire build class (including
            // any partial-class files) may carry `.Default()`. Reflection collects targets from all
            // files uniformly, so this check is naturally file-layout-independent.
            var markedDefaults = targets.Values.Where(t => t.IsDefault).Select(t => t.Name).ToList();
            if (markedDefaults.Count > 1)
            {
                Console.Error.WriteLine(
                    $"Multiple targets are marked `.Default()`: {string.Join(", ", markedDefaults)}. " +
                    "Exactly one target may be the default. Remove `.Default()` from all but one.");
                return 2;
            }

            // Internal + Default mutual exclusion.
            var defaultAndInternal = targets.Values
                .Where(t => t.IsDefault && t.IsInternal)
                .Select(t => t.Name)
                .ToList();
            if (defaultAndInternal.Count > 0)
            {
                foreach (var name in defaultAndInternal)
                    Console.Error.WriteLine(
                        $"Target '{name}' is marked both Internal and Default — pick one. " +
                        "Internal means non-callable; Default means runs when no target is given.");
                return 2;
            }

            // Stranded-internal warning: Internal target with no incoming dependency edges
            // and nothing else wiring it in. It will never run.
            foreach (var target in targets.Values.Where(t => t.IsInternal))
            {
                var incoming = targets.Values
                    .Any(other => other.Dependencies.Contains(target.Name)
                               || other.Triggers.Contains(target.Name)
                               || other.OrderBefore.Contains(target.Name));
                if (!incoming)
                {
                    Console.Error.WriteLine(
                        $"warning: Target '{target.Name}' is marked Internal but nothing depends on it. " +
                        "It will never run. Either wire it as a DependsOn of another target, or remove .Internal() if it's meant to be callable.");
                }
            }

            var graph = new TargetGraph(targets);

            // #70: the declared [Parameter] cli-keys let ParseInvocation tell a real --param from a
            // typo, so unknown flags fail closed instead of silently running the default graph.
            var parameterKeys = ParameterBinder.CliKeys(build.GetType());
            var (mode, targetNames, listMode, showAll, verbosity, skipTargets, skipDeps, format, reporterKind) = ParseInvocation(args, targets, parameterKeys);

            // Validate --skip <name> values map to actual targets — typos
            // would otherwise silently no-op (the skip set would just never
            // match anything in the execution order).
            foreach (var skipped in skipTargets)
            {
                if (!targets.ContainsKey(skipped))
                {
                    Console.Error.WriteLine($"tamp: --skip target '{skipped}' is not a known target.");
                    Console.Error.WriteLine("      Use `--list` to see available targets.");
                    return 2;
                }
            }

            // --skip-deps requires an explicit target — it semantically means
            // "run only the named target's Executes." With no target named,
            // there's nothing to skip-around.
            if (skipDeps && targetNames.Count == 0)
            {
                Console.Error.WriteLine("tamp: --skip-deps requires an explicit target name.");
                Console.Error.WriteLine("      Use `dotnet tamp <Target> --skip-deps` to run just that target.");
                return 2;
            }

            if (listMode is ListMode.Flat or ListMode.Tree)
            {
                if (format == OutputFormat.Json)
                {
                    PrintTargetCatalogJson(targets, build, showAll);
                }
                else
                {
                    PrintTargetList(targets, tree: listMode is ListMode.Tree, showAll);
                }
                return 0;
            }

            // #15: `--from X` / `--downstream X` — run X plus its transitive dependents
            // (downstream slice), skipping external upstream as assumed-satisfied. Overrides
            // the parsed target set (X arrives as the flag value, not a target).
            IReadOnlySet<string>? runOnly = null;
            var fromTarget = ResolveFromTarget(args);
            if (fromTarget is not null)
            {
                if (!targets.ContainsKey(fromTarget))
                {
                    Console.Error.WriteLine($"tamp: --from target '{fromTarget}' is not a known target.");
                    Console.Error.WriteLine("      Use `--list` to see available targets.");
                    return 2;
                }
                var slice = graph.DependentsClosure(fromTarget);
                targetNames = slice;
                runOnly = new HashSet<string>(slice, StringComparer.Ordinal);
            }

            if (targetNames.Count == 0)
            {
                Console.Error.WriteLine("No target specified and no `.Default()`-marked target, `Default`-named target, or `Ci`-named target found.");
                Console.Error.WriteLine("Mark one target with `.Default()` or pass a target name. Use `--list` to see available targets.");
                return 2;
            }

            // Refuse direct invocation of Internal targets with a friendly error.
            // The --from slice legitimately includes internal dependents, so it's exempt.
            foreach (var requested in fromTarget is null ? targetNames : Array.Empty<string>())
            {
                if (targets.TryGetValue(requested, out var spec) && spec.IsInternal)
                {
                    var dependents = targets.Values
                        .Where(t => t.Dependencies.Contains(requested)
                                 || t.Triggers.Contains(requested)
                                 || t.OrderBefore.Contains(requested))
                        .Select(t => t.Name)
                        .ToList();
                    Console.Error.WriteLine($"tamp: Target '{requested}' is internal — not directly callable.");
                    if (dependents.Count > 0)
                        Console.Error.WriteLine(
                            $"      It runs as a dependency of: {string.Join(", ", dependents)}.");
                    else
                        Console.Error.WriteLine(
                            "      Nothing depends on it. Either wire it as a dependency or remove .Internal() to expose it.");
                    Console.Error.WriteLine(
                        "      Call one of those targets, or remove .Internal() from its definition.");
                    return 2;
                }
            }

            // #22: `--plan --format json` prints the resolved execution order as JSON
            // and runs nothing (powers the MCP `plan` tool). Text `--plan` still flows
            // through the executor's Plan mode below.
            if (mode == ExecutionMode.Plan && format == OutputFormat.Json)
            {
                var planOrder = graph.ComputeExecutionOrder(targetNames.ToArray());
                PrintPlanJson(targetNames, planOrder);
                return 0;
            }

            // TAM-140: in --reporter=json mode, suppress the ASCII banner so
            // stdout carries only NDJSON events.
            if (reporterKind != ReporterKind.Json)
            {
                PrintBanner(Console.Out);
            }

            // Resolve project info for diagnostics (ADR 0018):
            // [BuildProject] attribute > [Solution] filename > repo dir name > "unknown".
            var solutionPath = build.ResolveInjectedSolution()?.Path.Value;
            var projectInfo = Tamp.Diagnostics.BuildProjectInfo.Resolve(
                typeof(T),
                solutionPath,
                RootDirectory.Value);

            // ADR 0019 (1.16 hard cutover): --reporter=json is no longer a separate legacy
            // schema. It emits the CANONICAL event stream to stdout with the human console fully
            // suppressed — the same envelope --events writes to a file. The stdout stream is an
            // IBuildEventSink (below), not an IBuildReporter, so the default reporter is a no-op.
            IBuildReporter defaultReporter = NoopBuildReporter.Instance;
            TextWriter? logOutput = reporterKind == ReporterKind.Json ? TextWriter.Null : null;

            // --reporter=json: canonical NDJSON to stdout. Keep the real stdout for the stream and
            // redirect Console.Out so target-body writes (a Console.WriteLine inside an Executes
            // block) can't interleave with / corrupt the structured stream. Restored in the
            // finally. The sink does not own the writer, so disposing it flushes but never closes
            // stdout.
            if (mode == ExecutionMode.Run && reporterKind == ReporterKind.Json)
            {
                savedConsoleOut = Console.Out;
                stdoutEventSink = new NdjsonEventSink(savedConsoleOut);
                Console.SetOut(TextWriter.Null);
            }

            // TAM-230: collect any [BuildReporter]-marked fields the build script
            // declared (Telegram, Slack, custom, …) and compose them with the
            // default. The single-reporter fast path stays unchanged when no
            // adopter reporters are registered.
            var adopterReporters = CollectBuildReporters(build);
            IBuildReporter reporter = adopterReporters.Count == 0
                ? defaultReporter
                : new CompositeBuildReporter(new[] { defaultReporter }.Concat(adopterReporters).ToArray());

            // `#0b` (ADR 0019): agent NDJSON event sink to a FILE — `--events <path>` / `TAMP_EVENTS`.
            // Purely additive, best-effort (warns + skips on failure). Only for an actual run.
            if (mode == ExecutionMode.Run
                && ResolveEventsTarget(args, Environment.GetEnvironmentVariable) is { } eventsPath)
            {
                eventSink = NdjsonEventSink.TryCreate(eventsPath, Console.Error);
            }

            // The one canonical stream can fan out to both stdout (--reporter=json) and a file
            // (--events) at once; compose when both are live. Both carry the identical envelope.
            IBuildEventSink? runSink = (stdoutEventSink, eventSink) switch
            {
                (null, null) => null,
                (not null, null) => stdoutEventSink,
                (null, not null) => eventSink,
                _ => new CompositeBuildEventSink(stdoutEventSink, eventSink),
            };

            // #20: capability enforcement mode + elevation (default Off — humans unaffected).
            var (capabilityMode, allowSideEffects) = ResolveCapabilityMode(args, Environment.GetEnvironmentVariable);

            var executor = new Executor(
                graph, mode, output: logOutput, verbosity, projectInfo,
                skippedByUser: skipTargets,
                skipDependencies: skipDeps,
                reporter: reporter,
                eventSink: runSink,
                capabilityMode: capabilityMode,
                allowSideEffects: allowSideEffects,
                runOnly: runOnly,
                ruleFilters: _ruleFilter,
                cacheAdvice: ResolveCacheAdvice(args, Environment.GetEnvironmentVariable),
                captureLogs: ResolveCaptureLogs(args, Environment.GetEnvironmentVariable));
            return executor.Run(targetNames.ToArray()).ExitCode;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"tamp: {ex.Message}");
            return 1;
        }
        finally
        {
            // Best-effort cleanup of build-scoped scratch directories. Runs
            // for every exit path including the InvalidOperationException
            // catch above so a flaky build doesn't slowly fill /tmp.
            try { build?.CleanUpScratchDirs(); }
            catch { /* swallow — cleanup must not change the build's exit code */ }

            // Flush + close the NDJSON event stream(s) (best-effort; never affects exit code).
            try { eventSink?.Dispose(); }
            catch { /* swallow */ }
            try { stdoutEventSink?.Dispose(); }   // flushes stdout; does not close it (writer not owned)
            catch { /* swallow */ }
            // Restore the real stdout that --reporter=json redirected away from target-body writes.
            if (savedConsoleOut is not null)
            {
                try { Console.SetOut(savedConsoleOut); }
                catch { /* swallow */ }
            }
        }
    }

    /// <summary>
    /// Resolve the target for the `#0b` NDJSON event stream: the <c>--events &lt;path&gt;</c>
    /// / <c>--events=&lt;path&gt;</c> flag wins, otherwise the <c>TAMP_EVENTS</c> env var.
    /// Returns <see langword="null"/> when neither is set. A cheap pre-scan (mirrors
    /// <see cref="IsListOnlyInvocation"/>) so <see cref="ParseInvocation"/>'s shape is
    /// untouched; the flag value is already skipped from target parsing by the
    /// unknown-flag handling in <see cref="ParseInvocation"/>.
    /// </summary>
    internal static string? ResolveEventsTarget(string[] args, Func<string, string?> getEnv)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--events=", StringComparison.Ordinal))
            {
                var v = a["--events=".Length..];
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            else if (a == "--events" && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                var v = args[i + 1];
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        var env = getEnv("TAMP_EVENTS");
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    /// <summary>Resolve the #17 would-skip cache advisory opt-in: <c>--cache-advice</c> flag or truthy <c>TAMP_CACHE_ADVICE</c>. Default off.</summary>
    internal static bool ResolveCacheAdvice(string[] args, Func<string, string?> getEnv)
    {
        if (args.Contains("--cache-advice")) return true;
        var env = getEnv("TAMP_CACHE_ADVICE")?.Trim();
        return !string.IsNullOrEmpty(env) && env is not "0" && !env.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resolve the #23 per-target log capture opt-in: <c>--capture-logs</c> flag or truthy <c>TAMP_CAPTURE_LOGS</c>. Default off.</summary>
    internal static bool ResolveCaptureLogs(string[] args, Func<string, string?> getEnv)
    {
        if (args.Contains("--capture-logs")) return true;
        var env = getEnv("TAMP_CAPTURE_LOGS")?.Trim();
        return !string.IsNullOrEmpty(env) && env is not "0" && !env.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resolve the #16 <c>--rule &lt;id&gt;</c> / <c>--rule=&lt;id&gt;</c> filters (repeatable). Cheap pre-scan; <see cref="ParseInvocation"/> untouched.</summary>
    internal static IReadOnlyList<string> ResolveRuleFilters(string[] args)
    {
        var rules = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--rule=", StringComparison.Ordinal))
            {
                var v = a["--rule=".Length..];
                if (!string.IsNullOrWhiteSpace(v)) rules.Add(v);
            }
            else if (a == "--rule" && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                var v = args[i + 1];
                if (!string.IsNullOrWhiteSpace(v)) rules.Add(v);
            }
        }
        return rules;
    }

    /// <summary>
    /// Resolve the #15 downstream slice root: <c>--from X</c> / <c>--from=X</c>
    /// (alias <c>--downstream</c>). Returns X or null. A cheap pre-scan so
    /// <see cref="ParseInvocation"/> is untouched (the flag's value is already skipped
    /// from target parsing by the unknown-flag handling).
    /// </summary>
    internal static string? ResolveFromTarget(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            foreach (var flag in new[] { "--from", "--downstream" })
            {
                if (a.StartsWith(flag + "=", StringComparison.Ordinal))
                {
                    var v = a[(flag.Length + 1)..];
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
                else if (a == flag && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    var v = args[i + 1];
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Resolve the #20 capability enforcement mode + elevation. Mode: <c>--enforce=agent</c> /
    /// <c>--enforce agent</c> / <c>TAMP_CAPABILITY_MODE=agent</c> → <see cref="CapabilityMode.Agent"/>,
    /// else <see cref="CapabilityMode.Off"/> (default). Elevation: <c>--allow-side-effects</c> /
    /// a truthy <c>TAMP_ALLOW_SIDE_EFFECTS</c>. A cheap pre-scan, so <see cref="ParseInvocation"/> is untouched.
    /// </summary>
    internal static (CapabilityMode Mode, bool AllowSideEffects) ResolveCapabilityMode(string[] args, Func<string, string?> getEnv)
    {
        var mode = CapabilityMode.Off;
        var allow = false;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--enforce" || a.StartsWith("--enforce=", StringComparison.Ordinal))
            {
                // #70: a safety flag must fail CLOSED, not open. A misspelled value (or a bare
                // --enforce) is a hard error, never a silent Off that leaves side-effects unguarded.
                var v = a.StartsWith("--enforce=", StringComparison.Ordinal)
                    ? a["--enforce=".Length..].Trim()
                    : (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i].Trim() : null);
                mode = v?.ToLowerInvariant() switch
                {
                    "agent" => CapabilityMode.Agent,
                    "off" => CapabilityMode.Off,
                    _ => throw new InvalidOperationException(
                        $"Unknown --enforce value '{v}'. Use '--enforce=agent' (or '--enforce=off', the default)."),
                };
            }
            else if (a == "--allow-side-effects")
            {
                allow = true;
            }
        }
        if (string.Equals(getEnv("TAMP_CAPABILITY_MODE")?.Trim(), "agent", StringComparison.OrdinalIgnoreCase))
            mode = CapabilityMode.Agent;
        var envAllow = getEnv("TAMP_ALLOW_SIDE_EFFECTS")?.Trim();
        if (!string.IsNullOrEmpty(envAllow) && envAllow is not "0" && !envAllow.Equals("false", StringComparison.OrdinalIgnoreCase))
            allow = true;
        return (mode, allow);
    }

    /// <summary>True when the invocation is a help request (<c>--help</c> / <c>-h</c> / <c>-?</c>).</summary>
    internal static bool IsHelpInvocation(string[] args)
        => args.Any(a => a is "--help" or "-h" or "-?" or "/?");

    /// <summary>Print usage — recognized flags plus callable targets — and run nothing (#70).</summary>
    private static void PrintHelp(IReadOnlyDictionary<string, TargetSpec> targets)
    {
        PrintBanner(Console.Out);
        var w = Console.Out;
        w.WriteLine("Usage: dotnet <build> [targets...] [flags]");
        w.WriteLine();
        w.WriteLine("With no target, the .Default()-marked target (or a target named Default/Ci) runs.");
        w.WriteLine();
        w.WriteLine("Flags:");
        w.WriteLine("  --list [--all]           List callable targets (add --format json for machine output).");
        w.WriteLine("  --list-tree              List targets as a dependency tree.");
        w.WriteLine("  --plan [--format json]   Show the resolved execution order; run nothing.");
        w.WriteLine("  --dry-run                Print the command plan without executing.");
        w.WriteLine("  --from <target>          Run <target> plus its downstream dependents.");
        w.WriteLine("  --skip <target>          Skip a target; --skip-deps runs only the named target.");
        w.WriteLine("  --rule <id>              Scope diagnostics to rule id(s) (repeatable).");
        w.WriteLine("  --events <path>          Write the canonical NDJSON event stream to a file.");
        w.WriteLine("  --reporter json|text     Machine (canonical NDJSON to stdout) vs human output.");
        w.WriteLine("  --enforce agent          Deny side-effectful work by default (--allow-side-effects to elevate).");
        w.WriteLine("  --capture-logs           Tee per-target output to redacted .tamp/logs.");
        w.WriteLine("  --cache-advice           Report would-skip advisories for unchanged inputs.");
        w.WriteLine("  --verbosity <level>      quiet | minimal | normal | verbose | diagnostic.");
        w.WriteLine("  --help, -h               Show this help.");
        w.WriteLine();
        w.WriteLine("Any other `--name value` binds a [Parameter] declared on the build.");
        w.WriteLine();
        PrintTargetList(targets, tree: false, showAll: false);
    }

    /// <summary>Parse the build invocation: zero-or-more target names plus mode flags.</summary>
    /// <remarks>
    /// All non-flag tokens are target names; the executor runs them as a
    /// deduped invoked set. Flags <c>--dry-run</c>, <c>--plan</c>,
    /// <c>--list</c>, <c>--list-tree</c> control mode. If no targets are
    /// given, the build defaults to a target literally named <c>Default</c>
    /// or <c>Ci</c> if present.
    /// </remarks>
    internal static (ExecutionMode, IReadOnlyList<string>, ListMode, bool ShowAll, LogLevel Verbosity, IReadOnlySet<string> SkipTargets, bool SkipDeps, OutputFormat Format, ReporterKind Reporter) ParseInvocation(
        string[] args, IReadOnlyDictionary<string, TargetSpec> targets, IReadOnlySet<string>? parameterKeys = null)
    {
        parameterKeys ??= new HashSet<string>(StringComparer.Ordinal);
        var mode = ExecutionMode.Run;
        var listMode = ListMode.None;
        var showAll = false;
        var verbosity = LogLevel.Info;
        var targetNames = new List<string>();
        var skipTargets = new HashSet<string>(StringComparer.Ordinal);
        var skipDeps = false;
        var format = OutputFormat.Text;
        var reporterKind = ReporterKind.Text;
        var skipNextValue = false;
        var unknownFlags = new List<string>();   // #70: fail closed on typos / misspelled flags

        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            if (skipNextValue) { skipNextValue = false; continue; }

            if (raw.StartsWith("--", StringComparison.Ordinal))
            {
                var rest = raw[2..];
                var key = rest;
                string? inlineValue = null;
                var eq = key.IndexOf('=');
                if (eq >= 0) { inlineValue = key[(eq + 1)..]; key = key[..eq]; }
                switch (key)
                {
                    case "dry-run": mode = ExecutionMode.DryRun; break;
                    case "plan": mode = ExecutionMode.Plan; break;
                    case "list": listMode = ListMode.Flat; break;
                    case "list-tree": listMode = ListMode.Tree; break;
                    case "all": showAll = true; break;
                    case "verbosity":
                        var verbValue = inlineValue ?? (i + 1 < args.Length ? args[++i] : null);
                        if (verbValue is not null) verbosity = ParseVerbosity(verbValue);
                        break;
                    case "quiet": verbosity = LogLevel.Error; break;
                    case "verbose": verbosity = LogLevel.Debug; break;
                    case "diagnostic": verbosity = LogLevel.Trace; break;
                    case "skip":
                        var skipValue = inlineValue ?? (i + 1 < args.Length ? args[++i] : null);
                        if (string.IsNullOrWhiteSpace(skipValue))
                            throw new InvalidOperationException("--skip requires a target name (e.g. `--skip StampVersion`).");
                        skipTargets.Add(skipValue!);
                        break;
                    case "skip-deps":
                        skipDeps = true;
                        break;
                    // Valueless pre-scan / mode flags (resolved before ParseInvocation, or handled
                    // elsewhere): list them here as no-ops so they don't fall into the default
                    // parameter-binding case and swallow a following target name (e.g.
                    // `tamp --capture-logs Compile`) — or, post-#70, get flagged as unknown.
                    case "cache-advice":
                    case "capture-logs":
                    case "allow-side-effects":
                    case "help":
                        break;
                    // Valued pre-scan flags (resolved before ParseInvocation by ResolveEventsTarget /
                    // ResolveRuleFilters / ResolveFromTarget / ResolveCapabilityMode). No-op here, but
                    // consume the value token in the space form so it isn't taken as a target name.
                    case "events":
                    case "rule":
                    case "from":
                    case "downstream":
                    case "enforce":
                        if (inlineValue is null && i + 1 < args.Length
                            && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                            i++;
                        break;
                    case "format":
                        var formatValue = inlineValue ?? (i + 1 < args.Length ? args[++i] : null);
                        format = (formatValue?.Trim().ToLowerInvariant()) switch
                        {
                            "json" => OutputFormat.Json,
                            "text" or null => OutputFormat.Text,
                            _ => throw new InvalidOperationException(
                                $"Unknown --format value '{formatValue}'. Use 'text' or 'json'."),
                        };
                        break;
                    case "reporter":
                        var reporterValue = inlineValue ?? (i + 1 < args.Length ? args[++i] : null);
                        reporterKind = (reporterValue?.Trim().ToLowerInvariant()) switch
                        {
                            "json" => ReporterKind.Json,
                            "text" or null => ReporterKind.Text,
                            _ => throw new InvalidOperationException(
                                $"Unknown --reporter value '{reporterValue}'. Use 'text' or 'json'."),
                        };
                        break;
                    default:
                        // A --flag that matches a declared [Parameter] binds it (ParameterBinder
                        // does the actual binding later; here we just consume its value token).
                        // Anything else is an UNKNOWN flag — #70: collect it and fail closed after
                        // the loop rather than silently ignoring it. Silent-ignore let a typo run
                        // the default graph and, worse, a misspelled safety flag (--enforce) fail
                        // open. If the next arg is a value (not a flag), consume it either way so a
                        // typo's value isn't mistaken for a target name.
                        if (parameterKeys.Contains(key))
                        {
                            if (inlineValue is null && i + 1 < args.Length
                                && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                                skipNextValue = true;
                        }
                        else
                        {
                            unknownFlags.Add(raw);
                            if (inlineValue is null && i + 1 < args.Length
                                && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                                skipNextValue = true;
                        }
                        break;
                }
                continue;
            }
            targetNames.Add(raw);
        }

        // #70: fail closed on unrecognized flags — a typo must not silently run the default graph
        // (nor let a misspelled --enforce disable enforcement). Mirrors --from / capability default-deny.
        if (unknownFlags.Count > 0)
            throw new InvalidOperationException(
                $"Unknown argument{(unknownFlags.Count == 1 ? "" : "s")}: {string.Join(", ", unknownFlags)}. "
                + "Run with --help to see valid flags and targets.");

        if (targetNames.Count == 0 && listMode is ListMode.None)
        {
            // Precedence: .Default()-marked target wins, then name-based fallback for
            // back-compat (`Default` then `Ci`). Uniqueness of the marked default is
            // already enforced upstream in Execute<T>.
            var marked = targets.Values.FirstOrDefault(t => t.IsDefault);
            if (marked is not null) targetNames.Add(marked.Name);
            else if (targets.ContainsKey("Default")) targetNames.Add("Default");
            else if (targets.ContainsKey("Ci")) targetNames.Add("Ci");
        }

        return (mode, targetNames, listMode, showAll, verbosity, skipTargets, skipDeps, format, reporterKind);
    }

    /// <summary>Reporter selection for build event emission (TAM-140).</summary>
    internal enum ReporterKind { Text, Json }

    /// <summary>Maps user-facing verbosity strings to internal log levels.</summary>
    internal static LogLevel ParseVerbosity(string value) => value.Trim().ToLowerInvariant() switch
    {
        "quiet" or "q" => LogLevel.Error,
        "minimal" or "m" => LogLevel.Warn,
        "normal" or "n" => LogLevel.Info,
        "verbose" or "v" => LogLevel.Debug,
        "diagnostic" or "d" => LogLevel.Trace,
        _ => throw new InvalidOperationException(
            $"Unknown --verbosity value '{value}'. Use quiet, minimal, normal, verbose, or diagnostic."),
    };

    private static void PrintTargetList(IReadOnlyDictionary<string, TargetSpec> targets, bool tree, bool showAll)
    {
        if (targets.Count == 0)
        {
            Console.WriteLine("(no targets defined)");
            return;
        }

        // 1.1.0+: targets are listable + callable by default. `.Internal()` opts out —
        // hides from `--list` AND prevents direct CLI invocation. Pass `--all` to reveal
        // internal targets in the listing. The legacy `.TopLevel()` decorator is a no-op
        // kept for back-compat; it no longer affects listing.
        var visible = showAll
            ? targets.Values
            : targets.Values.Where(t => !t.IsInternal);

        var sorted = visible.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        if (sorted.Count == 0)
        {
            Console.WriteLine("(every target is marked Internal; pass --all to show)");
            return;
        }

        foreach (var t in sorted)
        {
            var phase = t.Phase == Phase.None ? string.Empty : $" [{t.Phase}]";
            var desc = string.IsNullOrEmpty(t.Description) ? string.Empty : $"  — {t.Description}";
            var marker = (showAll && t.IsInternal) ? " (internal)" : string.Empty;
            Console.WriteLine($"{t.Name}{phase}{marker}{desc}");
            if (tree && t.Dependencies.Count > 0)
                foreach (var dep in t.Dependencies)
                    Console.WriteLine($"    depends on: {dep}");
        }

        if (!showAll)
        {
            var hidden = targets.Values.Count(t => t.IsInternal);
            if (hidden > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"({hidden} internal target{(hidden == 1 ? "" : "s")} hidden; pass --all to show)");
            }
        }
    }

    internal enum ListMode { None, Flat, Tree }

    /// <summary>
    /// Reflect over <paramref name="build"/>, materialise every
    /// <see cref="Target"/>-typed property into a frozen
    /// <see cref="TargetSpec"/>, and return them keyed by name.
    /// </summary>
    internal static IReadOnlyDictionary<string, TargetSpec> CollectTargets(TampBuild build)
    {
        if (build is null) throw new ArgumentNullException(nameof(build));

        var result = new Dictionary<string, TargetSpec>(StringComparer.Ordinal);
        var type = build.GetType();

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // Pass 1: build a map from each Target property's compiled lambda method
        // to its property name. This powers the params Target[] overloads on
        // DependsOn/After/Before/Triggers/TriggeredBy/OnFailureOf (1.3.0+).
        // CallerArgumentExpression can't capture per-element source for params,
        // so we resolve names via the underlying MethodInfo — stable across
        // property-getter invocations because the lambda body compiles to a
        // single method.
        var methodMap = new Dictionary<MethodInfo, string>();
        var targetProperties = new List<(System.Reflection.PropertyInfo Prop, Target Delegate)>();
        foreach (var prop in type.GetProperties(flags))
        {
            if (prop.PropertyType != typeof(Target)) continue;
            if (prop.GetIndexParameters().Length > 0) continue;

            var del = (Target?)prop.GetValue(build);
            if (del is null)
                throw new InvalidOperationException(
                    $"Target property '{type.FullName}.{prop.Name}' returned null.");

            // Same lambda compiles to the same MethodInfo across invocations; if a
            // future build class somehow shares a method across properties, the
            // first wins and subsequent overlap is ignored.
            if (!methodMap.ContainsKey(del.Method))
                methodMap[del.Method] = prop.Name;
            targetProperties.Add((prop, del));
        }

        // Pass 2: invoke each property's delegate against a TargetDefinition
        // primed with the method map, so params Target[] calls inside the body
        // can resolve their references.
        foreach (var (prop, del) in targetProperties)
        {
            var def = new TargetDefinition(methodMap);
            del(def);
            var spec = def.Build(prop.Name);
            if (result.ContainsKey(spec.Name))
                throw new InvalidOperationException(
                    $"Duplicate target name '{spec.Name}' in {type.FullName}.");
            result[spec.Name] = spec;
        }

        return result;
    }

    /// <summary>
    /// Walk the build's fields and properties for <see cref="BuildReporterAttribute"/>
    /// markers; return the non-null <see cref="IBuildReporter"/>-typed values for
    /// fan-out via <see cref="CompositeBuildReporter"/> (TAM-230). Null members
    /// are silently skipped — adopters often guard their reporter construction on
    /// "is the env var set?" and a null result means "this reporter is intentionally
    /// not configured for this build".
    /// </summary>
    internal static IReadOnlyList<IBuildReporter> CollectBuildReporters(TampBuild build)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = build.GetType();
        var collected = new List<IBuildReporter>();

        foreach (var f in type.GetFields(flags))
        {
            if (f.GetCustomAttribute<BuildReporterAttribute>(inherit: true) is null) continue;
            if (!typeof(IBuildReporter).IsAssignableFrom(f.FieldType))
                throw new InvalidOperationException(
                    $"[BuildReporter] on field '{type.Name}.{f.Name}' requires an IBuildReporter-typed field; got {f.FieldType.Name}.");
            if (f.GetValue(build) is IBuildReporter r) collected.Add(r);
        }

        foreach (var p in type.GetProperties(flags))
        {
            if (p.GetCustomAttribute<BuildReporterAttribute>(inherit: true) is null) continue;
            if (!typeof(IBuildReporter).IsAssignableFrom(p.PropertyType))
                throw new InvalidOperationException(
                    $"[BuildReporter] on property '{type.Name}.{p.Name}' requires an IBuildReporter-typed property; got {p.PropertyType.Name}.");
            if (p.GetIndexParameters().Length > 0) continue;
            if (p.GetValue(build) is IBuildReporter r) collected.Add(r);
        }

        return collected;
    }
}
