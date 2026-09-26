using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Regression test for #31: <see cref="ProcessRunner"/> reads stdout + stderr on
/// separate threads and (as the executor wires it) writes both into one
/// <c>CapturingTextWriter → RedactingTextWriter</c> sink. Those writers are
/// single-threaded by contract, so the two handlers are serialized at the
/// ProcessRunner boundary. This drives a process that emits many interleaved
/// stdout+stderr lines and asserts no exception, correct exit, and — critically —
/// that a secret appearing on both streams is fully redacted under concurrency.
/// </summary>
public sealed class ProcessRunnerIoRaceTests
{
    private const string Token = "SEKRETabc123";

    private static CommandPlan DualStreamPlan(int lines)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // cmd for-loop: one stdout + one stderr line per iteration.
            return new CommandPlan
            {
                Executable = "cmd.exe",
                Arguments = new[] { "/c", $"for /L %i in (1,1,{lines}) do @(echo o-{Token}& echo e-{Token} 1>&2)" },
            };
        }
        return new CommandPlan
        {
            Executable = "/bin/sh",
            Arguments = new[] { "-c", $"i=0; while [ $i -lt {lines} ]; do echo o-{Token}; echo e-{Token} 1>&2; i=$((i+1)); done" },
        };
    }

    [Fact]
    public void Concurrent_Stdout_Stderr_Into_One_Redacting_Sink_Does_Not_Race_Or_Leak()
    {
        var table = new RedactionTable();
        table.Register(new Secret("Tok", Token));
        var inner = new StringWriter();
        var redacting = new RedactingTextWriter(inner, table);
        var capturing = new CapturingTextWriter(redacting, new TargetOutputBuffer());

        // Same sink for both streams — the executor's real configuration, and the
        // shape that triggered the shared-StringBuilder race pre-fix.
        var exit = ProcessRunner.Execute(DualStreamPlan(lines: 300), capturing, capturing);
        capturing.Flush();

        Assert.Equal(0, exit);
        var captured = inner.ToString();
        Assert.NotEqual(0, captured.Length);
        Assert.DoesNotContain(Token, captured);          // secret never survives, even split across concurrent writes
        Assert.Contains("<Secret:Tok>", captured);       // it was redacted, not just absent
    }
}
