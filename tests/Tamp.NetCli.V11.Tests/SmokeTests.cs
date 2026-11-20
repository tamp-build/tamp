using Xunit;

namespace Tamp.NetCli.V11.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void Assembly_Loads()
    {
        var assembly = typeof(Tamp.NetCli.V11.DotNet).Assembly;
        Assert.Equal("Tamp.NetCli.V11", assembly.GetName().Name);
    }
}
