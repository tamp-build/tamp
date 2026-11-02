using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tamp.Cli;

/// <summary>
/// Turns a failed build-project restore caused by NU1605 ("Detected package
/// downgrade") on a <c>Tamp.*</c> package into one actionable message (#119).
/// Typical trigger: a satellite such as <c>Tamp.Heft.V1</c> was built against a
/// newer <c>Tamp.Core</c> than <c>build/Build.csproj</c> pins.
/// </summary>
/// <remarks>
/// The input is NuGet's own message text, as found in the <c>logs</c> section of
/// <c>obj/project.assets.json</c> or in captured <c>dotnet</c> output:
/// <code>
/// Detected package downgrade: Tamp.Core from 1.17.5 to 1.17.3. Reference the package directly from the project to select a different version.
///  Build -> Tamp.Heft.V1 0.1.0 -> Tamp.Core (>= 1.17.5)
///  Build -> Tamp.Core (>= 1.17.3)
/// </code>
/// </remarks>
internal static class RestoreDowngradeDiagnostic
{
    /// <summary>One NU1605 downgrade: <see cref="RequiredBy"/> needs <see cref="Package"/> &gt;= <see cref="Required"/>, the project pins <see cref="Pinned"/>.</summary>
    internal sealed record Downgrade(string Package, string Required, string Pinned, string? RequiredBy);

    /// <summary>Packages released together from this repo's single version (RELEASING.md); fix them as a set.</summary>
    private static readonly string[] CorePackages = ["Tamp.Core", "Tamp.NetCli.V10"];

    // Bounded so a pathological log line can't stall the CLI (Sonar S6444); a timeout makes
    // TryDescribe return null instead of a diagnostic.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex Header = new(
        @"Detected package downgrade:\s*(?<pkg>[^\s]+)\s+from\s+(?<from>[^\s]+)\s+to\s+(?<to>[^\s.]+(?:\.[^\s.]+)*?)\.?(?=\s|$)",
        RegexOptions.CultureInvariant, MatchTimeout);

    // "<chain> -> <PackageId> <version> -> <Package> (>= <version>)" — the element just before the last arrow.
    private static readonly Regex Path = new(
        @"->\s*(?<by>[^\s>]+)\s+(?<byver>[^\s>]+)\s*->\s*(?<pkg>[^\s(]+)\s*\(\s*>=\s*(?<ver>[^)\s]+)\s*\)",
        RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>
    /// Extract every NU1605 downgrade from <paramref name="text"/>. Tolerates the
    /// MSBuild prefix (<c>…/Build.csproj : error NU1605: Warning As Error: …</c>),
    /// either line ending, and the same downgrade repeated per target framework.
    /// </summary>
    internal static IReadOnlyList<Downgrade> Parse(string text)
    {
        var found = new List<Downgrade>();
        if (string.IsNullOrEmpty(text) || !text.Contains("downgrade", StringComparison.OrdinalIgnoreCase)) return found;

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var h = Header.Match(lines[i]);
            if (!h.Success) continue;
            var pkg = h.Groups["pkg"].Value;
            var from = h.Groups["from"].Value;
            var to = h.Groups["to"].Value;

            // Dependency paths follow on the next lines (until the next header). The one that
            // goes through another package names who needs the higher version.
            string? requiredBy = null;
            // Some loggers print the whole message on one line, so start with the rest of the header line.
            for (var j = i; j < lines.Length && (j == i || !Header.IsMatch(lines[j])); j++)
            {
                var m = Path.Match(j == i ? lines[j][(h.Index + h.Length)..] : lines[j]);
                if (m.Success && string.Equals(m.Groups["pkg"].Value, pkg, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(m.Groups["ver"].Value, from, StringComparison.OrdinalIgnoreCase))
                {
                    requiredBy = $"{m.Groups["by"].Value} {m.Groups["byver"].Value}";
                    break;
                }
            }

            var d = new Downgrade(pkg, from, to, requiredBy);
            if (!found.Contains(d)) found.Add(d);
        }
        return found;
    }

    /// <summary>
    /// Build the user-facing message for the <c>Tamp.*</c> downgrades in
    /// <paramref name="downgrades"/>; null if none involve a Tamp package.
    /// </summary>
    internal static string? Format(IReadOnlyList<Downgrade> downgrades, string buildProjectDisplay)
    {
        var tamp = downgrades.Where(IsTamp).ToList();
        if (tamp.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine($"tamp: {buildProjectDisplay} failed to restore: a package needs a newer Tamp than the build project pins (NU1605, package downgrade).");
        foreach (var d in tamp)
        {
            var who = d.RequiredBy ?? "a dependency";
            sb.AppendLine($"  {who} needs {d.Package} >= {d.Required}; {buildProjectDisplay} pins {d.Package} {d.Pinned}.");
        }

        var core = tamp.Where(d => CorePackages.Contains(d.Package, StringComparer.OrdinalIgnoreCase)).ToList();
        if (core.Count > 0)
        {
            var target = core.Select(d => d.Required).OrderByDescending(ParseOrZero).First();
            sb.AppendLine($"  Fix: raise Tamp.Core and Tamp.NetCli.V10 in {buildProjectDisplay}, and dotnet-tamp in .config/dotnet-tools.json, to {target}");
            sb.Append("       (they release together), then run `dotnet tool restore`.");
        }
        foreach (var d in tamp.Where(d => !CorePackages.Contains(d.Package, StringComparer.OrdinalIgnoreCase))
                              .GroupBy(d => d.Package, StringComparer.OrdinalIgnoreCase))
        {
            if (sb[^1] != '\n') sb.AppendLine();
            var target = d.Select(x => x.Required).OrderByDescending(ParseOrZero).First();
            sb.Append($"  Fix: raise {d.Key} in {buildProjectDisplay} to {target}.");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Look for NU1605 entries in the build project's <c>obj/project.assets.json</c>
    /// (NuGet writes restore errors there even when restore fails) and format them.
    /// Null when there's nothing Tamp-related to say or the file isn't where we expect.
    /// </summary>
    internal static string? TryDescribe(string buildProject, string? capturedOutput = null)
    {
        try
        {
            var downgrades = new List<Downgrade>();
            if (!string.IsNullOrEmpty(capturedOutput)) downgrades.AddRange(Parse(capturedOutput));

            var assets = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(buildProject) ?? ".", "obj", "project.assets.json");
            if (File.Exists(assets))
                foreach (var message in ReadAssetsLogMessages(File.ReadAllText(assets), "NU1605"))
                    foreach (var d in Parse(message))
                        if (!downgrades.Contains(d)) downgrades.Add(d);

            var display = DisplayPath(buildProject);
            return Format(downgrades, display);
        }
        catch (Exception)
        {
            return null;   // diagnostics are best-effort; never mask the build's own exit code
        }
    }

    /// <summary>Messages of the <c>logs</c> entries with the given NuGet code in a project.assets.json.</summary>
    internal static IEnumerable<string> ReadAssetsLogMessages(string assetsJson, string code)
    {
        using var doc = JsonDocument.Parse(assetsJson);
        if (!doc.RootElement.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var entry in logs.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("code", out var c) || !string.Equals(c.GetString(), code, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.TryGetProperty("message", out var m) && m.GetString() is { } s) list.Add(s);
        }
        return list;
    }

    private static bool IsTamp(Downgrade d)
        => d.Package.StartsWith("Tamp.", StringComparison.OrdinalIgnoreCase)
           || (d.RequiredBy?.StartsWith("Tamp.", StringComparison.OrdinalIgnoreCase) ?? false);

    private static Version ParseOrZero(string v)
    {
        var dash = v.IndexOf('-', StringComparison.Ordinal);
        return Version.TryParse(dash < 0 ? v : v[..dash], out var parsed) ? parsed : new Version(0, 0);
    }

    private static string DisplayPath(string buildProject)
    {
        var rel = System.IO.Path.GetRelativePath(Environment.CurrentDirectory, buildProject);
        return rel.StartsWith("..", StringComparison.Ordinal) ? buildProject : rel.Replace('\\', '/');
    }
}
