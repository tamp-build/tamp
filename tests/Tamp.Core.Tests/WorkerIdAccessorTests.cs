using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// #77: a consumer build can read the resolved worker id from target code (public
/// <see cref="TampBuild.WorkerId"/> + <see cref="TampBuild.WorkerActor"/>) instead of re-deriving
/// the precedence and risking drift from core.
/// </summary>
public sealed class WorkerIdAccessorTests
{
    private sealed class B : TampBuild
    {
        public Target Noop => _ => _.Executes(() => { });
    }

    [Fact]
    public void WorkerId_Is_Public_And_Matches_The_Resolver()
    {
        var build = new B();
        Assert.False(string.IsNullOrWhiteSpace(build.WorkerId));
        Assert.Equal(WorkerIdResolver.ResolveDefault(), build.WorkerId);   // same value the event stream stamps
    }

    [Theory]
    [InlineData("agent:pool/3", "pool/3", "agent")]
    [InlineData("human:scott@example.com", "scott@example.com", "human")]
    [InlineData("bot:ci-runner-7", "ci-runner-7", "bot")]
    [InlineData("scott", "scott", "human")]              // bare value ⇒ kind human
    [InlineData("", "", "human")]
    public void SplitWorkerId_Splits_On_First_Colon_With_Human_Default(string workerId, string id, string kind)
    {
        var (gotId, gotKind) = TampBuild.SplitWorkerId(workerId);
        Assert.Equal(id, gotId);
        Assert.Equal(kind, gotKind);
    }

    [Fact]
    public void WorkerActor_Splits_The_Resolved_Id()
    {
        var build = new B();
        var (id, kind) = build.WorkerActor;
        var (expId, expKind) = TampBuild.SplitWorkerId(build.WorkerId);
        Assert.Equal(expId, id);
        Assert.Equal(expKind, kind);
        Assert.Contains(kind, new[] { "human", "agent", "bot" });   // real resolution yields a known-ish kind
    }
}
