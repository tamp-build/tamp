using Xunit;

namespace Tamp.Cli.Tests;

/// <summary><c>tamp migrate</c> forwards to Tamp.Migrate at the CLI's own version (ADR 0021).</summary>
public sealed class MigrateForwarderTests
{
    [Fact]
    public void Forwards_Everything_After_Migrate_To_The_Same_Version_Of_Tamp_Migrate()
    {
        var args = MigrateForwarder.Arguments("1.19.0", ["nuke", "--dry-run", "--root", "../repo"]);

        // SDK options before the package id: anything after it reaches the tool, not `dotnet tool execute`.
        Assert.Equal(["tool", "execute", "--allow-roll-forward", "Tamp.Migrate@1.19.0", "nuke", "--dry-run", "--root", "../repo"], args);
    }

    [Theory]
    // The tool ran and reported its own problem: 0 success, 2 usage, 3 a dirty working tree.
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    // `dotnet tool execute` itself failed (an SDK older than 10 doesn't have it), so the hint is the answer.
    [InlineData(1, true)]
    [InlineData(127, true)]
    public void The_Sdk_Hint_Is_Only_Printed_When_The_Tool_Never_Ran(int exitCode, bool expected)
        => Assert.Equal(expected, MigrateForwarder.DispatchFailed(exitCode));

    [Fact]
    public void The_Version_Is_A_Package_Version_Without_Build_Metadata()
    {
        var v = MigrateForwarder.PackageVersion;
        Assert.DoesNotContain('+', v);
        Assert.Matches(@"^\d+\.\d+\.\d+", v);
    }
}
