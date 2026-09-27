using System.IO;
using System.Linq;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// ADR 0020 / epic #57 Phase 1: interface-based components. `CollectTargets` discovers
/// Target-typed default interface members, class targets override same-named component targets,
/// cross-interface name collisions fail closed, component deps resolve via the method map, and
/// the `IHaz*` pattern injects build-provided values into component target bodies. A build that
/// implements no component interface is unaffected (additive).
/// </summary>
public sealed class ComponentDiscoveryTests
{
    // ── Component interfaces (default interface members) ──────────────────────
    private interface ICompileComp
    {
        Target Compile => _ => _.Executes(() => { });
    }

    // Cross-component dependency via a Target reference (exercises the method map,
    // not the string overload): ITestComp : ICompileComp so `Compile` is in scope.
    private interface ITestComp : ICompileComp
    {
        Target Test => _ => _.DependsOn(Compile).Executes(() => { });
    }

    // IHaz* injection: the component needs a value the build provides.
    private interface IHazGreeting { string Greeting { get; } }
    private interface IGreetComp : IHazGreeting
    {
        Target Greet => _ => _.Executes(() => Captured.Greeting = Greeting);
    }
    private static class Captured { public static string? Greeting; }

    // Two unrelated interfaces contributing the SAME target name → ambiguity.
    private interface IAlpha { Target Dup => _ => _.Executes(() => { }); }
    private interface IBravo { Target Dup => _ => _.Executes(() => { }); }

    // ── Builds ────────────────────────────────────────────────────────────────
    private sealed class PlainBuild : TampBuild
    {
        public Target Only => _ => _.Executes(() => { });
    }

    private sealed class ComposedBuild : TampBuild, ITestComp   // brings ICompileComp too
    {
    }

    private sealed class OverrideBuild : TampBuild, ICompileComp
    {
        public static bool ClassCompileRan;
        public Target Compile => _ => _.Executes(() => ClassCompileRan = true);   // overrides the component
    }

    private sealed class GreetBuild : TampBuild, IGreetComp
    {
        public string Greeting => "hello-from-build";
    }

    private sealed class AmbiguousBuild : TampBuild, IAlpha, IBravo { }

    private static void Run(TampBuild build, string target)
        => new Executor(new TargetGraph(TampBuild.CollectTargets(build)), output: TextWriter.Null).Run(target);

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public void Build_With_No_Components_Is_Unaffected()
    {
        var targets = TampBuild.CollectTargets(new PlainBuild());
        Assert.Equal(new[] { "Only" }, targets.Keys.OrderBy(k => k));
    }

    [Fact]
    public void Component_Targets_Are_Discovered_And_Wired()
    {
        var targets = TampBuild.CollectTargets(new ComposedBuild());
        Assert.Contains("Compile", targets.Keys);
        Assert.Contains("Test", targets.Keys);
        // The cross-component dependency resolved via the method map, not a magic string.
        Assert.Contains("Compile", targets["Test"].Dependencies);
    }

    [Fact]
    public void Class_Target_Overrides_A_Same_Named_Component_Target()
    {
        OverrideBuild.ClassCompileRan = false;
        var build = new OverrideBuild();
        var targets = TampBuild.CollectTargets(build);
        Assert.Single(targets.Keys, k => k == "Compile");   // exactly one Compile

        Run(build, "Compile");
        Assert.True(OverrideBuild.ClassCompileRan, "the class's Compile body should run, not the component's");
    }

    [Fact]
    public void IHaz_Property_Injects_The_Builds_Value_Into_The_Component_Body()
    {
        Captured.Greeting = null;
        Run(new GreetBuild(), "Greet");
        Assert.Equal("hello-from-build", Captured.Greeting);
    }

    [Fact]
    public void Ambiguous_Component_Target_Across_Interfaces_Fails_Closed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TampBuild.CollectTargets(new AmbiguousBuild()));
        Assert.Contains("Dup", ex.Message);
        Assert.Contains(nameof(IAlpha), ex.Message);
        Assert.Contains(nameof(IBravo), ex.Message);
    }
}
