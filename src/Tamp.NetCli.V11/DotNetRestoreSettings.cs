namespace Tamp.NetCli.V11;

public sealed class DotNetRestoreSettings : DotNetSettingsBase
{
    public bool NoCache { get; set; }
    public bool Force { get; set; }
    public List<string> Sources { get; } = [];
    public string? PackagesDirectory { get; set; }
    public string? Runtime { get; set; }
    public string? ConfigFile { get; set; }
    public bool DisableParallel { get; set; }
    public bool ForceEvaluate { get; set; }
    public bool UseLockFile { get; set; }
    public bool LockedMode { get; set; }
    public string? LockFilePath { get; set; }

    public DotNetRestoreSettings SetProject(string? project) { Project = project; return this; }
    public DotNetRestoreSettings SetNoCache(bool noCache) { NoCache = noCache; return this; }
    public DotNetRestoreSettings SetForce(bool force) { Force = force; return this; }
    public DotNetRestoreSettings AddSource(string source) { Sources.Add(source); return this; }
    public DotNetRestoreSettings SetPackagesDirectory(string? path) { PackagesDirectory = path; return this; }
    public DotNetRestoreSettings SetRuntime(string? runtime) { Runtime = runtime; return this; }
    public DotNetRestoreSettings SetConfigFile(string? path) { ConfigFile = path; return this; }
    public DotNetRestoreSettings SetDisableParallel(bool v) { DisableParallel = v; return this; }
    public DotNetRestoreSettings SetUseLockFile(bool v) { UseLockFile = v; return this; }
    public DotNetRestoreSettings SetLockedMode(bool v) { LockedMode = v; return this; }
    public DotNetRestoreSettings SetLockFilePath(string? path) { LockFilePath = path; return this; }
    public DotNetRestoreSettings SetVerbosity(DotNetVerbosity v) { Verbosity = v; return this; }
    public DotNetRestoreSettings SetWorkingDirectory(string? cwd) { WorkingDirectory = cwd; return this; }

    protected override IEnumerable<string> BuildVerbArguments()
    {
        yield return "restore";
        if (!string.IsNullOrEmpty(Project)) yield return Project!;
        if (NoCache) yield return "--no-cache";
        if (Force) yield return "--force";
        foreach (var s in Sources) { yield return "--source"; yield return s; }
        if (!string.IsNullOrEmpty(PackagesDirectory)) { yield return "--packages"; yield return PackagesDirectory!; }
        if (!string.IsNullOrEmpty(Runtime)) { yield return "--runtime"; yield return Runtime!; }
        if (!string.IsNullOrEmpty(ConfigFile)) { yield return "--configfile"; yield return ConfigFile!; }
        if (DisableParallel) yield return "--disable-parallel";
        if (ForceEvaluate) yield return "--force-evaluate";
        if (UseLockFile) yield return "--use-lock-file";
        if (LockedMode) yield return "--locked-mode";
        if (!string.IsNullOrEmpty(LockFilePath)) { yield return "--lock-file-path"; yield return LockFilePath!; }
    }
}
