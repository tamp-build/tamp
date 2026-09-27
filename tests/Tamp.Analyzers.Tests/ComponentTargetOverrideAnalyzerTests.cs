using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Tamp.Analyzers;
using Xunit;

namespace Tamp.Analyzers.Tests;

/// <summary>
/// Tests for <see cref="ComponentTargetOverrideAnalyzer"/> (TAMP007, ADR 0020): a build-class Target
/// that overrides a same-named component (default interface member) target is flagged unless marked
/// <c>[OverridesComponent]</c> — so a collision with a component is never silent — and the analyzer
/// stays quiet for normal targets, explicit interface implementations, and the interface DIMs.
/// </summary>
public sealed class ComponentTargetOverrideAnalyzerTests
{
    private const string StubSource = """
        namespace Tamp
        {
            public sealed class Target { }
            public sealed class OverridesComponentAttribute : System.Attribute { }
        }

        namespace Tamp.Components
        {
            using Tamp;
            public interface IRestore { Target Restore => default!; }
            public interface ICompile : IRestore { Target Compile => default!; }
        }
        """;

    private static async Task<ImmutableArray<Diagnostic>> RunAsync(string userSource)
    {
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Runtime.CompilerServices.RuntimeHelpers).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            "test-asm",
            new[] { CSharpSyntaxTree.ParseText(StubSource), CSharpSyntaxTree.ParseText(userSource) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new ComponentTargetOverrideAnalyzer());
        return await compilation.WithAnalyzers(analyzers).GetAnalyzerDiagnosticsAsync();
    }

    private static async Task<System.Collections.Generic.List<Diagnostic>> Tamp007Async(string src)
        => (await RunAsync(src)).Where(d => d.Id == "TAMP007").ToList();

    [Fact]
    public async Task Fires_When_Class_Target_Overrides_A_Component_Target()
    {
        var src = """
            using Tamp;
            using Tamp.Components;
            class Build : ICompile
            {
                public Target Compile => default!;
            }
            """;
        var diags = await Tamp007Async(src);
        var d = Assert.Single(diags);
        Assert.Contains("Compile", d.GetMessage());
        Assert.Contains("ICompile", d.GetMessage());
        Assert.Contains("OverridesComponent", d.GetMessage());
    }

    [Fact]
    public async Task Silent_When_Override_Is_Marked_OverridesComponent()
    {
        var src = """
            using Tamp;
            using Tamp.Components;
            class Build : ICompile
            {
                [OverridesComponent] public Target Compile => default!;
            }
            """;
        Assert.Empty(await Tamp007Async(src));
    }

    [Fact]
    public async Task Silent_For_A_Normal_Target_With_No_Component_Collision()
    {
        var src = """
            using Tamp;
            using Tamp.Components;
            class Build : ICompile
            {
                public Target Deploy => default!;   // no component contributes 'Deploy'
            }
            """;
        Assert.Empty(await Tamp007Async(src));
    }

    [Fact]
    public async Task Silent_For_Explicit_Interface_Implementation()
    {
        var src = """
            using Tamp;
            using Tamp.Components;
            class Build : ICompile
            {
                Target ICompile.Compile => default!;   // explicit impl is a deliberate provision
            }
            """;
        Assert.Empty(await Tamp007Async(src));
    }

    [Fact]
    public async Task Silent_On_The_Component_Interface_Dims_Themselves()
    {
        // The stub already declares the component DIMs; compiling with no overriding build must not fire.
        var src = """
            using Tamp;
            class Plain { }
            """;
        Assert.Empty(await Tamp007Async(src));
    }
}
