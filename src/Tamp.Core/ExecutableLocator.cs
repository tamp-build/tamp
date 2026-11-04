namespace Tamp;

/// <summary>
/// Resolves an OS command to an absolute path before Tamp launches it (Sonar S4036), so a launch never
/// depends on the OS's own lookup.
/// </summary>
/// <remarks>
/// <para>
/// A build agent's <c>PATH</c> is attacker-influenced more often than it looks: a planted <c>git.exe</c> in
/// the checkout (Windows' process launch searches the working directory before <c>PATH</c>), a relative or
/// empty <c>PATH</c> entry (which means "the current directory"), a poisoned user-level entry on a shared
/// runner. <see cref="Resolve"/> walks <c>PATH</c> itself and <b>refuses</b> relative entries and anything
/// inside the current or the launch working directory, then hands back the absolute path to launch.
/// </para>
/// <para>
/// Known locations come first where a command has one (<c>/usr/bin/security</c> on macOS; the running
/// <c>dotnet</c> host, then <c>DOTNET_ROOT</c>). These are explicit and aren't subject to the working-directory
/// refusal, so a repo-local SDK install reached through <c>DOTNET_ROOT</c> keeps working.
/// </para>
/// <para>
/// <see cref="Tool.TryFromPath"/> uses the same <c>PATH</c> walk with <see cref="FindOnPath"/>'s lenient mode, so
/// <c>[FromPath]</c> tools a build deliberately puts on <c>PATH</c> under its own tree still resolve.
/// </para>
/// </remarks>
internal sealed class ExecutableLocator
{
    private readonly string? _path;
    private readonly string _currentDirectory;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string?> _env;
    private readonly string? _processPath;

    /// <param name="path">The <c>PATH</c> value to search.</param>
    /// <param name="currentDirectory">The process's current directory (refused, with everything under it).</param>
    /// <param name="isWindows">Windows: <c>;</c>-separated, executables carry an extension, case-insensitive paths.</param>
    /// <param name="fileExists">File probe; tests substitute a fake file system.</param>
    /// <param name="environment">Environment lookup for <c>DOTNET_ROOT</c>.</param>
    /// <param name="processPath">The running process's executable (<see cref="Environment.ProcessPath"/>).</param>
    public ExecutableLocator(string? path, string currentDirectory, bool isWindows,
        Func<string, bool>? fileExists = null, Func<string, string?>? environment = null, string? processPath = null)
    {
        _path = path;
        _currentDirectory = currentDirectory;
        IsWindows = isWindows;
        _fileExists = fileExists ?? File.Exists;
        _env = environment ?? (_ => null);
        _processPath = processPath;
    }

    /// <summary>The locator for this process: its <c>PATH</c>, current directory, OS and file system.</summary>
    public static ExecutableLocator Current => new(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.CurrentDirectory,
        OperatingSystem.IsWindows(),
        File.Exists,
        Environment.GetEnvironmentVariable,
        Environment.ProcessPath);

    public bool IsWindows { get; }

    private StringComparison PathComparison => IsWindows || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    // Extensions a launch without the shell can start. The extension-less name is never launchable on Windows.
    private string[] LaunchExtensions => IsWindows ? [".exe", ".cmd", ".bat"] : [""];

    /// <summary>
    /// The absolute path of <paramref name="name"/>: the first of <paramref name="knownLocations"/> that exists,
    /// else the first match on <c>PATH</c> that isn't relative and isn't inside the current directory or
    /// <paramref name="workingDirectory"/>. Null when there is none.
    /// </summary>
    public string? Resolve(string name, string? workingDirectory = null, params string[] knownLocations)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Command name must not be null or whitespace.", nameof(name));
        if (name.IndexOfAny(['/', '\\']) >= 0) throw new ArgumentException($"'{name}' is a path, not a command name.", nameof(name));

        foreach (var known in knownLocations)
            if (IsAbsolute(known) && _fileExists(known)) return known;

        var refused = new List<string> { _currentDirectory };
        if (!string.IsNullOrEmpty(workingDirectory)) refused.Add(workingDirectory);
        return FindOnPath(name, LaunchExtensions, refused);
    }

    /// <summary>
    /// The <c>dotnet</c> host to launch: the one running this process when it is <c>dotnet</c>, else
    /// <c>$DOTNET_ROOT/dotnet</c>, else <c>dotnet</c> on <c>PATH</c> under <see cref="Resolve"/>'s rules.
    /// </summary>
    public string? ResolveDotnet(string? workingDirectory = null)
    {
        var exe = IsWindows ? "dotnet.exe" : "dotnet";
        var known = new List<string>();
        if (_processPath is { } p && string.Equals(Path.GetFileName(p), exe, PathComparison)) known.Add(p);
        if (_env("DOTNET_ROOT") is { Length: > 0 } root) known.Add(Path.Combine(root, exe));
        return Resolve("dotnet", workingDirectory, [.. known]);
    }

    /// <summary>
    /// Walk <c>PATH</c> for <paramref name="name"/> + each of <paramref name="extensions"/>, in order. With
    /// <paramref name="refusedRoots"/> null the walk is lenient: every entry is searched, as
    /// <see cref="Tool.TryFromPath"/> always has. Otherwise relative entries and entries inside a refused root
    /// are skipped.
    /// </summary>
    public string? FindOnPath(string name, IReadOnlyList<string> extensions, IReadOnlyList<string>? refusedRoots)
    {
        var separator = IsWindows ? ';' : ':';
        foreach (var raw in (_path ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0) continue;
            if (refusedRoots is not null && (!IsAbsolute(dir) || refusedRoots.Any(r => IsInside(dir, r)))) continue;

            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (_fileExists(candidate)) return candidate;
            }
        }
        return null;
    }

    private bool IsAbsolute(string dir) =>
        IsWindows
            ? (dir.Length >= 3 && char.IsLetter(dir[0]) && dir[1] == ':' && dir[2] is '\\' or '/') || dir.StartsWith(@"\\", StringComparison.Ordinal)
            : dir.StartsWith('/');

    private bool IsInside(string dir, string root)
    {
        var d = Normalize(dir);
        var r = Normalize(root);
        return d.Equals(r, PathComparison) || d.StartsWith(r + Separator, PathComparison);
    }

    private char Separator => IsWindows ? '\\' : '/';

    private string Normalize(string p)
    {
        var s = IsWindows ? p.Replace('/', '\\') : p;
        // Collapse "." / ".." segments without touching the real file system's notion of the current directory.
        var parts = new List<string>();
        foreach (var seg in s.Split(Separator))
        {
            if (seg is "." or "") { if (parts.Count == 0) parts.Add(seg); continue; }
            if (seg == ".." && parts.Count > 1) { parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return string.Join(Separator, parts).TrimEnd(Separator);
    }
}
