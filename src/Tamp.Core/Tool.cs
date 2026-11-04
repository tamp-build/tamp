namespace Tamp;

/// <summary>
/// Resolved tool reference: an executable path plus optional default
/// settings. Modules and build scripts get a <see cref="Tool"/> from a
/// <see cref="NuGetPackageAttribute"/>-decorated property and use
/// <see cref="Plan(string[])"/> or <see cref="Plan(IEnumerable{string})"/>
/// to produce a <see cref="CommandPlan"/> for the executor.
/// </summary>
/// <remarks>
/// Tool is a thin convenience layer over <see cref="CommandPlan"/>. It
/// exists so attribute-driven tool resolution (NuGet, npm, system PATH,
/// local-path lookups) can hand back a single object that downstream
/// build code uses uniformly.
/// </remarks>
public sealed class Tool
{
    public Tool(AbsolutePath executable, string? workingDirectory = null)
    {
        Executable = executable ?? throw new ArgumentNullException(nameof(executable));
        WorkingDirectory = workingDirectory;
    }

    /// <summary>Resolved absolute path to the tool's executable.</summary>
    public AbsolutePath Executable { get; }

    /// <summary>Working directory the runner uses when dispatching plans built from this tool. Null = current directory.</summary>
    public string? WorkingDirectory { get; }

    /// <summary>Build a <see cref="CommandPlan"/> with the given arguments.</summary>
    public CommandPlan Plan(params string[] arguments) => Plan((IEnumerable<string>)arguments);

    /// <inheritdoc cref="Plan(string[])"/>
    public CommandPlan Plan(IEnumerable<string> arguments)
        => new()
        {
            Executable = Executable.Value,
            Arguments = arguments.ToList(),
            WorkingDirectory = WorkingDirectory,
        };

    /// <summary>
    /// Resolve a native executable on <c>PATH</c>. On Windows, probes <c>.cmd</c>, <c>.exe</c>,
    /// <c>.bat</c>, <c>.ps1</c>, and the extension-less name (in that order). Returns null when not found.
    /// </summary>
    /// <param name="name">Executable name without extension (e.g. <c>"yarn"</c>, <c>"docker"</c>, <c>"git"</c>).</param>
    /// <param name="workingDirectory">Optional default working directory for plans built from the returned Tool.</param>
    public static Tool? TryFromPath(string name, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Tool name must not be null or whitespace.", nameof(name));

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".cmd", ".exe", ".bat", ".ps1", "" }
            : new[] { "" };

        // The same PATH walk Tamp's own launches use (ExecutableLocator), in its lenient mode: a [FromPath]
        // tool the build put on PATH under its own tree is a deliberate choice, so nothing is refused here.
        var found = ExecutableLocator.Current.FindOnPath(name, extensions, refusedRoots: null);
        return found is null ? null : new Tool(AbsolutePath.Create(found), workingDirectory);
    }

    /// <summary>
    /// Resolve a native executable on <c>PATH</c>, or throw <see cref="InvalidOperationException"/> when not found.
    /// </summary>
    public static Tool FromPath(string name, string? workingDirectory = null)
        => TryFromPath(name, workingDirectory)
           ?? throw new InvalidOperationException(
               $"Could not find '{name}' on PATH. Install it and ensure the install directory is on PATH.");

    /// <summary>
    /// Resolve a tool installed under <c>&lt;projectRoot&gt;/node_modules/.bin/&lt;name&gt;</c>.
    /// On Windows, probes the <c>.cmd</c> shim first, then the bare name. Returns null when not found.
    /// </summary>
    public static Tool? TryFromNodeModules(string name, AbsolutePath projectRoot, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Tool name must not be null or whitespace.", nameof(name));
        if (projectRoot is null) throw new ArgumentNullException(nameof(projectRoot));

        var binDir = projectRoot / "node_modules" / ".bin";
        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".cmd", ".exe", "" }
            : new[] { "" };

        foreach (var ext in extensions)
        {
            var candidate = (binDir / (name + ext)).Value;
            if (File.Exists(candidate))
                return new Tool(AbsolutePath.Create(candidate), workingDirectory ?? projectRoot.Value);
        }
        return null;
    }

    /// <summary>
    /// Resolve a tool from <c>node_modules/.bin/</c>, or throw <see cref="InvalidOperationException"/>.
    /// Common workflow: pair with <c>Yarn.Install</c> via <c>DependsOn</c> so the resolution runs after install.
    /// </summary>
    public static Tool FromNodeModules(string name, AbsolutePath projectRoot, string? workingDirectory = null)
        => TryFromNodeModules(name, projectRoot, workingDirectory)
           ?? throw new InvalidOperationException(
               $"Could not find '{name}' under {projectRoot / "node_modules" / ".bin"}. Did you run `yarn install` (or `npm install`)?");
}
