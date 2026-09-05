namespace Tamp.NetCli.V8;

/// <summary>Crash dump sizes accepted by VSTest's <c>--blame-crash-dump-type</c>.</summary>
public enum DotNetCrashDumpType
{
    /// <summary>Collect a small crash dump.</summary>
    Mini,
    /// <summary>Collect a full crash dump.</summary>
    Full,
}

public sealed class DotNetTestSettings : DotNetSettingsBase
{
    public Configuration? Configuration { get; set; }
    public bool NoBuild { get; set; }
    public bool NoRestore { get; set; }
    public string? Filter { get; set; }
    public List<string> Loggers { get; } = [];
    public List<string> DataCollectors { get; } = [];
    public string? ResultsDirectory { get; set; }
    public string? Settings { get; set; }
    public string? Runtime { get; set; }
    public string? Framework { get; set; }
    /// <summary>Collect crash diagnostics when the VSTest test host exits unexpectedly.</summary>
    public bool BlameCrash { get; set; }

    private DotNetCrashDumpType? _blameCrashDumpType;
    /// <summary>Crash dump size. Null uses the CLI default; specifying a size implies crash diagnostics.</summary>
    public DotNetCrashDumpType? BlameCrashDumpType
    {
        get => _blameCrashDumpType;
        set
        {
            if (value is { } type && !Enum.IsDefined(type))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported crash dump type.");
            _blameCrashDumpType = value;
        }
    }

    public bool BlameHang { get; set; }
    public TimeSpan? BlameHangTimeout { get; set; }
    public Dictionary<string, string> Properties { get; } = new();

    /// <summary>
    /// When true (default), and <see cref="Project"/> targets a <c>.sln</c>/<c>.slnx</c>, any TRX
    /// logger string with a static <c>LogFileName=foo.trx</c> is rewritten to
    /// <c>LogFilePrefix=foo</c> so VSTest auto-disambiguates per assembly. Without this rewrite,
    /// solution-mode runs overwrite the TRX once per test project — only the last assembly's
    /// results survive. Set to false to preserve the literal logger string (legacy behavior).
    /// </summary>
    public bool AutoExpandTrxForSolution { get; set; } = true;

    public DotNetTestSettings SetProject(string? project) { Project = project; return this; }
    public DotNetTestSettings SetConfiguration(Configuration c) { Configuration = c; return this; }
    public DotNetTestSettings SetNoBuild(bool v) { NoBuild = v; return this; }
    public DotNetTestSettings SetNoRestore(bool v) { NoRestore = v; return this; }
    public DotNetTestSettings SetFilter(string? filter) { Filter = filter; return this; }
    public DotNetTestSettings AddLogger(string logger) { Loggers.Add(logger); return this; }
    public DotNetTestSettings AddDataCollector(string name) { DataCollectors.Add(name); return this; }
    public DotNetTestSettings SetResultsDirectory(string? path) { ResultsDirectory = path; return this; }
    public DotNetTestSettings SetSettings(string? path) { Settings = path; return this; }
    public DotNetTestSettings SetRuntime(string? runtime) { Runtime = runtime; return this; }
    public DotNetTestSettings SetFramework(string? tfm) { Framework = tfm; return this; }
    /// <summary>Enable or disable VSTest crash diagnostics.</summary>
    public DotNetTestSettings SetBlameCrash(bool v) { BlameCrash = v; return this; }
    /// <summary>Set the crash dump size, or null to omit the dump-type flag.</summary>
    public DotNetTestSettings SetBlameCrashDumpType(DotNetCrashDumpType? v) { BlameCrashDumpType = v; return this; }
    public DotNetTestSettings SetBlameHang(bool v) { BlameHang = v; return this; }
    public DotNetTestSettings SetBlameHangTimeout(TimeSpan? t) { BlameHangTimeout = t; return this; }
    public DotNetTestSettings SetProperty(string name, string value) { Properties[name] = value; return this; }
    public DotNetTestSettings SetVerbosity(DotNetVerbosity v) { Verbosity = v; return this; }
    public DotNetTestSettings SetWorkingDirectory(string? cwd) { WorkingDirectory = cwd; return this; }
    public DotNetTestSettings SetAutoExpandTrxForSolution(bool v = true) { AutoExpandTrxForSolution = v; return this; }

    protected override IEnumerable<string> BuildVerbArguments()
    {
        yield return "test";
        if (!string.IsNullOrEmpty(Project)) yield return Project!;
        if (ConfigurationToken(Configuration) is { } cfg) { yield return "--configuration"; yield return cfg; }
        if (NoBuild) yield return "--no-build";
        if (NoRestore) yield return "--no-restore";
        if (!string.IsNullOrEmpty(Filter)) { yield return "--filter"; yield return Filter!; }
        foreach (var l in EmitLoggers()) { yield return "--logger"; yield return l; }
        foreach (var c in DataCollectors) { yield return "--collect"; yield return c; }
        if (!string.IsNullOrEmpty(ResultsDirectory)) { yield return "--results-directory"; yield return ResultsDirectory!; }
        if (!string.IsNullOrEmpty(Settings)) { yield return "--settings"; yield return Settings!; }
        if (!string.IsNullOrEmpty(Runtime)) { yield return "--runtime"; yield return Runtime!; }
        if (!string.IsNullOrEmpty(Framework)) { yield return "--framework"; yield return Framework!; }
        if (BlameCrash) yield return "--blame-crash";
        if (BlameCrashDumpType is { } dumpType)
        {
            yield return "--blame-crash-dump-type";
            yield return dumpType.ToString().ToLowerInvariant();
        }
        if (BlameHang) yield return "--blame-hang";
        if (BlameHangTimeout is { } t) { yield return "--blame-hang-timeout"; yield return $"{(int)t.TotalMilliseconds}ms"; }
        foreach (var (k, v) in Properties)
            yield return $"-p:{k}={v}";
    }

    private IEnumerable<string> EmitLoggers()
    {
        if (!AutoExpandTrxForSolution || !IsSolutionProject(Project))
            return Loggers;
        return Loggers.Select(RewriteTrxLoggerForSolution);
    }

    internal static bool IsSolutionProject(string? project)
    {
        if (string.IsNullOrEmpty(project)) return false;
        var ext = System.IO.Path.GetExtension(project);
        return string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When a TRX logger string carries a static <c>LogFileName=foo.trx</c>, rewrites it to
    /// <c>LogFilePrefix=foo</c>. Non-TRX loggers and TRX loggers without LogFileName pass through.
    /// </summary>
    internal static string RewriteTrxLoggerForSolution(string logger)
    {
        if (string.IsNullOrEmpty(logger)) return logger;
        var firstSep = logger.IndexOf(';');
        var prefix = firstSep < 0 ? logger : logger[..firstSep];
        if (!string.Equals(prefix.Trim(), "trx", StringComparison.OrdinalIgnoreCase))
            return logger;
        if (firstSep < 0) return logger;

        var segments = logger[(firstSep + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries);
        var rewrote = false;
        var rebuilt = new List<string> { prefix };
        foreach (var seg in segments)
        {
            var eq = seg.IndexOf('=');
            if (eq <= 0) { rebuilt.Add(seg); continue; }
            var key = seg[..eq].Trim();
            var value = seg[(eq + 1)..];
            if (string.Equals(key, "LogFileName", StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = value.EndsWith(".trx", StringComparison.OrdinalIgnoreCase)
                    ? value[..^4]
                    : value;
                rebuilt.Add($"LogFilePrefix={trimmed}");
                rewrote = true;
            }
            else
            {
                rebuilt.Add(seg);
            }
        }
        return rewrote ? string.Join(";", rebuilt) : logger;
    }
}
