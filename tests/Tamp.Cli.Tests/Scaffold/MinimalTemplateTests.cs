using Tamp.Cli.Scaffold.Templates;
using Tamp.Scaffold;
using Xunit;

namespace Tamp.Cli.Tests.Scaffold;

/// <summary>
/// Snapshot tests for the v0.1.0 minimal template. The expected strings below
/// pin the API surface adopters see — when the template evolves, these
/// snapshots update explicitly.
/// </summary>
public sealed class MinimalTemplateTests
{
    private static ScaffoldContext FakeContext(AbsolutePath? repoRoot = null) => new()
    {
        RepoRoot = repoRoot ?? AbsolutePath.Create("/tmp/x"),
        TampCoreVersion = "1.4.0",
    };

    [Fact]
    public void Renders_Five_Files_In_Expected_Layout()
    {
        var t = new MinimalTemplate();
        var specs = t.Render(FakeContext()).ToList();

        Assert.Equal(5, specs.Count);

        Assert.EndsWith($"build{System.IO.Path.DirectorySeparatorChar}Build.cs", specs[0].Path.Value);
        Assert.Equal(WriteMode.Create, specs[0].Mode);
        Assert.False(specs[0].Executable);

        Assert.EndsWith($"build{System.IO.Path.DirectorySeparatorChar}Build.csproj", specs[1].Path.Value);
        Assert.Equal(WriteMode.Create, specs[1].Mode);

        Assert.EndsWith($".config{System.IO.Path.DirectorySeparatorChar}dotnet-tools.json", specs[2].Path.Value);
        Assert.Equal(WriteMode.SkipIfExists, specs[2].Mode);

        Assert.EndsWith("tamp.sh", specs[3].Path.Value);
        Assert.Equal(WriteMode.SkipIfExists, specs[3].Mode);
        Assert.True(specs[3].Executable, "tamp.sh must emit with the executable bit on POSIX");

        Assert.EndsWith("tamp.cmd", specs[4].Path.Value);
        Assert.Equal(WriteMode.SkipIfExists, specs[4].Mode);
        Assert.False(specs[4].Executable);
    }

    [Fact]
    public void Tamp_Sh_Shim_Forwards_To_Dotnet_Tamp_And_Restores_Tool_Manifest()
    {
        var rendered = MinimalTemplate.RenderTampSh();
        Assert.StartsWith("#!/usr/bin/env bash", rendered);
        Assert.Contains("dotnet tool restore", rendered);
        Assert.Contains("exec dotnet tamp", rendered);
        Assert.Contains("set -euo pipefail", rendered);                  // safety
    }

    [Fact]
    public void Tamp_Cmd_Shim_Forwards_To_Dotnet_Tamp_And_Restores_Tool_Manifest()
    {
        var rendered = MinimalTemplate.RenderTampCmd();
        Assert.StartsWith("@echo off", rendered);
        Assert.Contains("dotnet tool restore", rendered);
        Assert.Contains("dotnet tamp %*", rendered);
        Assert.Contains("setlocal", rendered);
    }

    // tamp#120: cmd.exe misparses LF-only batch files (worst under OEM code
    // pages like 437 with multi-byte characters present), running a fragment
    // of a later line ("'m' is not recognized"). The shim must be CRLF and
    // pure ASCII no matter which OS / checkout the CLI was compiled on.
    [Fact]
    public void Tamp_Cmd_Shim_Uses_Crlf_Line_Endings_Only()
    {
        var rendered = MinimalTemplate.RenderTampCmd();
        Assert.Contains("\r\n", rendered);
        for (var i = 0; i < rendered.Length; i++)
        {
            if (rendered[i] == '\n')
                Assert.True(i > 0 && rendered[i - 1] == '\r', $"bare LF at index {i}");
            if (rendered[i] == '\r')
                Assert.True(i + 1 < rendered.Length && rendered[i + 1] == '\n', $"bare CR at index {i}");
        }
        Assert.EndsWith("\r\n", rendered);
    }

    [Fact]
    public void Tamp_Cmd_Shim_Is_Pure_Ascii()
    {
        var rendered = MinimalTemplate.RenderTampCmd();
        for (var i = 0; i < rendered.Length; i++)
            Assert.True(rendered[i] < 0x80, $"non-ASCII U+{(int)rendered[i]:X4} at index {i}");
    }

    [Fact]
    public void Tamp_Cmd_Shim_Joins_Onto_Dp0_Without_Trim()
    {
        // %~dp0 always ends in '\', so paths join directly onto it.
        var rendered = MinimalTemplate.RenderTampCmd();
        Assert.Contains("set \"SCRIPT_DIR=%~dp0\"", rendered);
        Assert.Contains("%SCRIPT_DIR%.config\\dotnet-tools.json", rendered);
        Assert.DoesNotContain("%SCRIPT_DIR:~", rendered);
        Assert.DoesNotContain("%SCRIPT_DIR%\\", rendered);
    }

    [Fact]
    public void Tamp_Sh_Shim_Uses_Lf_Line_Endings_Only()
    {
        // Mirror of the .cmd guard: a CRLF tamp.sh breaks bash ("$'\r': command not found").
        Assert.DoesNotContain("\r", MinimalTemplate.RenderTampSh());
    }

    [Fact]
    public void Shims_Land_On_Disk_With_Their_Line_Endings_Through_The_Runner()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tamp-shim-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var specs = new MinimalTemplate().Render(FakeContext(AbsolutePath.Create(root)))
                .Where(s => s.Path.Value.EndsWith("tamp.cmd") || s.Path.Value.EndsWith("tamp.sh"))
                .ToList();
            new Tamp.Cli.Scaffold.ScaffoldRunner().Run(specs);

            var cmd = System.IO.File.ReadAllBytes(System.IO.Path.Combine(root, "tamp.cmd"));
            var sh = System.IO.File.ReadAllBytes(System.IO.Path.Combine(root, "tamp.sh"));

            Assert.All(cmd, b => Assert.True(b < 0x80, $"non-ASCII byte 0x{b:X2} in tamp.cmd"));
            for (var i = 0; i < cmd.Length; i++)
                if (cmd[i] == (byte)'\n') Assert.True(i > 0 && cmd[i - 1] == (byte)'\r', $"bare LF in tamp.cmd at byte {i}");
            Assert.DoesNotContain((byte)'\r', sh);
            Assert.Contains((byte)'\n', sh);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Build_Cs_Snapshot_Pins_The_1_3_Surface()
    {
        // The minimum surface this template depends on: CleanArtifacts(), .Default(),
        // .Internal(), and the params Target[] DependsOn shape. Changing the output
        // is intentional — re-pin this snapshot when it happens.
        var rendered = MinimalTemplate.RenderBuildCs(FakeContext());

        Assert.Contains("class Build : TampBuild", rendered);
        Assert.Contains("[Solution] readonly Solution Solution = null!;", rendered);
        Assert.Contains("Target Clean => _ => _.Executes(() => CleanArtifacts());", rendered);
        Assert.Contains(".Internal()", rendered);
        Assert.Contains(".Default()", rendered);
        Assert.Contains(".DependsOn(Restore)", rendered);
        Assert.Contains(".DependsOn(Compile)", rendered);
        Assert.DoesNotContain(".TopLevel()", rendered);          // obsolete 1.1.0+
        Assert.DoesNotContain("nameof(", rendered);              // killed by CallerArgumentExpression
        Assert.DoesNotContain("GlobDirectories", rendered);      // replaced by CleanArtifacts()
    }

    [Fact]
    public void Build_Csproj_Pins_Both_Tamp_Core_And_Net_Cli_To_Context_Version()
    {
        var rendered = MinimalTemplate.RenderBuildCsproj(FakeContext());

        Assert.Contains("<PackageReference Include=\"Tamp.Core\" Version=\"1.4.0\" />", rendered);
        Assert.Contains("<PackageReference Include=\"Tamp.NetCli.V10\" Version=\"1.4.0\" />", rendered);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", rendered);
        Assert.Contains("<OutputType>Exe</OutputType>", rendered);
    }

    [Fact]
    public void Tools_Json_Registers_Dotnet_Tamp_At_Context_Version()
    {
        var rendered = MinimalTemplate.RenderToolsJson(FakeContext());

        Assert.Contains("\"version\": 1", rendered);
        Assert.Contains("\"isRoot\": true", rendered);
        Assert.Contains("\"dotnet-tamp\"", rendered);
        Assert.Contains("\"version\": \"1.4.0\"", rendered);
        Assert.Contains("\"commands\": [\"dotnet-tamp\"]", rendered);
    }

    [Fact]
    public void Minimum_Tamp_Core_Version_Is_1_3_0()
    {
        // The template uses Default()/Internal()/CleanArtifacts()/params-Target[]-DependsOn,
        // all of which land in 1.3.0 or earlier. Drift protection requires this metadata.
        Assert.Equal("1.3.0", new MinimalTemplate().MinimumTampCoreVersion);
    }

    [Fact]
    public void Name_And_Description_Are_Stable()
    {
        var t = new MinimalTemplate();
        Assert.Equal("minimal", t.Name);
        Assert.False(string.IsNullOrWhiteSpace(t.Description));
    }
}
