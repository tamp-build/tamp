using System.IO;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests for #15 slice-running: `--from X` runs X + its transitive dependents, skips
/// external upstream as assumed-satisfied, and is fail-closed on that upstream's declared
/// Produces artifacts. Pure graph, no hashing.
/// </summary>
public sealed class SliceRunningTests
{
    private sealed class CapturingSink : IBuildEventSink
    {
        public readonly List<BuildEvent> Events = new();
        public void Emit(BuildEvent e) => Events.Add(e);
    }

    private sealed class Chain : TampBuild
    {
        public static string? RestoreProduces;
        public Target Restore => _ =>
        {
            var d = _;
            if (RestoreProduces is not null) d = d.Produces(RestoreProduces);
            return d.Executes(() => { });
        };
        public Target Compile => _ => _.DependsOn(nameof(Restore)).Executes(() => { });
        public Target Test => _ => _.DependsOn(nameof(Compile)).Executes(() => { });
        public Target Pack => _ => _.DependsOn(nameof(Test)).Executes(() => { });
        public static void Reset() => RestoreProduces = null;
    }

    private static (List<BuildEvent> Events, int Exit) RunFrom(string from)
    {
        var targets = TampBuild.CollectTargets(new Chain());
        var graph = new TargetGraph(targets);
        var runOnly = new HashSet<string>(graph.DependentsClosure(from), StringComparer.Ordinal);
        var sink = new CapturingSink();
        var exit = new Executor(graph, output: TextWriter.Null, eventSink: sink, runOnly: runOnly).Run(runOnly.ToArray()).ExitCode;
        return (sink.Events, exit);
    }

    private static IEnumerable<string> Started(List<BuildEvent> e) =>
        e.Where(x => x.Type == BuildEventTypes.TargetStarted).Select(x => x.TargetId!);

    [Fact]
    public void DependentsClosure_Is_X_Plus_Transitive_Dependents()
    {
        Chain.Reset();
        var graph = new TargetGraph(TampBuild.CollectTargets(new Chain()));
        Assert.Equal(new[] { "Compile", "Pack", "Test" },
            graph.DependentsClosure("Compile").OrderBy(n => n).ToArray());
    }

    [Fact]
    public void From_Runs_The_Slice_And_Skips_Upstream_As_Satisfied()
    {
        Chain.Reset();   // Restore declares no Produces → can't verify → skipped silently
        var (events, exit) = RunFrom("Compile");
        Assert.Equal(0, exit);

        Assert.Equal(new[] { "Compile", "Test", "Pack" }, Started(events).ToArray());   // slice ran, in order
        var restore = (TargetFinishedPayload)events.Single(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == "Restore").Payload;
        Assert.Equal(BuildEventStatus.Skipped, restore.Status);
        Assert.Contains("--from", restore.Reason!);
        Assert.DoesNotContain("Restore", Started(events));   // upstream never ran
    }

    [Fact]
    public void From_Is_Fail_Closed_When_Upstream_Declared_Artifact_Is_Missing()
    {
        Chain.Reset();
        Chain.RestoreProduces = $".tamp/temp/slice-missing-{Guid.NewGuid():N}/*.txt";   // nothing on disk
        var (events, exit) = RunFrom("Compile");

        Assert.NotEqual(0, exit);
        var restore = (TargetFinishedPayload)events.Single(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == "Restore").Payload;
        Assert.Equal(BuildEventStatus.Failure, restore.Status);
        Assert.Contains("fail-closed", restore.Reason!);
        Assert.DoesNotContain("Compile", Started(events));   // slice didn't run — the build stopped
    }

    [Fact]
    public void From_Passes_When_Upstream_Declared_Artifact_Exists()
    {
        Chain.Reset();
        var relDir = $".tamp/temp/slice-present-{Guid.NewGuid():N}";
        var absDir = Path.Combine(TampBuild.RootDirectory.Value, relDir.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            Directory.CreateDirectory(absDir);
            File.WriteAllText(Path.Combine(absDir, "out.txt"), "x");
            Chain.RestoreProduces = $"{relDir}/*.txt";

            var (events, exit) = RunFrom("Compile");
            Assert.Equal(0, exit);
            Assert.Contains("Compile", Started(events));   // upstream artifact present → slice runs
        }
        finally { if (Directory.Exists(absDir)) Directory.Delete(absDir, recursive: true); }
    }

    [Fact]
    public void From_A_Leaf_Runs_Just_That_Target()
    {
        Chain.Reset();
        var (events, exit) = RunFrom("Pack");
        Assert.Equal(0, exit);
        Assert.Equal(new[] { "Pack" }, Started(events).ToArray());
    }

    [Fact]
    public void ResolveFromTarget_Reads_Flag_Forms()
    {
        Assert.Equal("Compile", TampBuild.ResolveFromTarget(new[] { "--from", "Compile" }));
        Assert.Equal("Compile", TampBuild.ResolveFromTarget(new[] { "--from=Compile" }));
        Assert.Equal("Compile", TampBuild.ResolveFromTarget(new[] { "--downstream", "Compile" }));
        Assert.Null(TampBuild.ResolveFromTarget(new[] { "Compile" }));
    }
}
