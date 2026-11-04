using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// #134 (Sonar S6444): every Tamp.Core regex runs with the one shared timeout. Each site still parses normal
/// input exactly as before, a pathological input finishes fast (the patterns are linear, so the timeout is
/// never reached), and a timeout surfaces as an error naming the input length instead of a silent no-match.
/// </summary>
public sealed class TampRegexTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "tamp-regex-" + Guid.NewGuid().ToString("N"));

    public TampRegexTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------ one timeout, in one place

    [Theory]
    [InlineData(typeof(GitRepository), "RemoteUrlRegex")]
    [InlineData(typeof(Solution), "ProjectLineRegex")]
    [InlineData(typeof(TargetDefinition), "SimpleIdentifier")]
    public void Every_Site_Uses_The_Shared_Timeout(Type owner, string field)
    {
        var regex = (Regex)owner.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(TampRegex.MatchTimeout, regex.MatchTimeout);
        Assert.True(TampRegex.MatchTimeout > TimeSpan.Zero && TampRegex.MatchTimeout <= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_Timeout_Is_An_Error_Naming_The_Input_Not_A_Silent_Miss()
    {
        // A deliberately catastrophic pattern with a tiny ceiling stands in for a future bad edit.
        var bad = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(5));
        var input = new string('a', 40) + "!";

        var ex = Assert.Throws<InvalidDataException>(() => TampRegex.Match(bad, input, "a Project line of x.sln"));
        Assert.Contains("a Project line of x.sln", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"{input.Length} characters", ex.Message, StringComparison.Ordinal);
        Assert.IsType<RegexMatchTimeoutException>(ex.InnerException);
    }

    // ------------------------------------------------------------------ GitRepository: origin url

    [Theory]
    [InlineData("\turl = git@github.com:owner/repo.git", "git@github.com:owner/repo.git")]
    [InlineData("url=https://github.com/owner/repo.git", "https://github.com/owner/repo.git")]
    [InlineData("    url   =   https://example.com/a b.git   ", "https://example.com/a b.git")]
    [InlineData("\turl = https://x/y.git\t", "https://x/y.git")]
    public void Origin_Url_Parses_As_Before(string line, string expected)
        => Assert.Equal(expected, LoadRepo(line).RemoteUrl);

    [Theory]
    [InlineData("\tpushurl = https://push/x.git")]
    [InlineData("\turlx = https://x.git")]
    [InlineData("\turl =    ")]
    public void A_Line_That_Is_Not_An_Origin_Url_Is_Skipped(string line)
        => Assert.Null(LoadRepo(line).RemoteUrl);

    [Fact]
    public void A_Pathological_Url_Line_Finishes_Fast()
    {
        // The old lazy `(.+?)\s*$` re-tried the tail at every character: quadratic on a long whitespace run.
        var line = "\turl = a" + new string(' ', 200_000) + "b" + new string(' ', 200_000);
        var sw = Stopwatch.StartNew();
        var repo = LoadRepo(line);
        Assert.StartsWith("a ", repo.RemoteUrl, StringComparison.Ordinal);
        Assert.EndsWith(" b", repo.RemoteUrl, StringComparison.Ordinal);
        Assert.True(sw.Elapsed < TampRegex.MatchTimeout, $"took {sw.Elapsed}");
    }

    private GitRepository LoadRepo(string urlLine)
    {
        Directory.CreateDirectory(Path.Combine(_scratch, ".git"));
        File.WriteAllText(Path.Combine(_scratch, ".git", "HEAD"), "abc1234567890abcdef1234567890abcdef12345\n");
        File.WriteAllText(Path.Combine(_scratch, ".git", "config"),
            "[core]\n\tbare = false\n[remote \"origin\"]\n" + urlLine + "\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n");
        return GitRepository.Load(AbsolutePath.Create(_scratch));
    }

    // ------------------------------------------------------------------ Solution: .sln Project lines

    [Fact]
    public void Sln_Project_Lines_Parse_As_Before()
    {
        var sln = WriteSln(
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Foo\", \"src\\Foo\\Foo.csproj\", \"{11111111-2222-3333-4444-555555555555}\"",
            "Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"Bar.Tests\", \"tests\\Bar.Tests\\Bar.Tests.csproj\", \"{AAAAAAAA-2222-3333-4444-555555555555}\"",
            "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"src\", \"src\", \"{BBBBBBBB-2222-3333-4444-555555555555}\"");

        var solution = Solution.Load(sln);

        Assert.Equal(["Foo", "Bar.Tests"], solution.Projects.Select(p => p.Name));
        Assert.EndsWith(Path.Combine("tests", "Bar.Tests", "Bar.Tests.csproj"), solution.Projects[1].Path.Value);
        Assert.Contains(solution.Folders, f => f.Name == "/src/");
    }

    [Fact]
    public void A_Pathological_Sln_Line_Finishes_Fast_And_Is_Skipped()
    {
        // Almost a Project line, padded so a backtracking pattern would thrash: no match, quickly.
        var line = "Project(\"{" + new string('A', 200_000) + "\") = \"" + new string(' ', 200_000);
        var sw = Stopwatch.StartNew();
        var solution = Solution.Load(WriteSln(line));
        Assert.Empty(solution.Projects);
        Assert.True(sw.Elapsed < TampRegex.MatchTimeout, $"took {sw.Elapsed}");
    }

    private AbsolutePath WriteSln(params string[] projectLines)
    {
        var path = Path.Combine(_scratch, "Legacy.sln");
        File.WriteAllText(path,
            "Microsoft Visual Studio Solution File, Format Version 12.00\n"
            + string.Concat(projectLines.Select(l => l + "\nEndProject\n"))
            + "Global\nEndGlobal\n");
        return AbsolutePath.Create(path);
    }

    // ------------------------------------------------------------------ Target: captured target references

    [Theory]
    [InlineData("Restore", "Restore")]
    [InlineData("  this.Compile ", "Compile")]
    [InlineData("_internal2", "_internal2")]
    public void Target_References_Normalise_As_Before(string expression, string expected)
        => Assert.Equal(expected, TargetDefinition.NormalizeCapturedTargetName(expression, "DependsOn"));

    [Theory]
    [InlineData("Build.Restore")]
    [InlineData("2Fast")]
    [InlineData("Restore()")]
    [InlineData("Ré")]
    public void Non_Simple_Target_References_Are_Rejected_As_Before(string expression)
        => Assert.Throws<ArgumentException>(() => TargetDefinition.NormalizeCapturedTargetName(expression, "DependsOn"));

    [Fact]
    public void A_Long_Target_Reference_Finishes_Fast()
    {
        var sw = Stopwatch.StartNew();
        Assert.Throws<ArgumentException>(() => TargetDefinition.NormalizeCapturedTargetName(new string('a', 500_000) + "!", "DependsOn"));
        Assert.True(sw.Elapsed < TampRegex.MatchTimeout, $"took {sw.Elapsed}");
    }
}
