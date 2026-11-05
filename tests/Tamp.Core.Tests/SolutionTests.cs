using Xunit;

namespace Tamp.Core.Tests;

public sealed class SolutionTests : IDisposable
{
    private readonly string _scratch;

    public SolutionTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "tamp-sln-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private AbsolutePath Write(string relative, string content)
    {
        var p = Path.Combine(_scratch, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return AbsolutePath.Create(p);
    }

    private AbsolutePath ScratchRoot() => AbsolutePath.Create(_scratch);

    // ---- [Solution] attribute discovery (TAM-108, TAM-109) ----

    [Fact]
    public void Attribute_Positional_Ctor_Sets_Path()
    {
        var attr = new SolutionAttribute("src/dotnet/Foo.slnx");
        Assert.Equal("src/dotnet/Foo.slnx", attr.Path);
    }

    [Fact]
    public void Attribute_Positional_Ctor_Rejects_Empty()
    {
        Assert.Throws<ArgumentException>(() => new SolutionAttribute(""));
        Assert.Throws<ArgumentException>(() => new SolutionAttribute("   "));
    }

    [Fact]
    public void Attribute_Explicit_Path_Wins_Over_Discovery()
    {
        Write("Foo.slnx", "<Solution />");                 // top-level decoy
        Write("nested/Bar.slnx", "<Solution />");         // target
        var attr = new SolutionAttribute("nested/Bar.slnx");
        var located = attr.LocateSolutionFor(ScratchRoot());
        Assert.EndsWith("Bar.slnx", located.Value);
    }

    [Fact]
    public void Attribute_Explicit_Missing_Path_Throws_With_Helpful_Message()
    {
        var attr = new SolutionAttribute("does-not-exist.slnx");
        var ex = Assert.Throws<InvalidOperationException>(() => attr.LocateSolutionFor(ScratchRoot()));
        Assert.Contains("does-not-exist.slnx", ex.Message);
        Assert.Contains("file does not exist", ex.Message);
    }

    [Fact]
    public void Attribute_Discovers_Solution_At_Root()
    {
        Write("Foo.slnx", "<Solution />");
        var attr = new SolutionAttribute();
        var located = attr.LocateSolutionFor(ScratchRoot());
        Assert.EndsWith("Foo.slnx", located.Value);
    }

    [Fact]
    public void Attribute_Discovers_Single_Solution_In_Subtree()
    {
        // TAM-108 — monorepo with one solution under src/dotnet/.
        Write("src/dotnet/HoldFast.Backend.slnx", "<Solution />");
        var attr = new SolutionAttribute();
        var located = attr.LocateSolutionFor(ScratchRoot());
        Assert.EndsWith("HoldFast.Backend.slnx", located.Value);
    }

    [Fact]
    public void Attribute_Skips_node_modules_bin_obj_During_Subtree_Search()
    {
        // Performance / correctness: don't descend into the noise. A solution buried in
        // node_modules/ would NOT match — the consumer can still set Path explicitly.
        Write("node_modules/some-package/Buried.slnx", "<Solution />");
        Write("bin/Release/Buried.slnx", "<Solution />");
        Write("src/Real.slnx", "<Solution />");
        var attr = new SolutionAttribute();
        var located = attr.LocateSolutionFor(ScratchRoot());
        Assert.EndsWith("Real.slnx", located.Value);
    }

    [Fact]
    public void Attribute_Ambiguous_Subtree_Match_Throws_With_Candidates()
    {
        Write("src/A.slnx", "<Solution />");
        Write("other/B.slnx", "<Solution />");
        var attr = new SolutionAttribute();
        var ex = Assert.Throws<InvalidOperationException>(() => attr.LocateSolutionFor(ScratchRoot()));
        Assert.Contains("multiple solution files", ex.Message);
        Assert.Contains("A.slnx", ex.Message);
        Assert.Contains("B.slnx", ex.Message);
        Assert.Contains("[Solution(\"", ex.Message);  // points at positional ctor in fix-it hint
    }

    [Fact]
    public void Attribute_No_Solution_Anywhere_Throws_With_Install_Hint()
    {
        var attr = new SolutionAttribute();
        var ex = Assert.Throws<InvalidOperationException>(() => attr.LocateSolutionFor(ScratchRoot()));
        Assert.Contains("could not locate any .slnx", ex.Message);
    }

    [Fact]
    public void Attribute_Top_Level_Wins_Over_Subtree()
    {
        Write("Foo.slnx", "<Solution />");
        Write("src/dotnet/Bar.slnx", "<Solution />");
        var attr = new SolutionAttribute();
        var located = attr.LocateSolutionFor(ScratchRoot());
        Assert.EndsWith("Foo.slnx", located.Value);
    }

    [Fact]
    public void Attribute_Mixed_slnx_And_sln_In_Subtree_Both_Counted()
    {
        Write("src/dotnet/Foo.sln", "Project");
        var attr = new SolutionAttribute();
        var located = attr.LocateSolutionFor(ScratchRoot());
        Assert.EndsWith("Foo.sln", located.Value);
    }

    // ---- .slnx ----

    [Fact]
    public void Slnx_With_Single_Project_Resolves_Project_Path()
    {
        var slnx = Write("My.slnx", """
            <Solution>
              <Project Path="src/Foo/Foo.csproj" />
            </Solution>
            """);
        var sln = Solution.Load(slnx);
        Assert.Equal("My", sln.Name);
        Assert.Single(sln.Projects);
        Assert.Equal("Foo", sln.Projects[0].Name);
        Assert.EndsWith(Path.Combine("src", "Foo", "Foo.csproj"), sln.Projects[0].Path.Value);
    }

    [Fact]
    public void Slnx_Folders_Are_Tracked_And_Apply_To_Nested_Projects()
    {
        var slnx = Write("My.slnx", """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/A/A.csproj" />
                <Project Path="src/B/B.csproj" />
              </Folder>
              <Folder Name="/tests/">
                <Project Path="tests/A.Tests/A.Tests.csproj" />
              </Folder>
            </Solution>
            """);
        var sln = Solution.Load(slnx);
        Assert.Equal(3, sln.Projects.Count);
        Assert.Contains(sln.Folders, f => f.Name == "/src/");
        Assert.Contains(sln.Folders, f => f.Name == "/tests/");
        var a = sln.GetProject("A");
        Assert.NotNull(a);
        Assert.Equal("/src/", a!.SolutionFolderPath);
    }

    [Fact]
    public void Slnx_Empty_Solution_Has_No_Projects_Or_Folders()
    {
        var slnx = Write("Empty.slnx", "<Solution></Solution>");
        var sln = Solution.Load(slnx);
        Assert.Empty(sln.Projects);
        Assert.Empty(sln.Folders);
    }

    [Fact]
    public void Slnx_With_Files_Folder_Does_Not_Surface_Files_As_Projects()
    {
        var slnx = Write("My.slnx", """
            <Solution>
              <Folder Name="/Solution Items/">
                <File Path="README.md" />
              </Folder>
              <Folder Name="/src/">
                <Project Path="src/Foo/Foo.csproj" />
              </Folder>
            </Solution>
            """);
        var sln = Solution.Load(slnx);
        Assert.Single(sln.Projects);
        Assert.Equal("Foo", sln.Projects[0].Name);
    }

    // ---- .sln (legacy) ----

    [Fact]
    public void Sln_Single_Project_Round_Trips()
    {
        var sln = Write("Legacy.sln",
            "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
            "# Visual Studio Version 17\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Foo\", \"src\\Foo\\Foo.csproj\", \"{11111111-2222-3333-4444-555555555555}\"\n" +
            "EndProject\n" +
            "Global\nEndGlobal\n");
        var solution = Solution.Load(sln);
        Assert.Single(solution.Projects);
        Assert.Equal("Foo", solution.Projects[0].Name);
        Assert.EndsWith(Path.Combine("src", "Foo", "Foo.csproj"), solution.Projects[0].Path.Value);
    }

    [Fact]
    public void Sln_Solution_Folders_Are_Tracked_Not_As_Projects()
    {
        var sln = Write("Legacy.sln",
            "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
            "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"Solution Items\", \"Solution Items\", \"{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}\"\n" +
            "EndProject\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Foo\", \"src\\Foo\\Foo.csproj\", \"{11111111-2222-3333-4444-555555555555}\"\n" +
            "EndProject\n");
        var solution = Solution.Load(sln);
        Assert.Single(solution.Projects);  // Foo only
        Assert.Single(solution.Folders);   // Solution Items
    }

    // ---- Failure modes ----

    [Fact]
    public void Load_Throws_On_Missing_File()
    {
        var missing = AbsolutePath.Create(Path.Combine(_scratch, "nope.slnx"));
        Assert.Throws<InvalidOperationException>(() => Solution.Load(missing));
    }

    [Fact]
    public void Load_Throws_On_Unsupported_Extension()
    {
        var fake = Write("Weird.txt", "");
        Assert.Throws<InvalidOperationException>(() => Solution.Load(fake));
    }

    // ---- GetProject ----

    [Fact]
    public void GetProject_Is_Case_Insensitive()
    {
        var slnx = Write("My.slnx", """
            <Solution>
              <Project Path="src/Foo/Foo.csproj" />
            </Solution>
            """);
        var sln = Solution.Load(slnx);
        Assert.NotNull(sln.GetProject("foo"));
        Assert.NotNull(sln.GetProject("FOO"));
    }

    [Fact]
    public void GetProject_Returns_Null_For_Unknown_Name()
    {
        var slnx = Write("My.slnx", "<Solution></Solution>");
        var sln = Solution.Load(slnx);
        Assert.Null(sln.GetProject("Missing"));
    }

    [Fact]
    public void Solution_Converts_To_Its_Path_For_String_Taking_Apis()
    {
        // NUKE parity: `SetProject(Solution)` passes the solution file's path (the migrator's SetProjectFile row).
        var sln = Write("Legacy.sln", "Microsoft Visual Studio Solution File, Format Version 12.00\nGlobal\nEndGlobal\n");
        var solution = Solution.Load(sln);

        string asString = solution;

        Assert.Equal(sln.Value, asString);
        Assert.Equal(solution.Path.Value, Takes(solution));
        static string Takes(string project) => project;
    }
}
