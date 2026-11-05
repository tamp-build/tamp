using System.Reflection;
using System.Text.RegularExpressions;
using Tamp.Components;
using Tamp.NetCli.V10;
using Xunit;
using YamlDotNet.Serialization;

namespace Tamp.Migrate.Tests;

/// <summary>
/// ADR 0021 / #58: <c>docs/nuke-migration/mapping.yaml</c> is what <c>tamp migrate nuke</c> and the agent path
/// act on, so it is checked like code. Ids are unique and stable in shape, every field uses the closed
/// vocabulary its schema header declares, the tiers are self-consistent, and every Tamp-side symbol of an
/// in-tree package actually exists.
/// </summary>
public sealed class MappingTests
{
    private static readonly string[] Categories = ["boilerplate", "injection", "paths", "target_dsl", "logging", "components"];
    private static readonly string[] Tiers = ["auto", "assisted", "manual"];
    private static readonly string[] RewriteKinds = ["keep", "rename", "template", "remove", "handler"];
    private static readonly string[] Placeholders = ["receiver", "args"];
    private static readonly string[] Priorities = ["high", "med", "low"];
    private static readonly Regex IdPattern = new(@"^[a-z][a-z0-9_]*(\.[a-z0-9][a-z0-9-]*)+$");

    // Each match kind and the fields it must carry.
    private static readonly Dictionary<string, string[]> MatchKinds = new()
    {
        ["base_type"] = ["names"],
        ["attribute"] = ["names"],
        ["invocation"] = ["names"],
        ["identifier"] = ["names"],
        ["member"] = ["names"],
        ["using"] = ["namespace"],
        ["entrypoint"] = [],
        ["field_invocation"] = ["attributes"],
        ["file"] = ["globs"],
    };

    private static readonly Assembly[] InTree = [typeof(TampBuild).Assembly, typeof(DotNet).Assembly, typeof(ICompile).Assembly];

    private static readonly Lazy<Dictionary<string, object>> Mapping = new(() =>
        new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "nuke-migration", "mapping.yaml"))));

    private static IEnumerable<Dictionary<object, object>> Rules => List(Mapping.Value["rules"]).Select(Map);
    private static Dictionary<object, object> Tools => Map(Mapping.Value["tools"]);

    [Fact]
    public void Is_Schema_0_2_For_Nuke_8_And_Later()
    {
        Assert.Equal("0.2", Mapping.Value["version"]);
        Assert.Equal(">=8.0", Mapping.Value["nuke"]);
    }

    [Fact]
    public void Ids_Are_Unique_And_Well_Formed()
    {
        var ids = Rules.Select(r => Str(r, "id"))
            .Concat(Tools.Values.Select(t => Str(Map(t), "id")))
            .Concat(GapEntries().Select(g => Str(g, "id")))
            .ToList();
        Assert.All(ids, id => Assert.Matches(IdPattern, id));
        var duplicates = ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "duplicate ids: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void Each_Rule_Id_Starts_With_Its_Category()
    {
        foreach (var r in Rules)
        {
            var category = Str(r, "category");
            Assert.Contains(category, Categories);
            Assert.StartsWith(category + ".", Str(r, "id"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Each_Rule_Has_A_Valid_Match()
    {
        foreach (var r in Rules)
        {
            var id = Req(r, "id");
            var match = Map(r["match"]);
            var kind = Req(match, "kind");
            Assert.True(MatchKinds.TryGetValue(kind, out var required), $"{id}: unknown match kind '{kind}'");
            foreach (var field in required)
            {
                Assert.True(match.ContainsKey(field), $"{id}: match kind '{kind}' needs '{field}'");
                if (field != "namespace") Assert.NotEmpty(List(match[field]));
            }
        }
    }

    [Fact]
    public void Tiers_Are_Self_Consistent()
    {
        foreach (var r in Rules)
        {
            var id = Str(r, "id");
            var tier = Str(r, "tier");
            Assert.Contains(tier, Tiers);
            var hasRewrite = r.ContainsKey("rewrite");
            var hasMarker = r.ContainsKey("marker") && !string.IsNullOrWhiteSpace(Str(r, "marker"));
            switch (tier)
            {
                case "auto":
                    Assert.True(hasRewrite, $"{id}: an auto rule must say how it rewrites (keep, if nothing changes)");
                    Assert.False(hasMarker, $"{id}: an auto rule leaves no marker; use a note");
                    break;
                case "assisted":
                    Assert.True(hasMarker, $"{id}: an assisted rule must tell the person what to confirm");
                    break;
                case "manual":
                    Assert.False(hasRewrite, $"{id}: a manual rule must not rewrite");
                    Assert.True(hasMarker, $"{id}: a manual rule must say what to do by hand");
                    break;
            }
            if (Str(r, "tamp") is null) Assert.NotEqual("auto", tier);
        }
    }

    [Fact]
    public void Rewrites_Use_Known_Kinds_Placeholders_And_Handlers()
    {
        var handlers = Map(Mapping.Value["handlers"]).Keys.Cast<string>().ToHashSet();
        var used = new HashSet<string>();
        foreach (var r in Rules.Where(r => r.ContainsKey("rewrite")))
        {
            var id = Req(r, "id");
            var rewrite = Map(r["rewrite"]);
            var kind = Req(rewrite, "kind");
            Assert.True(RewriteKinds.Contains(kind), $"{id}: unknown rewrite kind '{kind}'");
            switch (kind)
            {
                case "rename":
                    Assert.Matches(@"^[A-Za-z_][A-Za-z0-9_]*$", Str(rewrite, "to"));
                    break;
                case "template":
                    AssertPlaceholders(id, Str(rewrite, "to")!);
                    break;
                case "handler":
                    var name = Str(rewrite, "name")!;
                    Assert.True(handlers.Contains(name), $"{id}: unknown handler '{name}'");
                    used.Add(name);
                    break;
            }
        }
        Assert.Empty(handlers.Except(used));
    }

    [Fact]
    public void Every_Tamp_Symbol_A_Rule_Names_Exists()
    {
        var missing = Rules.Where(r => r.ContainsKey("verify"))
            .SelectMany(r => List(r["verify"]).Select(v => (Id: Str(r, "id"), Symbol: (string)v)))
            .Where(x => !Resolves(x.Symbol))
            .Select(x => $"{x.Id}: {x.Symbol}")
            .ToList();
        Assert.True(missing.Count == 0, "not in Tamp:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_Rule_That_Names_An_In_Tree_Tamp_Api_Verifies_It()
    {
        // A rule that rewrites to (or keeps) Tamp code must say which symbols it relies on, so drift fails here.
        foreach (var r in Rules.Where(r => r.ContainsKey("rewrite") && Str(Map(r["rewrite"]), "kind") is "keep" or "rename" or "template"))
            Assert.True(r.ContainsKey("verify"), $"{Str(r, "id")}: says what Tamp code to write but verifies none of it");
    }

    [Fact]
    public void Tool_Families_Are_Complete()
    {
        foreach (var (key, value) in Tools)
        {
            var t = Map(value);
            var id = Str(t, "id");
            foreach (var field in new[] { "nuke_class", "nuke_namespace", "package", "namespace", "facade", "tier" })
                Assert.False(string.IsNullOrWhiteSpace(Str(t, field)), $"{key}: missing {field}");
            Assert.Contains(Str(t, "tier"), Tiers);
            Assert.EndsWith("Tasks", Str(t, "nuke_class"), StringComparison.Ordinal);
            Assert.StartsWith("Nuke.", Str(t, "nuke_namespace"), StringComparison.Ordinal);
            Assert.Matches(@"^Tamp\.[A-Za-z0-9.]+(\{N\})?$", Str(t, "package"));
            Assert.NotEmpty(Map(t["verbs"]));
            Assert.Equal("tool." + key.ToString()!.Replace('_', '-'), id);
        }
    }

    [Fact]
    public void In_Tree_Tool_Verbs_And_Settings_Exist()
    {
        foreach (var (key, value) in Tools)
        {
            var t = Map(value);
            if (!t.TryGetValue("in_tree", out var inTree) || !bool.Parse((string)inTree)) continue;

            var ns = Str(t, "namespace")!.Replace("{N}", "10", StringComparison.Ordinal);
            var facade = InTree.Select(a => a.GetType($"{ns}.{Str(t, "facade")}")).FirstOrDefault(x => x is not null);
            Assert.True(facade is not null, $"{key}: no {ns}.{Str(t, "facade")}");
            var settingsTypes = InTree.SelectMany(a => a.GetExportedTypes())
                .Where(x => x.Namespace == ns && x.Name.EndsWith("Settings", StringComparison.Ordinal)).ToList();

            foreach (var (nuke, verb) in Map(t["verbs"]))
            {
                var name = verb is string s ? s : Str(Map(verb), "to")!;
                Assert.True(facade!.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(m => m.Name == name),
                    $"{key}: {nuke} -> {facade.Name}.{name}, which doesn't exist");
            }

            foreach (var (nuke, setting) in Map(t["settings"]))
            {
                var s = Map(setting);
                Assert.Contains(Str(s, "tier"), Tiers);
                string name;
                if (s.ContainsKey("template"))
                {
                    var template = Str(s, "template")!;
                    AssertPlaceholders($"{key}.{nuke}", template);
                    name = template[..template.IndexOf('(', StringComparison.Ordinal)];
                }
                else name = Str(s, "to")!;
                Assert.True(settingsTypes.Any(x => x.GetMethods().Any(m => m.Name == name)),
                    $"{key}: setting {nuke} -> {name}, which no {ns} settings type has");
            }
        }
    }

    [Fact]
    public void Namespaces_Map_Nuke_Namespaces_To_In_Tree_Tamp_Namespaces()
    {
        // The `usings` handler drops each key and adds its value, so every value must be a real namespace.
        var tampNamespaces = InTree.SelectMany(a => a.GetExportedTypes()).Select(t => t.Namespace).ToHashSet();
        var namespaces = Map(Mapping.Value["namespaces"]);
        Assert.NotEmpty(namespaces);
        var tools = Tools.Values.Select(t => Str(Map(t), "nuke_namespace")).ToHashSet();
        foreach (var (nuke, tamp) in namespaces)
        {
            Assert.StartsWith("Nuke.", (string)nuke, StringComparison.Ordinal);
            Assert.DoesNotContain((string)nuke, tools); // tool namespaces are mapped by their family
            Assert.True(tampNamespaces.Contains((string)tamp), $"namespaces: {nuke} -> {tamp}, which isn't an in-tree namespace");
        }
    }

    [Fact]
    public void Gaps_Are_Well_Formed()
    {
        var gaps = Map(Mapping.Value["gaps"]);
        foreach (var g in List(gaps["no_wrapper"]).Select(Map))
        {
            Assert.Contains(Str(g, "priority"), Priorities);
            Assert.All(List(g["nuke"]).Cast<string>(), n => Assert.EndsWith("Tasks", n, StringComparison.Ordinal));
        }
        foreach (var g in List(gaps["covered_differently"]).Select(Map))
            Assert.All(List(g["nuke"]).Cast<string>(), n => Assert.EndsWith("Tasks", n, StringComparison.Ordinal));
        foreach (var g in List(gaps["framework_features"]).Select(Map))
            Assert.Contains(Str(g, "status"), new[] { "none", "covered" });
    }

    [Fact]
    public void No_Gap_Names_A_Class_That_Has_A_Tool_Family()
    {
        var mapped = Tools.Values.Select(t => Str(Map(t), "nuke_class")).ToHashSet();
        var gapped = GapEntries().Where(g => g.ContainsKey("nuke")).SelectMany(g => List(g["nuke"]).Cast<string>());
        Assert.All(gapped, g => Assert.DoesNotContain(g, mapped));
    }

    [Fact]
    public void The_Reference_Names_Every_Tool_Package()
    {
        var reference = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "nuke-migration", "mapping-reference.md"));
        foreach (var t in Tools.Values.Select(Map))
            Assert.True(reference.Contains(Str(t, "package")!, StringComparison.Ordinal), $"mapping-reference.md doesn't mention {Str(t, "package")}");
    }

    private static IEnumerable<Dictionary<object, object>> GapEntries()
    {
        var gaps = Map(Mapping.Value["gaps"]);
        return gaps.Values.SelectMany(v => List(v).Select(Map));
    }

    private static void AssertPlaceholders(string id, string template)
    {
        foreach (Match m in Regex.Matches(template, @"\{([^}]*)\}"))
            Assert.True(Placeholders.Contains(m.Groups[1].Value), $"{id}: unknown placeholder {{{m.Groups[1].Value}}} in '{template}'");
    }

    /// <summary><c>Namespace.Type</c> or <c>Namespace.Type.Member</c>, public or not (a build class sees protected members).</summary>
    private static bool Resolves(string symbol)
    {
        if (FindType(symbol) is not null) return true;
        var dot = symbol.LastIndexOf('.');
        if (dot < 0) return false;
        var type = FindType(symbol[..dot]);
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        return type is not null && type.GetMember(symbol[(dot + 1)..], all).Length > 0;
    }

    private static Type? FindType(string name) => InTree.Select(a => a.GetType(name)).FirstOrDefault(t => t is not null);

    private static Dictionary<object, object> Map(object o) => Assert.IsType<Dictionary<object, object>>(o);
    private static List<object> List(object o) => Assert.IsType<List<object>>(o);
    private static string Req(Dictionary<object, object> d, string key) =>
        Str(d, key) ?? throw new Xunit.Sdk.XunitException($"missing '{key}' in {string.Join(", ", d.Keys)}");

    private static string? Str(Dictionary<object, object> d, string key) => d.TryGetValue(key, out var v) ? v as string : null;
}
