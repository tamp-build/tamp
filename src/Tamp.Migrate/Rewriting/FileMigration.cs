using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tamp.Migrate.Mapping;
using Tamp.Migrate.Report;

namespace Tamp.Migrate.Rewriting;

/// <summary>
/// One file's migration. <see cref="Analyse"/> walks the original tree and records edits (keyed by original
/// node) and markers (keyed by the statement / member / using they sit above); <see cref="Apply"/> runs them
/// in one rewrite, then rebuilds the usings.
/// </summary>
internal sealed class FileMigration
{
    // Static BCL receivers whose methods share names with NUKE's path helpers (File.Copy, Directory.Move).
    // A syntactic rewrite can't see types, so a path rule never fires on these.
    private static readonly HashSet<string> BclReceivers = new(StringComparer.Ordinal)
    {
        "File", "Directory", "Path", "Console", "Array", "Enumerable", "Task", "Math", "Regex", "string", "String",
        "Environment", "Process", "Buffer", "Interlocked", "Volatile", "Activator",
    };

    private readonly MigrationMapping _mapping;
    private readonly MigrationOptions _options;
    private readonly string _path;
    private readonly BuildIndex _build;
    private readonly List<MigrationSite> _sites;
    private readonly Dictionary<SyntaxNode, List<Func<SyntaxNode, SyntaxNode?>>> _edits = [];
    private readonly Dictionary<SyntaxNode, List<(int Position, string Line)>> _markers = [];
    private readonly string _eol;

    // Tool families this file calls (by using static or qualified class), and the ones with a call left as-is.
    private readonly HashSet<ToolFamily> _familiesUsed = [];
    private readonly HashSet<ToolFamily> _familiesLeft = [];

    public FileMigration(MigrationMapping mapping, MigrationOptions options, string path, SyntaxTree tree, BuildIndex build, List<MigrationSite> sites)
    {
        _mapping = mapping;
        _options = options;
        _path = path;
        Tree = tree;
        _build = build;
        _sites = sites;
        var text = tree.GetText().ToString();
        _eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    public SyntaxTree Tree { get; }

    /// <summary>The Tamp packages this file's converted code needs (tool wrappers, Tamp.Components).</summary>
    public HashSet<string> Packages { get; } = new(StringComparer.Ordinal);

    /// <summary><c>Main</c> methods that call NUKE's <c>Execute&lt;T&gt;(x =&gt; x.Target)</c>, with the target names.</summary>
    public List<(MethodDeclarationSyntax Main, List<string> Defaults)> EntryPoints { get; } = [];

    private CompilationUnitSyntax Root => (CompilationUnitSyntax)Tree.GetRoot();

    public void Analyse()
    {
        var staticFamilies = Root.Usings
            .Where(u => u.StaticKeyword != default && u.Name is not null)
            .Select(u => _mapping.Tools.FirstOrDefault(t => u.Name!.ToString() == $"{t.NukeNamespace}.{t.NukeClass}"))
            .Where(t => t is not null)
            .Cast<ToolFamily>()
            .ToList();
        // Every family this file reaches, by `using static ..XyzTasks` or `using Nuke.Common.Tools.Xyz`.
        var families = staticFamilies
            .Concat(Root.Usings.Where(u => u.StaticKeyword == default && u.Name is not null)
                .SelectMany(u => _mapping.Tools.Where(t => t.NukeNamespace == u.Name!.ToString())))
            .Distinct()
            .ToList();

        foreach (var node in Root.DescendantNodes())
        {
            switch (node)
            {
                case SimpleBaseTypeSyntax b: BaseType(b); break;
                case AttributeSyntax a: Attribute(a); break;
                case InvocationExpressionSyntax i: Invocation(i, staticFamilies); break;
                case IdentifierNameSyntax id: Identifier(id); SettingsType(id, families); break;
                case GenericNameSyntax g: GenericIdentifier(g); break;
                case MemberAccessExpressionSyntax ma: Member(ma); break;
                case MethodDeclarationSyntax m: Method(m); MemberDeclaration(m, m.Identifier.Text); break;
                case PropertyDeclarationSyntax p: MemberDeclaration(p, p.Identifier.Text); ConfigurationMember(p, p.Type); break;
                case FieldDeclarationSyntax f: ConfigurationMember(f, f.Declaration.Type); break;
            }
        }
    }

    // ---------------------------------------------------------------- matchers

    private void BaseType(SimpleBaseTypeSyntax b)
    {
        var name = b.Type switch { QualifiedNameSyntax q => q.Right.Identifier.Text, SimpleNameSyntax s => s.Identifier.Text, _ => null };
        foreach (var rule in Rules("base_type").Where(r => r.Names.Contains(name!)))
        {
            if (rule.Handler == "configuration-class")
            {
                ConfigurationClass(b, rule);
                continue;
            }
            if (rule.RewriteKind == "rename")
                Edit(b.Type, n => SyntaxFactory.IdentifierName(rule.RewriteTo!).WithTriviaFrom(n));
            Site(b, rule, Outcome.Applied, null);
        }
    }

    private void Attribute(AttributeSyntax a)
    {
        var name = Syntax.AttributeName(a);
        var member = a.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        var siblings = member?.AttributeLists.SelectMany(l => l.Attributes).Select(Syntax.AttributeName).ToList() ?? [];

        foreach (var rule in Rules("attribute").Where(r => r.Names.Contains(name)))
        {
            switch (rule.RewriteKind, rule.Handler)
            {
                case ("keep", _):
                    // A [Parameter] next to [Secret] is consumed by the secret-field handler.
                    if (!(rule.Id == "injection.parameter" && siblings.Contains("Secret")))
                        Resolve(a, rule); // auto: applied; assisted (valid as written, but check): marker
                    break;
                case ("rename", _):
                    Edit(a.Name, n => SyntaxFactory.IdentifierName(rule.RewriteTo!).WithTriviaFrom(n));
                    Resolve(a, rule);
                    break;
                case ("handler", "from-path"):
                    FromPath(a, rule);
                    break;
                case ("handler", "secret-field"):
                    SecretField(a, rule);
                    break;
                default:
                    Resolve(a, rule, rewritten: false);
                    break;
            }
        }
    }

    private void Invocation(InvocationExpressionSyntax inv, List<ToolFamily> staticFamilies)
    {
        var name = Syntax.InvokedName(inv);
        if (name is null) return;
        var receiver = Syntax.Receiver(inv);
        var receiverName = (receiver as IdentifierNameSyntax)?.Identifier.Text;
        // `Serilog.Log.Information(..)` names the receiver `Log` too: match a rule's receiver on the last name.
        var receiverTail = receiver switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            _ => null,
        };

        // NUKE tool task: `DotNetBuild(..)` under `using static ..DotNetTasks`, or `DotNetTasks.DotNetBuild(..)`.
        var family = receiverName is not null
            ? _mapping.Tools.FirstOrDefault(t => t.NukeClass == receiverName)
            : staticFamilies.FirstOrDefault(t => t.Verbs.ContainsKey(name) || name.StartsWith(t.VerbPrefix, StringComparison.Ordinal));
        if (family is not null)
        {
            ToolCall(inv, family, name);
            return;
        }

        if (receiverName is not null && _mapping.Gaps.FirstOrDefault(g => g.NukeClasses.Contains(receiverName)) is { } gap)
        {
            Mark(inv, gap.Id, Outcome.Unmapped, $"{receiverName}.{name}: {gap.Text}");
            return;
        }

        if (receiver is null && inv.Expression is IdentifierNameSyntax field && _build.ToolFields.Contains(field.Identifier.Text))
        {
            foreach (var rule in Rules("field_invocation"))
            {
                if (rule.RewriteKind == "template" && !rule.RewriteTo!.Contains("{receiver}.", StringComparison.Ordinal))
                {
                    Resolve(inv, rule, rewritten: false);
                    continue;
                }
                if (rule.RewriteKind == "template")
                    Edit(inv, n => Template(rule.RewriteTo!, field.Identifier.Text, ArgsText((InvocationExpressionSyntax)n)).WithTriviaFrom(n));
                Resolve(inv, rule, rewritten: rule.RewriteKind == "template");
            }
            return;
        }

        foreach (var rule in Rules("invocation").Where(r => r.Names.Contains(name)))
        {
            if (rule.Receiver is not null && rule.Receiver != receiverTail) continue;
            if (rule.Receiver is null && receiverName is not null && BclReceivers.Contains(receiverName)) continue;

            switch (rule.RewriteKind)
            {
                case "keep":
                    Resolve(inv, rule, rewritten: true);
                    break;
                case "rename":
                    Edit(inv, n => Rename((InvocationExpressionSyntax)n, rule.RewriteTo!));
                    Resolve(inv, rule);
                    break;
                case "template" when receiver is not null || !rule.RewriteTo!.Contains("{receiver}", StringComparison.Ordinal):
                    Edit(inv, n =>
                    {
                        var visited = (InvocationExpressionSyntax)n;
                        var template = rule.RewriteTo!;
                        // Keep a fluent chain's line break: `{receiver}.` takes the original `\n    .` with it.
                        if (visited.Expression is MemberAccessExpressionSyntax vma)
                            template = template.Replace("{receiver}.", vma.Expression.WithoutLeadingTrivia().ToFullString() + vma.OperatorToken.ToFullString(), StringComparison.Ordinal);
                        var r = Syntax.Receiver(visited)?.ToString() ?? "";
                        return Template(template, r, ArgsText(visited)).WithTriviaFrom(n);
                    });
                    Resolve(inv, rule);
                    break;
                default:
                    Resolve(inv, rule, rewritten: false);
                    break;
            }
        }
    }

    private void Identifier(IdentifierNameSyntax id)
    {
        // Invoked names belong to the invocation rules; names inside usings, attribute names and qualified
        // names (namespaces) aren't the build's API surface.
        if (id.Parent is InvocationExpressionSyntax inv && inv.Expression == id) return;
        if (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id
            && ma.Expression is not (ThisExpressionSyntax or BaseExpressionSyntax or IdentifierNameSyntax { Identifier.Text: "NukeBuild" }))
            return;
        if (id.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax } ima && ima.Name == id) return;
        if (id.Parent is QualifiedNameSyntax || id.FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null) return;
        if (id.Parent is AttributeSyntax) return;
        if (id.Parent is SimpleBaseTypeSyntax && Rules("base_type").Any(r => r.Names.Contains(id.Identifier.Text))) return;

        foreach (var rule in Rules("identifier").Where(r => r.Names.Contains(id.Identifier.Text)))
        {
            if (rule.RewriteKind == "rename")
                Edit(id, n => SyntaxFactory.IdentifierName(rule.RewriteTo!).WithTriviaFrom(n));
            Resolve(id, rule, rewritten: rule.RewriteKind is "keep" or "rename");
        }
    }

    // `x.AllProjects` (not invoked).
    private void Member(MemberAccessExpressionSyntax ma)
    {
        if (ma.Parent is InvocationExpressionSyntax inv && inv.Expression == ma) return;
        foreach (var rule in Rules("member").Where(r => r.Names.Contains(ma.Name.Identifier.Text)))
        {
            if (rule.RewriteKind == "rename")
                Edit(ma, n => ((MemberAccessExpressionSyntax)n).WithName(SyntaxFactory.IdentifierName(rule.RewriteTo!).WithTriviaFrom(((MemberAccessExpressionSyntax)n).Name)));
            Resolve(ma, rule, rewritten: rule.RewriteKind is "keep" or "rename");
        }
    }

    // A member declared with a rule's name: NUKE's `protected override void OnBuildInitialized()`.
    private void MemberDeclaration(MemberDeclarationSyntax member, string name)
    {
        foreach (var rule in Rules("member").Where(r => r.Names.Contains(name)))
            Resolve(member, rule, rewritten: false);
    }

    // `Configure<DotNetPackSettings>`: an identifier rule matched on a generic name.
    private void GenericIdentifier(GenericNameSyntax g)
    {
        if (g.Parent is InvocationExpressionSyntax inv && inv.Expression == g) return;
        if (g.Parent is MemberAccessExpressionSyntax ma && ma.Name == g) return;
        foreach (var rule in Rules("identifier").Where(r => r.Names.Contains(g.Identifier.Text)))
            Resolve(g, rule, rewritten: false);
    }

    // `DotNetBuildSettings BuildDefaults(DotNetBuildSettings s)`: after the usings change, a NUKE settings type
    // name silently names Tamp's type of the same name, whose setters differ. Derived from each family's verbs.
    private void SettingsType(IdentifierNameSyntax id, IReadOnlyList<ToolFamily> families)
    {
        var name = id.Identifier.Text;
        if (!name.EndsWith("Settings", StringComparison.Ordinal)) return;
        if (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id) return;
        var family = families.FirstOrDefault(f => f.Verbs.ContainsKey(name[..^"Settings".Length]));
        if (family is null) return;
        Mark(id, family.Id, Outcome.Manual,
            $"{name} here was NUKE's settings type; it now names Tamp's {name}, whose setters differ. Check each setter used on it against {family.Id}'s settings, or inline them into the {family.Facade} call.");
    }

    // With the template Configuration removed, `Configuration` means Tamp's enum: flag where it's declared.
    private void ConfigurationMember(MemberDeclarationSyntax member, TypeSyntax type)
    {
        if (!_build.TemplateConfiguration || type is not IdentifierNameSyntax { Identifier.Text: "Configuration" }) return;
        if (member.FirstAncestorOrSelf<ClassDeclarationSyntax>() is { } cls && Syntax.IsTemplateConfiguration(cls)) return;
        var rule = _mapping.Rules.First(r => r.Handler == "configuration-class");
        Mark(member, rule.Id, Outcome.Assisted, rule.Marker!);
    }

    private void Method(MethodDeclarationSyntax m)
    {
        if (m.Identifier.Text != "Main" || !m.Modifiers.Any(SyntaxKind.StaticKeyword)) return;
        var call = m.ExpressionBody?.Expression as InvocationExpressionSyntax
            ?? (m.Body?.Statements is [ReturnStatementSyntax { Expression: InvocationExpressionSyntax r }] ? r : null);
        if (call?.Expression is not GenericNameSyntax { Identifier.Text: "Execute" }) return;
        var args = call.ArgumentList.Arguments;
        if (args.Any(a => a.Expression is not SimpleLambdaExpressionSyntax)) return; // already Tamp: Execute<T>(args)
        if (args.Count == 0 && m.ParameterList.Parameters.Count > 0) return;
        var defaults = args
            .Select(a => (SimpleLambdaExpressionSyntax)a.Expression)
            .Select(l => l.ExpressionBody is MemberAccessExpressionSyntax body ? body.Name.Identifier.Text : null)
            .ToList();
        if (defaults.Any(d => d is null)) return;
        EntryPoints.Add((m, defaults.Select(d => d!).ToList()));
    }

    // ---------------------------------------------------------------- handlers

    // The `nuke :setup` template: `class Configuration : Enumeration` with static Debug/Release fields and an
    // implicit string conversion. Tamp has that type built in, and a build-declared one would hide it.
    private void ConfigurationClass(SimpleBaseTypeSyntax b, MappingRule rule)
    {
        if (b.FirstAncestorOrSelf<ClassDeclarationSyntax>() is not { } cls) return;
        var isTemplate = Syntax.IsTemplateConfiguration(cls);
        if (!isTemplate)
        {
            Mark(cls, rule.Id, Outcome.Manual, cls.Identifier.Text == "Configuration"
                ? "This Configuration has values beyond Debug/Release, so it isn't replaced by Tamp's built-in Configuration. Keep it as a string parameter, or as an enum named differently: a build-declared Configuration hides Tamp's."
                : "NUKE's Enumeration has no Tamp equivalent. Make this an enum, or a string with the allowed values checked in a target.");
            return;
        }
        // Removed, so the marker goes on the members typed Configuration (ConfigurationMember), not on the class.
        Edit(cls, _ => null);
        Site(cls, rule, Outcome.Assisted, rule.Marker);
    }

    /// <summary>True when the migration removed every member of a file that had some (delete the file).</summary>
    public bool EmptiedByMigration { get; private set; }

    private void FromPath(AttributeSyntax a, MappingRule rule)
    {
        var explicitName = a.ArgumentList?.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
        var field = a.FirstAncestorOrSelf<FieldDeclarationSyntax>();
        var name = explicitName?.Token.ValueText
            ?? field?.Declaration.Variables.FirstOrDefault()?.Identifier.Text.ToLowerInvariant();
        if (name is null)
        {
            Mark(a, rule.Id, Outcome.Manual, $"[{Syntax.AttributeName(a)}] isn't on a field; write the [FromPath(\"...\")] Tool by hand.");
            return;
        }
        var literal = SyntaxFactory.Literal(name).Text;
        Edit(a, n => SyntaxFactory.ParseAttributeArgumentList($"({literal})") is var list
            ? SyntaxFactory.Attribute(SyntaxFactory.IdentifierName("FromPath"), list).WithTriviaFrom(n)
            : n);
        Resolve(a, rule);
    }

    private void SecretField(AttributeSyntax a, MappingRule rule)
    {
        if (a.FirstAncestorOrSelf<FieldDeclarationSyntax>() is not { } field)
        {
            Resolve(a, rule, rewritten: false);
            return;
        }

        var needsType = field.Declaration.Type is PredefinedTypeSyntax p && p.Keyword.IsKind(SyntaxKind.StringKeyword);
        var hasParameter = field.AttributeLists.SelectMany(l => l.Attributes).Any(x => Syntax.AttributeName(x) == "Parameter");
        if (needsType || hasParameter)
        {
            Edit(field, n =>
            {
                var f = (FieldDeclarationSyntax)n;
                var leading = f.GetLeadingTrivia();
                var lists = new List<AttributeListSyntax>();
                foreach (var list in f.AttributeLists)
                {
                    var attributes = list.Attributes;
                    while (attributes.FirstOrDefault(x => Syntax.AttributeName(x) == "Parameter") is { } parameter)
                        attributes = attributes.Remove(parameter);
                    if (attributes.Count > 0) lists.Add(list.WithAttributes(attributes));
                }
                if (lists.Count > 0) lists[0] = lists[0].WithLeadingTrivia(leading);
                var result = f.WithAttributeLists(SyntaxFactory.List(lists));
                if (needsType)
                    result = result.WithDeclaration(result.Declaration.WithType(
                        SyntaxFactory.IdentifierName("Secret").WithTriviaFrom(result.Declaration.Type)));
                return lists.Count > 0 ? result : result.WithLeadingTrivia(leading);
            });
        }
        Resolve(a, rule, rewritten: needsType || hasParameter, anchor: field);
    }

    private void ToolCall(InvocationExpressionSyntax inv, ToolFamily family, string verb)
    {
        _familiesUsed.Add(family);
        var major = _options.PackageMajor;

        if (!family.Verbs.TryGetValue(verb, out var to))
        {
            Leave(inv, family, Outcome.Unmapped,
                $"{family.Id} has no mapping for {verb}. Write it with the {family.PackageFor(major)} wrapper, or a [FromPath] Tool.");
            return;
        }

        var target = $"{family.Facade}.{to.To}";
        if (family.Tier != Tier.Auto)
        {
            Leave(inv, family, Outcome.Manual,
                $"{verb} -> {target} ({family.PackageFor(major)}). Add the package and port this call and its settings by hand; "
                + $"{family.Id}'s settings aren't mapped." + (family.Notes is { } n ? $" Note: {n}" : ""));
            return;
        }

        // The settings lambda: `s => s.SetA(..).SetB(..)`, every link mapped.
        var chain = new List<(string Name, ToolSetting Setting)>();
        if (inv.ArgumentList.Arguments.Count > 1)
        {
            Leave(inv, family, Outcome.Manual, $"{verb} with {inv.ArgumentList.Arguments.Count} arguments isn't a shape {family.Id} maps; port it by hand.");
            return;
        }
        if (inv.ArgumentList.Arguments.Count == 1)
        {
            var body = SettingsBody(inv);
            string? unmapped = null;
            if (body is null || !TryChain(body, family, chain, out unmapped))
            {
                var what = body is null
                    ? "its settings argument isn't a `s => s.Set..()` lambda"
                    : $"{unmapped} has no {family.Id} mapping";
                Leave(inv, family, Outcome.Unmapped, $"{verb} -> {target}: {what}. Port this call by hand.");
                return;
            }
        }

        // Where the call sits decides the shape: NUKE runs the tool on the call; Tamp's verb returns a CommandPlan.
        bool statement = inv.Parent is ExpressionStatementSyntax;
        bool returned = inv.Parent is LambdaExpressionSyntax { Parent: ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax executes } } }
            && Syntax.InvokedName(executes) == "Executes";
        if (!statement && !returned)
        {
            Leave(inv, family, Outcome.Manual,
                $"{verb}'s result is used here; {target} returns a CommandPlan. Return it from Executes, or run it with ProcessRunner.Run.");
            return;
        }

        var assisted = chain.Where(c => c.Setting.Tier != Tier.Auto).Select(c => c.Name).Reverse().ToList();
        if (to.Tier != Tier.Auto) assisted.Insert(0, verb);
        Edit(inv, n => RewriteToolCall((InvocationExpressionSyntax)n, family, to.To, statement));

        if (assisted.Count == 0) Site(inv, family.Id, Outcome.Applied, null);
        else
            Mark(inv, family.Id, Outcome.Assisted,
                $"Converted to {target}; confirm {string.Join(", ", assisted)}." + (family.Notes is { } n ? $" Note: {n}" : ""));

    }

    private static ExpressionSyntax? SettingsBody(InvocationExpressionSyntax inv)
    {
        var (param, body) = inv.ArgumentList.Arguments[0].Expression switch
        {
            SimpleLambdaExpressionSyntax s => (s.Parameter.Identifier.Text, s.ExpressionBody),
            ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } p => (p.ParameterList.Parameters[0].Identifier.Text, p.ExpressionBody),
            _ => (null, null),
        };
        return body is not null && Syntax.ChainRoot(body) is IdentifierNameSyntax root && root.Identifier.Text == param ? body : null;
    }

    private static bool TryChain(ExpressionSyntax body, ToolFamily family, List<(string, ToolSetting)> chain, out string? unmapped)
    {
        unmapped = null;
        var e = body;
        while (e is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma })
        {
            var name = ma.Name.Identifier.Text;
            if (!family.Settings.TryGetValue(name, out var setting))
            {
                unmapped = name;
                return false;
            }
            chain.Add((name, setting));
            e = ma.Expression;
        }
        if (e is not IdentifierNameSyntax)
        {
            unmapped = e.ToString();
            return false;
        }
        return true;
    }

    private SyntaxNode RewriteToolCall(InvocationExpressionSyntax inv, ToolFamily family, string verb, bool statement)
    {
        var args = inv.ArgumentList;
        if (args.Arguments.Count == 1)
        {
            var lambda = (LambdaExpressionSyntax)args.Arguments[0].Expression;
            var body = RewriteSettings((ExpressionSyntax)lambda.Body, family);
            var newLambda = lambda.WithBody(body);
            args = args.WithArguments(SyntaxFactory.SingletonSeparatedList(args.Arguments[0].WithExpression(newLambda)));
        }

        ExpressionSyntax call = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(family.Facade), SyntaxFactory.IdentifierName(verb)),
            args);
        if (statement)
            call = SyntaxFactory.InvocationExpression(
                SyntaxFactory.ParseExpression("ProcessRunner.Run"),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(call))));
        return call.WithTriviaFrom(inv);
    }

    private static ExpressionSyntax RewriteSettings(ExpressionSyntax e, ToolFamily family)
    {
        if (e is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } inv) return e;
        var inner = RewriteSettings(ma.Expression, family);
        var setting = family.Settings[ma.Name.Identifier.Text];
        string name;
        var args = inv.ArgumentList;
        if (setting.Template is { } template)
        {
            var open = template.IndexOf('(', StringComparison.Ordinal);
            name = template[..open];
            args = SyntaxFactory.ParseArgumentList(template[open..].Replace("{args}", ArgsText(inv), StringComparison.Ordinal))
                .WithTriviaFrom(inv.ArgumentList);
        }
        else
        {
            name = setting.To!;
            if (setting.Args is { } fixedArgs)
                args = SyntaxFactory.ParseArgumentList($"({fixedArgs})").WithTriviaFrom(inv.ArgumentList);
        }
        return inv
            .WithExpression(ma.WithExpression(inner).WithName(SyntaxFactory.IdentifierName(name).WithTriviaFrom(ma.Name)))
            .WithArgumentList(args);
    }

    private void Leave(SyntaxNode node, ToolFamily family, Outcome outcome, string marker)
    {
        _familiesLeft.Add(family);
        Mark(node, family.Id, outcome, marker);
    }

    // ---------------------------------------------------------------- edits, markers, sites

    public void Edit(SyntaxNode original, Func<SyntaxNode, SyntaxNode?> edit)
    {
        if (!_edits.TryGetValue(original, out var list)) _edits[original] = list = [];
        list.Add(edit);
    }

    /// <summary>Record a site for a rule per its tier: auto = applied; otherwise a marker.</summary>
    private void Resolve(SyntaxNode node, MappingRule rule, bool rewritten = true, SyntaxNode? anchor = null)
    {
        if (rule.Tier == Tier.Auto && rewritten)
            Site(node, rule, Outcome.Applied, null);
        else
            Mark(anchor ?? node, rule.Id, rewritten ? Outcome.Assisted : Outcome.Manual, rule.Marker ?? $"{rule.Id}: not converted.");
    }

    public void Mark(SyntaxNode node, MappingRule rule, Outcome outcome, string text) => Mark(node, rule.Id, outcome, text);

    public void Mark(SyntaxNode node, string id, Outcome outcome, string text)
    {
        Site(node, id, outcome, text);
        var anchor = Anchor(node);
        var line = $"// {NukeMigrator.MarkerPrefix}({id}): {text}";
        if (anchor.GetLeadingTrivia().ToFullString().Contains($"{NukeMigrator.MarkerPrefix}({id})", StringComparison.Ordinal))
            return; // already marked by an earlier run
        if (!_markers.TryGetValue(anchor, out var list)) _markers[anchor] = list = [];
        if (!list.Any(x => x.Line == line)) list.Add((node.SpanStart, line));
    }

    public void Site(SyntaxNode node, MappingRule rule, Outcome outcome, string? marker) => Site(node, rule.Id, outcome, marker);

    private void Site(SyntaxNode node, string id, Outcome outcome, string? marker) =>
        _sites.Add(new MigrationSite(_path, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, id, outcome, marker));

    private static SyntaxNode Anchor(SyntaxNode node) =>
        node.AncestorsAndSelf().FirstOrDefault(n => n is (StatementSyntax and not BlockSyntax) or MemberDeclarationSyntax or UsingDirectiveSyntax)
        ?? node;

    private IEnumerable<MappingRule> Rules(string kind) => _mapping.Rules.Where(r => r.MatchKind == kind);

    // ---------------------------------------------------------------- apply

    public string Apply()
    {
        var rewritten = (CompilationUnitSyntax)new Applier(this).Visit(Root)!;
        rewritten = Usings(rewritten);
        EmptiedByMigration = Root.Members.Count > 0 && rewritten.Members.Count == 0;
        return rewritten.ToFullString();
    }

    private sealed class Applier(FileMigration m) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? Visit(SyntaxNode? node)
        {
            if (node is null) return null;
            var visited = base.Visit(node);
            if (visited is null) return null;
            if (m._edits.TryGetValue(node, out var edits))
            {
                foreach (var edit in edits)
                {
                    var next = edit(visited);
                    if (next is null) return null; // the edit removed the node
                    visited = next;
                }
            }
            if (m._markers.TryGetValue(node, out var lines))
                visited = m.WithMarkers(node, visited, lines.OrderBy(l => l.Position).Select(l => l.Line));
            return visited;
        }
    }

    private SyntaxNode WithMarkers(SyntaxNode original, SyntaxNode node, IEnumerable<string> lines)
    {
        var leading = node.GetLeadingTrivia();
        var previous = original.GetFirstToken().GetPreviousToken();
        var startsLine = previous == default
            || previous.TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia))
            || leading.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia));

        var trivia = new List<SyntaxTrivia>();
        string indent;
        if (startsLine)
        {
            indent = leading.Count > 0 && leading[^1].IsKind(SyntaxKind.WhitespaceTrivia) ? leading[^1].ToString() : "";
            trivia.AddRange(indent.Length > 0 ? leading.Take(leading.Count - 1) : leading);
        }
        else
        {
            // `{ Call(); }` on one line: break the line so the marker sits above the statement it names
            // (and a second run finds it in that statement's leading trivia).
            var line = Tree.GetText().Lines.GetLineFromPosition(original.SpanStart).ToString();
            indent = line[..(line.Length - line.TrimStart().Length)];
            trivia.Add(SyntaxFactory.EndOfLine(_eol));
        }
        foreach (var line in lines)
        {
            if (indent.Length > 0) trivia.Add(SyntaxFactory.Whitespace(indent));
            trivia.Add(SyntaxFactory.Comment(line));
            trivia.Add(SyntaxFactory.EndOfLine(_eol));
        }
        if (indent.Length > 0) trivia.Add(SyntaxFactory.Whitespace(indent));
        return node.WithLeadingTrivia(trivia);
    }

    // ---------------------------------------------------------------- usings

    private CompilationUnitSyntax Usings(CompilationUnitSyntax cu)
    {
        var usingsRule = _mapping.Rules.FirstOrDefault(r => r.MatchKind == "using" && r.Handler == "usings");
        var componentsRule = _mapping.Rules.FirstOrDefault(r => r.MatchKind == "using" && r.Handler == "components-namespace");
        if (usingsRule is null) return cu;

        var major = _options.PackageMajor;
        var add = new SortedSet<string>(StringComparer.Ordinal);
        var result = new List<UsingDirectiveSyntax>();
        var carried = SyntaxTriviaList.Empty; // comments on dropped usings (a file header) move to the Tamp usings
        var insertAt = -1;

        for (var i = 0; i < cu.Usings.Count; i++)
        {
            var u = cu.Usings[i];
            var original = Root.Usings[i];
            var name = u.Name?.ToString();
            if (name is null || !IsNuke(name, usingsRule.Namespace!))
            {
                result.Add(u);
                continue;
            }
            if (insertAt < 0) insertAt = result.Count;
            var isStatic = u.StaticKeyword != default;
            var family = isStatic
                ? _mapping.Tools.FirstOrDefault(t => name == $"{t.NukeNamespace}.{t.NukeClass}")
                : _mapping.Tools.FirstOrDefault(t => name == t.NukeNamespace);

            if (family is not null)
            {
                if (family.Tier == Tier.Auto)
                {
                    add.Add("Tamp");
                    add.Add(family.NamespaceFor(major));
                    Packages.Add(family.PackageFor(major));
                }
                if (family.Tier == Tier.Auto && !_familiesLeft.Contains(family))
                {
                    Site(original, usingsRule, Outcome.Applied, null);
                    Drop(u);
                }
                else
                {
                    var text = family.Tier == Tier.Auto
                        ? $"Kept while a {family.NukeClass} call in this file is unconverted; remove it once they're ported."
                        : $"Add {family.PackageFor(major)} and `using {family.NamespaceFor(major)};`, then remove this once its calls are ported.";
                    result.Add(MarkUsing(u, original, family.Id, Outcome.Manual, text));
                }
                continue;
            }

            if (isStatic && _mapping.Gaps.FirstOrDefault(g => g.NukeClasses.Any(c => name.EndsWith("." + c, StringComparison.Ordinal))) is { } gap)
            {
                result.Add(MarkUsing(u, original, gap.Id, Outcome.Unmapped, gap.Text));
                continue;
            }

            if (!isStatic && componentsRule is not null && IsNuke(name, componentsRule.Namespace!))
            {
                var renamed = SyntaxFactory.ParseCompilationUnit($"using Tamp{name["Nuke".Length..]};{_eol}").Usings[0]
                    .WithLeadingTrivia(u.GetLeadingTrivia());
                add.Add("Tamp");
                Packages.Add("Tamp.Components");
                result.Add(MarkUsing(renamed, original, componentsRule.Id, Outcome.Assisted, componentsRule.Marker!));
                continue;
            }

            if (!isStatic && _mapping.Namespaces.TryGetValue(name, out var tamp))
            {
                add.Add(tamp);
                Site(original, usingsRule, Outcome.Applied, null);
                Drop(u);
                continue;
            }

            result.Add(MarkUsing(u, original, usingsRule.Id, Outcome.Unmapped,
                $"`{name}` has no Tamp mapping. Replace what this file uses from it, then remove the using."));
        }

        if (insertAt < 0) return cu;
        var present = result.Where(u => u.StaticKeyword == default && u.Alias is null)
            .Select(u => u.Name?.ToString()).ToHashSet(StringComparer.Ordinal);
        result.InsertRange(insertAt, add.Where(ns => !present.Contains(ns))
            .Select(ns => SyntaxFactory.ParseCompilationUnit($"using {ns};{_eol}").Usings[0]));

        if (carried.Count > 0)
        {
            if (insertAt < result.Count)
                result[insertAt] = result[insertAt].WithLeadingTrivia(carried.AddRange(result[insertAt].GetLeadingTrivia()));
            else if (cu.Members.Count > 0)
                cu = cu.WithMembers(cu.Members.Replace(cu.Members[0], cu.Members[0].WithLeadingTrivia(carried.AddRange(cu.Members[0].GetLeadingTrivia()))));
        }
        return cu.WithUsings(SyntaxFactory.List(result));

        void Drop(UsingDirectiveSyntax u)
        {
            var lead = u.GetLeadingTrivia();
            if (lead.Any(x => !x.IsKind(SyntaxKind.WhitespaceTrivia) && !x.IsKind(SyntaxKind.EndOfLineTrivia)))
                carried = carried.AddRange(lead);
        }
    }

    private UsingDirectiveSyntax MarkUsing(UsingDirectiveSyntax u, UsingDirectiveSyntax original, string id, Outcome outcome, string text)
    {
        Site(original, id, outcome, text);
        if (original.GetLeadingTrivia().ToFullString().Contains($"{NukeMigrator.MarkerPrefix}({id})", StringComparison.Ordinal)) return u;
        return (UsingDirectiveSyntax)WithMarkers(original, u, [$"// {NukeMigrator.MarkerPrefix}({id}): {text}"]);
    }

    private static bool IsNuke(string name, string ns) =>
        name == ns || name.StartsWith(ns + ".", StringComparison.Ordinal);

    private static string ArgsText(InvocationExpressionSyntax inv) =>
        string.Join(", ", inv.ArgumentList.Arguments.Select(a => a.ToString()));

    private static ExpressionSyntax Template(string template, string receiver, string args) =>
        SyntaxFactory.ParseExpression(template
            .Replace("{receiver}", receiver, StringComparison.Ordinal)
            .Replace("{args}", args, StringComparison.Ordinal));

    private static SyntaxNode Rename(InvocationExpressionSyntax inv, string to) => inv.Expression switch
    {
        MemberAccessExpressionSyntax ma => inv.WithExpression(ma.WithName(SyntaxFactory.IdentifierName(to).WithTriviaFrom(ma.Name))),
        IdentifierNameSyntax id => inv.WithExpression(SyntaxFactory.IdentifierName(to).WithTriviaFrom(id)),
        _ => inv,
    };
}
