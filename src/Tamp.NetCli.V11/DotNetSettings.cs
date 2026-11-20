namespace Tamp.NetCli.V11;

/// <summary>
/// Verbosity levels accepted by the .NET 11 SDK <c>--verbosity</c> flag.
/// </summary>
public enum DotNetVerbosity
{
    Quiet,
    Minimal,
    Normal,
    Detailed,
    Diagnostic,
}

/// <summary>
/// Common base for the per-command settings classes in this module.
/// Holds the shared knobs (project path, verbosity, environment) and
/// provides helpers for building the argument list.
/// </summary>
public abstract class DotNetSettingsBase
{
    /// <summary>
    /// Path to the project, solution, or directory the command targets.
    /// Null lets <c>dotnet</c> auto-discover from the current directory.
    /// </summary>
    public string? Project { get; set; }

    public DotNetVerbosity? Verbosity { get; set; }

    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Additional environment variables to set on the spawned process.
    /// </summary>
    public Dictionary<string, string> EnvironmentVariables { get; } = new();

    /// <summary>
    /// Subclasses build the per-verb argument list here, starting with
    /// the verb itself.
    /// </summary>
    protected abstract IEnumerable<string> BuildVerbArguments();

    /// <summary>
    /// Subclasses override when their plan should declare typed Secrets so
    /// the runner's redaction table covers their values in any logged
    /// output. Default: no secrets.
    /// </summary>
    protected virtual IReadOnlyList<Secret> BuildSecrets() => Array.Empty<Secret>();

    public CommandPlan ToCommandPlan()
    {
        var args = new List<string>(BuildVerbArguments());
        AddCommonArguments(args);
        var env = new Dictionary<string, string>(EnvironmentVariables)
        {
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        };
        return new CommandPlan
        {
            Executable = "dotnet",
            Arguments = args,
            Environment = env,
            WorkingDirectory = WorkingDirectory,
            Secrets = BuildSecrets(),
        };
    }

    private void AddCommonArguments(List<string> args)
    {
        if (Verbosity is { } v)
        {
            args.Add("--verbosity");
            args.Add(VerbosityFlag(v));
        }
    }

    private static string VerbosityFlag(DotNetVerbosity v) => v switch
    {
        DotNetVerbosity.Quiet => "quiet",
        DotNetVerbosity.Minimal => "minimal",
        DotNetVerbosity.Normal => "normal",
        DotNetVerbosity.Detailed => "detailed",
        DotNetVerbosity.Diagnostic => "diagnostic",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown verbosity."),
    };

    protected static string? ConfigurationToken(Configuration? c) => c switch
    {
        Configuration.Debug => "Debug",
        Configuration.Release => "Release",
        null => null,
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, "Unknown configuration."),
    };
}
