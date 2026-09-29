using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tamp.Analyzers;

/// <summary>
/// TAMP004 — fires when <c>Tamp.Secret.Reveal()</c> is called outside an approved context.
/// The masking primitives in <c>Tamp.Core</c> (<c>Secret.ToString()</c>, <c>CommandPlan.Secrets</c>,
/// runner-side env-var masking) only protect values they can see; calling <c>Reveal()</c> and
/// then logging the raw string defeats all of them.
/// </summary>
/// <remarks>
/// <para>Approved containing-class heuristics:</para>
/// <list type="bullet">
///   <item>Class name ends in <c>Settings</c> or <c>SettingsBase</c> (the canonical Tamp wrapper-settings shape — every satellite uses this pattern).</item>
///   <item>Class is in the <c>Tamp.Cli</c>, <c>Tamp.NetCli.V*</c>, <c>Tamp.Core</c> namespaces (framework internals).</item>
///   <item>Test code (containing class ends in <c>Tests</c> or namespace contains <c>.Tests</c>).</item>
/// </list>
/// <para>
/// Outside those contexts, calling <c>Reveal()</c> is almost always wrong — the typical accidental
/// leak shape is <c>Logger.LogInformation("token={token}", secret.Reveal())</c>, which writes the
/// cleartext to whatever log sink is wired up. <c>secret.ToString()</c> would have written
/// <c>&lt;Secret:Name&gt;</c> instead.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SecretRevealOutsideApprovedContextAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TAMP004";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Secret.Reveal() called outside an approved context",
        messageFormat: "Secret.Reveal() is being called outside an approved context (containing class '{0}'). Approved contexts are wrapper settings classes (names ending in 'Settings'/'SettingsBase'), Tamp framework internals (Tamp.Cli, Tamp.NetCli.V*, Tamp.Core), and test code. Outside those, prefer Secret.ToString() (returns '<Secret:Name>') or routing the value through CommandPlan.Secrets so it's masked in process traces.",
        category: "Tamp.Security",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Secret.Reveal() returns the cleartext value. The masking primitives in Tamp.Core (ToString, CommandPlan.Secrets, env-var masking) protect values they can see, but calling Reveal() and then logging the raw string defeats them. Restrict Reveal() calls to wrapper settings classes that are about to build a command line / env var for a child process.",
        helpLinkUri: "https://github.com/tamp-build/tamp/blob/main/docs/analyzers/TAMP004.md");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // We're looking for `something.Reveal()` — a MemberAccessExpression on the left.
        if (invocation.Expression is not MemberAccessExpressionSyntax mae) return;
        if (mae.Name.Identifier.Text != "Reveal") return;

        // Reject quickly if Reveal takes args — Secret.Reveal() is parameterless.
        if (invocation.ArgumentList?.Arguments.Count is not (0 or null)) return;

        // Resolve the symbol — confirm it's actually Tamp.Secret.Reveal, not some other Reveal method.
        var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
        if (symbol is null) return;
        if (symbol.Name != "Reveal") return;
        if (symbol.ContainingType is null) return;
        if (symbol.ContainingType.Name != "Secret") return;
        if (symbol.ContainingType.ContainingNamespace?.ToDisplayString() != "Tamp") return;

        // Find the enclosing class/struct/record to check context.
        var enclosing = invocation.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        if (enclosing is null) return;
        var classDeclSymbol = context.SemanticModel.GetDeclaredSymbol(enclosing, context.CancellationToken) as INamedTypeSymbol;
        if (classDeclSymbol is null) return;

        var className = classDeclSymbol.Name;
        var ns = classDeclSymbol.ContainingNamespace?.ToDisplayString() ?? "";

        if (IsApprovedContext(className, ns, classDeclSymbol)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), className));
    }

    private static bool IsApprovedContext(string className, string ns, INamedTypeSymbol classSymbol)
    {
        // Approved class-name shapes — wrapper settings (canonical Tamp pattern).
        if (className.EndsWith("Settings", System.StringComparison.Ordinal)) return true;
        if (className.EndsWith("SettingsBase", System.StringComparison.Ordinal)) return true;

        // Approved namespaces — Tamp framework internals.
        if (ns == "Tamp" || ns == "Tamp.Core") return true;
        if (ns.StartsWith("Tamp.Cli", System.StringComparison.Ordinal)) return true;
        if (ns.StartsWith("Tamp.NetCli.V", System.StringComparison.Ordinal)) return true;
        // Tamp.Http is foundational egress infrastructure (the base HTTP client + auth): its credential
        // types must reveal a Secret to build the Authorization header — the same legitimate wire-egress
        // role as Tamp.NetCli.V* building a command line. Peer framework-internal; approved. (TAM-… / tamp-http)
        if (ns.StartsWith("Tamp.Http", System.StringComparison.Ordinal)) return true;

        // Test code — anywhere with .Tests in the namespace or class name ending Tests.
        if (className.EndsWith("Tests", System.StringComparison.Ordinal)) return true;
        if (ns.Contains(".Tests")) return true;

        // Tamp.Core 1.7.0+ — explicit inheritance from WrapperSettingsBase (TAM-197).
        // Walks the inheritance chain looking for Tamp.WrapperSettingsBase.
        for (var t = classSymbol.BaseType; t is not null; t = t.BaseType)
        {
            if (t.Name == "WrapperSettingsBase"
                && t.ContainingNamespace?.ToDisplayString() == "Tamp")
                return true;
        }

        return false;
    }
}
