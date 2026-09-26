using Xunit;

namespace Tamp.Core.Tests;

public sealed class ExecutorTests
{
    private static TargetGraph GraphWith(params (string Name, string[] Deps, Action[] Actions, Func<IEnumerable<CommandPlan>>[] Plans)[] entries)
    {
        var d = new Dictionary<string, TargetSpec>(StringComparer.Ordinal);
        foreach (var (name, deps, actions, plans) in entries)
        {
            d[name] = new TargetSpec
            {
                Name = name,
                Dependencies = deps,
                Actions = actions,
                PlanFactories = plans,
            };
        }
        return new TargetGraph(d);
    }

    [Fact]
    public void Run_Mode_Executes_Actions_In_Topological_Order()
    {
        var order = new List<string>();
        var graph = GraphWith(
            ("A", [], [() => order.Add("A")], []),
            ("B", ["A"], [() => order.Add("B")], []),
            ("C", ["B"], [() => order.Add("C")], []));
        var exec = new Executor(graph, ExecutionMode.Run, TextWriter.Null);
        var result = exec.Run("C");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["A", "B", "C"], order);
    }

    [Fact]
    public void Plan_Mode_Lists_Targets_Without_Executing_Actions()
    {
        var ran = false;
        var graph = GraphWith(
            ("A", [], [() => ran = true], []));
        var sw = new StringWriter();
        var exec = new Executor(graph, ExecutionMode.Plan, sw);
        var result = exec.Run("A");
        Assert.Equal(0, result.ExitCode);
        Assert.False(ran);
        Assert.Contains("A", sw.ToString());
    }

    [Fact]
    public void DryRun_Mode_Prints_Plans_Without_Executing()
    {
        var ran = false;
        var graph = GraphWith(
            ("A", [],
                [() => ran = true],
                [() => new[] { new CommandPlan { Executable = "echo", Arguments = ["hi"] } }]));
        var sw = new StringWriter();
        var exec = new Executor(graph, ExecutionMode.DryRun, sw);
        var result = exec.Run("A");
        Assert.Equal(0, result.ExitCode);
        Assert.False(ran);
        var output = sw.ToString();
        Assert.Contains("DRY RUN", output);
        Assert.Contains("echo", output);
        Assert.Contains("hi", output);
    }

    [Fact]
    public void DryRun_Mode_Reports_Plan_Count()
    {
        var graph = GraphWith(
            ("A", [],
                [],
                [() => new[]
                {
                    new CommandPlan { Executable = "a", Arguments = [] },
                    new CommandPlan { Executable = "b", Arguments = [] },
                }]));
        var exec = new Executor(graph, ExecutionMode.DryRun, TextWriter.Null);
        var result = exec.Run("A");
        Assert.Equal(2, result.CommandPlansPrinted);
    }

    [Fact]
    public void Action_Throwing_Aborts_Build_With_Failed_Target()
    {
        var graph = GraphWith(
            ("Bad", [], [() => throw new InvalidOperationException("boom")], []));
        var exec = new Executor(graph, ExecutionMode.Run, TextWriter.Null);
        var result = exec.Run("Bad");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("Bad", result.FailedTarget);
    }

    [Fact]
    public void Continue_Mode_Records_Failure_And_Exits_NonZero_Without_Masking(/* #4 */)
    {
        // FailureMode.Continue must not mask a failure as Done + exit 0 — it fails the run,
        // it just doesn't abort the build. (Regression for tamp #4.)
        var d = new Dictionary<string, TargetSpec>(StringComparer.Ordinal)
        {
            ["Sloppy"] = new TargetSpec
            {
                Name = "Sloppy",
                FailureMode = FailureMode.Continue,
                Actions = [() => throw new InvalidOperationException("boom")],
            },
        };
        var exec = new Executor(new TargetGraph(d), ExecutionMode.Run, TextWriter.Null);
        var result = exec.Run("Sloppy");

        Assert.NotEqual(0, result.ExitCode);   // failure is no longer swallowed
        Assert.Equal("Sloppy", result.FailedTarget);
        Assert.Equal(TargetStatus.Failed,
            result.ExecutionRecords.Single(r => r.Name == "Sloppy").Status);
    }

    [Fact]
    public void Continue_Mode_Does_Not_Abort_The_Build_But_Still_Fails(/* #4 */)
    {
        // A Continue target that throws still lets later targets run (non-aborting), but the
        // overall run fails — distinguishing Continue from the default Fatal (which would NotRun B).
        var ranB = false;
        var d = new Dictionary<string, TargetSpec>(StringComparer.Ordinal)
        {
            ["A"] = new TargetSpec { Name = "A", FailureMode = FailureMode.Continue, Actions = [() => throw new InvalidOperationException("boom")] },
            ["B"] = new TargetSpec { Name = "B", Dependencies = ["A"], Actions = [() => ranB = true] },
        };
        var exec = new Executor(new TargetGraph(d), ExecutionMode.Run, TextWriter.Null);
        var result = exec.Run("B");

        Assert.True(ranB);                     // build was not aborted by A's failure
        Assert.NotEqual(0, result.ExitCode);   // …but the run still fails
        Assert.Equal(TargetStatus.Failed, result.ExecutionRecords.Single(r => r.Name == "A").Status);
        Assert.Equal(TargetStatus.Done, result.ExecutionRecords.Single(r => r.Name == "B").Status);
    }

    [Fact]
    public void Continue_Mode_Runs_Remaining_Plans_Then_Fails(/* #4 issue repro */)
    {
        // The issue's three-plan probe: plan 2 fails; plans 1 and 3 still run; the target is
        // Failed and the process exits non-zero (no longer a green build on a failed plan).
        static CommandPlan Echo(string text) =>
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                ? new CommandPlan { Executable = "cmd.exe", Arguments = ["/c", $"echo {text}"] }
                : new CommandPlan { Executable = "/bin/sh", Arguments = ["-c", $"echo {text}"] };
        static CommandPlan Fail() =>
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                ? new CommandPlan { Executable = "cmd.exe", Arguments = ["/c", "exit 1"] }
                : new CommandPlan { Executable = "/bin/sh", Arguments = ["-c", "exit 1"] };

        var d = new Dictionary<string, TargetSpec>(StringComparer.Ordinal)
        {
            ["Probe"] = new TargetSpec
            {
                Name = "Probe",
                FailureMode = FailureMode.Continue,
                PlanFactories = [() => new[] { Echo("BEFORE-FAIL"), Fail(), Echo("AFTER-FAIL") }],
            },
        };
        var output = new StringWriter();
        var result = new Executor(new TargetGraph(d), ExecutionMode.Run, output).Run("Probe");

        var text = output.ToString();
        Assert.Contains("BEFORE-FAIL", text);        // plan 1 ran
        Assert.Contains("AFTER-FAIL", text);         // plan 3 still ran after plan 2 failed
        Assert.NotEqual(0, result.ExitCode);         // …but the target failed the build
        Assert.Equal(TargetStatus.Failed, result.ExecutionRecords.Single(r => r.Name == "Probe").Status);
    }

    [Fact]
    public void Result_Includes_Targets_Traversed_Count()
    {
        var graph = GraphWith(
            ("A", [], [], []),
            ("B", ["A"], [], []),
            ("C", ["B"], [], []));
        var exec = new Executor(graph, ExecutionMode.Run, TextWriter.Null);
        var result = exec.Run("C");
        Assert.Equal(3, result.TargetsTraversed);
    }

    [Fact]
    public void Constructor_Throws_On_Null_Graph()
    {
        Assert.Throws<ArgumentNullException>(() => new Executor(null!));
    }
}
