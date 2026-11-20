namespace Tamp.NetCli.V11;

public sealed class DotNetBuildSettings : DotNetSettingsBase
{
    public Configuration? Configuration { get; set; }
    public bool NoRestore { get; set; }
    public bool NoIncremental { get; set; }
    public bool NoDependencies { get; set; }
    public string? Output { get; set; }
    public string? Runtime { get; set; }
    public string? Framework { get; set; }
    public string? VersionSuffix { get; set; }
    public Dictionary<string, string> Properties { get; } = new();

    public DotNetBuildSettings SetProject(string? project) { Project = project; return this; }
    public DotNetBuildSettings SetConfiguration(Configuration c) { Configuration = c; return this; }
    public DotNetBuildSettings SetNoRestore(bool v) { NoRestore = v; return this; }
    public DotNetBuildSettings SetNoIncremental(bool v) { NoIncremental = v; return this; }
    public DotNetBuildSettings SetNoDependencies(bool v) { NoDependencies = v; return this; }
    public DotNetBuildSettings SetOutput(string? path) { Output = path; return this; }
    public DotNetBuildSettings SetRuntime(string? runtime) { Runtime = runtime; return this; }
    public DotNetBuildSettings SetFramework(string? tfm) { Framework = tfm; return this; }
    public DotNetBuildSettings SetVersionSuffix(string? suffix) { VersionSuffix = suffix; return this; }
    public DotNetBuildSettings SetProperty(string name, string value) { Properties[name] = value; return this; }
    public DotNetBuildSettings SetVerbosity(DotNetVerbosity v) { Verbosity = v; return this; }
    public DotNetBuildSettings SetWorkingDirectory(string? cwd) { WorkingDirectory = cwd; return this; }

    protected override IEnumerable<string> BuildVerbArguments()
    {
        yield return "build";
        if (!string.IsNullOrEmpty(Project)) yield return Project!;
        if (ConfigurationToken(Configuration) is { } cfg) { yield return "--configuration"; yield return cfg; }
        if (NoRestore) yield return "--no-restore";
        if (NoIncremental) yield return "--no-incremental";
        if (NoDependencies) yield return "--no-dependencies";
        if (!string.IsNullOrEmpty(Output)) { yield return "--output"; yield return Output!; }
        if (!string.IsNullOrEmpty(Runtime)) { yield return "--runtime"; yield return Runtime!; }
        if (!string.IsNullOrEmpty(Framework)) { yield return "--framework"; yield return Framework!; }
        if (!string.IsNullOrEmpty(VersionSuffix)) { yield return "--version-suffix"; yield return VersionSuffix!; }
        foreach (var (k, v) in Properties)
            yield return $"-p:{k}={v}";
    }
}
