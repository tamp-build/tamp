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

    [Fact]
    public void The_Version_Is_A_Package_Version_Without_Build_Metadata()
    {
        var v = MigrateForwarder.PackageVersion;
        Assert.DoesNotContain('+', v);
        Assert.Matches(@"^\d+\.\d+\.\d+", v);
    }
}
