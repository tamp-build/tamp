namespace Tamp.DotNetCoverage.V18;

/// <summary>
/// Wrapper for Microsoft's <c>dotnet-coverage</c> tool (current major
/// v17). Two verbs are surfaced: <c>Collect</c> wraps a child
/// command with coverage collection, and <c>Merge</c> combines
/// (and optionally converts the format of) collected coverage files.
/// </summary>
/// <remarks>
/// Resolve the tool via <see cref="NuGetPackageAttribute"/>:
/// <code>
/// [NuGetPackage("dotnet-coverage", Version = "17.5.0")] readonly Tool Coverage;
/// </code>
/// or hand-construct from a known path. The tool is published as a
/// .NET global tool — the <c>[NuGetPackage]</c> attribute installs and
/// caches it on first use.
/// <para>
/// Sonar integration recipe: Collect into binary <c>.coverage</c>,
/// Merge with <c>OutputFormat = Cobertura</c> to convert, then point
/// <c>SonarScanner</c>'s <c>sonar.coverageReportPaths</c> at the
/// resulting cobertura XML.
/// </para>
/// </remarks>
public static class DotNetCoverage
{
    /// <summary>
    /// Run <paramref name="inner"/> under <c>dotnet-coverage collect</c>.
    /// The wrapped command's executable, arguments, working directory,
    /// environment, and registered secrets are preserved; output flags
    /// for the coverage tool are layered on top.
    /// </summary>
    public static CommandPlan Collect(Tool tool, CommandPlan inner, Action<DotNetCoverageCollectSettings>? configure = null)
    {
        if (tool is null) throw new ArgumentNullException(nameof(tool));
        if (inner is null) throw new ArgumentNullException(nameof(inner));
        var s = new DotNetCoverageCollectSettings();
        configure?.Invoke(s);
        return BuildCollectPlan(tool, inner, s);
    }

    /// <summary>
    /// Object-init equivalent of <see cref="Collect(Tool, CommandPlan, Action{DotNetCoverageCollectSettings}?)"/>
    /// (TAM-161). Both shapes produce identical CommandPlans.
    /// </summary>
    public static CommandPlan Collect(Tool tool, CommandPlan inner, DotNetCoverageCollectSettings settings)
    {
        if (tool is null) throw new ArgumentNullException(nameof(tool));
        if (inner is null) throw new ArgumentNullException(nameof(inner));
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        return BuildCollectPlan(tool, inner, settings);
    }

    private static CommandPlan BuildCollectPlan(Tool tool, CommandPlan inner, DotNetCoverageCollectSettings s)
    {
        var args = new List<string> { "collect" };
        if (!string.IsNullOrEmpty(s.SessionId)) { args.Add("--session-id"); args.Add(s.SessionId!); }
        if (!string.IsNullOrEmpty(s.Settings)) { args.Add("--settings"); args.Add(s.Settings!); }
        foreach (var f in s.IncludeFiles) { args.Add("--include-files"); args.Add(f); }
        if (!string.IsNullOrEmpty(s.Output)) { args.Add("--output"); args.Add(s.Output!); }
        if (s.OutputFormat is { } fmt) { args.Add("--output-format"); args.Add(fmt.ToFlagValue()); }
        if (!string.IsNullOrEmpty(s.LogFile)) { args.Add("--log-file"); args.Add(s.LogFile!); }
        if (s.LogLevel is { } ll) { args.Add("--log-level"); args.Add(ll.ToString()); }
        if (s.DisableConsoleOutput) args.Add("--disable-console-output");
        if (s.NoLogo) args.Add("--nologo");

        // The wrapped command + its args follow the coverage flags.
        // dotnet-coverage's `collect` accepts `<command> <args>...` as
        // the trailing positional, so we don't need to escape into a
        // single quoted string — the args are passed individually.
        args.Add(inner.Executable);
        foreach (var a in inner.Arguments) args.Add(a);

        // Inner plan's environment + working directory + secrets propagate.
        // The collect wrapper layers its own env vars on top of the inner's.
        var env = new Dictionary<string, string>(inner.Environment);
        foreach (var (k, v) in s.EnvironmentVariables) env[k] = v;

        return new CommandPlan
        {
            Executable = tool.Executable.Value,
            Arguments = args,
            Environment = env,
            WorkingDirectory = s.WorkingDirectory ?? inner.WorkingDirectory ?? tool.WorkingDirectory,
            Secrets = inner.Secrets,
            StandardInput = inner.StandardInput,
        };
    }

    /// <summary>
    /// Merge multiple coverage reports into one, optionally converting
    /// the output format. Doubles as the format-conversion path:
    /// <c>dotnet-coverage</c> doesn't have a separate <c>convert</c>
    /// verb; pass a single input + a different <see cref="DotNetCoverageMergeSettings.OutputFormat"/>
    /// to convert.
    /// </summary>
    public static CommandPlan Merge(Tool tool, Action<DotNetCoverageMergeSettings> configure)
    {
        if (tool is null) throw new ArgumentNullException(nameof(tool));
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        var s = new DotNetCoverageMergeSettings();
        configure(s);
        return BuildMergePlan(tool, s);
    }

    /// <summary>
    /// Object-init equivalent of <see cref="Merge(Tool, Action{DotNetCoverageMergeSettings})"/>
    /// (TAM-161). Both shapes produce identical CommandPlans.
    /// </summary>
    public static CommandPlan Merge(Tool tool, DotNetCoverageMergeSettings settings)
    {
        if (tool is null) throw new ArgumentNullException(nameof(tool));
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        return BuildMergePlan(tool, settings);
    }

    private static CommandPlan BuildMergePlan(Tool tool, DotNetCoverageMergeSettings s)
    {
        if (s.Inputs.Count == 0)
            throw new InvalidOperationException("DotNetCoverage.Merge requires at least one input file.");

        var args = new List<string> { "merge" };
        if (!string.IsNullOrEmpty(s.Output)) { args.Add("--output"); args.Add(s.Output!); }
        if (s.OutputFormat is { } fmt) { args.Add("--output-format"); args.Add(fmt.ToFlagValue()); }
        if (s.RemoveInputFiles) args.Add("--remove-input-files");
        if (!string.IsNullOrEmpty(s.LogFile)) { args.Add("--log-file"); args.Add(s.LogFile!); }
        if (s.LogLevel is { } ll) { args.Add("--log-level"); args.Add(ll.ToString()); }
        if (s.DisableConsoleOutput) args.Add("--disable-console-output");
        if (s.NoLogo) args.Add("--nologo");
        // Inputs are positional, last.
        foreach (var input in s.Inputs) args.Add(input);

        return new CommandPlan
        {
            Executable = tool.Executable.Value,
            Arguments = args,
            Environment = new Dictionary<string, string>(s.EnvironmentVariables),
            WorkingDirectory = s.WorkingDirectory ?? tool.WorkingDirectory,
        };
    }
}
