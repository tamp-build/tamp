using System.IO;
using System.Linq;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// #70: argument parsing fails CLOSED. An unrecognized flag is a hard error before any target
/// runs (a typo must not silently run the default graph); a misspelled/invalid <c>--enforce</c>
/// value errors rather than silently leaving enforcement Off (a safety flag must not fail open);
/// and <c>--help</c> prints usage and runs nothing. Declared <c>[Parameter]</c> flags still bind.
/// </summary>
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class ArgumentFailClosedTests
{
    private sealed class ParamBuild : TampBuild
    {
        public static int RanCount;
        [Parameter] public string? Config { get; set; }
        public Target Build => _ => _.Default().Executes(() => { RanCount++; });
    }

    private static (string Stdout, string Stderr, int Exit) RunCaptured(string[] args)
    {
        ParamBuild.RanCount = 0;
        var so = new StringWriter();
        var se = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(so);
        Console.SetError(se);
        try
        {
            var exit = TampBuild.Execute<ParamBuild>(args);   // run BEFORE reading the writers
            return (so.ToString(), se.ToString(), exit);
        }
        finally { Console.SetOut(prevOut); Console.SetError(prevErr); }
    }

    private static IReadOnlyDictionary<string, TargetSpec> Targets() => TampBuild.CollectTargets(new ParamBuild());
    private static readonly IReadOnlySet<string> ParamKeys = new HashSet<string>(StringComparer.Ordinal) { "config" };

    // ─── Unknown flags fail closed ────────────────────────────────────────

    [Fact]
    public void Unknown_Flag_Is_A_Hard_Error()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TampBuild.ParseInvocation(new[] { "Build", "--captures-logs" }, Targets(), ParamKeys));
        Assert.Contains("--captures-logs", ex.Message);
        Assert.Contains("--help", ex.Message);
    }

    [Fact]
    public void Misspelled_Enforce_Flag_Is_Unknown_And_Fails_Closed()
    {
        // The flag itself is misspelled (--enfrce) — it must not be silently ignored, which is
        // what let a safety flag fail open before.
        Assert.Throws<InvalidOperationException>(() =>
            TampBuild.ParseInvocation(new[] { "Build", "--enfrce=agent" }, Targets(), ParamKeys));
    }

    [Fact]
    public void Declared_Parameter_Flag_Is_Accepted()
    {
        // A real [Parameter] must still bind — both spaced and = forms — and must not be
        // mistaken for an unknown flag or consume a following target name.
        var (_, targets1, _, _, _, _, _, _, _) = TampBuild.ParseInvocation(
            new[] { "Build", "--config", "Release" }, Targets(), ParamKeys);
        Assert.Contains("Build", targets1);
        var (_, targets2, _, _, _, _, _, _, _) = TampBuild.ParseInvocation(
            new[] { "--config=Release", "Build" }, Targets(), ParamKeys);
        Assert.Contains("Build", targets2);
    }

    [Fact]
    public void Known_Framework_Flags_Do_Not_Trip_The_Check()
    {
        // A representative spread of framework flags (switch + pre-scan) must parse cleanly.
        var ex = Record.Exception(() => TampBuild.ParseInvocation(
            new[] { "Build", "--events", "e.ndjson", "--enforce=agent", "--from", "Build",
                    "--rule", "CS0246", "--capture-logs", "--allow-side-effects", "--reporter=json" },
            Targets(), ParamKeys));
        Assert.Null(ex);
    }

    // ─── --enforce fails closed on a bad value ────────────────────────────

    [Theory]
    [InlineData("agent", CapabilityMode.Agent)]
    [InlineData("off", CapabilityMode.Off)]
    public void Enforce_Accepts_Agent_And_Off(string value, CapabilityMode expected)
    {
        var (mode, _) = TampBuild.ResolveCapabilityMode(new[] { "Build", $"--enforce={value}" }, _ => null);
        Assert.Equal(expected, mode);
    }

    [Fact]
    public void Enforce_With_Misspelled_Value_Throws_Not_Silently_Off()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TampBuild.ResolveCapabilityMode(new[] { "Build", "--enforce=agnet" }, _ => null));
        Assert.Contains("--enforce", ex.Message);
        Assert.Contains("agnet", ex.Message);
    }

    // ─── --help prints usage and runs nothing ─────────────────────────────

    [Fact]
    public void Help_Prints_Usage_And_Runs_No_Target()
    {
        var (stdout, _, exit) = RunCaptured(new[] { "--help" });
        Assert.Equal(0, exit);
        Assert.Equal(0, ParamBuild.RanCount);              // did NOT run the default graph
        Assert.Contains("Usage:", stdout);
        Assert.Contains("--enforce", stdout);
        Assert.Contains("Build", stdout);                  // lists the callable target
    }

    [Fact]
    public void Unknown_Flag_Through_Execute_Errors_Without_Running()
    {
        var (_, stderr, exit) = RunCaptured(new[] { "--captures-logs" });
        Assert.NotEqual(0, exit);
        Assert.Equal(0, ParamBuild.RanCount);              // fail closed: default graph did NOT run
        Assert.Contains("Unknown argument", stderr);
    }

    // ─── #71: `mcp` / `init` are global-tool verbs, not consumer-build verbs ─

    [Fact]
    public void Mcp_Verb_On_Consumer_Build_Points_To_The_Global_Tool_And_Runs_Nothing()
    {
        var (_, stderr, exit) = RunCaptured(new[] { "mcp" });
        Assert.NotEqual(0, exit);
        Assert.Equal(0, ParamBuild.RanCount);              // did NOT fall through to the default graph
        Assert.Contains("tamp mcp", stderr);               // points at the supported entrypoint
    }

    [Fact]
    public void Init_Verb_On_Consumer_Build_Points_To_The_Global_Tool()
    {
        var (_, stderr, exit) = RunCaptured(new[] { "init" });
        Assert.NotEqual(0, exit);
        Assert.Equal(0, ParamBuild.RanCount);
        Assert.Contains("tamp init", stderr);
    }
}
