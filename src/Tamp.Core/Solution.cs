using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Tamp;

/// <summary>
/// Read-only view of a .NET solution file (<c>.slnx</c> or <c>.sln</c>).
/// </summary>
/// <remarks>
/// Surface is deliberately minimal for v1: enumerate projects (with
/// resolved paths), enumerate folders, look up by name. Authoring support
/// (add/remove/save) is not in scope — that belongs to <c>dotnet sln</c>
/// and the IDE.
/// </remarks>
public sealed class Solution
{
    private Solution(AbsolutePath path, string name, IReadOnlyList<SolutionProject> projects, IReadOnlyList<SolutionFolder> folders)
    {
        Path = path;
        Name = name;
        Projects = projects;
        Folders = folders;
    }

    public AbsolutePath Path { get; }
    public string Name { get; }
    public IReadOnlyList<SolutionProject> Projects { get; }
    public IReadOnlyList<SolutionFolder> Folders { get; }

    /// <summary>
    /// The solution file's path, for string-taking APIs: <c>DotNet.Build(s =&gt; s.SetProject(Solution))</c>.
    /// Mirrors <see cref="AbsolutePath"/>'s conversion, and NUKE's <c>Solution</c>, so a NUKE build's
    /// <c>SetProjectFile(Solution)</c> migrates to <c>SetProject(Solution)</c> unchanged.
    /// </summary>
    public static implicit operator string(Solution solution) => solution.Path.Value;

    /// <summary>Find a project by its display name (case-insensitive).</summary>
    public SolutionProject? GetProject(string name)
        => Projects.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Load a solution from its file path. Format detected by extension.</summary>
    public static Solution Load(AbsolutePath path)
    {
        if (!path.FileExists())
            throw new InvalidOperationException($"Solution file not found: {path}");
        return path.Extension.ToLowerInvariant() switch
        {
            ".slnx" => LoadSlnx(path),
            ".sln" => LoadSln(path),
            _ => throw new InvalidOperationException($"Unsupported solution extension '{path.Extension}'. Use .slnx or .sln."),
        };
    }

    private static Solution LoadSlnx(AbsolutePath path)
    {
        var doc = XDocument.Load(path.Value);
        var root = doc.Root ?? throw new InvalidOperationException($"Solution file is empty: {path}");
        var slnDir = path.Parent ?? throw new InvalidOperationException($"Cannot resolve parent of {path}");

        var projects = new List<SolutionProject>();
        var folders = new List<SolutionFolder>();
        WalkSlnx(root, slnDir, currentFolderPath: null, projects, folders);

        return new Solution(path, path.NameWithoutExtension, projects, folders);
    }

    private static void WalkSlnx(XElement element, AbsolutePath slnDir, string? currentFolderPath, List<SolutionProject> projects, List<SolutionFolder> folders)
    {
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Project":
                    var projPath = (string?)child.Attribute("Path") ?? string.Empty;
                    if (string.IsNullOrEmpty(projPath)) break;
                    var resolved = AbsolutePath.Create(System.IO.Path.Combine(slnDir.Value, projPath));
                    projects.Add(new SolutionProject(
                        Name: System.IO.Path.GetFileNameWithoutExtension(projPath),
                        Path: resolved,
                        SolutionFolderPath: currentFolderPath));
                    break;

                case "Folder":
                    var folderName = (string?)child.Attribute("Name") ?? "/";
                    folders.Add(new SolutionFolder(folderName));
                    WalkSlnx(child, slnDir, folderName, projects, folders);
                    break;

                default:
                    // Files, properties, configurations etc. — ignored at v1.
                    break;
            }
        }
    }

    // Anchored, and every repeat is a negated class bounded by its own delimiter: linear-time.
    private static readonly Regex ProjectLineRegex = TampRegex.Create(
        @"^Project\(""\{(?<typeGuid>[^}]+)\}""\)\s*=\s*""(?<name>[^""]+)"",\s*""(?<path>[^""]+)"",\s*""\{(?<projGuid>[^}]+)\}""");

    /// <summary>Solution-folder type GUID used by Visual Studio in legacy .sln files.</summary>
    private const string SolutionFolderTypeGuid = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

    private static Solution LoadSln(AbsolutePath path)
    {
        var slnDir = path.Parent ?? throw new InvalidOperationException($"Cannot resolve parent of {path}");
        var projects = new List<SolutionProject>();
        var folders = new List<SolutionFolder>();
        foreach (var raw in path.ReadAllLines())
        {
            var line = raw.TrimStart('﻿', ' ', '\t');
            if (!line.StartsWith("Project(", StringComparison.Ordinal)) continue;
            var m = TampRegex.Match(ProjectLineRegex, line, $"a Project line of {path}");
            if (!m.Success) continue;

            var name = m.Groups["name"].Value;
            var typeGuid = m.Groups["typeGuid"].Value.ToUpperInvariant();
            var relPath = m.Groups["path"].Value.Replace('\\', System.IO.Path.DirectorySeparatorChar);

            if (string.Equals(typeGuid, SolutionFolderTypeGuid, StringComparison.OrdinalIgnoreCase))
            {
                folders.Add(new SolutionFolder("/" + name + "/"));
                continue;
            }

            var resolved = AbsolutePath.Create(System.IO.Path.Combine(slnDir.Value, relPath));
            projects.Add(new SolutionProject(
                Name: name,
                Path: resolved,
                SolutionFolderPath: null));  // .sln folder→project nesting lives elsewhere; ignore for v1
        }
        return new Solution(path, path.NameWithoutExtension, projects, folders);
    }
}

/// <summary>A single project entry in a solution.</summary>
public sealed record SolutionProject(string Name, AbsolutePath Path, string? SolutionFolderPath);

/// <summary>A single solution folder.</summary>
public sealed record SolutionFolder(string Name);

/// <summary>
/// Auto-inject the build's <see cref="Solution"/> from the discovered solution file.
/// </summary>
/// <remarks>
/// <para>Path resolution, in order:</para>
/// <list type="number">
///   <item>Positional ctor arg or <see cref="Path"/> property (relative to <see cref="TampBuild.RootDirectory"/>) — wins when set.</item>
///   <item>First <c>.slnx</c> or <c>.sln</c> directly in <see cref="TampBuild.RootDirectory"/>.</item>
///   <item>If exactly one <c>.slnx</c>/<c>.sln</c> exists anywhere in the subtree — common monorepo shape (e.g. <c>src/dotnet/Foo.slnx</c>).</item>
/// </list>
/// <para>
/// Subtree search skips noisy directories (<c>node_modules</c>, <c>bin</c>, <c>obj</c>, <c>.git</c>, <c>artifacts</c>) for performance.
/// If multiple solutions are found, the error lists candidates so the consumer can pick one explicitly.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SolutionAttribute : ValueInjectionAttribute
{
    /// <summary>Explicit path, relative to <see cref="TampBuild.RootDirectory"/>.</summary>
    public string? Path { get; init; }

    /// <summary>Default-state ctor — auto-discover the solution.</summary>
    public SolutionAttribute() { }

    /// <summary>Explicit path, relative to <see cref="TampBuild.RootDirectory"/>. Convenience shorthand for <c>[Solution(Path = "...")]</c>.</summary>
    public SolutionAttribute(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Solution path must not be null or whitespace.", nameof(path));
        Path = path;
    }

    public override object? GetValue(System.Reflection.MemberInfo member, Type memberType)
    {
        var path = LocateSolution();
        return Solution.Load(path);
    }

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".git", "artifacts", ".vs", ".idea", "TestResults",
    };

    private AbsolutePath LocateSolution() => LocateSolutionFor(TampBuild.RootDirectory);

    internal AbsolutePath LocateSolutionFor(AbsolutePath root)
    {
        if (!string.IsNullOrEmpty(Path))
        {
            var explicitPath = root / Path;
            if (!explicitPath.FileExists())
                throw new InvalidOperationException(
                    $"[Solution(\"{Path}\")] — file does not exist at {explicitPath}.");
            return explicitPath;
        }

        // 1. Solution(s) directly in root wins (the current monorepo-root convention).
        var topLevel = root.GlobFiles("*.slnx")
            .Concat(root.GlobFiles("*.sln"))
            .ToList();
        if (topLevel.Count == 1) return topLevel[0];
        if (topLevel.Count > 1) return topLevel[0]; // preserve historical "first one wins" behavior

        // 2. Single solution somewhere in the subtree → use it (the nested-solution monorepo shape).
        var subtree = FindInSubtree(root).Take(2).ToList();
        if (subtree.Count == 1) return subtree[0];
        if (subtree.Count > 1)
        {
            var all = FindInSubtree(root).Take(10).ToList();
            var list = string.Join("\n  ", all.Select(p => p.Value));
            throw new InvalidOperationException(
                $"[Solution] found multiple solution files in the subtree under {root}:\n  {list}\n" +
                "Set Path explicitly — `[Solution(\"src/dotnet/Foo.slnx\")]` or `[Solution(Path = \"...\")]`.");
        }

        throw new InvalidOperationException(
            $"[Solution] could not locate any .slnx or .sln under {root}. " +
            "Set Path explicitly — `[Solution(\"path/to/Foo.slnx\")]` — or place a solution file at the root.");
    }

    private static IEnumerable<AbsolutePath> FindInSubtree(AbsolutePath root)
    {
        var stack = new Stack<string>();
        stack.Push(root.Value);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> subDirs;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly);
                subDirs = Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var f in files)
            {
                var ext = System.IO.Path.GetExtension(f);
                if (string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase))
                {
                    // Skip the immediate-root matches — those are handled by step 1.
                    if (System.IO.Path.GetDirectoryName(f) != root.Value)
                        yield return AbsolutePath.Create(f);
                }
            }
            foreach (var sub in subDirs)
            {
                var name = System.IO.Path.GetFileName(sub);
                if (SkipDirectories.Contains(name)) continue;
                stack.Push(sub);
            }
        }
    }
}
