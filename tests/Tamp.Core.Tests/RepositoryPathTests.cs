using Tamp;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// tamp-syft#11 / tamp-grype#9 — both wrappers' private copies of this logic turned a source root of
/// <c>/</c> into the empty string, which then read as "no root supplied" and rejected every absolute
/// location. That made the one root value which correctly describes a scanner's virtual-filesystem
/// output the one value that could not be used.
/// </summary>
public class RepositoryPathTests
{
    // ─── the defect both issues were filed for ────────────────────────────

    [Theory]
    // syft records locations against the scan's virtual filesystem, so a lockfile at the root of the
    // scanned tree is "/pnpm-lock.yaml" — already repository-relative but for the leading slash.
    [InlineData("/pnpm-lock.yaml", "pnpm-lock.yaml")]
    [InlineData("/frontend/pnpm-lock.yaml", "frontend/pnpm-lock.yaml")]
    [InlineData("/a/b/c/go.mod", "a/b/c/go.mod")]
    public void The_Filesystem_Root_Strips_One_Leading_Slash(string location, string expected)
        => Assert.Equal(expected, RepositoryPath.RelativizeAgainst(location, "/"));

    [Theory]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("///")]
    [InlineData(@"\")]
    [InlineData("  /  ")]
    public void An_All_Slashes_Root_Normalizes_To_The_Root_Not_To_Empty(string root)
        => Assert.Equal("/", RepositoryPath.NormalizeRoot(root));

    [Fact]
    public void The_Root_Is_Never_Confused_With_An_Absent_Root()
    {
        // The crux: "" and null both mean "no root", and "/" must not collapse into them.
        Assert.NotNull(RepositoryPath.NormalizeRoot("/"));
        Assert.NotEqual("", RepositoryPath.NormalizeRoot("/"));

        // With a real root the location relativizes; with no root it cannot.
        Assert.Equal("pnpm-lock.yaml", RepositoryPath.Relativize("/pnpm-lock.yaml", "/"));
        Assert.Null(RepositoryPath.Relativize("/pnpm-lock.yaml", null));
    }

    // ─── roots that are real directories ──────────────────────────────────

    [Theory]
    [InlineData("/src/repo/go.mod", "/src/repo", "go.mod")]
    [InlineData("/src/repo/a/b.cs", "/src/repo", "a/b.cs")]
    [InlineData("/src/repo/go.mod", "/src/repo/", "go.mod")]
    // Windows: backslashes, and casing that does not match the configured root. Observed from syft:
    // C:\TEMP\... against a root of C:\Temp.
    [InlineData(@"C:\TEMP\repo\frontend\pnpm-lock.yaml", @"C:\Temp\repo", "frontend/pnpm-lock.yaml")]
    [InlineData(@"C:/repo/go.mod", "C:/repo", "go.mod")]
    [InlineData(@"c:\repo\go.mod", @"C:\repo", "go.mod")]
    // Already relative: the scanner emitted it relative to the scan root, which is what we want.
    [InlineData("go.mod", "/src/repo", "go.mod")]
    [InlineData("./frontend/yarn.lock", "/src/repo", "frontend/yarn.lock")]
    [InlineData(".//./go.mod", "/src/repo", "go.mod")]
    // A root joined to a path by string concatenation. Without collapsing, this relativized to
    // "/go.mod" — still absolute, which is exactly what must never reach a report.
    [InlineData("/src/repo//go.mod", "/src/repo", "go.mod")]
    [InlineData("/src/repo/./go.mod", "/src/repo", "go.mod")]
    [InlineData("/src//repo/go.mod", "/src/repo", "go.mod")]
    public void A_Location_Under_The_Root_Becomes_Relative(string location, string root, string expected)
        => Assert.Equal(expected, RepositoryPath.RelativizeAgainst(location, root));

    [Theory]
    [InlineData("/elsewhere/go.mod", "/src/repo")]
    [InlineData(@"D:\other\go.mod", @"C:\repo")]
    // A sibling directory whose name merely starts with the root's name must not match: the
    // separator is part of the comparison.
    [InlineData("/src/repository/go.mod", "/src/repo")]
    [InlineData("/src/repo-2/go.mod", "/src/repo")]
    // The root itself is a directory, not a file a finding can anchor to.
    [InlineData("/src/repo", "/src/repo")]
    [InlineData("/src/repo/", "/src/repo")]
    [InlineData("/", "/")]
    public void A_Location_Outside_The_Root_Is_Null(string location, string root)
        => Assert.Null(RepositoryPath.RelativizeAgainst(location, root));

    [Fact]
    public void POSIX_Comparison_Stays_Case_Sensitive()
    {
        // Case is significant on POSIX, so /SRC/repo is genuinely a different directory.
        Assert.Null(RepositoryPath.RelativizeAgainst("/SRC/repo/go.mod", "/src/repo"));
        Assert.Equal("go.mod", RepositoryPath.RelativizeAgainst("/src/repo/go.mod", "/src/repo"));
    }

    // ─── absent root ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_Root_Normalizes_To_Null(string? root)
        => Assert.Null(RepositoryPath.NormalizeRoot(root));

    [Fact]
    public void Without_A_Root_An_Absolute_Location_Cannot_Be_Rooted_And_A_Relative_One_Passes()
    {
        Assert.Null(RepositoryPath.RelativizeAgainst("/src/repo/go.mod", null));
        Assert.Equal("go.mod", RepositoryPath.RelativizeAgainst("go.mod", null));
        Assert.Equal("a/b.cs", RepositoryPath.RelativizeAgainst(@"a\b.cs", null));
    }

    // ─── empty and nonsense input ─────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("./")]
    public void An_Empty_Or_Root_Only_Location_Is_Null(string? location)
        => Assert.Null(RepositoryPath.RelativizeAgainst(location, "/src/repo"));

    // ─── absoluteness ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("/x", true)]
    [InlineData("//server/share", true)]
    [InlineData("C:/x", true)]
    [InlineData("c:/x", true)]
    [InlineData("go.mod", false)]
    [InlineData("a/b", false)]
    // A drive letter with no separator is drive-relative on Windows, not absolute.
    [InlineData("C:x", false)]
    [InlineData("C:", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Absoluteness_Is_Recognised(string? path, bool expected)
        => Assert.Equal(expected, RepositoryPath.IsAbsolute(path));

    [Theory]
    [InlineData(@"C:\x", true)]
    [InlineData("C:/x", true)]
    [InlineData("C:", true)]
    [InlineData("/x", false)]
    [InlineData("x", false)]
    [InlineData(null, false)]
    public void Windows_Shaped_Paths_Are_Recognised(string? path, bool expected)
        => Assert.Equal(expected, RepositoryPath.LooksWindows(path));

    [Fact]
    public void A_UNC_Root_Is_Handled()
    {
        // The syft copy handled these and the grype copy did not — one of the three ways the two
        // private copies had already diverged.
        Assert.Equal("go.mod", RepositoryPath.RelativizeAgainst(@"\\server\share\go.mod", @"\\server\share"));
        Assert.Null(RepositoryPath.RelativizeAgainst(@"\\other\share\go.mod", @"\\server\share"));
    }

    // ─── the property that matters to callers ─────────────────────────────

    [Fact]
    public void Dot_Dot_Is_Not_Resolved_Away()
    {
        // Resolving ".." would let a location escape the root it was just checked against, so it is
        // left alone and travels verbatim. A scanner reporting one is suspect anyway.
        Assert.Equal("a/../b.cs", RepositoryPath.RelativizeAgainst("/src/repo/a/../b.cs", "/src/repo"));
        Assert.Null(RepositoryPath.RelativizeAgainst("/src/repo/../escaped.cs", "/src/repo/sub"));
    }

    [Fact]
    public void A_Relativized_Path_Is_Never_Absolute()
    {
        // The whole point: a caller emits the result into a report, and an absolute path there
        // anchors the finding to nothing in the repository. Whatever comes back must be usable.
        string?[] locations =
        [
            "/pnpm-lock.yaml", "/src/repo/go.mod", @"C:\Temp\repo\x.cs", "already/relative.cs",
            "./dot/relative.cs", @"\\server\share\x.cs", "/src/repo//go.mod", "/src/repo/./a//b.cs",
        ];
        string?[] roots = ["/", "/src/repo", @"C:\Temp\repo", @"\\server\share", null];

        foreach (var location in locations)
            foreach (var root in roots)
            {
                var result = RepositoryPath.RelativizeAgainst(location, root);
                if (result is null) continue;
                Assert.False(RepositoryPath.IsAbsolute(result), $"'{location}' under '{root}' gave absolute '{result}'");
                Assert.DoesNotContain('\\', result);
                Assert.False(result.StartsWith('/'), $"'{result}' still starts with a slash");
                Assert.NotEqual("", result);
            }
    }
}
