namespace Tamp.NetCli.V11;

public sealed class DotNetPackSettings : DotNetSettingsBase
{
    public Configuration? Configuration { get; set; }
    public bool NoBuild { get; set; }
    public bool NoRestore { get; set; }
    public bool NoDependencies { get; set; }
    public string? Output { get; set; }
    public string? VersionSuffix { get; set; }
    public bool IncludeSymbols { get; set; }
    public bool IncludeSource { get; set; }
    public bool Serviceable { get; set; }
    public string? Runtime { get; set; }
    public Dictionary<string, string> Properties { get; } = new();

    public DotNetPackSettings SetProject(string? project) { Project = project; return this; }
    public DotNetPackSettings SetConfiguration(Configuration c) { Configuration = c; return this; }
    public DotNetPackSettings SetNoBuild(bool v) { NoBuild = v; return this; }
    public DotNetPackSettings SetNoRestore(bool v) { NoRestore = v; return this; }
    public DotNetPackSettings SetNoDependencies(bool v) { NoDependencies = v; return this; }
    public DotNetPackSettings SetOutput(string? path) { Output = path; return this; }
    public DotNetPackSettings SetVersionSuffix(string? suffix) { VersionSuffix = suffix; return this; }
    public DotNetPackSettings SetIncludeSymbols(bool v) { IncludeSymbols = v; return this; }
    public DotNetPackSettings SetIncludeSource(bool v) { IncludeSource = v; return this; }
    public DotNetPackSettings SetServiceable(bool v) { Serviceable = v; return this; }
    public DotNetPackSettings SetRuntime(string? runtime) { Runtime = runtime; return this; }
    public DotNetPackSettings SetProperty(string name, string value) { Properties[name] = value; return this; }
    public DotNetPackSettings SetVerbosity(DotNetVerbosity v) { Verbosity = v; return this; }
    public DotNetPackSettings SetWorkingDirectory(string? cwd) { WorkingDirectory = cwd; return this; }

    protected override IEnumerable<string> BuildVerbArguments()
    {
        yield return "pack";
        if (!string.IsNullOrEmpty(Project)) yield return Project!;
        if (ConfigurationToken(Configuration) is { } cfg) { yield return "--configuration"; yield return cfg; }
        if (NoBuild) yield return "--no-build";
        if (NoRestore) yield return "--no-restore";
        if (NoDependencies) yield return "--no-dependencies";
        if (!string.IsNullOrEmpty(Output)) { yield return "--output"; yield return Output!; }
        if (!string.IsNullOrEmpty(VersionSuffix)) { yield return "--version-suffix"; yield return VersionSuffix!; }
        if (IncludeSymbols) yield return "--include-symbols";
        if (IncludeSource) yield return "--include-source";
        if (Serviceable) yield return "--serviceable";
        if (!string.IsNullOrEmpty(Runtime)) { yield return "--runtime"; yield return Runtime!; }
        foreach (var (k, v) in Properties)
            yield return $"-p:{k}={v}";
    }
}
