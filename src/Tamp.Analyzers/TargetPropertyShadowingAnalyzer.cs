using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tamp.Analyzers;

/// <summary>
/// TAMP006 — flags <c>Target</c>-typed properties whose name shadows
/// a same-named public static class in any imported namespace. Sibling of
/// TAMP005 (field shadowing); same root cause, different syntactic surface.
/// </summary>
/// <remarks>
/// <para>
/// HoldFast canary friction #18 (2026-05-13). The natural target name
/// matching the satellite name shadows the static facade class:
/// </para>
/// <code>
/// using Tamp.GraphQLCodegen.V5;
/// class Build : TampBuild
/// {
///     Target GraphQLCodegen => _ => _                                  // shadows the static class
///         .Executes(() => GraphQLCodegen.Generate(...));                // resolves to Target, not class
/// }
/// </code>
/// <para>
/// C# instance-member resolution wins, so the call resolves to the
/// <c>Target</c>-typed property and the static facade becomes unreachable
/// without a qualified name. Confusing for adopters who reach for the
/// "obvious" target name.
/// </para>
/// <para>
/// Fix is a rename: <c>FrontendCodegen</c>, <c>RunCodegen</c>, anything
/// distinct from the satellite class. The analyzer makes the convention
/// enforceable rather than left to copy-paste discipline.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TargetPropertyShadowingAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TAMP006";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Target property name shadows static facade class",
        messageFormat: "Target property '{0}' shadows static class '{1}'. Calls to '{0}.Verb(...)' resolve to the property, not the facade. Rename the target (e.g. 'Run{0}', 'Do{0}', or a verb-form name) to keep the facade reachable.",
        category: "Tamp.Authoring",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Target-typed properties whose name matches an imported static class create a member-resolution conflict. C# resolves the instance member first, making 'Name.Verb(...)' calls inside the target's body resolve to the property rather than the static facade. Rename the target.",
        helpLinkUri: "https://github.com/tamp-build/tamp/blob/main/docs/analyzers/TAMP006.md");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.PropertyDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var prop = (PropertyDeclarationSyntax)context.Node;

        // Filter to Target-typed properties only. We resolve via the semantic
        // model so any namespace alias / re-export of Tamp.Target is caught.
        var typeInfo = context.SemanticModel.GetTypeInfo(prop.Type, context.CancellationToken);
        if (typeInfo.Type is not INamedTypeSymbol named) return;
        if (named.Name != "Target") return;
        if (named.ContainingNamespace?.ToDisplayString() != "Tamp") return;

        var name = prop.Identifier.Text;

        var conflicting = FacadeClassShadowing.FindShadowed(
            context.SemanticModel, prop.SyntaxTree, name, context.CancellationToken);
        if (conflicting is null) return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            prop.Identifier.GetLocation(),
            name,
            conflicting.ToDisplayString()));
    }
}
