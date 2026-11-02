using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Tamp.Scaffold;

/// <summary>
/// Authoring style for wrapper-settings call sites in scaffolded Build.cs
/// files (TAM-161). Both styles produce identical CommandPlans; the choice
/// is purely a stylistic preference.
/// </summary>
public enum SettingsStyle
{
    /// <summary>
    /// Fluent <c>Set*</c> chain — the canonical shape, what NUKE migrants
    /// recognize from years of muscle memory. <c>tamp init</c>'s default.
    /// <code>
    /// DotNet.Build(s => s.SetProject(Solution.Path).SetConfiguration(Configuration))
    /// </code>
    /// </summary>
    Fluent = 0,

    /// <summary>
    /// C# object-initializer shape — discoverable via IntelliSense's
    /// property-completion path, naturally reads top-to-bottom. Opt-in via
    /// <c>tamp init --settings-style=init</c>.
    /// <code>
    /// DotNet.Build(new() { Project = Solution.Path, Configuration = Configuration })
    /// </code>
    /// </summary>
    Init = 1,
}

/// <summary>
/// Immutable snapshot of what the CLI knows about the target repository.
/// Probes assemble this before any template runs; templates read from it.
/// </summary>
public sealed record ScaffoldContext
{
    public required AbsolutePath RepoRoot { get; init; }

    /// <summary>Resolved .NET solution file path, or null if zero / multiple solutions are present.</summary>
    public AbsolutePath? Solution { get; init; }

    /// <summary>
    /// Tamp.* package version templates should pin in generated project files.
    /// The CLI fills this with the newest stable Tamp.Core on NuGet (never older
    /// than the CLI), an explicit <c>--tamp-version</c>, or the CLI's own version
    /// when the feed can't be reached; tests override it directly.
    /// </summary>
    public required string TampCoreVersion { get; init; }

    public bool DotnetToolsJsonExists { get; init; }
    public bool BuildCsAlreadyPresent { get; init; }

    /// <summary>
    /// Settings-call authoring style for the rendered Build.cs (TAM-161).
    /// Default <see cref="SettingsStyle.Fluent"/> — adopters can opt into
    /// the object-init shape via <c>tamp init --settings-style=init</c>.
    /// </summary>
    public SettingsStyle SettingsStyle { get; init; } = SettingsStyle.Fluent;

    /// <summary>
    /// Extension slot. Future probes (YarnWorkspaceProbe, DockerfileProbe, etc.)
    /// contribute named facts here instead of growing this record. Read-only.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProbeData { get; init; }
        = ReadOnlyDictionary<string, string>.Empty;
}

/// <summary>
/// Mutable builder probes accumulate into; the CLI command freezes the
/// result into a <see cref="ScaffoldContext"/> before handing it to templates.
/// </summary>
public sealed class ScaffoldContextBuilder
{
    public required AbsolutePath RepoRoot { get; init; }
    public AbsolutePath? Solution { get; set; }
    public required string TampCoreVersion { get; init; }
    public bool DotnetToolsJsonExists { get; set; }
    public bool BuildCsAlreadyPresent { get; set; }
    public SettingsStyle SettingsStyle { get; set; } = SettingsStyle.Fluent;

    private readonly Dictionary<string, string> _probeData = new(System.StringComparer.Ordinal);

    public void Set(string key, string value) => _probeData[key] = value;

    public ScaffoldContext Build() => new()
    {
        RepoRoot = RepoRoot,
        Solution = Solution,
        TampCoreVersion = TampCoreVersion,
        DotnetToolsJsonExists = DotnetToolsJsonExists,
        BuildCsAlreadyPresent = BuildCsAlreadyPresent,
        SettingsStyle = SettingsStyle,
        ProbeData = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(_probeData, System.StringComparer.Ordinal)),
    };
}
