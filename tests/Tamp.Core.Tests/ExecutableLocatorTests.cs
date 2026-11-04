using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// #133 (Sonar S4036): OS commands Tamp launches are resolved to an absolute path first, and a candidate that
/// is relative or inside the working directory is refused. Every case runs against a fake file system, so the
/// Windows-mode cases run on Linux and the other way round, and nothing is launched.
/// </summary>
public sealed class ExecutableLocatorTests
{
    private static ExecutableLocator Unix(string path, params string[] files) =>
        new(path, "/repo", isWindows: false, Fs(files));

    private static ExecutableLocator Windows(string path, params string[] files) =>
        new(path, @"C:\Repo", isWindows: true, Fs(files));

    // Compare with '/' so a Windows-mode path built on a Unix host (and vice versa) still matches.
    private static Func<string, bool> Fs(string[] files)
    {
        var set = files.Select(N).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return f => set.Contains(N(f));
    }

    private static string N(string? p) => (p ?? "").Replace('\\', '/');

    [Fact]
    public void Resolves_A_Command_On_Path_To_Its_Absolute_Path()
    {
        var git = Unix("/usr/local/bin:/usr/bin", "/usr/bin/git").Resolve("git");
        Assert.Equal("/usr/bin/git", N(git));
    }

    [Fact]
    public void Missing_Command_Resolves_To_Null()
        => Assert.Null(Unix("/usr/local/bin:/usr/bin").Resolve("git"));

    [Theory]
    [InlineData(".")]
    [InlineData("bin")]
    [InlineData("./tools")]
    [InlineData("../elsewhere")]
    public void A_Relative_Path_Entry_Is_Refused(string entry)
    {
        // A relative entry means "relative to wherever the process happens to be": exactly the planted-binary hole.
        var locator = new ExecutableLocator($"{entry}:/usr/bin", "/repo", isWindows: false,
            f => N(f).EndsWith("/git", StringComparison.Ordinal) && !N(f).StartsWith("/usr/bin/", StringComparison.Ordinal) || N(f) == "/usr/bin/git");
        Assert.Equal("/usr/bin/git", N(locator.Resolve("git")));
    }

    [Fact]
    public void A_Candidate_Inside_The_Current_Directory_Is_Refused()
    {
        var locator = Unix("/repo/tools:/repo:/usr/bin", "/repo/tools/git", "/repo/git", "/usr/bin/git");
        Assert.Equal("/usr/bin/git", N(locator.Resolve("git")));
    }

    [Fact]
    public void Only_A_Candidate_Inside_The_Current_Directory_Means_Not_Found()
        => Assert.Null(Unix("/repo/tools", "/repo/tools/git").Resolve("git"));

    [Fact]
    public void A_Candidate_Inside_The_Launch_Working_Directory_Is_Refused()
    {
        var locator = Unix("/work/checkout/bin:/usr/bin", "/work/checkout/bin/git", "/usr/bin/git");
        Assert.Equal("/usr/bin/git", N(locator.Resolve("git", workingDirectory: "/work/checkout")));
    }

    [Theory]
    [InlineData("/repo/../usr/bin", true)]    // normalises to /usr/bin: outside
    [InlineData("/usr/../repo/tools", false)] // normalises to /repo/tools: inside
    [InlineData("/repository/bin", true)]     // a sibling that merely shares the prefix
    public void Containment_Is_Decided_On_Normalised_Paths(string entry, bool found)
    {
        var dir = N(entry);
        var locator = new ExecutableLocator(entry, "/repo", isWindows: false, f => N(f) == dir + "/git");
        Assert.Equal(found, locator.Resolve("git") is not null);
    }

    [Fact]
    public void A_Known_Location_Wins_Over_Path()
    {
        var locator = Unix("/opt/evil", "/opt/evil/security", "/usr/bin/security");
        Assert.Equal("/usr/bin/security", locator.Resolve("security", null, "/usr/bin/security"));
    }

    [Fact]
    public void A_Missing_Known_Location_Falls_Back_To_Path()
        => Assert.Equal("/usr/bin/security", N(Unix("/usr/bin", "/usr/bin/security").Resolve("security", null, "/nope/security")));

    [Fact]
    public void A_Name_That_Is_A_Path_Is_Rejected()
    {
        Assert.Throws<ArgumentException>(() => Unix("/usr/bin").Resolve("./git"));
        Assert.Throws<ArgumentException>(() => Unix("/usr/bin").Resolve(@"tools\git"));
        Assert.Throws<ArgumentException>(() => Unix("/usr/bin").Resolve(" "));
    }

    // ------------------------------------------------------------------ Windows rules

    [Fact]
    public void Windows_Resolves_A_Launchable_Extension()
    {
        var locator = Windows(@"C:\Program Files\Git\cmd;C:\Windows", @"C:\Program Files\Git\cmd\git.exe");
        Assert.Equal("C:/Program Files/Git/cmd/git.exe", N(locator.Resolve("git")));
    }

    [Fact]
    public void Windows_Never_Resolves_An_Extensionless_File()
        => Assert.Null(Windows(@"C:\Tools", @"C:\Tools\git").Resolve("git"));

    [Fact]
    public void Windows_Refuses_The_Checkout_Case_Insensitively()
    {
        // The classic: a git.exe committed to the repo, reached through a PATH entry that differs only in case.
        var locator = Windows(@"c:\repo\tools;C:\Program Files\Git\cmd", @"c:\repo\tools\git.exe", @"C:\Program Files\Git\cmd\git.exe");
        Assert.Equal("C:/Program Files/Git/cmd/git.exe", N(locator.Resolve("git")));
    }

    [Theory]
    [InlineData(@"tools")]
    [InlineData(@"\tools")] // rooted but drive-relative: still "wherever the process is"
    public void Windows_Refuses_Relative_Entries(string entry)
        => Assert.Null(Windows(entry, entry + @"\git.exe").Resolve("git"));

    // ------------------------------------------------------------------ dotnet

    [Fact]
    public void Dotnet_Prefers_The_Running_Host()
    {
        var locator = new ExecutableLocator("/usr/bin", "/repo", isWindows: false, Fs(["/usr/bin/dotnet", "/opt/dotnet/dotnet"]),
            _ => null, processPath: "/opt/dotnet/dotnet");
        Assert.Equal("/opt/dotnet/dotnet", locator.ResolveDotnet());
    }

    [Fact]
    public void Dotnet_Uses_Dotnet_Root_Even_Inside_The_Checkout()
    {
        // A repo-local SDK (./.dotnet via dotnet-install) is reached explicitly through DOTNET_ROOT, not PATH.
        var locator = new ExecutableLocator("/repo/.dotnet:/usr/bin", "/repo", isWindows: false, Fs(["/repo/.dotnet/dotnet"]),
            k => k == "DOTNET_ROOT" ? "/repo/.dotnet" : null, processPath: "/home/u/.dotnet/tools/tamp");
        Assert.Equal("/repo/.dotnet/dotnet", N(locator.ResolveDotnet()));
    }

    [Fact]
    public void Dotnet_Falls_Back_To_Path_Under_The_Same_Rules()
    {
        var locator = new ExecutableLocator("/repo/.dotnet:/usr/share/dotnet", "/repo", isWindows: false,
            Fs(["/repo/.dotnet/dotnet", "/usr/share/dotnet/dotnet"]), _ => null, processPath: "/home/u/.dotnet/tools/tamp");
        Assert.Equal("/usr/share/dotnet/dotnet", N(locator.ResolveDotnet()));
    }

    // ------------------------------------------------------------------ the lenient walk Tool.TryFromPath keeps

    [Fact]
    public void The_Lenient_Walk_Still_Searches_Every_Entry()
    {
        // [FromPath] tools a build deliberately puts on PATH under its own tree keep resolving (no behaviour change).
        var locator = Unix("/repo/tools:/usr/bin", "/repo/tools/grype", "/usr/bin/grype");
        Assert.Equal("/repo/tools/grype", N(locator.FindOnPath("grype", [""], refusedRoots: null)));
    }

    // ------------------------------------------------------------------ the call sites degrade cleanly

    [Fact]
    public void Linux_Secret_Store_Without_Secret_Tool_Is_A_Clean_Null()
        => Assert.Null(new LinuxSecretToolStore(Unix("/usr/bin")).TryGet("ANY"));

    [Fact]
    public void Linux_Secret_Store_Refuses_A_Checkout_Local_Secret_Tool()
        // Would be handed the secret; with only a working-directory candidate it is "not installed", not launched.
        => Assert.Null(new LinuxSecretToolStore(Unix("/repo/bin", "/repo/bin/secret-tool")).TryGet("ANY"));

    [Fact]
    public void MacOs_Keychain_Store_Without_Security_Is_A_Clean_Null()
        => Assert.Null(new MacOsKeychainStore(Unix("/repo/bin", "/repo/bin/security")).TryGet("ANY"));

    [Fact]
    public void MacOs_Keychain_Store_Pins_The_System_Binary()
        => Assert.Equal("/usr/bin/security", MacOsKeychainStore.SecurityPath);

    [Fact]
    public void Git_Runner_Without_Git_Says_So()
    {
        var runner = new ProcessGitRunner(Unix("/repo/bin", "/repo/bin/git"));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            runner.Run(AbsolutePath.Create(Path.GetTempPath()), ["status"], TimeSpan.FromSeconds(5)));
        Assert.Contains("git not found on PATH", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Current_Locator_Finds_Dotnet_On_A_Normal_Machine()
        // The test host is dotnet, so this holds on every CI leg: no behaviour change where the tools are installed.
        => Assert.NotNull(ExecutableLocator.Current.ResolveDotnet());
}
