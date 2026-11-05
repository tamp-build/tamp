using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tamp.Migrate.Mapping;
using Tamp.Migrate.Report;
using Tamp.Migrate.Rewriting;
using Tamp.NetCli.V10;
using Xunit;

namespace Tamp.Migrate.Tests;

/// <summary>
/// #131: the auto-tier rewrite. One before/after case per <c>auto</c> row of the embedded mapping, a marker
/// case per <c>assisted</c> / <c>manual</c> row, the coverage report, idempotence, and the DotNet-family output
/// compiled against the in-tree Tamp.Core and Tamp.NetCli.V10. Source strings only: no SDK, no processes.
/// </summary>
public sealed class MigratorTests
{
    private static readonly MigrationMapping Mapping = MigrationMapping.LoadEmbedded();

    private const string NukeHeader = "using Nuke.Common;\n\nclass Build : NukeBuild\n{\n";
    private const string NukeDotNetHeader = "using Nuke.Common;\nusing static Nuke.Common.Tools.DotNet.DotNetTasks;\n\nclass Build : NukeBuild\n{\n";
    private const string TampHeader = "using Tamp;\n\nclass Build : TampBuild\n{\n";
    private const string TampDotNetHeader = "using Tamp;\nusing Tamp.NetCli.V10;\n\nclass Build : TampBuild\n{\n";

    // ------------------------------------------------------------------ auto rows: input -> exact output

    public static TheoryData<string, string, string> AutoCases => new()
    {
        { "boilerplate.base-class", "", "" },
        {
            "boilerplate.entrypoint-default",
            "    public static int Main() => Execute<Build>(x => x.Compile);\n    Target Compile => _ => _\n        .Executes(() => { });\n",
            "    public static int Main(string[] args) => Execute<Build>(args);\n    Target Compile => _ => _.Default()\n        .Executes(() => { });\n"
        },
        { "injection.parameter", "    [Parameter(\"Configuration\")] readonly string Configuration = \"Debug\";\n", "    [Parameter(\"Configuration\")] readonly string Configuration = \"Debug\";\n" },
        { "injection.solution", "    [Solution] readonly Solution Solution;\n", "    [Solution] readonly Solution Solution;\n" },
        { "injection.path-executable", "    [PathExecutable] readonly Tool Npm;\n    [PathVariable(\"git\")] readonly Tool GitTool;\n", "    [FromPath(\"npm\")] readonly Tool Npm;\n    [FromPath(\"git\")] readonly Tool GitTool;\n" },
        { "injection.is-local-build", "    bool Ci => IsServerBuild && !IsLocalBuild;\n", "    bool Ci => IsServerBuild && !IsLocalBuild;\n" },
        { "paths.root-directory", "    AbsolutePath Src => RootDirectory / \"src\";\n", "    AbsolutePath Src => RootDirectory / \"src\";\n" },
        { "paths.temporary-directory", "    AbsolutePath Tmp => TemporaryDirectory / \"x\";\n", "    AbsolutePath Tmp => TemporaryDirectory / \"x\";\n" },
        { "paths.absolute-path", "    AbsolutePath Out => RootDirectory / \"out\";\n", "    AbsolutePath Out => RootDirectory / \"out\";\n" },
        { "paths.glob", "    void G(AbsolutePath d) { d.GlobFiles(\"*.cs\"); d.GlobDirectories(\"*\"); }\n", "    void G(AbsolutePath d) { d.GlobFiles(\"*.cs\"); d.GlobDirectories(\"*\"); }\n" },
        { "paths.create-directory", "    void C(AbsolutePath d) => d.CreateDirectory();\n", "    void C(AbsolutePath d) => d.CreateDirectory();\n" },
        { "paths.create-or-clean-directory", "    void C(AbsolutePath d) => d.CreateOrCleanDirectory();\n", "    void C(AbsolutePath d) => d.DeleteDirectory().CreateDirectory();\n" },
        { "paths.delete", "    void D(AbsolutePath d) { d.DeleteDirectory(); d.DeleteFile(); }\n", "    void D(AbsolutePath d) { d.DeleteDirectory(); d.DeleteFile(); }\n" },
        { "paths.touch", "    void T(AbsolutePath f) => f.TouchFile();\n", "    void T(AbsolutePath f) => f.Touch();\n" },
        { "paths.read-write", "    void R(AbsolutePath f) => f.WriteAllText(f.ReadAllText());\n", "    void R(AbsolutePath f) => f.WriteAllText(f.ReadAllText());\n" },
        { "target_dsl.target", "    Target A => _ => _;\n", "    Target A => _ => _;\n" },
        { "target_dsl.depends-on", "    Target B => _ => _.DependsOn(A);\n", "    Target B => _ => _.DependsOn(A);\n" },
        { "target_dsl.ordering", "    Target B => _ => _.Before(A).After(C);\n", "    Target B => _ => _.Before(A).After(C);\n" },
        { "target_dsl.triggers", "    Target B => _ => _.Triggers(A).TriggeredBy(C);\n", "    Target B => _ => _.Triggers(A).TriggeredBy(C);\n" },
        { "target_dsl.executes", "    Target B => _ => _.Executes(() => { });\n", "    Target B => _ => _.Executes(() => { });\n" },
        { "target_dsl.description", "    Target B => _ => _.Description(\"b\");\n", "    Target B => _ => _.Description(\"b\");\n" },
        { "target_dsl.produces", "    Target B => _ => _.Produces(RootDirectory / \"*.nupkg\");\n", "    Target B => _ => _.Produces(RootDirectory / \"*.nupkg\");\n" },
        { "target_dsl.assured-after-failure", "    Target B => _ => _.AssuredAfterFailure();\n", "    Target B => _ => _.AssuredAfterFailure();\n" },
    };

    [Theory]
    [MemberData(nameof(AutoCases))]
    public void Auto_Row_Is_Applied_Exactly_With_No_Marker(string ruleId, string input, string expected)
    {
        var (output, report) = Run(NukeHeader + input + "}\n");

        Assert.Equal(TampHeader + expected + "}\n", output);
        Assert.Contains(report.Sites, s => s.RuleId == ruleId && s.Outcome == Outcome.Applied);
        Assert.Empty(report.Markers);
        Assert.DoesNotContain(NukeMigrator.MarkerPrefix, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_Auto_Rule_Has_A_Before_After_Case()
    {
        var covered = AutoCases.Select(c => (string)c[0]).ToHashSet();
        var auto = Mapping.Rules.Where(r => r.Tier == Tier.Auto).Select(r => r.Id);
        Assert.Empty(auto.Except(covered));
    }

    // ------------------------------------------------------------------ the DotNet family (tools.dotnet)

    public static TheoryData<string, string> DotNetCases => new()
    {
        // expression-bodied Executes: the plan is returned (declarative dispatch)
        {
            "    Target Compile => _ => _.Executes(() => DotNetBuild(s => s.SetProjectFile(\"a.sln\").SetConfiguration(\"Release\").EnableNoRestore()));\n",
            "    Target Compile => _ => _.Executes(() => DotNet.Build(s => s.SetProject(\"a.sln\").SetConfiguration(\"Release\").SetNoRestore(true)));\n"
        },
        // statement: NUKE ran the tool on the call; Tamp runs the plan with ProcessRunner.Run
        {
            "    Target Restore => _ => _.Executes(() =>\n    {\n        DotNetRestore(s => s\n            .SetProjectFile(\"a.sln\"));\n    });\n",
            "    Target Restore => _ => _.Executes(() =>\n    {\n        ProcessRunner.Run(DotNet.Restore(s => s\n            .SetProject(\"a.sln\")));\n    });\n"
        },
        // a template setting, a qualified class receiver, and no settings at all
        {
            "    Target Pack => _ => _.Executes(() => DotNetTasks.DotNetPack(s => s.SetVersion(\"1.2.3\").EnableIncludeSymbols()));\n    Target Clean => _ => _.Executes(() => DotNetClean());\n",
            "    Target Pack => _ => _.Executes(() => DotNet.Pack(s => s.SetProperty(\"Version\", \"1.2.3\").SetIncludeSymbols(true)));\n    Target Clean => _ => _.Executes(() => DotNet.Clean());\n"
        },
    };

    [Theory]
    [MemberData(nameof(DotNetCases))]
    public void DotNet_Task_Calls_Become_DotNet_Plans(string input, string expected)
    {
        var (output, report) = Run(NukeDotNetHeader + input + "}\n");

        Assert.Equal(TampDotNetHeader + expected + "}\n", output);
        Assert.Contains(report.Sites, s => s.RuleId == "tool.dotnet" && s.Outcome == Outcome.Applied);
        Assert.Empty(report.Markers);
    }

    [Fact]
    public void Every_Auto_DotNet_Setting_Is_Renamed_Per_The_Mapping()
    {
        // Data-driven from mapping.yaml: a setting row added there is applied with no code change.
        var dotnet = Mapping.Tools.Single(t => t.Key == "dotnet");
        foreach (var (nuke, setting) in dotnet.Settings.Where(s => s.Value.Tier == Tier.Auto))
        {
            var (output, report) = Run(NukeDotNetHeader + $"    Target T => _ => _.Executes(() => DotNetBuild(s => s.{nuke}(x)));\n}}\n");
            var expected = setting.Template is { } t
                ? "s." + t.Replace("{args}", "x", StringComparison.Ordinal)
                : $"s.{setting.To}({setting.Args ?? "x"})";
            Assert.Contains(expected, output, StringComparison.Ordinal);
            Assert.Empty(report.Markers);
        }
    }

    [Fact]
    public void DotNet_Calls_Use_The_Requested_Package_Major()
    {
        var (output, _) = Run(NukeDotNetHeader + "    Target T => _ => _.Executes(() => DotNetBuild());\n}\n", new MigrationOptions(PackageMajor: 8));
        Assert.Contains("using Tamp.NetCli.V8;", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DotNetToolRestore()", "has no mapping for DotNetToolRestore", Outcome.Unmapped)]
    [InlineData("DotNetBuild(s => s.SetProjectFile(\"a\").CombineWith(xs, (s, x) => s))", "CombineWith has no tool.dotnet mapping", Outcome.Unmapped)]
    [InlineData("DotNetBuild(settings)", "isn't a `s => s.Set..()` lambda", Outcome.Unmapped)]
    public void An_Unmapped_DotNet_Call_Is_Left_Alone_With_A_Marker(string call, string why, Outcome outcome)
    {
        var input = NukeDotNetHeader + $"    Target T => _ => _.Executes(() =>\n    {{\n        {call};\n    }});\n}}\n";
        var (output, report) = Run(input);

        Assert.Contains($"        {call};\n", output, StringComparison.Ordinal);
        Assert.Contains("// TAMP-MIGRATE(tool.dotnet): ", output, StringComparison.Ordinal);
        Assert.Contains(why, output, StringComparison.Ordinal);
        Assert.Contains(report.Sites, s => s.RuleId == "tool.dotnet" && s.Outcome == outcome);
        // The NUKE using stays while a call still needs it.
        Assert.Contains("using static Nuke.Common.Tools.DotNet.DotNetTasks;", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_DotNet_Call_Whose_Result_Is_Used_Is_Flagged_Not_Guessed()
    {
        var (output, report) = Run(NukeDotNetHeader + "    void T() { var outputs = DotNetBuild(s => s.SetProjectFile(\"a\")); }\n}\n");
        Assert.Contains("var outputs = DotNetBuild(s => s.SetProjectFile(\"a\"));", output, StringComparison.Ordinal);
        Assert.Contains(report.Sites, s => s.RuleId == "tool.dotnet" && s.Outcome == Outcome.Manual);
    }

    [Fact]
    public void An_Assisted_DotNet_Setting_Is_Converted_And_Marked()
    {
        var (output, report) = Run(NukeDotNetHeader + "    Target T => _ => _.Executes(() => DotNetNuGetPush(s => s.SetSource(Feed).SetApiKey(Key)));\n}\n");
        Assert.Contains("DotNet.NuGetPush(s => s.SetSource(Feed).SetApiKey(Key))", output, StringComparison.Ordinal);
        var site = Assert.Single(report.Sites, s => s.RuleId == "tool.dotnet");
        Assert.Equal(Outcome.Assisted, site.Outcome);
        Assert.Contains("confirm SetSource, SetApiKey", site.Marker, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Family_That_Is_Not_Auto_Is_Marked_And_Left_Alone()
    {
        var input = "using Nuke.Common;\nusing static Nuke.Common.Tools.Docker.DockerTasks;\n\nclass Build : NukeBuild\n{\n    void T() { DockerBuild(s => s.SetPath(\".\")); }\n}\n";
        var (output, report) = Run(input);
        Assert.Contains("DockerBuild(s => s.SetPath(\".\"));", output, StringComparison.Ordinal);
        Assert.Contains("using static Nuke.Common.Tools.Docker.DockerTasks;", output, StringComparison.Ordinal);
        Assert.Contains(report.Sites, s => s.RuleId == "tool.docker" && s.Outcome == Outcome.Manual && s.Marker!.Contains("Docker.Build (Tamp.Docker.V27)", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ assisted / manual rows: marker, never a silent guess

    public static TheoryData<string, string> MarkedCases => new()
    {
        { "boilerplate.ci-generation", "    [GitHubActions(\"ci\")] class Nested { }\n" },
        { "boilerplate.build-attributes", "    [ShutdownDotNetAfterServerBuild] class Nested { }\n" },
        { "injection.secret", "    [Secret] [Parameter] readonly string Token;\n" },
        { "injection.tool-invocation", "    [PathExecutable] readonly Tool Git;\n    void T() { Git(\"status --short\"); }\n" },
        { "injection.gitversion", "    [GitVersion] readonly GitVersion GitVersion;\n" },
        { "injection.git-repository", "    [GitRepository] readonly GitRepository Repository;\n" },
        { "injection.host", "    bool Gha => Host is GitHubActions;\n" },
        { "paths.copy", "    void C(AbsolutePath a, AbsolutePath b) => a.Copy(b);\n" },
        { "paths.move", "    void M(AbsolutePath a, AbsolutePath b) => a.Move(b);\n" },
        { "paths.copy-to-directory", "    void C(AbsolutePath a, AbsolutePath b) => a.CopyToDirectory(b);\n" },
        { "target_dsl.only-when", "    Target B => _ => _.OnlyWhenStatic(() => IsLocalBuild);\n" },
        { "target_dsl.requires", "    Target B => _ => _.Requires(() => Token);\n" },
        { "target_dsl.consumes", "    Target B => _ => _.Consumes(A);\n" },
        { "target_dsl.proceed-after-failure", "    Target B => _ => _.ProceedAfterFailure();\n" },
        { "target_dsl.unlisted", "    Target B => _ => _.Unlisted();\n" },
        { "target_dsl.partition", "    [Partition(2)] readonly Partition Partition;\n" },
        { "target_dsl.partition-call", "    Target B => _ => _.Partition(2);\n" },
        { "target_dsl.unmapped", "    Target B => _ => _.TryDependsOn(\"A\");\n" },
        { "logging.serilog", "    void L() { Log.Information(\"x {Y}\", 1); }\n" },
        { "components.unshipped", "    interface IX : IPublish { }\n" },
    };

    [Theory]
    [MemberData(nameof(MarkedCases))]
    public void Non_Auto_Row_Leaves_A_Marker(string ruleId, string input)
    {
        var rule = Mapping.Rule(ruleId);
        var (output, report) = Run(NukeHeader + input + "}\n");

        Assert.Contains($"// {NukeMigrator.MarkerPrefix}({ruleId}): {rule.Marker}", output, StringComparison.Ordinal);
        var site = Assert.Single(report.Sites, s => s.RuleId == ruleId);
        Assert.NotEqual(Outcome.Applied, site.Outcome);
        Assert.Equal(rule.Marker, site.Marker);

        if (rule.Tier == Tier.Manual)
        {
            // A manual row only adds its marker: drop the marker lines and the member is as it was.
            var withoutMarkers = string.Join("\n", output.Split('\n').Where(l => !l.Contains(NukeMigrator.MarkerPrefix, StringComparison.Ordinal)));
            Assert.Equal(TampHeader + input + "}\n", withoutMarkers);
            Assert.Equal(Outcome.Manual, site.Outcome);
        }
    }

    [Fact]
    public void Every_Non_Auto_Code_Rule_Has_A_Marker_Case()
    {
        var covered = MarkedCases.Select(c => (string)c[0]).ToHashSet();
        // using / file rules are covered by their own tests below.
        var marked = Mapping.Rules.Where(r => r.Tier != Tier.Auto && r.MatchKind is not ("using" or "file")).Select(r => r.Id);
        Assert.Empty(marked.Except(covered));
    }

    [Fact]
    public void Assisted_Rewrites_Are_Applied_Under_Their_Marker()
    {
        var (output, _) = Run(NukeHeader + "    Target B => _ => _\n        .OnlyWhenStatic(() => IsLocalBuild)\n        .Unlisted()\n        .ProceedAfterFailure();\n    [Secret] [Parameter] readonly string Token;\n}\n");
        Assert.Contains("        .OnlyWhen(() => IsLocalBuild)\n        .Internal()\n        .FailureMode(FailureMode.Continue);", output, StringComparison.Ordinal);
        Assert.Contains("    [Secret] readonly Secret Token;", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Path_Rules_Never_Fire_On_The_Bcl()
    {
        var (output, report) = Run(NukeHeader + "    void C() { File.Copy(\"a\", \"b\"); File.Move(\"a\", \"b\"); }\n}\n");
        Assert.Contains("File.Copy(\"a\", \"b\"); File.Move(\"a\", \"b\");", output, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Sites, s => s.RuleId is "paths.copy" or "paths.move");
    }

    // ------------------------------------------------------------------ usings, components, gaps, files

    [Fact]
    public void Usings_Map_Per_The_Namespaces_Table_And_Unmapped_Ones_Are_Kept_With_A_Marker()
    {
        var input = "// header\nusing System;\nusing Nuke.Common;\nusing Nuke.Common.IO;\nusing Nuke.Common.Utilities;\nusing Nuke.Common.Tools.DotNet;\n\nclass Build : NukeBuild\n{\n}\n";
        var (output, report) = Run(input);

        Assert.Equal(
            "// header\nusing System;\nusing Tamp;\nusing Tamp.NetCli.V10;\n"
            + "// TAMP-MIGRATE(boilerplate.usings): `Nuke.Common.Utilities` has no Tamp mapping. Replace what this file uses from it, then remove the using.\n"
            + "using Nuke.Common.Utilities;\n\nclass Build : TampBuild\n{\n}\n",
            output);
        Assert.Equal(3, report.Sites.Count(s => s.RuleId == "boilerplate.usings" && s.Outcome == Outcome.Applied));
        Assert.Single(report.Sites, s => s.RuleId == "boilerplate.usings" && s.Outcome == Outcome.Unmapped);
    }

    [Fact]
    public void A_File_Header_On_A_Dropped_Using_Is_Kept()
    {
        var (output, _) = Run("// Copyright (c) Example\nusing Nuke.Common;\n\nclass Build : NukeBuild\n{\n}\n");
        Assert.StartsWith("// Copyright (c) Example\nusing Tamp;\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Nuke_Components_Becomes_Tamp_Components_With_A_Marker()
    {
        var (output, report) = Run("using Nuke.Common;\nusing Nuke.Components;\n\nclass Build : NukeBuild, ICompile, ITest\n{\n}\n");
        Assert.Contains("using Tamp.Components;", output, StringComparison.Ordinal);
        Assert.Contains("class Build : TampBuild, ICompile, ITest", output, StringComparison.Ordinal);
        Assert.Contains(report.Sites, s => s.RuleId == "components.interfaces" && s.Outcome == Outcome.Assisted);
    }

    [Theory]
    [InlineData("Nuke.Common.Tools.Git.GitTasks", "gap.git")]
    [InlineData("Nuke.Common.Tools.PowerShell.PowerShellTasks", "gap.powershell")]
    public void A_Known_Gap_Is_Reported_As_Unmapped(string usingStatic, string gapId)
    {
        var (output, report) = Run($"using Nuke.Common;\nusing static {usingStatic};\n\nclass Build : NukeBuild\n{{\n}}\n");
        Assert.Contains($"// TAMP-MIGRATE({gapId}): ", output, StringComparison.Ordinal);
        Assert.Contains($"using static {usingStatic};", output, StringComparison.Ordinal);
        Assert.Contains(report.Sites, s => s.RuleId == gapId && s.Outcome == Outcome.Unmapped);
    }

    [Fact]
    public void NUKE_Bootstrap_Files_Are_Reported()
    {
        var result = new NukeMigrator(Mapping).Migrate([new SourceFile("build/Build.cs", NukeHeader + "}\n")],
            ["build.sh", "build.cmd", "README.md", ".nuke/parameters.json", ".nuke/temp/x"]);
        var sites = result.Report.Sites.Where(s => s.RuleId == "boilerplate.bootstrap-scripts").ToList();
        Assert.Equal([".nuke/parameters.json", "build.cmd", "build.sh"], sites.Select(s => s.File)); // one per glob, by file
        Assert.All(sites, s => Assert.Equal(Outcome.Assisted, s.Outcome)); // the tamp init bootstrap was generated
        Assert.Equal(["tamp.sh", "tamp.cmd", ".config/dotnet-tools.json"], result.Generated.Select(g => g.Path));
    }

    [Fact]
    public void A_Default_Target_In_Another_Partial_File_Gets_Default()
    {
        var result = new NukeMigrator(Mapping).Migrate([
            new SourceFile("build/Build.cs", "using Nuke.Common;\n\npartial class Build : NukeBuild\n{\n    public static int Main() => Execute<Build>(x => x.Pack);\n}\n"),
            new SourceFile("build/Build.Pack.cs", "using Nuke.Common;\n\npartial class Build\n{\n    Target Pack => _ => _\n        .Executes(() => { });\n}\n"),
        ]);
        Assert.Contains("Target Pack => _ => _.Default()\n", result.Files[1].Text, StringComparison.Ordinal);
        Assert.Equal(["build/Build.cs", "build/Build.Pack.cs"], result.Report.ChangedFiles);
    }

    [Fact]
    public void A_Missing_Default_Target_Is_Flagged()
    {
        var (output, report) = Run(NukeHeader + "    public static int Main() => Execute<Build>(x => x.Nope);\n}\n");
        Assert.Contains("TAMP-MIGRATE(boilerplate.entrypoint-default): Default target 'Nope' wasn't found", output, StringComparison.Ordinal);
        Assert.Contains(report.Sites, s => s.RuleId == "boilerplate.entrypoint-default" && s.Outcome == Outcome.Manual);
    }

    // ------------------------------------------------------------------ a whole build: report, idempotence, compiles

    private const string Corpus = """
        using System;
        using System.IO;
        using Nuke.Common;
        using Nuke.Common.IO;
        using Nuke.Common.ProjectModel;
        using Nuke.Common.Tooling;
        using Serilog;
        using static Nuke.Common.Tools.DotNet.DotNetTasks;

        class Build : NukeBuild
        {
            public static int Main() => Execute<Build>(x => x.Compile);

            [Parameter("Configuration")] readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
            [Secret] [Parameter] readonly string NuGetApiKey;
            [PathExecutable] readonly Tool Git;

            AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

            Target Clean => _ => _
                .Before(Restore)
                .Executes(() =>
                {
                    ArtifactsDirectory.CreateOrCleanDirectory();
                });

            Target Restore => _ => _
                .Executes(() =>
                {
                    DotNetRestore(s => s
                        .SetProjectFile("Example.sln"));
                });

            Target Compile => _ => _
                .DependsOn(Restore)
                .Executes(() => DotNetBuild(s => s
                    .SetProjectFile("Example.sln")
                    .SetConfiguration(Configuration)
                    .EnableNoRestore()));

            Target Pack => _ => _
                .DependsOn(Compile)
                .Produces(ArtifactsDirectory / "*.nupkg")
                .Executes(() =>
                {
                    Log.Information("Packing into {Dir}", ArtifactsDirectory);
                    DotNetPack(s => s
                        .SetProject("Example.sln")
                        .SetConfiguration(Configuration)
                        .EnableNoBuild()
                        .SetOutputDirectory(ArtifactsDirectory)
                        .SetVersion("1.0.0"));
                });
        }

        """;

    [Fact]
    public void The_Coverage_Report_Counts_Every_Site()
    {
        var (_, report) = Run(Corpus);

        // auto: 5 usings (4 + the DotNetTasks using static), base class, entrypoint, [Parameter], IsLocalBuild,
        //       [PathExecutable], RootDirectory, AbsolutePath, CreateOrCleanDirectory, Before, DependsOn x2,
        //       Executes x4, Produces, Target x4, and the 3 DotNet calls = 28.
        Assert.Equal(new CoverageSummary(Applied: 28, Assisted: 1, Manual: 1, Unmapped: 0), report.Summary);
        Assert.Collection(report.Markers,
            m => Assert.Equal(("injection.secret", Outcome.Assisted, 15), (m.RuleId, m.Outcome, m.Line)),
            m => Assert.Equal(("logging.serilog", Outcome.Manual, 46), (m.RuleId, m.Outcome, m.Line)));
        Assert.Equal(3, report.Sites.Count(s => s.RuleId == "tool.dotnet" && s.Outcome == Outcome.Applied));
        Assert.Equal(["build/Build.cs"], report.ChangedFiles);

        var json = report.ToJson();
        Assert.Contains("\"applied\": 28", json, StringComparison.Ordinal);
        Assert.Contains("\"ruleId\": \"logging.serilog\"", json, StringComparison.Ordinal);
        Assert.Contains("\"outcome\": \"Manual\"", json, StringComparison.Ordinal);
        Assert.Contains("| `build/Build.cs:46` | `logging.serilog` | manual |", report.ToMarkdown(), StringComparison.Ordinal);
    }

    [Fact]
    public void Running_Twice_Changes_Nothing_The_Second_Time()
    {
        var (once, _) = Run(Corpus);
        var (twice, report) = Run(once);

        Assert.Equal(once, twice);
        Assert.Empty(report.ChangedFiles);
    }

    [Fact]
    public void A_Crlf_Build_Stays_Crlf_And_Is_Still_Idempotent()
    {
        // Normalise first: this test file is itself CRLF on a Windows checkout.
        var crlf = Corpus.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)
            + "class Extra { void T() { Log.Warning(\"w\"); } }\r\n";
        var (once, _) = Run(crlf);
        var (twice, _) = Run(once);

        Assert.DoesNotContain("\n", once.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal); // no bare LF
        Assert.Contains("// TAMP-MIGRATE(logging.serilog): ", once, StringComparison.Ordinal);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Running_Twice_On_A_Marked_Build_Adds_No_Second_Marker()
    {
        var input = NukeDotNetHeader + "    void T() { DotNetToolRestore(); Log.Warning(\"w\"); }\n    [GitRepository] readonly GitRepository Repo;\n}\n";
        var (once, _) = Run(input);
        var (twice, report) = Run(once);

        Assert.Equal(once, twice);
        Assert.Empty(report.ChangedFiles);
        Assert.NotEmpty(report.Markers); // still reported: they're unresolved
    }

    [Fact]
    public void The_Converted_Corpus_Compiles_Against_Tamp()
    {
        var (output, _) = Run(Corpus);
        // The one Serilog call is a marker for a person; take it out so the rest must compile as written.
        // Line by line, not by "...\n": on a Windows checkout this file (and so Corpus) is CRLF.
        var tamp = string.Join("\n", output.Split('\n')
            .Where(l => l.TrimEnd('\r') != "using Serilog;" && !l.Contains("Log.Information(", StringComparison.Ordinal)));

        var errors = Compile(tamp);
        Assert.True(errors.Count == 0, string.Join("\n", errors) + "\n---\n" + tamp);
    }

    // ------------------------------------------------------------------ helpers

    private static (string Output, MigrationReport Report) Run(string source, MigrationOptions? options = null)
    {
        var result = new NukeMigrator(Mapping, options).Migrate([new SourceFile("build/Build.cs", source)]);
        return (result.Files[0].Text, result.Report);
    }

    private static List<string> Compile(string source)
    {
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = platform
            .Concat([typeof(TampBuild).Assembly.Location, typeof(DotNet).Assembly.Location])
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Converted",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Disable));
        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
    }
}
