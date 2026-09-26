namespace Tamp.DotNetCoverage.V18;

/// <summary>Log levels accepted by <c>dotnet-coverage</c>.</summary>
public enum CoverageLogLevel
{
    Error,
    Info,
    Verbose,
}

/// <summary>
/// Settings for <c>dotnet-coverage collect</c>. The wrapped command —
/// the process whose execution gets coverage-instrumented — is passed
/// to <c>DotNetCoverage.Collect</c> as a separate
/// <see cref="CommandPlan"/> argument, not via this settings object.
/// </summary>
public sealed class DotNetCoverageCollectSettings
{
    /// <summary>Output coverage file path.</summary>
    public string? Output { get; set; }

    /// <summary>Output format. Defaults to binary <c>coverage</c> if unset.</summary>
    public CoverageFormat? OutputFormat { get; set; }

    /// <summary>Code-coverage settings XML file (Visual Studio <c>.runsettings</c>-style).</summary>
    public string? Settings { get; set; }

    /// <summary>Coverage session ID. Auto-generated if unset.</summary>
    public string? SessionId { get; set; }

    /// <summary>Files to be statically instrumented (glob or path list).</summary>
    public List<string> IncludeFiles { get; } = [];

    /// <summary>Log file path (or directory ending in separator for per-process logs).</summary>
    public string? LogFile { get; set; }

    public CoverageLogLevel? LogLevel { get; set; }
    public bool DisableConsoleOutput { get; set; }

    /// <summary>Suppress the "Code Coverage" banner. Defaults true for clean CI logs.</summary>
    public bool NoLogo { get; set; } = true;

    /// <summary>Working directory for the spawned process.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Additional environment variables.</summary>
    public Dictionary<string, string> EnvironmentVariables { get; } = new();

    public DotNetCoverageCollectSettings SetOutput(string path) { Output = path; return this; }
    public DotNetCoverageCollectSettings SetOutputFormat(CoverageFormat format) { OutputFormat = format; return this; }
    public DotNetCoverageCollectSettings SetSettings(string? path) { Settings = path; return this; }
    public DotNetCoverageCollectSettings SetSessionId(string? id) { SessionId = id; return this; }
    public DotNetCoverageCollectSettings AddIncludeFile(string path) { IncludeFiles.Add(path); return this; }
    public DotNetCoverageCollectSettings SetLogFile(string? path) { LogFile = path; return this; }
    public DotNetCoverageCollectSettings SetLogLevel(CoverageLogLevel level) { LogLevel = level; return this; }
    public DotNetCoverageCollectSettings SetDisableConsoleOutput(bool v) { DisableConsoleOutput = v; return this; }
    public DotNetCoverageCollectSettings SetNoLogo(bool v) { NoLogo = v; return this; }
    public DotNetCoverageCollectSettings SetWorkingDirectory(string? cwd) { WorkingDirectory = cwd; return this; }
}
