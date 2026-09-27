using System.IO;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// #72: the <c>--verify-redaction</c> self-test proves the <c>--capture-logs</c> redaction path
/// scrubs a secret on the way to disk — the guarantee is otherwise unobservable (secrets pass as
/// argv, never into captured child output). Runs the same RedactingTextWriter + RedactionTable the
/// per-target log uses, against a real temp file.
/// </summary>
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class RedactionSelfTestTests
{
    private sealed class TinyBuild : TampBuild
    {
        public static int RanCount;
        public Target Build => _ => _.Default().Executes(() => { RanCount++; });
    }

    [Fact]
    public void RunRedactionSelfTest_Passes_And_Shows_The_Placeholder_Not_The_Raw_Value()
    {
        var sw = new StringWriter();
        var exit = TampBuild.RunRedactionSelfTest(sw);
        var output = sw.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("PASS", output);
        Assert.Contains("<Secret:selftest-sentinel>", output);   // placeholder present
        Assert.DoesNotContain("PRESENT (leak!)", output);        // raw value not on disk
        // The sentinel is redacted, so its unique prefix never appears in the report.
        Assert.DoesNotContain("TAMP-REDACTION-SELFTEST-", output);
    }

    [Fact]
    public void VerifyRedaction_Flag_Through_Execute_Passes_And_Runs_No_Target()
    {
        TinyBuild.RanCount = 0;
        var so = new StringWriter();
        var prev = Console.Out;
        Console.SetOut(so);
        int exit;
        try { exit = TampBuild.Execute<TinyBuild>(new[] { "--verify-redaction" }); }
        finally { Console.SetOut(prev); }

        Assert.Equal(0, exit);
        Assert.Equal(0, TinyBuild.RanCount);       // self-test runs nothing else
        Assert.Contains("PASS", so.ToString());
    }
}
