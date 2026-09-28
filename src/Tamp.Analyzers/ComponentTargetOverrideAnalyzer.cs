using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tamp.Analyzers;

/// <summary>
/// TAMP007 — flags a build-class <c>Target</c> property whose name matches a <em>component</em> target
/// (a <c>Target</c>-typed default interface member the class inherits, ADR 0020). By class-precedence
/// the class target overrides — silently replaces — the component's, so a name collision must never be
/// silent. Mark an intended override <c>[OverridesComponent]</c> to confirm it (and silence this), or
/// rename the target if the collision is accidental. Sibling of the TAMP005/006 shadowing family.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ComponentTargetOverrideAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TAMP007";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Target overrides a component target",
        messageFormat: "Target '{0}' overrides the component target from '{1}'. Class targets win by precedence, so the component's '{0}' will not run. Mark it [OverridesComponent] if the override is intended, or rename the target if the collision is accidental.",
        category: "Tamp.Authoring",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A Target on the build class whose name matches a component target (a Target-typed default interface member) overrides it by class-precedence, silently replacing the component's target. Mark intended overrides with [OverridesComponent], or rename to avoid an accidental shadow.",
        helpLinkUri: "https://github.com/tamp-build/tamp/blob/main/docs/analyzers/TAMP007.md");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.PropertyDeclaration);
    }

    private static bool IsTampTarget(ITypeSymbol? t)
        => t is INamedTypeSymbol n && n.Name == "Target" && n.ContainingNamespace?.ToDisplayString() == "Tamp";

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var propSyntax = (PropertyDeclarationSyntax)context.Node;

        // Only Target-typed properties, and only on a class (not the interface DIM itself).
        var typeInfo = context.SemanticModel.GetTypeInfo(propSyntax.Type, context.CancellationToken);
        if (!IsTampTarget(typeInfo.Type)) return;

        if (context.SemanticModel.GetDeclaredSymbol(propSyntax, context.CancellationToken) is not IPropertySymbol symbol) return;
        if (symbol.ContainingType is not { TypeKind: TypeKind.Class } owner) return;

        // An explicit interface implementation is an unambiguous, deliberate provision — not a shadow.
        if (symbol.ExplicitInterfaceImplementations.Length > 0) return;

        // [OverridesComponent] records intent → stay silent.
        if (symbol.GetAttributes().Any(a =>
                a.AttributeClass?.Name == "OverridesComponentAttribute" &&
                a.AttributeClass.ContainingNamespace?.ToDisplayString() == "Tamp"))
            return;

        var name = symbol.Name;
        foreach (var iface in owner.AllInterfaces)
        {
            var ifaceProp = iface.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault();
            if (ifaceProp is null || !IsTampTarget(ifaceProp.Type)) continue;

            // A component target is a DEFAULT interface member (has an implementation). An abstract
            // interface property of type Target (none in the standard set) is not a component target.
            if (ifaceProp.GetMethod is not { IsAbstract: false }) continue;

            context.ReportDiagnostic(Diagnostic.Create(
                Rule, propSyntax.Identifier.GetLocation(), name, iface.ToDisplayString()));
            return;   // one report per property is enough
        }
    }
}
