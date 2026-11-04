using YamlDotNet.Serialization;

namespace Tamp.Migrate.Mapping;

/// <summary>Confidence tier of a mapping row (ADR 0021).</summary>
public enum Tier
{
    /// <summary>Applied mechanically; no marker.</summary>
    Auto,
    /// <summary>Rewrite (if any) applied, and a marker left for a person to confirm.</summary>
    Assisted,
    /// <summary>No rewrite; marker only.</summary>
    Manual,
}

/// <summary>One <c>rules[]</c> row of <c>mapping.yaml</c>.</summary>
public sealed record MappingRule(
    string Id,
    string Category,
    Tier Tier,
    string MatchKind,
    IReadOnlyList<string> Names,
    string? Receiver,
    string? Namespace,
    IReadOnlyList<string> Attributes,
    IReadOnlyList<string> Globs,
    string? RewriteKind,
    string? RewriteTo,
    string? Handler,
    string? Marker);

/// <summary>One NUKE setting method of a tool family, and what it becomes.</summary>
public sealed record ToolSetting(string? To, string? Args, string? Template, Tier Tier);

/// <summary>One <c>tools.&lt;family&gt;</c> entry: a NUKE <c>XyzTasks</c> class and its Tamp wrapper.</summary>
public sealed record ToolFamily(
    string Key,
    string Id,
    string NukeClass,
    string NukeNamespace,
    string Package,
    IReadOnlyList<int> PackageMajors,
    string Namespace,
    string Facade,
    bool InTree,
    Tier Tier,
    IReadOnlyDictionary<string, (string To, Tier Tier)> Verbs,
    IReadOnlyDictionary<string, ToolSetting> Settings,
    string? Notes)
{
    /// <summary>The prefix NUKE puts on every verb of the class: <c>DotNetTasks</c> → <c>DotNet</c>.</summary>
    public string VerbPrefix => NukeClass[..^"Tasks".Length];

    /// <summary>The Tamp namespace for a package major (<c>Tamp.NetCli.V{N}</c> → <c>Tamp.NetCli.V10</c>).</summary>
    public string NamespaceFor(int? major) => Namespace.Replace("{N}", (major ?? DefaultMajor).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>The package id for a package major.</summary>
    public string PackageFor(int? major) => Package.Replace("{N}", (major ?? DefaultMajor).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private int DefaultMajor => PackageMajors.Count > 0 ? PackageMajors.Max() : 0;
}

/// <summary>A <c>gaps:</c> entry: NUKE surface with no Tamp equivalent (or one reached differently).</summary>
public sealed record MappingGap(string Id, IReadOnlyList<string> NukeClasses, string Text);

/// <summary>
/// The parsed <c>docs/nuke-migration/mapping.yaml</c> (schema 0.2). This is the single source of truth the
/// rewriter acts on; nothing here re-states a row of the table.
/// </summary>
public sealed class MigrationMapping
{
    private const string ResourceName = "Tamp.Migrate.mapping.yaml";

    public required string Version { get; init; }
    public required IReadOnlyList<MappingRule> Rules { get; init; }
    public required IReadOnlyList<ToolFamily> Tools { get; init; }
    public required IReadOnlyList<MappingGap> Gaps { get; init; }

    /// <summary>NUKE namespaces whose types the rules cover, mapped to the Tamp namespace that replaces them.</summary>
    public required IReadOnlyDictionary<string, string> Namespaces { get; init; }

    public MappingRule Rule(string id) => Rules.FirstOrDefault(r => r.Id == id)
        ?? throw new InvalidOperationException($"mapping.yaml has no rule '{id}'.");

    /// <summary>The mapping this build of the tool embeds.</summary>
    public static MigrationMapping LoadEmbedded()
    {
        using var stream = typeof(MigrationMapping).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static MigrationMapping Parse(string yaml)
    {
        var root = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(yaml)
            ?? throw new InvalidDataException("mapping.yaml is empty.");

        var version = Str(root, "version") ?? throw new InvalidDataException("mapping.yaml has no version.");
        if (version != "0.2")
            throw new InvalidDataException($"mapping.yaml schema {version} is not supported (expected 0.2).");

        return new MigrationMapping
        {
            Version = version,
            Rules = List(root, "rules").Select(o => ParseRule(Map(o))).ToList(),
            Tools = root.TryGetValue("tools", out var tools)
                ? Map(tools).Select(kv => ParseTool((string)kv.Key, Map(kv.Value))).ToList()
                : [],
            Gaps = root.TryGetValue("gaps", out var gaps)
                ? Map(gaps).Values.SelectMany(v => ((List<object>)v).Select(g => ParseGap(Map(g)))).ToList()
                : [],
            Namespaces = root.TryGetValue("namespaces", out var ns)
                ? Map(ns).ToDictionary(kv => (string)kv.Key, kv => (string)kv.Value, StringComparer.Ordinal)
                : new Dictionary<string, string>(),
        };
    }

    private static MappingRule ParseRule(Dictionary<object, object> r)
    {
        var match = Map(r["match"]);
        var rewrite = r.TryGetValue("rewrite", out var rw) ? Map(rw) : null;
        return new MappingRule(
            Id: Str(r, "id")!,
            Category: Str(r, "category")!,
            Tier: ParseTier(Str(r, "tier")),
            MatchKind: Str(match, "kind")!,
            Names: Strings(match, "names"),
            Receiver: Str(match, "receiver"),
            Namespace: Str(match, "namespace"),
            Attributes: Strings(match, "attributes"),
            Globs: Strings(match, "globs"),
            RewriteKind: rewrite is null ? null : Str(rewrite, "kind"),
            RewriteTo: rewrite is null ? null : Str(rewrite, "to"),
            Handler: rewrite is null ? null : Str(rewrite, "name"),
            Marker: Str(r, "marker"));
    }

    private static ToolFamily ParseTool(string key, Dictionary<object, object> t)
    {
        var tier = ParseTier(Str(t, "tier"));
        var verbs = new Dictionary<string, (string, Tier)>(StringComparer.Ordinal);
        foreach (var (nuke, value) in Map(t["verbs"]))
        {
            if (value is string to) verbs[(string)nuke] = (to, tier);
            else
            {
                var v = Map(value);
                verbs[(string)nuke] = (Str(v, "to")!, v.ContainsKey("tier") ? ParseTier(Str(v, "tier")) : tier);
            }
        }

        var settings = new Dictionary<string, ToolSetting>(StringComparer.Ordinal);
        if (t.TryGetValue("settings", out var s))
        {
            foreach (var (nuke, value) in Map(s))
            {
                var v = Map(value);
                settings[(string)nuke] = new ToolSetting(Str(v, "to"), Str(v, "args"), Str(v, "template"),
                    v.ContainsKey("tier") ? ParseTier(Str(v, "tier")) : tier);
            }
        }

        return new ToolFamily(
            Key: key,
            Id: Str(t, "id")!,
            NukeClass: Str(t, "nuke_class")!,
            NukeNamespace: Str(t, "nuke_namespace")!,
            Package: Str(t, "package")!,
            PackageMajors: Strings(t, "package_majors").Select(int.Parse).ToList(),
            Namespace: Str(t, "namespace")!,
            Facade: Str(t, "facade")!,
            InTree: Str(t, "in_tree") is { } inTree && bool.Parse(inTree),
            Tier: tier,
            Verbs: verbs,
            Settings: settings,
            Notes: Str(t, "notes"));
    }

    private static MappingGap ParseGap(Dictionary<object, object> g)
    {
        var text = Str(g, "workaround") is { } w ? "no Tamp wrapper; workaround: " + w
            : Str(g, "tamp") is { } tamp ? $"reached differently in Tamp: {tamp}" + (Str(g, "note") is { } n ? $" ({n})" : "")
            : Str(g, "note") ?? Str(g, "feature") ?? "";
        return new MappingGap(Str(g, "id")!, Strings(g, "nuke"), text);
    }

    private static Tier ParseTier(string? tier) => tier switch
    {
        "auto" => Tier.Auto,
        "assisted" => Tier.Assisted,
        "manual" => Tier.Manual,
        _ => throw new InvalidDataException($"unknown tier '{tier}'."),
    };

    private static Dictionary<object, object> Map(object o) =>
        o as Dictionary<object, object> ?? throw new InvalidDataException("expected a mapping in mapping.yaml.");

    private static List<object> List(Dictionary<object, object> d, string key) =>
        d.TryGetValue(key, out var v) && v is List<object> l ? l : [];

    private static string? Str(Dictionary<object, object> d, string key) =>
        d.TryGetValue(key, out var v) ? v as string : null;

    private static IReadOnlyList<string> Strings(Dictionary<object, object> d, string key) =>
        List(d, key).Select(o => (string)o).ToList();
}
