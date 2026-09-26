using System.IO;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests for #17 would-skip cache advisory: with --cache-advice + a declared InputHash, an
/// unchanged input reports WouldSkip=true (advisory) but the target still runs; changed inputs
/// report false; advice off / no InputHash report null and write no cache.
/// </summary>
public sealed class CacheAdvisoryTests
{
    private sealed class CapturingSink : IBuildEventSink
    {
        public readonly List<BuildEvent> Events = new();
        public void Emit(BuildEvent e) => Events.Add(e);
    }

    private sealed class Cfg : TampBuild
    {
        public static Func<string> Hash = () => "h1";
        public Target Run => _ => _.InputHash(() => Hash()).Executes(() => { });
    }

    private sealed class NoHashCfg : TampBuild
    {
        public Target Run => _ => _.Executes(() => { });
    }

    private static (bool? WouldSkip, bool Ran) Run(bool advice, AbsolutePath cachePath)
    {
        var targets = TampBuild.CollectTargets(new Cfg());
        var sink = new CapturingSink();
        new Executor(new TargetGraph(targets), output: TextWriter.Null, eventSink: sink,
            cacheAdvice: advice, hashCachePath: cachePath).Run("Run");
        var finished = (TargetFinishedPayload)sink.Events.Last(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == "Run").Payload;
        var ran = sink.Events.Any(e => e.Type == BuildEventTypes.TargetStarted && e.TargetId == "Run");
        return (finished.WouldSkip, ran);
    }

    private static AbsolutePath TempCache() =>
        AbsolutePath.Create(Path.Combine(Path.GetTempPath(), $"tamp-hashcache-{Guid.NewGuid():N}.json"));

    [Fact]
    public void Unchanged_Inputs_Report_WouldSkip_True_But_Still_Run()
    {
        var path = TempCache();
        Cfg.Hash = () => "h1";
        try
        {
            var first = Run(advice: true, path);
            Assert.False(first.WouldSkip);   // no prior hash yet
            Assert.True(first.Ran);

            var second = Run(advice: true, path);
            Assert.True(second.WouldSkip);   // inputs unchanged → advisory
            Assert.True(second.Ran);          // …but it STILL ran (advisory never skips)
        }
        finally { if (path.FileExists()) path.DeleteFile(); }
    }

    [Fact]
    public void Changed_Inputs_Report_WouldSkip_False()
    {
        var path = TempCache();
        try
        {
            Cfg.Hash = () => "h1";
            Run(advice: true, path);
            Cfg.Hash = () => "h2";
            var changed = Run(advice: true, path);
            Assert.False(changed.WouldSkip);
        }
        finally { if (path.FileExists()) path.DeleteFile(); }
    }

    [Fact]
    public void Advice_Off_Reports_Null_And_Writes_No_Cache()
    {
        var path = TempCache();
        Cfg.Hash = () => "h1";
        try
        {
            var r = Run(advice: false, path);
            Assert.Null(r.WouldSkip);
            Assert.False(path.FileExists());
        }
        finally { if (path.FileExists()) path.DeleteFile(); }
    }

    [Fact]
    public void No_InputHash_Reports_Null_Even_With_Advice()
    {
        var path = TempCache();
        try
        {
            var targets = TampBuild.CollectTargets(new NoHashCfg());
            var sink = new CapturingSink();
            new Executor(new TargetGraph(targets), output: TextWriter.Null, eventSink: sink,
                cacheAdvice: true, hashCachePath: path).Run("Run");
            var finished = (TargetFinishedPayload)sink.Events.Last(e => e.Type == BuildEventTypes.TargetFinished).Payload;
            Assert.Null(finished.WouldSkip);
        }
        finally { if (path.FileExists()) path.DeleteFile(); }
    }

    [Fact]
    public void ResolveCacheAdvice_Reads_Flag_And_Env()
    {
        Assert.True(TampBuild.ResolveCacheAdvice(new[] { "Run", "--cache-advice" }, _ => null));
        Assert.True(TampBuild.ResolveCacheAdvice(new[] { "Run" }, k => k == "TAMP_CACHE_ADVICE" ? "1" : null));
        Assert.False(TampBuild.ResolveCacheAdvice(new[] { "Run" }, _ => null));
        Assert.False(TampBuild.ResolveCacheAdvice(new[] { "Run" }, k => k == "TAMP_CACHE_ADVICE" ? "false" : null));
    }
}
