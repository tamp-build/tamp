using System.IO;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests for the #12 typed target result: framework-synthesized Outputs (from
/// Produces globs, with hash/size/kind) + artifact.produced events, InputsHash
/// from a declared InputHash, and a structured Remedy on failure.
/// </summary>
public sealed class TypedTargetResultTests
{
    private sealed class CapturingSink : IBuildEventSink
    {
        public readonly List<BuildEvent> Events = new();
        public void Emit(BuildEvent e) => Events.Add(e);
    }

    private sealed class Cfg : TampBuild
    {
        public static string? ProducesGlob;
        public static Action? Body;
        public static Func<string>? InputHashFn;
        public static bool RequireFalse;
        public static bool Throw;

        public Target Run => _ =>
        {
            var d = _;
            if (ProducesGlob is not null) d = d.Produces(ProducesGlob);
            if (InputHashFn is not null) d = d.InputHash(InputHashFn);
            if (RequireFalse) d = d.Requires(() => false);
            return d.Executes(() =>
            {
                Body?.Invoke();
                if (Throw) throw new InvalidOperationException("boom");
            });
        };

        public static void Reset()
        {
            ProducesGlob = null; Body = null; InputHashFn = null; RequireFalse = false; Throw = false;
        }
    }

    private static List<BuildEvent> Run()
    {
        var targets = TampBuild.CollectTargets(new Cfg());
        var sink = new CapturingSink();
        new Executor(new TargetGraph(targets), output: TextWriter.Null, eventSink: sink).Run("Run");
        return sink.Events;
    }

    [Fact]
    public void Success_Synthesizes_Outputs_And_Emits_ArtifactProduced()
    {
        Cfg.Reset();
        var relDir = $".tamp/temp/artitest-{Guid.NewGuid():N}";
        var absDir = Path.Combine(TampBuild.RootDirectory.Value, relDir.Replace('/', Path.DirectorySeparatorChar));
        var absFile = Path.Combine(absDir, "out.nupkg");
        try
        {
            Cfg.ProducesGlob = $"{relDir}/*.nupkg";
            Cfg.Body = () => { Directory.CreateDirectory(absDir); File.WriteAllText(absFile, "hello world"); };

            var events = Run();

            var finished = (TargetFinishedPayload)events.Single(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == "Run").Payload;
            Assert.Equal(BuildEventStatus.Success, finished.Status);
            var output = Assert.Single(finished.Outputs!);
            Assert.Equal($"{relDir}/out.nupkg", output.Path);          // worktree-relative, forward slashes
            Assert.StartsWith("sha256:", output.Hash);
            Assert.Equal("package", output.Kind);                       // .nupkg → package
            Assert.Equal(11, output.SizeBytes);                         // "hello world"

            // one artifact.produced per output, parented to the target span
            var artifact = events.Single(e => e.Type == BuildEventTypes.ArtifactProduced);
            var targetStarted = events.Single(e => e.Type == BuildEventTypes.TargetStarted && e.TargetId == "Run");
            Assert.Equal(targetStarted.SpanId, artifact.ParentSpanId);
            Assert.Equal($"{relDir}/out.nupkg", ((ArtifactProducedPayload)artifact.Payload).Path);
        }
        finally { if (Directory.Exists(absDir)) Directory.Delete(absDir, recursive: true); }
    }

    [Fact]
    public void No_Produces_Yields_No_Outputs_And_No_ArtifactProduced()
    {
        Cfg.Reset();
        var events = Run();
        var finished = (TargetFinishedPayload)events.Single(e => e.Type == BuildEventTypes.TargetFinished).Payload;
        Assert.Null(finished.Outputs);
        Assert.DoesNotContain(events, e => e.Type == BuildEventTypes.ArtifactProduced);
    }

    [Fact]
    public void InputsHash_Present_When_Declared()
    {
        Cfg.Reset();
        Cfg.InputHashFn = () => "sha256:deadbeef";
        var finished = (TargetFinishedPayload)Run().Single(e => e.Type == BuildEventTypes.TargetFinished).Payload;
        Assert.Equal("sha256:deadbeef", finished.InputsHash);
    }

    [Fact]
    public void Exception_Failure_Yields_Remedy_Class_Code()
    {
        Cfg.Reset();
        Cfg.Throw = true;
        var finished = (TargetFinishedPayload)Run().Single(e => e.Type == BuildEventTypes.TargetFinished).Payload;
        Assert.Equal(BuildEventStatus.Failure, finished.Status);
        Assert.NotNull(finished.Remedy);
        Assert.Equal("code", finished.Remedy!.Class);
        Assert.Equal("tamp Run", finished.Remedy.Reproduce);
        Assert.Contains("boom", finished.Remedy.Hint);
    }

    [Fact]
    public void Requires_Failure_Yields_Remedy_Class_Config()
    {
        Cfg.Reset();
        Cfg.RequireFalse = true;
        var finished = (TargetFinishedPayload)Run().Single(e => e.Type == BuildEventTypes.TargetFinished).Payload;
        Assert.Equal(BuildEventStatus.Failure, finished.Status);
        Assert.Equal("config", finished.Remedy!.Class);
        Assert.Equal("tamp Run", finished.Remedy.Reproduce);
    }
}
