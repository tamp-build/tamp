namespace Tamp.NetCli.V11;

/// <summary>
/// Settings for <c>dotnet clean</c> — removes <c>bin/</c> and <c>obj/</c> outputs
/// for the specified configuration (or all configurations if none is set).
/// </summary>
public sealed class DotNetCleanSettings : DotNetSettingsBase
{
    public Configuration? Configuration { get; set; }
    public string? Framework { get; set; }
    public string? Runtime { get; set; }
    public string? Output { get; set; }
    public bool NoLogo { get; set; }

    public DotNetCleanSettings SetProject(string? project) { Project = project; return this; }
    public DotNetCleanSettings SetConfiguration(Configuration c) { Configuration = c; return this; }
    public DotNetCleanSettings SetFramework(string? tfm) { Framework = tfm; return this; }
    public DotNetCleanSettings SetRuntime(string? runtime) { Runtime = runtime; return this; }
    public DotNetCleanSettings SetOutput(string? path) { Output = path; return this; }
    public DotNetCleanSettings SetNoLogo(bool v = true) { NoLogo = v; return this; }
    public DotNetCleanSettings SetVerbosity(DotNetVerbosity v) { Verbosity = v; return this; }
    public DotNetCleanSettings SetWorkingDirectory(string? cwd) { WorkingDirectory = cwd; return this; }

    protected override IEnumerable<string> BuildVerbArguments()
    {
        yield return "clean";
        if (!string.IsNullOrEmpty(Project)) yield return Project!;
        if (ConfigurationToken(Configuration) is { } cfg) { yield return "--configuration"; yield return cfg; }
        if (!string.IsNullOrEmpty(Framework)) { yield return "--framework"; yield return Framework!; }
        if (!string.IsNullOrEmpty(Runtime)) { yield return "--runtime"; yield return Runtime!; }
        if (!string.IsNullOrEmpty(Output)) { yield return "--output"; yield return Output!; }
        if (NoLogo) yield return "--nologo";
    }
}
