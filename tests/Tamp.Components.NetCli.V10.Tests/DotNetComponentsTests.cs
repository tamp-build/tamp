using System;
using System.IO;
using System.Linq;
using Tamp;
using Tamp.Components;
using Xunit;

namespace Tamp.Components.NetCli.V10.Tests;

/// <summary>
/// ADR 0020 Phase 3: the concrete dotnet bodies (IDotNetRestore/Compile/Test/Pack) produce the right
/// `dotnet` CommandPlans and thread the build's injected IHaz* values (Solution / Configuration /
/// ArtifactsDirectory) into them. Inspects each plan directly (no process spawned).
/// </summary>
public sealed class DotNetComponentsTests
{
    private static readonly Solution Sln = LoadTempSolution();

    private static Solution LoadTempSolution()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tamp-components-{Guid.NewGuid():N}.slnx");
        File.WriteAllText(path, "<Solution></Solution>");
        return Solution.Load(AbsolutePath.Create(path));
    }

    // A standard .NET build composed entirely from the concrete component set.
    private sealed class NetBuild : TampBuild, IDotNetTest, IDotNetPack
    {
        public Solution Solution => Sln;
        public Configuration Configuration => Configuration.Release;
        public AbsolutePath ArtifactsDirectory => AbsolutePath.Create(Path.Combine(Path.GetTempPath(), "tamp-artifacts"));
    }

    private static readonly NetBuild Build = new();

    [Fact]
    public void RestorePlan_Is_Dotnet_Restore_On_The_Solution()
    {
        var plan = ((IRestore)Build).RestorePlan();
        Assert.Equal("dotnet", plan.Executable);
        Assert.Contains("restore", plan.Arguments);
        Assert.Contains(Sln.Path.Value, plan.Arguments);
    }

    [Fact]
    public void CompilePlan_Is_Dotnet_Build_Release_NoRestore()
    {
        var args = ((ICompile)Build).CompilePlan().Arguments;
        Assert.Contains("build", args);
        Assert.Contains("Release", args);          // injected IHazConfiguration
        Assert.Contains("--no-restore", args);     // Restore ran as a dependency
    }

    [Fact]
    public void TestPlan_Is_Dotnet_Test_NoBuild_With_BlameCrash_And_Coverage()
    {
        var args = ((ITest)Build).TestPlan().Arguments;
        Assert.Contains("test", args);
        Assert.Contains("Release", args);
        Assert.Contains("--no-build", args);                          // Compile ran as a dependency
        Assert.Contains(args, a => a.Contains("blame"));              // --blame-crash best practice
        Assert.Contains(args, a => a.Contains("trx"));                // trx logger (fleet convention)
        Assert.Contains(args, a => a.Contains("XPlat Code Coverage"));// coverage collector
        Assert.Contains(args, a => a.Contains("test-results"));       // results directory under artifacts
    }

    [Fact]
    public void PackPlan_Is_Dotnet_Pack_NoBuild_Into_ArtifactsDirectory()
    {
        var args = ((IPack)Build).PackPlan().Arguments;
        Assert.Contains("pack", args);
        Assert.Contains("--no-build", args);
        Assert.Contains(Build.ArtifactsDirectory.Value, args);   // injected IHazArtifacts
    }

    [Fact]
    public void PackPlan_Honors_PACKAGE_VERSION_When_Set()
    {
        var prev = Environment.GetEnvironmentVariable("PACKAGE_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("PACKAGE_VERSION", "9.9.9-rc.1");
            var args = ((IPack)Build).PackPlan().Arguments;
            Assert.Contains(args, a => a.Contains("9.9.9-rc.1"));   // tag-driven version threaded as -p:Version=
        }
        finally { Environment.SetEnvironmentVariable("PACKAGE_VERSION", prev); }
    }

    [Fact]
    public void PackPlan_Omits_Version_When_PACKAGE_VERSION_Unset()
    {
        var prev = Environment.GetEnvironmentVariable("PACKAGE_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("PACKAGE_VERSION", null);
            var args = ((IPack)Build).PackPlan().Arguments;
            Assert.DoesNotContain(args, a => a.Contains("Version="));   // no override → static csproj version
        }
        finally { Environment.SetEnvironmentVariable("PACKAGE_VERSION", prev); }
    }
}
