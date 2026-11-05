using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tamp.Migrate.Mapping;
using Tamp.Migrate.Report;

namespace Tamp.Migrate.Rewriting;

/// <summary>A source file of the build: a path (relative to the repository root) and its text.</summary>
public sealed record SourceFile(string Path, string Text);

/// <summary>Knobs for a migration run.</summary>
/// <param name="PackageMajor">Major for <c>{N}</c>-versioned wrappers (<c>Tamp.NetCli.V{N}</c>); default: the newest the mapping lists.</param>
/// <param name="TampVersion">Version for the Tamp package references; default: this tool's own (one release train).</param>
/// <param name="BuildDirectory">The NUKE build project's directory, relative to the repository root.</param>
public sealed record MigrationOptions(int? PackageMajor = null, string? TampVersion = null, string BuildDirectory = "build");

/// <summary>The rewritten files (same order as the input), the other files the migration writes, and the coverage report.</summary>
public sealed record MigrationResult(IReadOnlyList<SourceFile> Files, IReadOnlyList<GeneratedFile> Generated, MigrationReport Report);

/// <summary>
/// The <c>tamp migrate nuke</c> rewriter (ADR 0021, Phase 2). Syntactic only: it parses the NUKE build with
/// no restore and no compilation, applies the <c>auto</c>-tier rows of <see cref="MigrationMapping"/>, leaves
/// a <c>// TAMP-MIGRATE(&lt;id&gt;): …</c> marker at every other recognised site, and reports each site.
/// Running it on its own output changes nothing.
/// </summary>
public sealed class NukeMigrator
{
    /// <summary>The marker prefix every site the rewriter won't finish carries.</summary>
    public const string MarkerPrefix = "TAMP-MIGRATE";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private readonly MigrationMapping _mapping;
    private readonly MigrationOptions _options;

    public NukeMigrator(MigrationMapping mapping, MigrationOptions? options = null)
    {
        _mapping = mapping;
        _options = options ?? new MigrationOptions();
        var unsupported = mapping.Rules.Where(r => !IsSupported(r)).Select(r => r.Id).ToList();
        if (unsupported.Count > 0)
            throw new NotSupportedException(
                "mapping.yaml has rules this version of Tamp.Migrate can't apply: " + string.Join(", ", unsupported));
    }

    /// <summary>
    /// Which (match, rewrite) shapes the engine implements. A row added to mapping.yaml in one of these
    /// shapes takes effect with no code change; anything else fails here rather than being skipped.
    /// </summary>
    internal static bool IsSupported(MappingRule r) => (r.MatchKind, r.RewriteKind, r.Handler) switch
    {
        ("base_type", "rename" or "keep", _) => true,
        ("attribute", null or "keep" or "rename", _) => true,
        ("attribute", "handler", "secret-field" or "from-path") => true,
        ("invocation", null or "keep" or "rename" or "template", _) => true,
        ("identifier", null or "keep" or "rename", _) => true,
        ("using", "handler", "usings" or "components-namespace") => true,
        ("entrypoint", "handler", "entrypoint-default") => true,
        ("field_invocation", null or "template", _) => true,
        ("file", null or "handler", _) => true,
        _ => false,
    };

    /// <param name="files">The build's C# files.</param>
    /// <param name="repositoryFiles">Repository-relative paths, for the <c>file</c> rules (build project, bootstrap scripts).</param>
    /// <param name="readFile">Reads a repository-relative file the <c>file</c> rules rewrite; null when it doesn't exist.</param>
    public MigrationResult Migrate(IReadOnlyList<SourceFile> files, IEnumerable<string>? repositoryFiles = null, Func<string, string?>? readFile = null)
    {
        var sites = new List<MigrationSite>();
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Text, ParseOptions, f.Path)).ToList();
        var build = BuildIndex.Create(_mapping, trees);
        var migrations = trees.Select((t, i) => new FileMigration(_mapping, _options, files[i].Path, t, build, sites)).ToList();

        foreach (var m in migrations) m.Analyse();
        EntryPoint(migrations, build);

        var output = new List<SourceFile>();
        var changed = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var text = migrations[i].Apply();
            if (text != files[i].Text) changed.Add(files[i].Path);
            output.Add(new SourceFile(files[i].Path, text));
        }

        var generated = new List<GeneratedFile>();
        if (repositoryFiles is not null)
        {
            var scaffolder = new BuildScaffolder(_mapping, _options, repositoryFiles.Select(f => f.Replace('\\', '/')).ToList(), readFile ?? (_ => null));
            scaffolder.Run(migrations.SelectMany(m => m.Packages).ToHashSet(StringComparer.Ordinal));
            generated = scaffolder.Generated;
            sites.AddRange(scaffolder.Sites);
        }

        var ordered = sites.OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line).ThenBy(s => s.RuleId, StringComparer.Ordinal).ToList();
        return new MigrationResult(output, generated, new MigrationReport
        {
            MappingVersion = _mapping.Version,
            Files = files.Select(f => f.Path).ToList(),
            ChangedFiles = changed.Concat(generated.Select(g => g.Path)).ToList(),
            Sites = ordered,
        });
    }

    // `Main() => Execute<Build>(x => x.Compile)` -> `Main(string[] args) => Execute<Build>(args)` plus
    // `.Default()` on Compile, which may live in another (partial) file.
    private void EntryPoint(List<FileMigration> migrations, BuildIndex build)
    {
        var rule = _mapping.Rules.FirstOrDefault(r => r.MatchKind == "entrypoint");
        if (rule is null) return;
        foreach (var m in migrations)
        {
            foreach (var (main, defaults) in m.EntryPoints)
            {
                if (defaults.Count > 1)
                {
                    m.Mark(main, rule, Outcome.Manual,
                        "NUKE named several default targets; Tamp has one. Mark one target .Default(), or add a target that DependsOn the others.");
                    continue;
                }
                if (defaults.Count == 1)
                {
                    if (!build.Targets.TryGetValue(defaults[0], out var target))
                    {
                        m.Mark(main, rule, Outcome.Manual, $"Default target '{defaults[0]}' wasn't found in the build; add .Default() to it.");
                        continue;
                    }
                    if (!target.AlreadyDefault)
                        migrations.Single(x => x.Tree == target.Root.SyntaxTree).Edit(target.Root, n => AddDefault((ExpressionSyntax)n));
                }
                m.Edit(main, RewriteMain);
                m.Site(main, rule, Outcome.Applied, null);
            }
        }
    }

    private static SyntaxNode AddDefault(ExpressionSyntax root) =>
        SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    root.WithoutTrivia(), SyntaxFactory.IdentifierName("Default")))
            .WithTriviaFrom(root);

    private static SyntaxNode RewriteMain(SyntaxNode node)
    {
        var method = (MethodDeclarationSyntax)node;
        var args = SyntaxFactory.ParseArgumentList("(args)");
        var parameters = SyntaxFactory.ParseParameterList("(string[] args)").WithTriviaFrom(method.ParameterList);
        method = method.WithParameterList(parameters);
        if (method.ExpressionBody is { Expression: InvocationExpressionSyntax inv } body)
            return method.WithExpressionBody(body.WithExpression(inv.WithArgumentList(args.WithTriviaFrom(inv.ArgumentList))));
        var ret = method.Body!.Statements.OfType<ReturnStatementSyntax>().Single();
        var call = (InvocationExpressionSyntax)ret.Expression!;
        return method.ReplaceNode(call, call.WithArgumentList(args.WithTriviaFrom(call.ArgumentList)));
    }

}

/// <summary>Facts about the whole build gathered before any file is rewritten (partial classes span files).</summary>
internal sealed class BuildIndex
{
    public required Dictionary<string, TargetInfo> Targets { get; init; }

    /// <summary>Fields carrying a <c>field_invocation</c> rule's attributes (NUKE <c>Tool</c> delegates).</summary>
    public required HashSet<string> ToolFields { get; init; }

    public static BuildIndex Create(MigrationMapping mapping, IEnumerable<SyntaxTree> trees)
    {
        var toolAttributes = mapping.Rules.Where(r => r.MatchKind == "field_invocation")
            .SelectMany(r => r.Attributes).ToHashSet(StringComparer.Ordinal);
        var targets = new Dictionary<string, TargetInfo>(StringComparer.Ordinal);
        var fields = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in trees.Select(t => t.GetRoot()))
        {
            foreach (var p in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (p.Type is not IdentifierNameSyntax { Identifier.Text: "Target" }) continue;
                if (p.ExpressionBody?.Expression is not SimpleLambdaExpressionSyntax { ExpressionBody: { } body } lambda) continue;
                var chainRoot = Syntax.ChainRoot(body);
                if (chainRoot is not IdentifierNameSyntax id || id.Identifier.Text != lambda.Parameter.Identifier.Text) continue;
                var isDefault = body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                    .Any(i => Syntax.InvokedName(i) == "Default" && i.ArgumentList.Arguments.Count == 0);
                targets.TryAdd(p.Identifier.Text, new TargetInfo(id, isDefault));
            }

            foreach (var f in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                if (!f.AttributeLists.SelectMany(l => l.Attributes).Any(a => toolAttributes.Contains(Syntax.AttributeName(a)))) continue;
                foreach (var v in f.Declaration.Variables) fields.Add(v.Identifier.Text);
            }
        }

        return new BuildIndex { Targets = targets, ToolFields = fields };
    }
}

/// <summary>A target property; <see cref="Root"/> is the <c>_</c> its fluent chain starts from.</summary>
internal sealed record TargetInfo(ExpressionSyntax Root, bool AlreadyDefault);

/// <summary>Small syntactic helpers.</summary>
internal static class Syntax
{
    /// <summary>The innermost receiver of a fluent chain: <c>_</c> in <c>_.A().B()</c>.</summary>
    public static ExpressionSyntax ChainRoot(ExpressionSyntax e)
    {
        while (true)
        {
            switch (e)
            {
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma }: e = ma.Expression; break;
                case MemberAccessExpressionSyntax ma: e = ma.Expression; break;
                default: return e;
            }
        }
    }

    /// <summary>The simple name an invocation calls: <c>Name</c> in <c>x.Name(..)</c>, <c>Name(..)</c> or <c>Name&lt;T&gt;(..)</c>.</summary>
    public static string? InvokedName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        SimpleNameSyntax n => n.Identifier.Text,
        _ => null,
    };

    /// <summary>The receiver of <c>x.Name(..)</c>, or null for an unqualified call.</summary>
    public static ExpressionSyntax? Receiver(InvocationExpressionSyntax inv) =>
        inv.Expression is MemberAccessExpressionSyntax ma ? ma.Expression : null;

    /// <summary>An attribute's simple name without the <c>Attribute</c> suffix.</summary>
    public static string AttributeName(AttributeSyntax a)
    {
        var name = a.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            AliasQualifiedNameSyntax q => q.Name.Identifier.Text,
            SimpleNameSyntax s => s.Identifier.Text,
            _ => a.Name.ToString(),
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length
            ? name[..^"Attribute".Length]
            : name;
    }
}
