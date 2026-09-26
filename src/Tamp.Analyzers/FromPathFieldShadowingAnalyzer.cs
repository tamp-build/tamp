using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tamp.Analyzers;

/// <summary>
/// TAMP005 — flags fields decorated with <c>[FromPath]</c> or
/// <c>[FromNodeModules]</c> whose field name shadows a same-named public
/// static class in any imported namespace. The shadowing makes adopter
/// `Cargo.Build(Cargo, ...)` calls resolve to `Tool.Build` (which doesn't
/// exist), with a confusing `Tool does not contain a definition for Build`
/// compiler error.
/// </summary>
/// <remarks>
/// <para>
/// DasBook canary friction (2026-05-13). The natural Build.cs idiom is:
/// </para>
/// <code>
/// using Tamp.Cargo;
/// class Build : TampBuild
/// {
///     [FromPath("cargo")] readonly Tool Cargo = null!;   // shadows Tamp.Cargo.Cargo
///     // Cargo.Build(Cargo, s => s...) — fails to compile, points at the wrong thing.
/// }
/// </code>
/// <para>
/// The fix is to rename the field (convention: <c>CargoBin</c> / <c>&lt;Tool&gt;Tool</c>)
/// so it no longer shadows the static facade class. This analyzer makes that
/// convention enforceable rather than a copy-paste discipline.
/// </para>
/// <para>
/// Detection: for each field decorated with <c>[FromPath]</c> /
/// <c>[FromNodeModules]</c>, look up the field's simple name in every
/// using-imported namespace via the semantic model. If a public static class
/// of that name exists, the field shadows it; report TAMP005 at the field
/// declarator's location.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FromPathFieldShadowingAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TAMP005";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "[FromPath] field name shadows static facade class",
        messageFormat: "[FromPath]/[FromNodeModules] field '{0}' shadows static class '{1}'. " +
                       "Rename the field (convention: '{0}Bin' or '{0}Tool') to keep '{0}.Verb(...)' " +
                       "resolving to the facade.",
        category: "Tamp.Authoring",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Fields injected by Tamp's reflection binder (via [FromPath] / [FromNodeModules]) " +
                     "are typically named to match the wrapped CLI ('Cargo', 'Npm', 'Tauri'). When the " +
                     "adopter also imports the matching satellite namespace, the field shadows the static " +
                     "facade class, breaking any `<Name>.Verb(...)` call. Rename the field to " +
                     "`<Name>Bin` / `<Name>Tool` to avoid the conflict.",
        helpLinkUri: "https://github.com/tamp-build/tamp/blob/main/docs/analyzers/TAMP005.md");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.FieldDeclaration);
    }

    private static readonly System.Collections.Generic.HashSet<string> ReflectionBoundAttributeNames = new()
    {
        "FromPath", "FromPathAttribute",
        "FromNodeModules", "FromNodeModulesAttribute",
    };

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var fieldDecl = (FieldDeclarationSyntax)context.Node;

        if (!HasReflectionBoundAttribute(fieldDecl)) return;

        foreach (var variable in fieldDecl.Declaration.Variables)
        {
            var fieldName = variable.Identifier.Text;

            // Common Tamp adoption tip — `<Tool>Bin` is the canonical fix.
            // If the adopter ALREADY uses a non-shadowing name, no point checking.
            // Heuristic short-circuit: anything ending in "Bin", "Tool", "Cli", "Exe" is fine.
            if (LooksAlreadyDeshadowed(fieldName)) continue;

            var conflicting = FindShadowedFacadeClass(
                context.SemanticModel, fieldDecl, fieldName, context.CancellationToken);
            if (conflicting is null) continue;

            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                variable.Identifier.GetLocation(),
                fieldName,
                conflicting.ToDisplayString()));
        }
    }

    private static bool LooksAlreadyDeshadowed(string name) =>
        name.EndsWith("Bin", System.StringComparison.Ordinal) ||
        name.EndsWith("Tool", System.StringComparison.Ordinal) ||
        name.EndsWith("Cli", System.StringComparison.Ordinal) ||
        name.EndsWith("Exe", System.StringComparison.Ordinal);

    private static bool HasReflectionBoundAttribute(FieldDeclarationSyntax field)
    {
        foreach (var list in field.AttributeLists)
            foreach (var attribute in list.Attributes)
            {
                var nameSyntax = attribute.Name;
                var name = nameSyntax is QualifiedNameSyntax qualified
                    ? qualified.Right.Identifier.Text
                    : (nameSyntax as SimpleNameSyntax)?.Identifier.Text
                      ?? nameSyntax.ToString();
                if (ReflectionBoundAttributeNames.Contains(name)) return true;
            }
        return false;
    }

    /// <summary>
    /// Walk every using-imported namespace in the field's compilation unit
    /// (regular + global usings) and return the first public static class
    /// whose simple name matches <paramref name="fieldName"/>. Returns null
    /// if no conflict exists.
    /// </summary>
    private static INamedTypeSymbol? FindShadowedFacadeClass(
        SemanticModel model,
        FieldDeclarationSyntax field,
        string fieldName,
        System.Threading.CancellationToken cancellationToken)
        => FacadeClassShadowing.FindShadowed(model, field.SyntaxTree, fieldName, cancellationToken);
}

/// <summary>
/// Shared shadowing-detection helper used by TAMP005 (field shadowing) and
/// TAMP006 (Target-property shadowing). Walks the syntax tree's
/// using-imported namespaces and returns the first public static class
/// matching the requested member name, or <c>null</c>.
/// </summary>
internal static class FacadeClassShadowing
{
    public static INamedTypeSymbol? FindShadowed(
        SemanticModel model,
        SyntaxTree tree,
        string memberName,
        System.Threading.CancellationToken cancellationToken)
    {
        var compilationUnit = tree.GetCompilationUnitRoot(cancellationToken);

        foreach (var usingDirective in compilationUnit.Usings)
        {
            // Skip `using static X` (target is a type, not a namespace) and
            // alias usings (`using Foo = Bar.Baz;`) — neither imports the
            // namespace's top-level types in the bare-name resolution.
            if (usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)) continue;
            if (usingDirective.Alias is not null) continue;
            if (usingDirective.Name is null) continue;

            var nsSymbol = model.GetSymbolInfo(usingDirective.Name, cancellationToken).Symbol
                            as INamespaceSymbol;
            if (nsSymbol is null) continue;

            var match = nsSymbol.GetTypeMembers(memberName).FirstOrDefault(t =>
                t.IsStatic &&
                t.DeclaredAccessibility == Accessibility.Public);
            if (match is not null) return match;
        }
        return null;
    }
}
