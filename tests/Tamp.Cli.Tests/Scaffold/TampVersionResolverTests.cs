using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tamp.Cli.Scaffold;
using Xunit;

namespace Tamp.Cli.Tests.Scaffold;

/// <summary>
/// #119: `tamp init` scaffolds the newest stable Tamp on the feed, never older than
/// the CLI, and falls back to the CLI's version when the feed can't be read.
/// </summary>
public sealed class TampVersionResolverTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    private static Task<TampVersionResolution> Resolve(string cli, ITampVersionFeed feed, string? pinned = null, TimeSpan? timeout = null)
        => TampVersionResolver.ResolveAsync(cli, pinned, feed, timeout ?? TimeSpan.FromSeconds(5), CancellationToken.None);

    [Fact]
    public async Task Picks_Newest_Stable_Version_On_The_Feed()
    {
        var r = await Resolve("1.17.3", new FakeVersionFeed("1.15.2", "1.17.3", "1.17.5", "1.17.4"));
        Assert.Equal("1.17.5", r.Version);
        Assert.Equal(TampVersionSource.Feed, r.Source);
        Assert.Null(r.FailureReason);
    }

    [Fact]
    public async Task Ignores_Prereleases_Even_When_Higher()
    {
        var r = await Resolve("1.17.3", new FakeVersionFeed("1.17.5", "1.18.0-rc.1", "2.0.0-alpha"));
        Assert.Equal("1.17.5", r.Version);
    }

    [Fact]
    public async Task Compares_Numerically_Not_Lexically()
    {
        var r = await Resolve("1.9.0", new FakeVersionFeed("1.9.0", "1.10.0", "1.2.0"));
        Assert.Equal("1.10.0", r.Version);
    }

    [Fact]
    public async Task Never_Goes_Below_The_Cli_Version()
    {
        // A dev / just-built CLI newer than anything published: keep the CLI's version.
        var r = await Resolve("1.17.6", new FakeVersionFeed("1.17.3", "1.17.5"));
        Assert.Equal("1.17.6", r.Version);
        Assert.Equal(TampVersionSource.Cli, r.Source);
        Assert.Null(r.FailureReason);   // not a failure — the CLI is simply newest
    }

    [Fact]
    public async Task Falls_Back_To_Cli_Version_On_Timeout()
    {
        var r = await Resolve("1.17.3", FakeVersionFeed.Hanging(), timeout: Short);
        Assert.Equal("1.17.3", r.Version);
        Assert.Equal(TampVersionSource.Cli, r.Source);
        Assert.Contains("timed out", r.FailureReason);
    }

    [Fact]
    public async Task Returns_On_Time_Even_If_The_Feed_Ignores_Cancellation()
    {
        var task = Resolve("1.17.3", FakeVersionFeed.Unresponsive(), timeout: Short);
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(task, finished);
        Assert.Equal(TampVersionSource.Cli, (await task).Source);
    }

    [Fact]
    public async Task Falls_Back_To_Cli_Version_On_Network_Error()
    {
        var feed = FakeVersionFeed.Failing(new HttpRequestException("outer", new Exception("Name or service not known")));
        var r = await Resolve("1.17.3", feed);
        Assert.Equal("1.17.3", r.Version);
        Assert.Equal(TampVersionSource.Cli, r.Source);
        Assert.Equal("Name or service not known", r.FailureReason);   // innermost cause
    }

    [Fact]
    public async Task Falls_Back_To_Cli_Version_On_Unparseable_Response()
    {
        var feed = FakeVersionFeed.Failing(new JsonException("bad json"));
        var r = await Resolve("1.17.3", feed);
        Assert.Equal("1.17.3", r.Version);
        Assert.NotNull(r.FailureReason);
    }

    [Fact]
    public async Task Falls_Back_When_Feed_Lists_No_Stable_Versions()
    {
        var r = await Resolve("1.17.3", new FakeVersionFeed("2.0.0-alpha", "garbage"));
        Assert.Equal("1.17.3", r.Version);
        Assert.NotNull(r.FailureReason);
    }

    [Fact]
    public async Task Pinned_Version_Skips_The_Feed()
    {
        var feed = FakeVersionFeed.Failing(new InvalidOperationException("must not be called"));
        var r = await Resolve("1.17.3", feed, pinned: "1.17.4");
        Assert.Equal("1.17.4", r.Version);
        Assert.Equal(TampVersionSource.Flag, r.Source);
        Assert.Equal(0, feed.Calls);
    }

    [Fact]
    public async Task Caller_Cancellation_Is_Not_Swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TampVersionResolver.ResolveAsync("1.17.3", null, FakeVersionFeed.Hanging(), TimeSpan.FromSeconds(5), cts.Token));
    }

    [Theory]
    [InlineData("1.17.5", true)]
    [InlineData("1.18.0-rc.1", true)]
    [InlineData("1.17.5+sha.abc", true)]
    [InlineData("1.17", false)]
    [InlineData("v1.17.5", false)]
    [InlineData("01.17.5", false)]
    [InlineData("latest", false)]
    [InlineData("", false)]
    public void IsValidVersion_Accepts_SemVer_Only(string value, bool expected)
        => Assert.Equal(expected, TampVersionResolver.IsValidVersion(value));

    [Fact]
    public void PickLatestStable_Strips_Build_Metadata()
        => Assert.Equal("1.17.5", TampVersionResolver.PickLatestStable(["1.17.5+abc", "1.17.4"], "1.17.0"));

    [Fact]
    public void ParseIndex_Reads_Flat_Container_Payload()
    {
        var versions = NuGetFlatContainerVersionFeed.ParseIndex("""{"versions":["1.17.4","1.17.5"]}""");
        Assert.Equal(["1.17.4", "1.17.5"], versions);
    }

    [Theory]
    [InlineData("""{"items":[]}""")]
    [InlineData("""[]""")]
    [InlineData("""<html>proxy login</html>""")]
    public void ParseIndex_Rejects_Unexpected_Payloads(string json)
        => Assert.ThrowsAny<Exception>(() => NuGetFlatContainerVersionFeed.ParseIndex(json));

    [Fact]
    public void Feed_Url_Targets_Tamp_Core_Index()
    {
        var feed = new NuGetFlatContainerVersionFeed("https://mirror.example/v3-flatcontainer");
        Assert.Equal("https://mirror.example/v3-flatcontainer/", feed.DisplayName);
    }

    [Fact]
    public async Task Unreachable_Feed_Url_Fails_Fast_And_Falls_Back()
    {
        // Port 1 on loopback: connection refused immediately, no external network involved.
        var feed = new NuGetFlatContainerVersionFeed("http://127.0.0.1:1/v3-flatcontainer/");
        var r = await TampVersionResolver.ResolveAsync("1.17.3", null, feed, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("1.17.3", r.Version);
        Assert.Equal(TampVersionSource.Cli, r.Source);
        Assert.NotNull(r.FailureReason);
    }
}
