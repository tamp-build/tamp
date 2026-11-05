using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Tamp.Cli.Scaffold.Templates;
using Tamp.Migrate.Mapping;
using Tamp.Migrate.Report;

namespace Tamp.Migrate.Rewriting;

/// <summary>A file the migration writes or rewrites outside the build's C# sources.</summary>
/// <param name="Path">Repository-relative path.</param>
/// <param name="Text">The full content to write.</param>
/// <param name="Created">True when the file didn't exist before.</param>
/// <param name="Executable">True for the POSIX shim, which needs the execute bit.</param>
public sealed record GeneratedFile(string Path, string Text, bool Created, bool Executable = false);

/// <summary>
/// The <c>file</c>-rule handlers (ADR 0021 Phase 2 step 4): <c>build-project</c> rewrites the NUKE build project
/// in place, and <c>scaffold-bootstrap</c> writes <c>tamp init</c>'s bootstrap (<see cref="BootstrapFiles"/>).
/// Both are idempotent: a second run generates nothing.
/// </summary>
internal sealed class BuildScaffolder(
    MigrationMapping mapping, MigrationOptions options, IReadOnlyList<string> repositoryFiles, Func<string, string?> readFile)
{
    private static readonly string[] SupportedFrameworks = ["net8.0", "net9.0", "net10.0"];

    public List<GeneratedFile> Generated { get; } = [];
    public List<MigrationSite> Sites { get; } = [];

    /// <param name="packages">The Tamp packages the converted C# needs (Tamp.Core is always added).</param>
    public void Run(IReadOnlySet<string> packages)
    {
        foreach (var rule in mapping.Rules.Where(r => r.MatchKind == "file"))
        {
            // Every build project, but one site per bootstrap glob: `.nuke/**` would otherwise list .nuke/temp/*.
            var matched = (rule.Handler == "build-project"
                    ? repositoryFiles.Where(f => rule.Globs.Any(g => Glob.Matches(Retarget(g), f)))
                    : rule.Globs.Select(g => repositoryFiles.Where(f => Glob.Matches(Retarget(g), f)).Order(StringComparer.Ordinal).FirstOrDefault()))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            if (matched.Count == 0) continue;

            switch (rule.Handler)
            {
                case "build-project":
                    foreach (var project in matched) BuildProject(rule, project, packages);
                    break;
                case "scaffold-bootstrap":
                    Bootstrap();
                    foreach (var f in matched)
                        Sites.Add(new MigrationSite(f, 0, rule.Id, Outcome.Assisted, rule.Marker));
                    break;
                default:
                    foreach (var f in matched)
                        Sites.Add(new MigrationSite(f, 0, rule.Id, Outcome.Manual, rule.Marker));
                    break;
            }
        }
    }

    // The mapping's globs say `build/`; the build project may live under --build-dir.
    private string Retarget(string glob) =>
        glob.StartsWith("build/", StringComparison.Ordinal) ? options.BuildDirectory.TrimEnd('/') + glob["build".Length..] : glob;

    private string TampVersion => options.TampVersion ?? ToolVersion.Current;

    // ------------------------------------------------------------------ build-project

    private void BuildProject(MappingRule rule, string path, IReadOnlySet<string> packages)
    {
        var text = readFile(path);
        if (text is null) return;
        XDocument doc;
        try { doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException ex)
        {
            Sites.Add(new MigrationSite(path, 0, rule.Id, Outcome.Manual, $"Couldn't parse the build project ({ex.Message}); convert it by hand."));
            return;
        }
        var project = doc.Root!;
        var notes = new List<string>();

        // NUKE's own properties (NukeRootDirectory, NukeScriptDirectory, NukeTelemetryVersion, ...).
        foreach (var p in project.Elements("PropertyGroup").Elements().Where(e => e.Name.LocalName.StartsWith("Nuke", StringComparison.Ordinal)).ToList())
            Remove(p);

        // Tamp.Core supports net8.0 / net9.0 / net10.0.
        foreach (var tfm in project.Elements("PropertyGroup").Elements("TargetFramework"))
        {
            if (!SupportedFrameworks.Contains(tfm.Value.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                notes.Add($"TargetFramework {tfm.Value.Trim()} -> net10.0 (Tamp.Core targets net8.0/net9.0/net10.0)");
                tfm.Value = "net10.0";
            }
        }

        foreach (var r in References(project).Where(r => Include(r).StartsWith("Nuke.", StringComparison.OrdinalIgnoreCase)).ToList())
            Remove(r);

        foreach (var d in project.Elements("ItemGroup").Elements("PackageDownload").ToList())
        {
            var version = (d.Attribute("Version")?.Value ?? "").Trim('[', ']');
            notes.Add($"PackageDownload {Include(d)} {version} -> a [NuGetPackage(\"{Include(d)}\", Version = \"{version}\")] Tool field");
            Remove(d);
        }

        var present = References(project).Select(Include).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = new[] { "Tamp.Core" }.Concat(packages.Order(StringComparer.Ordinal)).Where(p => !present.Contains(p)).ToList();
        if (wanted.Count > 0)
        {
            var group = References(project).FirstOrDefault()?.Parent ?? AddItemGroup(project);
            var childIndent = Indent(group) + "  ";
            foreach (var id in wanted)
                InsertLast(group, childIndent, new XElement("PackageReference", new XAttribute("Include", id), new XAttribute("Version", TampVersion)));
        }

        var output = ToText(doc, text);
        if (output == text) return; // already migrated
        Generated.Add(new GeneratedFile(path, output, Created: false));
        var marker = rule.Marker + (notes.Count > 0 ? " Changed: " + string.Join("; ", notes) + "." : "");
        Sites.Add(new MigrationSite(path, 0, rule.Id, Outcome.Assisted, marker));
    }

    private static IEnumerable<XElement> References(XElement project) => project.Elements("ItemGroup").Elements("PackageReference");

    private static string Include(XElement e) => e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value ?? "";

    private static void Remove(XElement e)
    {
        if (e.PreviousNode is XText ws && string.IsNullOrWhiteSpace(ws.Value)) ws.Remove();
        var parent = e.Parent;
        e.Remove();
        // An ItemGroup / PropertyGroup left with nothing but whitespace goes too.
        if (parent is not null && parent.Name.LocalName is "ItemGroup" or "PropertyGroup" && !parent.Elements().Any())
            Remove(parent);
    }

    private static XElement AddItemGroup(XElement project)
    {
        var group = new XElement("ItemGroup", new XText("\n  "));
        InsertLast(project, "  ", group);
        return group;
    }

    // Append a child on its own line, before the parent's closing-tag indentation.
    private static void InsertLast(XElement parent, string indent, XElement child)
    {
        if (parent.LastNode is XText closing && string.IsNullOrWhiteSpace(closing.Value))
            closing.AddBeforeSelf(new XText("\n" + indent), child);
        else
            parent.Add(new XText("\n" + indent), child, new XText("\n"));
    }

    // The whitespace before an element on its line: its indentation.
    private static string Indent(XElement e) =>
        e.PreviousNode is XText t && t.Value.LastIndexOf('\n') is var i and >= 0 ? t.Value[(i + 1)..] : "";

    private static string ToText(XDocument doc, string original)
    {
        var body = doc.Root!.ToString(SaveOptions.DisableFormatting);
        var head = original.TrimStart().StartsWith("<?xml", StringComparison.Ordinal) ? original[..(original.IndexOf("?>", StringComparison.Ordinal) + 2)] + "\n" : "";
        var trailing = original.EndsWith('\n') ? "\n" : "";
        var eol = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return (head + body + trailing).ReplaceLineEndings(eol);
    }

    // ------------------------------------------------------------------ scaffold-bootstrap

    private void Bootstrap()
    {
        if (!repositoryFiles.Contains("tamp.sh")) Generated.Add(new GeneratedFile("tamp.sh", BootstrapFiles.TampSh(), Created: true, Executable: true));
        if (!repositoryFiles.Contains("tamp.cmd")) Generated.Add(new GeneratedFile("tamp.cmd", BootstrapFiles.TampCmd(), Created: true));

        const string manifest = ".config/dotnet-tools.json";
        var existing = repositoryFiles.Contains(manifest) ? readFile(manifest) : null;
        if (existing is null)
        {
            Generated.Add(new GeneratedFile(manifest, BootstrapFiles.ToolsJson(TampVersion), Created: true));
            return;
        }

        // NUKE builds usually already have a manifest (nuke.globaltool): add dotnet-tamp beside it.
        if (JsonNode.Parse(existing) is not JsonObject root) return;
        var tools = root["tools"] as JsonObject ?? (JsonObject)(root["tools"] = new JsonObject());
        if (tools.ContainsKey("dotnet-tamp")) return;
        tools["dotnet-tamp"] = new JsonObject { ["version"] = TampVersion, ["commands"] = new JsonArray("dotnet-tamp") };
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        Generated.Add(new GeneratedFile(manifest, json.ReplaceLineEndings(existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n"), Created: false));
        if (tools.ContainsKey("nuke.globaltool"))
            Sites.Add(new MigrationSite(manifest, 0, "boilerplate.bootstrap-scripts", Outcome.Assisted,
                "dotnet-tamp was added to the tool manifest. Remove nuke.globaltool once NUKE's scripts are gone."));
    }
}

/// <summary>Glob matching for the mapping's <c>file</c> rules: <c>*</c> within a segment, <c>**</c> across segments.</summary>
internal static class Glob
{
    public static bool Matches(string glob, string path)
    {
        var pattern = string.Join(".*", glob.Split("**").Select(part =>
            string.Join("[^/]*", part.Split('*').Select(System.Text.RegularExpressions.Regex.Escape))));
        return System.Text.RegularExpressions.Regex.IsMatch(path.Replace('\\', '/'), "^" + pattern + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }
}

/// <summary>This tool's package version, which is also the Tamp version it targets (one release train).</summary>
internal static class ToolVersion
{
    public static string Current
    {
        get
        {
            var info = typeof(ToolVersion).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            var v = info ?? typeof(ToolVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }
}
