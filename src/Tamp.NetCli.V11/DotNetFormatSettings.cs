namespace Tamp.NetCli.V11;

/// <summary>
/// Severity threshold for <c>dotnet format</c> when running diagnostic
/// fixers (style + analyzer subcommands). Whitespace formatting ignores
/// this setting.
/// </summary>
public enum DotNetFormatSeverity
{
    /// <summary><c>info</c> — fix every issue at info level or higher.</summary>
    Info,
    /// <summary><c>warn</c> — fix issues at warning level or higher.</summary>
    Warn,
    /// <summary><c>error</c> — fix only errors.</summary>
    Error,
}

/// <summary>
/// Common knobs across every <c>dotnet format</c> subcommand. Holds the
/// flags <c>format</c>, <c>format whitespace</c>, <c>format style</c>,
/// and <c>format analyzers</c> all share: project / solution argument,
/// include / exclude path lists, restore behavior, the
/// <c>--verify-no-changes</c> CI gate, and report paths.
/// </summary>
public abstract class DotNetFormatBaseSettings : DotNetSettingsBase
{
    /// <summary>Skip the implicit <c>dotnet restore</c> before formatting. Maps to <c>--no-restore</c>.</summary>
    public bool NoRestore { get; set; }

    /// <summary>Exit non-zero if any file would be reformatted. The CI gate flag. Maps to <c>--verify-no-changes</c>.</summary>
    public bool VerifyNoChanges { get; set; }

    /// <summary>Relative paths to include in formatting. Joined with spaces and emitted as <c>--include &lt;list&gt;</c>.</summary>
    public List<string> Include { get; } = [];

    /// <summary>Relative paths to exclude from formatting. Joined with spaces and emitted as <c>--exclude &lt;list&gt;</c>.</summary>
    public List<string> Exclude { get; } = [];

    /// <summary>Format SDK-generated source files (typically excluded). Maps to <c>--include-generated</c>.</summary>
    public bool IncludeGenerated { get; set; }

    /// <summary>Path to a binary log file capturing project / solution load. Maps to <c>--binarylog</c>.</summary>
    public string? BinaryLog { get; set; }

    /// <summary>Directory to write the JSON report. Maps to <c>--report</c>.</summary>
    public string? Report { get; set; }

    protected IEnumerable<string> EmitFormatBaseArguments()
    {
        if (!string.IsNullOrEmpty(Project)) yield return Project!;
        if (NoRestore) yield return "--no-restore";
        if (VerifyNoChanges) yield return "--verify-no-changes";
        if (Include.Count > 0) { yield return "--include"; yield return string.Join(' ', Include); }
        if (Exclude.Count > 0) { yield return "--exclude"; yield return string.Join(' ', Exclude); }
        if (IncludeGenerated) yield return "--include-generated";
        if (!string.IsNullOrEmpty(BinaryLog)) { yield return "--binarylog"; yield return BinaryLog!; }
        if (!string.IsNullOrEmpty(Report)) { yield return "--report"; yield return Report!; }
    }
}

/// <summary>
/// Adds the diagnostic-fixer knobs that <c>format</c>, <c>format style</c>,
/// and <c>format analyzers</c> all support but <c>format whitespace</c>
/// does not (whitespace-only formatting has no diagnostics to filter).
/// </summary>
public abstract class DotNetFormatWithDiagnosticsBase : DotNetFormatBaseSettings
{
    /// <summary>Diagnostic ids to filter to (e.g. <c>IDE0005</c>, <c>CA1822</c>). Joined with spaces. Maps to <c>--diagnostics</c>.</summary>
    public List<string> DiagnosticIds { get; } = [];

    /// <summary>Diagnostic ids to ignore. Joined with spaces. Maps to <c>--exclude-diagnostics</c>.</summary>
    public List<string> ExcludeDiagnosticIds { get; } = [];

    /// <summary>Severity threshold for fixing. Maps to <c>--severity</c>.</summary>
    public DotNetFormatSeverity? Severity { get; set; }

    protected IEnumerable<string> EmitDiagnosticsArguments()
    {
        if (DiagnosticIds.Count > 0) { yield return "--diagnostics"; yield return string.Join(' ', DiagnosticIds); }
        if (ExcludeDiagnosticIds.Count > 0) { yield return "--exclude-diagnostics"; yield return string.Join(' ', ExcludeDiagnosticIds); }
        if (Severity is { } s) { yield return "--severity"; yield return SeverityToken(s); }
    }

    private static string SeverityToken(DotNetFormatSeverity s) => s switch
    {
        DotNetFormatSeverity.Info => "info",
        DotNetFormatSeverity.Warn => "warn",
        DotNetFormatSeverity.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, "Unknown severity."),
    };
}

/// <summary>Settings for <c>dotnet format</c> (runs whitespace + style + analyzers).</summary>
public sealed class DotNetFormatSettings : DotNetFormatWithDiagnosticsBase
{
    public DotNetFormatSettings SetProject(string? project) { Project = project; return this; }
    public DotNetFormatSettings SetNoRestore(bool v = true) { NoRestore = v; return this; }
    public DotNetFormatSettings SetVerifyNoChanges(bool v = true) { VerifyNoChanges = v; return this; }
    public DotNetFormatSettings AddInclude(string path) { Include.Add(path); return this; }
    public DotNetFormatSettings AddExclude(string path) { Exclude.Add(path); return this; }
    public DotNetFormatSettings SetIncludeGenerated(bool v = true) { IncludeGenerated = v; return this; }
    public DotNetFormatSettings SetBinaryLog(string? path) { BinaryLog = path; return this; }
    public DotNetFormatSettings SetReport(string? path) { Report = path; return this; }
    public DotNetFormatSettings AddDiagnosticId(string id) { DiagnosticIds.Add(id); return this; }
    public DotNetFormatSettings AddExcludeDiagnosticId(string id) { ExcludeDiagnosticIds.Add(id); return this; }
    public DotNetFormatSettings SetSeverity(DotNetFormatSeverity severity) { Severity = severity; return this; }
    public DotNetFormatSettings SetVerbosity(DotNetVerbosity v) { Verbosity = v; return this; }
    public DotNetFormatSettings SetWorkingDirectory(string? cwd) { WorkingDirectory = cwd; return this; }

    protected override IEnumerable<string> BuildVerbArguments()
    {
        yield return "format";
        foreach (var a in EmitFormatBaseArguments()) yield return a;
        foreach (var a in EmitDiagnosticsArguments()) yield return a;
    }
}

/// <summary>Settings for <c>dotnet format whitespace</c> (whitespace fixes only).</summary>
public sealed class DotNetFormatWhitespaceSettings : DotNetFormatBaseSettings
{
    /// <summary>Treat the project argument as a folder of loose files. Maps to <c>--folder</c>. Whitespace-only.</summary>
    public bool Folder { get; set; }

    public DotNetFormatWhitespaceSettings SetProject(string? project) { Project = project; return this; }
    public DotNetFormatWhitespaceSettings SetFolder(bool v = true) { Folder = v; return this; }
    public DotNetFormatWhitespaceSettings SetNoRestore(bool v = true) { NoRestore = v; return this; }
    public DotNetFormatWhitespaceSettings SetVerifyNoChanges(bool v = true) { VerifyNoChanges = v; return this; }
    public DotNetFormatWhitespaceSettings AddInclude(string path) { Include.Add(path); return this; }
    public DotNetFormatWhitespaceSettings AddExclude(string path) { Exclude.Add(path); return this; }
    public DotNetFormatWhitespaceSettings SetIncludeGenerated(bool v = true) { IncludeGenerated = v; return this; }
    public DotNetFormatWhitespaceSettings SetBinaryLog(string? path) { BinaryLog = path; return this; }
    public DotNetFormatWhitespaceSettings SetReport(string? path) { Report = path; return this; }
    public DotNetFormatWhitespaceSettings SetVerbosity(DotNetVerbosity v) { Verbosity = v; return this; }
    public DotNetFormatWhitespaceSettings SetWorkingDirectory(string? cwd) { WorkingDirectory = cwd; return this; }

    protected override IEnumerable<string> BuildVerbArguments()
    {
        yield return "format";
        yield return "whitespace";
        foreach (var a in EmitFormatBaseArguments()) yield return a;
        if (Folder) yield return "--folder";
    }
}

/// <summary>Settings for <c>dotnet format style</c> (code-style analyzers only).</summary>
public sealed class DotNetFormatStyleSettings : DotNetFormatWithDiagnosticsBase
{
    public DotNetFormatStyleSettings SetProject(string? project) { Project = project; return this; }
    public DotNetFormatStyleSettings SetNoRestore(bool v = true) { NoRestore = v; return this; }
    public DotNetFormatStyleSettings SetVerifyNoChanges(bool v = true) { VerifyNoChanges = v; return this; }
    public DotNetFormatStyleSettings AddInclude(string path) { Include.Add(path); return this; }
    public DotNetFormatStyleSettings AddExclude(string path) { Exclude.Add(path); return this; }
    public DotNetFormatStyleSettings SetIncludeGenerated(bool v = true) { IncludeGenerated = v; return this; }
    public DotNetFormatStyleSettings SetBinaryLog(string? path) { BinaryLog = path; return this; }
    public DotNetFormatStyleSettings SetReport(string? path) { Report = path; return this; }
    public DotNetFormatStyleSettings AddDiagnosticId(string id) { DiagnosticIds.Add(id); return this; }
    public DotNetFormatStyleSettings AddExcludeDiagnosticId(string id) { ExcludeDiagnosticIds.Add(id); return this; }
    public DotNetFormatStyleSettings SetSeverity(DotNetFormatSeverity severity) { Severity = severity; return this; }
    public DotNetFormatStyleSettings SetVerbosity(DotNetVerbosity v) { Verbosity = v; return this; }
    public DotNetFormatStyleSettings SetWorkingDirectory(string? cwd) { WorkingDirectory = cwd; return this; }

    protected override IEnumerable<string> BuildVerbArguments()
    {
        yield return "format";
        yield return "style";
        foreach (var a in EmitFormatBaseArguments()) yield return a;
        foreach (var a in EmitDiagnosticsArguments()) yield return a;
    }
}

/// <summary>Settings for <c>dotnet format analyzers</c> (3rd-party analyzers only).</summary>
public sealed class DotNetFormatAnalyzersSettings : DotNetFormatWithDiagnosticsBase
{
    public DotNetFormatAnalyzersSettings SetProject(string? project) { Project = project; return this; }
    public DotNetFormatAnalyzersSettings SetNoRestore(bool v = true) { NoRestore = v; return this; }
    public DotNetFormatAnalyzersSettings SetVerifyNoChanges(bool v = true) { VerifyNoChanges = v; return this; }
    public DotNetFormatAnalyzersSettings AddInclude(string path) { Include.Add(path); return this; }
    public DotNetFormatAnalyzersSettings AddExclude(string path) { Exclude.Add(path); return this; }
    public DotNetFormatAnalyzersSettings SetIncludeGenerated(bool v = true) { IncludeGenerated = v; return this; }
    public DotNetFormatAnalyzersSettings SetBinaryLog(string? path) { BinaryLog = path; return this; }
    public DotNetFormatAnalyzersSettings SetReport(string? path) { Report = path; return this; }
    public DotNetFormatAnalyzersSettings AddDiagnosticId(string id) { DiagnosticIds.Add(id); return this; }
    public DotNetFormatAnalyzersSettings AddExcludeDiagnosticId(string id) { ExcludeDiagnosticIds.Add(id); return this; }
    public DotNetFormatAnalyzersSettings SetSeverity(DotNetFormatSeverity severity) { Severity = severity; return this; }
    public DotNetFormatAnalyzersSettings SetVerbosity(DotNetVerbosity v) { Verbosity = v; return this; }
    public DotNetFormatAnalyzersSettings SetWorkingDirectory(string? cwd) { WorkingDirectory = cwd; return this; }

    protected override IEnumerable<string> BuildVerbArguments()
    {
        yield return "format";
        yield return "analyzers";
        foreach (var a in EmitFormatBaseArguments()) yield return a;
        foreach (var a in EmitDiagnosticsArguments()) yield return a;
    }
}
