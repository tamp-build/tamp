namespace Tamp.NetCli.V11;

public sealed class DotNetPublishSettings : DotNetSettingsBase
{
    public Configuration? Configuration { get; set; }
    public bool NoBuild { get; set; }
    public bool NoRestore { get; set; }
    public bool NoDependencies { get; set; }
    public string? Output { get; set; }
    public string? Runtime { get; set; }
    public string? Framework { get; set; }
    public bool? SelfContained { get; set; }
    public bool PublishSingleFile { get; set; }
    public bool PublishTrimmed { get; set; }
    public bool PublishReadyToRun { get; set; }
    public string? VersionSuffix { get; set; }
    public Dictionary<string, string> Properties { get; } = new();

    public DotNetPublishSettings SetProject(string? project) { Project = project; return this; }
    public DotNetPublishSettings SetConfiguration(Configuration c) { Configuration = c; return this; }
    public DotNetPublishSettings SetNoBuild(bool v) { NoBuild = v; return this; }
    public DotNetPublishSettings SetNoRestore(bool v) { NoRestore = v; return this; }
    public DotNetPublishSettings SetNoDependencies(bool v) { NoDependencies = v; return this; }
    public DotNetPublishSettings SetOutput(string? path) { Output = path; return this; }
    public DotNetPublishSettings SetRuntime(string? runtime) { Runtime = runtime; return this; }
    public DotNetPublishSettings SetFramework(string? tfm) { Framework = tfm; return this; }
    public DotNetPublishSettings SetSelfContained(bool v) { SelfContained = v; return this; }
    public DotNetPublishSettings SetPublishSingleFile(bool v) { PublishSingleFile = v; return this; }
    public DotNetPublishSettings SetPublishTrimmed(bool v) { PublishTrimmed = v; return this; }
    public DotNetPublishSettings SetPublishReadyToRun(bool v) { PublishReadyToRun = v; return this; }
    public DotNetPublishSettings SetVersionSuffix(string? suffix) { VersionSuffix = suffix; return this; }
    public DotNetPublishSettings SetProperty(string name, string value) { Properties[name] = value; return this; }
    public DotNetPublishSettings SetVerbosity(DotNetVerbosity v) { Verbosity = v; return this; }
    public DotNetPublishSettings SetWorkingDirectory(string? cwd) { WorkingDirectory = cwd; return this; }

    protected override IEnumerable<string> BuildVerbArguments()
    {
        yield return "publish";
        if (!string.IsNullOrEmpty(Project)) yield return Project!;
        if (ConfigurationToken(Configuration) is { } cfg) { yield return "--configuration"; yield return cfg; }
        if (NoBuild) yield return "--no-build";
        if (NoRestore) yield return "--no-restore";
        if (NoDependencies) yield return "--no-dependencies";
        if (!string.IsNullOrEmpty(Output)) { yield return "--output"; yield return Output!; }
        if (!string.IsNullOrEmpty(Runtime)) { yield return "--runtime"; yield return Runtime!; }
        if (!string.IsNullOrEmpty(Framework)) { yield return "--framework"; yield return Framework!; }
        if (SelfContained is { } sc) { yield return sc ? "--self-contained" : "--no-self-contained"; }
        if (PublishSingleFile) yield return "-p:PublishSingleFile=true";
        if (PublishTrimmed) yield return "-p:PublishTrimmed=true";
        if (PublishReadyToRun) yield return "-p:PublishReadyToRun=true";
        if (!string.IsNullOrEmpty(VersionSuffix)) { yield return "--version-suffix"; yield return VersionSuffix!; }
        foreach (var (k, v) in Properties)
            yield return $"-p:{k}={v}";
    }
}
