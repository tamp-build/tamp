using System.Globalization;
using Tamp.Migrate.Mapping;
using Tamp.Migrate.Rewriting;

namespace Tamp.Migrate;

/// <summary>
/// <c>tamp-migrate nuke [--root &lt;dir&gt;] [--build-dir &lt;dir&gt;] [--dry-run] [--package-major &lt;N&gt;]</c>.
/// Rewrites the NUKE build's C# files in place (review with <c>git diff</c>) and writes
/// <c>tamp-migration.json</c> and <c>tamp-migration.md</c> to the repository root.
/// </summary>
internal static class Program
{
    private const int ExitUsage = 2;

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Usage(Console.Out);
            return args.Length == 0 ? ExitUsage : 0;
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
        int? major = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root" when i + 1 < args.Length: root = Path.GetFullPath(args[++i]); break;
                case "--build-dir" when i + 1 < args.Length: buildDir = args[++i]; break;
                case "--package-major" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n):
                    major = n;
                    i++;
                    break;
                case "--dry-run": dryRun = true; break;
                default:
                    Console.Error.WriteLine($"tamp-migrate: unknown or incomplete option '{args[i]}'.");
                    Usage(Console.Error);
                    return ExitUsage;
            }
        }

        var buildRoot = Path.Combine(root, buildDir);
        if (!Directory.Exists(buildRoot))
        {
            Console.Error.WriteLine($"tamp-migrate: no build directory at {buildRoot} (use --build-dir).");
            return ExitUsage;
        }

        var files = Directory.EnumerateFiles(buildRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsUnder(f, "bin") && !IsUnder(f, "obj"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new SourceFile(Relative(root, f), File.ReadAllText(f)))
            .ToList();
        var repositoryFiles = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly)
            .Select(f => Relative(root, f))
            .Concat(Directory.Exists(Path.Combine(root, ".nuke"))
                ? Directory.EnumerateFiles(Path.Combine(root, ".nuke"), "*", SearchOption.AllDirectories).Select(f => Relative(root, f))
                : []);

        var result = new NukeMigrator(MigrationMapping.LoadEmbedded(), new MigrationOptions(major)).Migrate(files, repositoryFiles);

        if (!dryRun)
        {
            foreach (var file in result.Files.Where(f => result.Report.ChangedFiles.Contains(f.Path)))
                File.WriteAllText(Path.Combine(root, file.Path), file.Text);
        }
        File.WriteAllText(Path.Combine(root, "tamp-migration.json"), result.Report.ToJson());
        File.WriteAllText(Path.Combine(root, "tamp-migration.md"), result.Report.ToMarkdown());

        var s = result.Report.Summary;
        Console.WriteLine(
            $"tamp-migrate: {files.Count} file(s), {result.Report.ChangedFiles.Count} {(dryRun ? "would change" : "changed")}; "
            + $"{s.Applied} applied, {s.Assisted} assisted, {s.Manual} manual, {s.Unmapped} unmapped. See tamp-migration.md.");
        return 0;
    }

    private static bool IsUnder(string path, string dir) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(dir, StringComparer.OrdinalIgnoreCase);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void Usage(TextWriter w)
    {
        w.WriteLine("tamp-migrate nuke [--root <dir>] [--build-dir <dir>] [--dry-run] [--package-major <N>]");
        w.WriteLine();
        w.WriteLine("  Rewrites a NUKE build to Tamp in place (run it on a clean git tree and review with git diff).");
        w.WriteLine("  Auto-tier mappings are applied; every other recognised site gets a // TAMP-MIGRATE(<id>) marker.");
        w.WriteLine("  Writes tamp-migration.json and tamp-migration.md to the root.");
        w.WriteLine();
        w.WriteLine("  --root           repository root (default: current directory)");
        w.WriteLine("  --build-dir      the NUKE build project's directory, relative to the root (default: build)");
        w.WriteLine("  --dry-run        report only; change no files");
        w.WriteLine("  --package-major  major for versioned wrappers, e.g. 8 for Tamp.NetCli.V8 (default: newest)");
    }
}
