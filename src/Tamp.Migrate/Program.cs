using System.Diagnostics;
using System.Globalization;
using Tamp.Migrate.Mapping;
using Tamp.Migrate.Rewriting;

namespace Tamp.Migrate;

/// <summary>
/// <c>tamp-migrate nuke [--root &lt;dir&gt;] [--build-dir &lt;dir&gt;] [--dry-run] [--allow-dirty]
/// [--package-major &lt;N&gt;] [--tamp-version &lt;v&gt;]</c>, also reached as <c>tamp migrate nuke …</c>.
/// Rewrites the NUKE build in place (review with <c>git diff</c>): its C# files, its build project, and
/// <c>tamp init</c>'s bootstrap. Writes <c>tamp-migration.json</c> and <c>tamp-migration.md</c> to the root.
/// </summary>
internal static class Program
{
    private const int ExitUsage = 2;
    private const int ExitDirtyTree = 3;

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Usage(Console.Out);
            return args.Length == 0 ? ExitUsage : 0;
        }
        if (args[0] is "--version")
        {
            Console.WriteLine($"tamp-migrate {ToolVersion.Current} (preview)");
            return 0;
        }
        if (args[0] != "nuke")
        {
            Console.Error.WriteLine($"tamp-migrate: unknown source '{args[0]}' (only 'nuke' is supported).");
            Usage(Console.Error);
            return ExitUsage;
        }

        var root = Directory.GetCurrentDirectory();
        var buildDir = "build";
        var dryRun = false;
        var allowDirty = false;
        int? major = null;
        string? tampVersion = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root" when i + 1 < args.Length: root = Path.GetFullPath(args[++i]); break;
                case "--build-dir" when i + 1 < args.Length: buildDir = args[++i].Replace('\\', '/').TrimEnd('/'); break;
                case "--package-major" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n):
                    major = n;
                    i++;
                    break;
                case "--tamp-version" when i + 1 < args.Length: tampVersion = args[++i]; break;
                case "--dry-run": dryRun = true; break;
                case "--allow-dirty": allowDirty = true; break;
                default:
                    Console.Error.WriteLine($"tamp-migrate: unknown or incomplete option '{args[i]}'.");
                    Usage(Console.Error);
                    return ExitUsage;
            }
        }

        Console.WriteLine($"tamp-migrate {ToolVersion.Current} (preview): assisted, not automatic. Review every change with git diff.");

        var buildRoot = Path.Combine(root, buildDir);
        if (!Directory.Exists(buildRoot))
        {
            Console.Error.WriteLine($"tamp-migrate: no build directory at {buildRoot} (use --build-dir).");
            return ExitUsage;
        }

        // The rewrite is in place, and git is the undo: refuse to mix it into uncommitted work.
        if (!dryRun && !allowDirty && CleanTree.Problem(root) is { } problem)
        {
            Console.Error.WriteLine($"tamp-migrate: {problem}.");
            Console.Error.WriteLine("  The migration rewrites files in place and `git diff` is how you review and undo it.");
            Console.Error.WriteLine("  Commit or stash first, run with --dry-run to see the report only, or pass --allow-dirty.");
            return ExitDirtyTree;
        }

        var files = Directory.EnumerateFiles(buildRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsUnder(root, f, "bin") && !IsUnder(root, f, "obj"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new SourceFile(Relative(root, f), File.ReadAllText(f)))
            .ToList();
        var repositoryFiles = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(buildRoot, "*", SearchOption.TopDirectoryOnly))
            .Concat(Existing(Path.Combine(root, ".config")))
            .Concat(Existing(Path.Combine(root, ".nuke"), SearchOption.AllDirectories))
            .Select(f => Relative(root, f))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var options = new MigrationOptions(major, tampVersion, buildDir);
        var result = new NukeMigrator(MigrationMapping.LoadEmbedded(), options)
            .Migrate(files, repositoryFiles, path => File.Exists(Path.Combine(root, path)) ? File.ReadAllText(Path.Combine(root, path)) : null);

        if (!dryRun)
        {
            foreach (var file in result.Files.Where(f => result.Report.ChangedFiles.Contains(f.Path)))
                File.WriteAllText(Path.Combine(root, file.Path), file.Text);
            foreach (var g in result.Generated)
            {
                var target = Path.Combine(root, g.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, g.Text);
                if (g.Executable && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(target, File.GetUnixFileMode(target) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }
        File.WriteAllText(Path.Combine(root, "tamp-migration.json"), result.Report.ToJson());
        File.WriteAllText(Path.Combine(root, "tamp-migration.md"), result.Report.ToMarkdown());

        var s = result.Report.Summary;
        Console.WriteLine(
            $"tamp-migrate: {files.Count} C# file(s), {result.Report.ChangedFiles.Count} file(s) {(dryRun ? "would change" : "changed")}; "
            + $"{s.Applied} applied, {s.Assisted} assisted, {s.Manual} manual, {s.Unmapped} unmapped. See tamp-migration.md.");
        return 0;
    }

    private static IEnumerable<string> Existing(string dir, SearchOption option = SearchOption.TopDirectoryOnly) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", option) : [];

    // bin/obj under the build directory, not anywhere in the absolute path (a repo can live under ~/obj).
    private static bool IsUnder(string root, string path, string dir) =>
        Relative(root, path).Split('/').Contains(dir, StringComparer.OrdinalIgnoreCase);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void Usage(TextWriter w)
    {
        w.WriteLine("tamp migrate nuke [--root <dir>] [--build-dir <dir>] [--dry-run] [--allow-dirty] [--package-major <N>] [--tamp-version <v>]");
        w.WriteLine();
        w.WriteLine("  Preview. Rewrites a NUKE build to Tamp in place: its C# files, its build project, and the");
        w.WriteLine("  tamp init bootstrap (tamp.sh, tamp.cmd, the dotnet-tamp tool entry). Auto-tier mappings are");
        w.WriteLine("  applied; every other recognised site gets a // TAMP-MIGRATE(<id>) marker. Writes");
        w.WriteLine("  tamp-migration.json and tamp-migration.md to the root.");
        w.WriteLine();
        w.WriteLine("  --root           repository root (default: current directory)");
        w.WriteLine("  --build-dir      the NUKE build project's directory, relative to the root (default: build)");
        w.WriteLine("  --dry-run        report only; change no files");
        w.WriteLine("  --allow-dirty    run even if the git working tree has uncommitted changes");
        w.WriteLine("  --package-major  major for versioned wrappers, e.g. 8 for Tamp.NetCli.V8 (default: newest)");
        w.WriteLine("  --tamp-version   version for the Tamp package references (default: this tool's version)");
    }
}

/// <summary>Is the repository a git working tree with nothing uncommitted? (The migration's undo is <c>git diff</c>.)</summary>
internal static class CleanTree
{
    /// <returns>Null when the tree is clean; otherwise why the migration should not run.</returns>
    // The strict resolver (#133): never a git planted in the repository being migrated.
    public static string? Problem(string root) => Problem(root, ExecutableLocator.Current.Resolve("git", root));

    /// <param name="root">The repository root.</param>
    /// <param name="git">The resolved git executable; null when there is none.</param>
    internal static string? Problem(string root, string? git)
    {
        if (git is null) return "git wasn't found on PATH, so a clean working tree can't be confirmed";

        var psi = new ProcessStartInfo(git)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "status", "--porcelain", "--untracked-files=normal" }) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return "git status timed out, so a clean working tree can't be confirmed";
            }
            if (p.ExitCode != 0) return $"{root} isn't a git working tree ({stderr.Result.Trim()})";
            // This tool's own report files don't count: re-running after a migration is normal.
            var changed = stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Count(l => !l.TrimEnd('\r').EndsWith("tamp-migration.json", StringComparison.Ordinal)
                         && !l.TrimEnd('\r').EndsWith("tamp-migration.md", StringComparison.Ordinal));
            return changed == 0 ? null : $"the working tree has {changed} uncommitted change(s)";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"git couldn't be run ({ex.Message})";
        }
    }
}
