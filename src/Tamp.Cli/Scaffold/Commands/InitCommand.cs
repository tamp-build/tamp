using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tamp.Cli.Scaffold.Probes;
using Tamp.Cli.Scaffold.Sources;
using Tamp.Scaffold;

namespace Tamp.Cli.Scaffold.Commands;

/// <summary>
/// Handles <c>tamp init</c>. v0.1.0 ships exactly one template
/// (<see cref="Templates.MinimalTemplate"/>) via the embedded source.
/// </summary>
public static class InitCommand
{
    public const int ExitOk = 0;
    public const int ExitUsage = 64;
    public const int ExitFileExists = 73;
    public const int ExitTemplateNotFound = 78;
    public const int ExitDriftMismatch = 79;
    public const int ExitNotImplemented = 80;

    /// <summary>
    /// Parse <c>tamp init ...</c> args and run. Returns the process exit code.
    /// </summary>
    public static int Run(string[] args, TextWriter? stdout = null, TextWriter? stderr = null)
        => Run(args, stdout, stderr, versionFeed: null);

    /// <summary>
    /// Test seam: <paramref name="versionFeed"/> replaces the NuGet lookup that picks the
    /// scaffolded Tamp version (#119). Null means the real feed
    /// (<see cref="NuGetFlatContainerVersionFeed.FromEnvironment"/>).
    /// </summary>
    internal static int Run(string[] args, TextWriter? stdout, TextWriter? stderr, ITampVersionFeed? versionFeed,
                            TimeSpan? versionLookupTimeout = null)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;

        var opts = ParseArgs(args, out var parseError);
        if (parseError is not null)
        {
            stderr.WriteLine($"tamp init: {parseError}");
            stderr.WriteLine();
            PrintHelp(stderr);
            return ExitUsage;
        }

        if (opts.Help) { PrintHelp(stdout); return ExitOk; }

        var feed = versionFeed ?? NuGetFlatContainerVersionFeed.FromEnvironment();
        var timeout = versionLookupTimeout ?? TampVersionResolver.DefaultTimeout;
        return RunAsync(opts, stdout, stderr, feed, timeout, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(InitOptions opts, TextWriter stdout, TextWriter stderr,
                                            ITampVersionFeed versionFeed, TimeSpan versionLookupTimeout,
                                            CancellationToken ct)
    {
        var repoRoot = AbsolutePath.Create(opts.WorkingDirectory ?? Environment.CurrentDirectory);

        // Sources in priority order: embedded first (offline guarantee), then NuGet.
        // v0.1.0 only registers embedded; the NuGet slot is reserved.
        var sources = new IScaffoldTemplateSource[] { new EmbeddedTemplateSource() };

        if (opts.ListTemplates)
        {
            await ListTemplatesAsync(sources, stdout, ct);
            return ExitOk;
        }

        // Reserved flags — fail with a clean message in v0.1.0.
        if (opts.UnsupportedFlag is { } flag)
        {
            stderr.WriteLine($"tamp init: '{flag.Flag}' lands in {flag.Version}; not available in this CLI. " +
                             "Use `tamp init` (with no template flag) for the minimal scaffold.");
            return ExitNotImplemented;
        }

        // Pre-flight refusal: if Build.cs already exists AND --force is not set, refuse
        // (before any network lookup).
        var buildCsAlreadyPresent = File.Exists(Path.Combine(repoRoot.Value, "build", "Build.cs"));
        if (buildCsAlreadyPresent && !opts.Force)
        {
            await stderr.WriteLineAsync($"tamp init: build/Build.cs already exists at '{repoRoot.Value}'. " +
                                        "Pass --force to overwrite.");
            return ExitFileExists;
        }

        // Resolve template — adopter-selected name or default to "minimal".
        var templateName = opts.TemplateName ?? "minimal";
        IScaffoldTemplate? template = null;
        foreach (var src in sources)
        {
            template = await src.ResolveAsync(templateName, ct);
            if (template is not null) break;
        }
        if (template is null)
        {
            stderr.WriteLine($"tamp init: no template named '{templateName}' registered. " +
                             "Run `tamp init --list-templates` to see available templates.");
            return ExitTemplateNotFound;
        }

        // Version to pin (#119): newest stable Tamp on the feed (never older than this CLI),
        // or --tamp-version, or this CLI's own version when the feed can't be reached.
        var cliVersion = ResolveCliVersion();
        var resolution = await TampVersionResolver.ResolveAsync(
            cliVersion, opts.TampVersion, versionFeed, versionLookupTimeout, ct);

        // Probe.
        var probes = new IRepoProbe[] { new DotnetSolutionProbe() };
        var ctxBuilder = new ScaffoldContextBuilder
        {
            RepoRoot = repoRoot,
            TampCoreVersion = resolution.Version,
        };
        ctxBuilder.DotnetToolsJsonExists = File.Exists(Path.Combine(repoRoot.Value, ".config", "dotnet-tools.json"));
        ctxBuilder.BuildCsAlreadyPresent = buildCsAlreadyPresent;
        ctxBuilder.SettingsStyle = opts.SettingsStyle;
        if (opts.SolutionOverride is not null) ctxBuilder.Solution = opts.SolutionOverride;
        else foreach (var p in probes) p.Probe(repoRoot, ctxBuilder);
        var ctx = ctxBuilder.Build();

        // Drift check.
        if (!TampCoreVersionIsAtLeast(ctx.TampCoreVersion, template.MinimumTampCoreVersion))
        {
            stderr.WriteLine(
                $"tamp init: template '{template.Name}' requires Tamp.Core >= {template.MinimumTampCoreVersion}; " +
                $"the scaffold would pin {ctx.TampCoreVersion}. " +
                (resolution.Source == TampVersionSource.Flag
                    ? $"Pass --tamp-version {template.MinimumTampCoreVersion} or newer."
                    : "Upgrade: `dotnet tool update -g dotnet-tamp`."));
            return ExitDriftMismatch;
        }

        ReportVersion(resolution, cliVersion, ctx.DotnetToolsJsonExists, stdout);

        // Report probe diagnostics (informational; doesn't fail the run).
        if (ctx.ProbeData.TryGetValue("dotnet.solution.detection", out var msg))
            stdout.WriteLine($"  note: solution probe — {msg}; generated Build.cs will use [Solution] auto-discovery.");

        // Render + run.
        var specs = template.Render(ctx).ToList();
        var runner = new ScaffoldRunner(dryRun: opts.DryRun, force: opts.Force);
        IReadOnlyList<FileWriteResult> results;
        try
        {
            results = runner.Run(specs);
        }
        catch (IOException ex)
        {
            stderr.WriteLine($"tamp init: {ex.Message}");
            return ExitFileExists;
        }

        // Print outcome.
        foreach (var r in results)
        {
            var verb = r.Outcome switch
            {
                FileWriteOutcome.Written     => "  wrote       ",
                FileWriteOutcome.Overwritten => "  overwrote   ",
                FileWriteOutcome.Skipped     => "  skipped     ",
                FileWriteOutcome.Planned     => "  would-write ",
                _ => "  ?           ",
            };
            stdout.WriteLine(verb + Relative(repoRoot, r.Path));
        }

        if (opts.DryRun)
        {
            stdout.WriteLine();
            stdout.WriteLine("(dry-run; nothing was written.)");
        }
        else
        {
            stdout.WriteLine();
            stdout.WriteLine("Next steps:");
            if (!ctx.DotnetToolsJsonExists)
                stdout.WriteLine("  dotnet tool restore         # pulls dotnet-tamp into the local tool manifest");
            stdout.WriteLine("  dotnet tamp Test            # run the scaffolded Test target");
            stdout.WriteLine();
            stdout.WriteLine("Docs: https://github.com/tamp-build/tamp/wiki/Getting-Started");
        }

        return ExitOk;
    }

    /// <summary>
    /// Say which Tamp version the scaffold pins and where it came from (#119), plus a
    /// one-line note when the feed couldn't be checked or the running CLI is older.
    /// </summary>
    internal static void ReportVersion(TampVersionResolution r, string cliVersion, bool toolsJsonExists, TextWriter stdout)
    {
        var origin = r.Source switch
        {
            TampVersionSource.Flag => "pinned with --tamp-version",
            TampVersionSource.Feed => $"latest stable Tamp.Core on {r.FeedName}",
            _ when r.FailureReason is null => $"this CLI's version; nothing newer on {r.FeedName}",
            _ => "this CLI's version",
        };
        stdout.WriteLine($"Tamp {r.Version} ({origin}) → Tamp.Core, Tamp.NetCli.V10" +
                         (toolsJsonExists ? "" : ", dotnet-tamp"));

        if (r.FailureReason is not null)
            stdout.WriteLine($"  note: couldn't check {r.FeedName} for a newer Tamp ({r.FailureReason}); " +
                             $"{r.Version} may be stale. Pass --tamp-version <x> to pin one explicitly.");

        if (toolsJsonExists)
            stdout.WriteLine($"  note: .config/dotnet-tools.json already exists and is left as-is; " +
                             $"keep its dotnet-tamp pin at {r.Version} to match.");

        if (!TampCoreVersionIsAtLeast(cliVersion, r.Version) && TampCoreVersionIsAtLeast(r.Version, cliVersion))
            stdout.WriteLine($"  note: this CLI is {cliVersion}; the project pins {r.Version}. " +
                             (toolsJsonExists ? "" : "The local tool manifest covers the project — ") +
                             "to update your global tool too: `dotnet tool update -g dotnet-tamp`.");
        stdout.WriteLine();
    }

    private static async Task ListTemplatesAsync(IReadOnlyList<IScaffoldTemplateSource> sources, TextWriter stdout, CancellationToken ct)
    {
        stdout.WriteLine("Available templates:");
        foreach (var src in sources)
        {
            IReadOnlyList<IScaffoldTemplate> templates;
            try { templates = await src.ListAsync(ct); }
            catch (System.NotImplementedException) { continue; }            // NuGet source stub; skip silently in v0.1.0
            foreach (var t in templates)
                stdout.WriteLine($"  {t.Name,-16} ({src.Source})  {t.Description}");
        }
    }

    internal static InitOptions ParseArgs(string[] args, out string? error)
    {
        error = null;
        var opts = new InitOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h": case "--help": opts.Help = true; break;
                case "--dry-run": opts.DryRun = true; break;
                case "--list-templates": opts.ListTemplates = true; break;
                case "--solution":
                    if (++i >= args.Length) { error = "--solution requires a path."; return opts; }
                    opts.SolutionOverride = AbsolutePath.Create(Path.GetFullPath(args[i]));
                    break;
                case "--template":
                    if (++i >= args.Length) { error = "--template requires a name (minimal, library, monorepo)."; return opts; }
                    opts.TemplateName = args[i];
                    break;
                case "--settings-style":
                    if (++i >= args.Length) { error = "--settings-style requires a value (fluent | init)."; return opts; }
                    switch (args[i].ToLowerInvariant())
                    {
                        case "fluent": opts.SettingsStyle = SettingsStyle.Fluent; break;
                        case "init":   opts.SettingsStyle = SettingsStyle.Init;   break;
                        default:
                            error = $"--settings-style: unknown value '{args[i]}' (expected 'fluent' or 'init').";
                            return opts;
                    }
                    break;
                case "--template-source":
                    opts.UnsupportedFlag = (a, "0.3.0"); if (++i < args.Length) { /* consume value */ }
                    break;
                case "--offline":
                    opts.UnsupportedFlag = (a, "0.3.0"); break;
                case "--force":
                    opts.Force = true;
                    break;
                case "--tamp-version":
                    if (++i >= args.Length) { error = "--tamp-version requires a version (e.g. 1.17.5)."; return opts; }
                    if (!TampVersionResolver.IsValidVersion(args[i]))
                    { error = $"--tamp-version: '{args[i]}' is not a valid version (expected e.g. 1.17.5)."; return opts; }
                    opts.TampVersion = args[i];
                    break;
                case "--with-ci":
                    opts.UnsupportedFlag = (a, "0.3.0"); if (++i < args.Length) { /* consume value */ }
                    break;
                case "--interactive":
                    opts.UnsupportedFlag = (a, "0.4.0"); break;
                default:
                    if (a.StartsWith("--", System.StringComparison.Ordinal))
                    { error = $"unknown flag '{a}'"; return opts; }
                    if (opts.WorkingDirectory is not null)
                    { error = $"unexpected positional arg '{a}' (already have working directory)"; return opts; }
                    opts.WorkingDirectory = a;
                    break;
            }
        }
        return opts;
    }

    /// <summary>
    /// The CLI's own assembly version. It is the floor for the scaffolded Tamp
    /// version and the fallback when the feed can't be reached (#119); the
    /// normal pin is the newest stable Tamp.Core on the feed.
    /// </summary>
    internal static string ResolveCliVersion()
    {
        var v = typeof(InitCommand).Assembly.GetName().Version;
        if (v is null) return "1.4.0";  // fallback; should never hit in a packed build
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    internal static bool TampCoreVersionIsAtLeast(string have, string require)
    {
        if (!System.Version.TryParse(NormalizeSemver(have), out var h)) return true;       // unparseable → be permissive
        if (!System.Version.TryParse(NormalizeSemver(require), out var r)) return true;
        return h >= r;
    }

    private static string NormalizeSemver(string s)
    {
        // System.Version.TryParse handles X.Y / X.Y.Z / X.Y.Z.W but trips on prerelease
        // suffixes ("1.4.0-alpha.1"). Strip the suffix and rely on a coarse compare —
        // good enough for drift gating; can tighten in 0.2.0 if it pulls weight.
        var dash = s.IndexOf('-', System.StringComparison.Ordinal);
        return dash < 0 ? s : s[..dash];
    }

    private static string Relative(AbsolutePath root, AbsolutePath p)
    {
        var r = root.Value.TrimEnd(Path.DirectorySeparatorChar);
        return p.Value.StartsWith(r, System.StringComparison.Ordinal)
            ? p.Value[(r.Length + 1)..]
            : p.Value;
    }

    private static void PrintHelp(TextWriter w)
    {
        w.WriteLine("tamp init — scaffold a Tamp build script into the current directory");
        w.WriteLine();
        w.WriteLine("USAGE:");
        w.WriteLine("  tamp init [--template <name>] [--solution <path>] [--tamp-version <x>] [--dry-run] [--force] [--list-templates]");
        w.WriteLine();
        w.WriteLine("TEMPLATES (embedded — offline-safe):");
        w.WriteLine("  minimal     Clean / Restore / Compile / Test (default)");
        w.WriteLine("  library     minimal + typed Pack target with nupkg output dir");
        w.WriteLine("  monorepo    per-project Test fan-out + Ci aggregate + Pack");
        w.WriteLine();
        w.WriteLine("WHAT IT WRITES (any template):");
        w.WriteLine("  build/Build.cs               build script (shape varies by template)");
        w.WriteLine("  build/Build.csproj           pins Tamp.Core + Tamp.NetCli.V10");
        w.WriteLine("  (all Tamp pins use one version: the newest stable Tamp.Core on NuGet, never older");
        w.WriteLine("   than this CLI; this CLI's own version if NuGet can't be reached within 5s)");
        w.WriteLine("  .config/dotnet-tools.json    registers dotnet-tamp as a local tool (if absent)");
        w.WriteLine("  tamp.sh + tamp.cmd           shims that `dotnet tool restore` on first run");
        w.WriteLine();
        w.WriteLine("FLAGS:");
        w.WriteLine("  --template <name>          Pick a non-minimal template (minimal | library | monorepo)");
        w.WriteLine("  --settings-style <style>   Wrapper settings shape in scaffolded Build.cs (fluent | init).");
        w.WriteLine("                              fluent (default) → DotNet.Build(s => s.SetProject(...))");
        w.WriteLine("                              init             → DotNet.Build(new() { Project = ... })");
        w.WriteLine("  --solution <path>          Explicit .NET solution path (otherwise auto-detected)");
        w.WriteLine("  --tamp-version <x>         Pin this Tamp version (Tamp.Core, Tamp.NetCli.V10, dotnet-tamp);");
        w.WriteLine("                              skips the NuGet lookup");
        w.WriteLine("  --dry-run                  Print would-write list; touch nothing");
        w.WriteLine("  --force                    Overwrite existing files");
        w.WriteLine("  --list-templates           List templates from all registered sources");
        w.WriteLine("  -h | --help                This message");
        w.WriteLine();
        w.WriteLine("RESERVED (parsed; not implemented):");
        w.WriteLine("  --template-source <pkg>    (0.3.0)  Pin a specific NuGet template package");
        w.WriteLine("  --offline                  (0.3.0)  Refuse network fallback");
        w.WriteLine("  --with-ci <vendor>         (0.3.0)  Emit CI workflow files");
        w.WriteLine("  --interactive              (0.4.0)  Prompt for choices");
        w.WriteLine();
        w.WriteLine("ENVIRONMENT:");
        w.WriteLine($"  {NuGetFlatContainerVersionFeed.FeedUrlEnvironmentVariable}   NuGet v3 flat-container base URL for the version lookup");
        w.WriteLine($"                       (default {NuGetFlatContainerVersionFeed.DefaultBaseUrl})");
    }

    /// <summary>Parsed flag state.</summary>
    internal sealed class InitOptions
    {
        public bool Help;
        public bool DryRun;
        public bool Force;
        public bool ListTemplates;
        public string? WorkingDirectory;
        public string? TemplateName;
        public AbsolutePath? SolutionOverride;
        public SettingsStyle SettingsStyle = SettingsStyle.Fluent;
        public string? TampVersion;
        public (string Flag, string Version)? UnsupportedFlag;
    }
}
