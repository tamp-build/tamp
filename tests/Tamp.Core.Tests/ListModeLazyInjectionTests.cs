using System.IO;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests for HoldFast friction #20 — <c>tamp --list</c> should not require
/// every [FromPath]/[ValueInjection] tool to be on PATH. Target
/// introspection runs without invoking the targets, so tool-resolution
/// failures during binding are tolerated when the invocation is list-only.
/// </summary>
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class ListModeLazyInjectionTests
{
    /// <summary>
    /// Always-throwing ValueInjection attribute. Simulates [FromPath("not-a-real-tool")]
    /// failing because the tool isn't on PATH — without coupling the test to FromPath's
    /// real resolution logic.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class AlwaysFailsInjectionAttribute : ValueInjectionAttribute
    {
        public override object? GetValue(System.Reflection.MemberInfo member, System.Type targetType)
            => throw new InvalidOperationException("simulated tool-resolution failure");
    }

    private sealed class TamperedBuild : TampBuild
    {
        [AlwaysFailsInjection] public object? FailingValue { get; set; }
        public Target NoOp => _ => _.Description("nothing to do");
    }

    private static string CaptureStdout(System.Action action)
    {
        var sw = new StringWriter();
        var prev = Console.Out;
        Console.SetOut(sw);
        try { action(); }
        finally { Console.SetOut(prev); }
        return sw.ToString();
    }

    [Fact]
    public void IsListOnlyInvocation_Detects_List_Flag()
    {
        Assert.True(TampBuild.IsListOnlyInvocation(new[] { "--list" }));
        Assert.True(TampBuild.IsListOnlyInvocation(new[] { "--list", "--all" }));
        Assert.True(TampBuild.IsListOnlyInvocation(new[] { "Compile", "--list" }));
    }

    [Fact]
    public void IsListOnlyInvocation_Detects_ListTree_Flag()
    {
        Assert.True(TampBuild.IsListOnlyInvocation(new[] { "--list-tree" }));
    }

    [Theory]
    [InlineData("--list=json")]
    [InlineData("--list-tree=json")]
    public void IsListOnlyInvocation_Detects_Inline_Equals_Form(string flag)
    {
        // Adopters who write --list=<something> — still list mode.
        Assert.True(TampBuild.IsListOnlyInvocation(new[] { flag }));
    }

    [Fact]
    public void IsListOnlyInvocation_Returns_False_For_Normal_Invocation()
    {
        Assert.False(TampBuild.IsListOnlyInvocation(new[] { "Compile" }));
        Assert.False(TampBuild.IsListOnlyInvocation(new[] { "Build", "--verbose" }));
        Assert.False(TampBuild.IsListOnlyInvocation(System.Array.Empty<string>()));
    }

    [Fact]
    public void Tamp_List_Succeeds_When_FromPath_Resolution_Would_Fail()
    {
        // Critical contract: --list MUST NOT die because a build's [FromPath]
        // tool isn't available. HoldFast hit this with trufflehog not yet
        // installed; CI failure on `tamp --list` is a non-starter.
        var exit = 0;
        var output = CaptureStdout(() =>
        {
            exit = TampBuild.Execute<TamperedBuild>(new[] { "--list" });
        });
        Assert.Equal(0, exit);
        Assert.Contains("NoOp", output);
    }

    [Fact]
    public void Tamp_ListTree_Succeeds_When_FromPath_Resolution_Would_Fail()
    {
        var exit = 0;
        var output = CaptureStdout(() =>
        {
            exit = TampBuild.Execute<TamperedBuild>(new[] { "--list-tree" });
        });
        Assert.Equal(0, exit);
        Assert.Contains("NoOp", output);
    }

    [Fact]
    public void Tamp_List_Json_Succeeds_When_FromPath_Resolution_Would_Fail()
    {
        var exit = 0;
        var output = CaptureStdout(() =>
        {
            exit = TampBuild.Execute<TamperedBuild>(new[] { "--list", "--format=json" });
        });
        Assert.Equal(0, exit);
        // The JSON catalog should still emit the target metadata.
        Assert.Contains("NoOp", output);
    }

    [Fact]
    public void Normal_Build_Still_Fails_Hard_On_Injection_Failure()
    {
        // The escape valve is list-mode-only — normal builds preserve the
        // fail-fast behavior so adopters notice missing tools at build time
        // instead of at target-run time.
        var stderr = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(new StringWriter());
        Console.SetError(stderr);
        int exit;
        try
        {
            exit = TampBuild.Execute<TamperedBuild>(new[] { "NoOp" });
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
        Assert.NotEqual(0, exit);
        Assert.Contains("simulated tool-resolution failure", stderr.ToString());
    }
}
