using System;
using System.IO;
using System.Net.Http;
using Tamp.Cli.Scaffold.Commands;
using Xunit;

namespace Tamp.Cli.Tests.Scaffold;

/// <summary>
/// #119: what `tamp init` pins, where it says it came from, and the --tamp-version flag.
/// </summary>
public sealed class InitVersionPinTests : IDisposable
{
    private readonly string _root;
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();
    private static readonly string Cli = InitCommand.ResolveCliVersion();
    private static readonly string Newer = Bump(Cli);

    public InitVersionPinTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tamp-init-ver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static string Bump(string v)
    {
        var p = Version.Parse(v);
        return $"{p.Major}.{p.Minor}.{p.Build + 1}";
    }

    private int Run(FakeVersionFeed feed, params string[] args)
        => InitCommand.Run([.. args, _root], _stdout, _stderr, feed, TimeSpan.FromMilliseconds(200));

    private string Csproj => File.ReadAllText(Path.Combine(_root, "build", "Build.csproj"));
    private string ToolsJson => File.ReadAllText(Path.Combine(_root, ".config", "dotnet-tools.json"));

    private void AssertPinnedEverywhere(string version)
    {
        Assert.Contains($"<PackageReference Include=\"Tamp.Core\" Version=\"{version}\" />", Csproj);
        Assert.Contains($"<PackageReference Include=\"Tamp.NetCli.V10\" Version=\"{version}\" />", Csproj);
        Assert.Contains($"\"version\": \"{version}\"", ToolsJson);
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("library")]
    [InlineData("monorepo")]
    public void Every_Template_Pins_The_Feed_Version_In_All_Three_Places(string template)
    {
        var exit = Run(new FakeVersionFeed(Cli, Newer, "99.0.0-preview.1"), "--template", template);
        Assert.Equal(InitCommand.ExitOk, exit);
        AssertPinnedEverywhere(Newer);
        Assert.DoesNotContain(Cli + "\"", Csproj.Replace(Newer, ""));
    }

    [Fact]
    public void Output_Names_Version_And_Feed_And_Suggests_Global_Tool_Update_When_Cli_Is_Older()
    {
        Run(new FakeVersionFeed(Cli, Newer));
        var stdout = _stdout.ToString();
        Assert.Contains($"Tamp {Newer} (latest stable Tamp.Core on https://feed.test/v3-flatcontainer/)", stdout);
        Assert.Contains($"this CLI is {Cli}", stdout);
        Assert.Contains("dotnet tool update -g dotnet-tamp", stdout);
    }

    [Fact]
    public void No_Update_Note_When_Cli_Is_Current()
    {
        Run(new FakeVersionFeed(Cli));
        var stdout = _stdout.ToString();
        Assert.Contains($"Tamp {Cli} (latest stable Tamp.Core on", stdout);
        Assert.DoesNotContain("dotnet tool update", stdout);
        Assert.DoesNotContain("couldn't check", stdout);
    }

    [Fact]
    public void Unreachable_Feed_Falls_Back_To_Cli_Version_With_Note_And_Succeeds()
    {
        var exit = Run(FakeVersionFeed.Failing(new HttpRequestException("No such host is known.")));
        Assert.Equal(InitCommand.ExitOk, exit);
        AssertPinnedEverywhere(Cli);
        var stdout = _stdout.ToString();
        Assert.Contains($"Tamp {Cli} (this CLI's version)", stdout);
        Assert.Contains("couldn't check https://feed.test/v3-flatcontainer/ for a newer Tamp (No such host is known)", stdout);
        Assert.Contains("may be stale", stdout);
    }

    [Fact]
    public void Hanging_Feed_Times_Out_And_Init_Still_Succeeds()
    {
        var exit = Run(FakeVersionFeed.Hanging());
        Assert.Equal(InitCommand.ExitOk, exit);
        AssertPinnedEverywhere(Cli);
        Assert.Contains("timed out", _stdout.ToString());
    }

    [Fact]
    public void Tamp_Version_Flag_Pins_And_Skips_Lookup()
    {
        var feed = FakeVersionFeed.Failing(new InvalidOperationException("must not be called"));
        var exit = Run(feed, "--tamp-version", "1.17.4");
        Assert.Equal(InitCommand.ExitOk, exit);
        Assert.Equal(0, feed.Calls);
        AssertPinnedEverywhere("1.17.4");
        Assert.Contains("Tamp 1.17.4 (pinned with --tamp-version)", _stdout.ToString());
    }

    [Fact]
    public void Tamp_Version_Below_Template_Minimum_Is_Refused()
    {
        var exit = Run(new FakeVersionFeed(Cli), "--tamp-version", "1.0.0");
        Assert.Equal(InitCommand.ExitDriftMismatch, exit);
        Assert.Contains("--tamp-version 1.3.0", _stderr.ToString());
    }

    [Fact]
    public void Dry_Run_Reports_The_Version_It_Would_Pin()
    {
        Run(new FakeVersionFeed(Cli, Newer), "--dry-run");
        Assert.Contains($"Tamp {Newer} (", _stdout.ToString());
        Assert.False(File.Exists(Path.Combine(_root, "build", "Build.csproj")));
    }

    [Fact]
    public void Existing_Tools_Manifest_Is_Called_Out()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".config"));
        File.WriteAllText(Path.Combine(_root, ".config", "dotnet-tools.json"), """{"version":1,"isRoot":true,"tools":{}}""");
        Run(new FakeVersionFeed(Cli, Newer));
        Assert.Contains($"keep its dotnet-tamp pin at {Newer}", _stdout.ToString());
    }

    [Fact]
    public void Refusal_Happens_Before_Any_Lookup()
    {
        Directory.CreateDirectory(Path.Combine(_root, "build"));
        File.WriteAllText(Path.Combine(_root, "build", "Build.cs"), "// preexisting");
        var feed = new FakeVersionFeed(Cli);
        Assert.Equal(InitCommand.ExitFileExists, Run(feed));
        Assert.Equal(0, feed.Calls);
    }

    // ─── arg parser ─────────────────────────────────────────────────────

    [Fact]
    public void Parser_Reads_Tamp_Version()
    {
        var opts = InitCommand.ParseArgs(["--tamp-version", "1.18.0-rc.1", "dir"], out var error);
        Assert.Null(error);
        Assert.Equal("1.18.0-rc.1", opts.TampVersion);
        Assert.Equal("dir", opts.WorkingDirectory);
    }

    [Fact]
    public void Parser_Leaves_Tamp_Version_Null_By_Default()
    {
        var opts = InitCommand.ParseArgs([], out var error);
        Assert.Null(error);
        Assert.Null(opts.TampVersion);
    }

    [Fact]
    public void Parser_Requires_A_Value()
    {
        InitCommand.ParseArgs(["--tamp-version"], out var error);
        Assert.Contains("--tamp-version requires a version", error);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("1.17")]
    [InlineData("v1.17.5")]
    public void Parser_Rejects_Invalid_Versions(string value)
    {
        InitCommand.ParseArgs(["--tamp-version", value], out var error);
        Assert.Contains("is not a valid version", error);
    }

    [Fact]
    public void Invalid_Version_Exits_Usage()
    {
        Assert.Equal(InitCommand.ExitUsage, Run(new FakeVersionFeed(Cli), "--tamp-version", "nope"));
        Assert.Contains("--tamp-version", _stderr.ToString());
    }

    [Fact]
    public void Help_Mentions_Tamp_Version_Flag_And_Feed_Variable()
    {
        InitCommand.Run(["--help"], _stdout, _stderr, new FakeVersionFeed(Cli));
        var help = _stdout.ToString();
        Assert.Contains("--tamp-version <x>", help);
        Assert.Contains("TAMP_INIT_FEED_URL", help);
    }
}
