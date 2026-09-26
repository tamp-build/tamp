using System.Diagnostics;
using Tamp.Diagnostics;

namespace Tamp;

/// <summary>
/// Dispatches or prints a <see cref="CommandPlan"/>. Owns the boundary
/// between Tamp's typed plan model and the operating system's process API.
/// </summary>
/// <remarks>
/// For v0 this is a sequential, fire-and-wait runner with stdout/stderr
/// passed through to the parent process. Streaming-output redaction
/// (TAM-27) and concurrent execution policies (resource scheduler, retry,
/// parallelism) are explicitly out of scope here and arrive in later
/// subtasks. Secrets in the plan's argument list are not yet substituted —
/// callers should construct plans with literal values and accept that
/// for v0 those values appear in the OS process table while the child
/// runs (an OS-level concern noted in ADR 0009 / the Secret type docs).
/// </remarks>
public static class ProcessRunner
{
    /// <summary>
    /// Spawn <paramref name="plan"/> and capture every stdout / stderr line
    /// in order, with the source stream tagged. Returns the exit code plus
    /// the captured lines for the caller to inspect.
    /// </summary>
    /// <remarks>
    /// Use this when a wrapper needs to read what the tool printed —
    /// e.g., parse <c>dotnet --version</c> output, read a progress JSON
    /// stream, or detect a specific error pattern. For build-pipeline
    /// dispatch where the output should just stream through, use
    /// <see cref="Execute"/>.
    /// </remarks>
    public static CaptureResult Capture(CommandPlan plan, TextWriter? alsoStdout = null, TextWriter? alsoStderr = null)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        var lines = new List<OutputLine>();
        var lockObj = new object();

        var psi = new ProcessStartInfo
        {
            FileName = plan.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = plan.StandardInput is not null,
        };
        if (plan.WorkingDirectory is not null)
            psi.WorkingDirectory = plan.WorkingDirectory;
        foreach (var arg in plan.Arguments)
            psi.ArgumentList.Add(arg);
        foreach (var (k, v) in plan.Environment)
            psi.Environment[k] = v;

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (lockObj) lines.Add(new OutputLine(OutputType.Stdout, e.Data));
            alsoStdout?.WriteLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (lockObj) lines.Add(new OutputLine(OutputType.Stderr, e.Data));
            alsoStderr?.WriteLine(e.Data);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (plan.StandardInput is not null)
        {
            process.StandardInput.Write(plan.StandardInput);
            process.StandardInput.Close();
        }
        process.WaitForExit();
        return new CaptureResult(process.ExitCode, lines);
    }

    /// <summary>Spawn the process described by <paramref name="plan"/> and wait for exit.</summary>
    /// <param name="plan">The plan to execute.</param>
    /// <param name="stdout">Stdout sink; defaults to <see cref="Console.Out"/>.</param>
    /// <param name="stderr">Stderr sink; defaults to <see cref="Console.Error"/>.</param>
    /// <param name="sourceTargetName">
    /// Optional name of the Tamp target dispatching this plan. Surfaced as the
    /// <c>tamp.cmd.source_target</c> tag on the diagnostics span so consumers
    /// can correlate command spans with parent target spans without relying
    /// solely on the activity context chain.
    /// </param>
    public static int Execute(CommandPlan plan, TextWriter? stdout = null, TextWriter? stderr = null, string? sourceTargetName = null)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        var stdoutSink = stdout ?? Console.Out;
        var stderrSink = stderr ?? Console.Error;

        var psi = new ProcessStartInfo
        {
            FileName = plan.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = plan.StandardInput is not null,
        };
        if (plan.WorkingDirectory is not null)
            psi.WorkingDirectory = plan.WorkingDirectory;
        foreach (var arg in plan.Arguments)
            psi.ArgumentList.Add(arg);
        foreach (var (k, v) in plan.Environment)
            psi.Environment[k] = v;

        // ── Diagnostics: per-command span (ADR 0018).
        using var span = TampDiagnostics.CommandsSource.StartActivity($"command:{plan.Executable}", ActivityKind.Client);
        if (span is not null)
        {
            span.SetTag(TampDiagnostics.Tags.CmdExecutable, plan.Executable);
            span.SetTag(TampDiagnostics.Tags.CmdArgsCount, plan.Arguments.Count);
            if (plan.WorkingDirectory is not null) span.SetTag(TampDiagnostics.Tags.CmdWorkingDirectory, plan.WorkingDirectory);
            span.SetTag(TampDiagnostics.Tags.CmdHadStdin, plan.StandardInput is not null);
            span.SetTag(TampDiagnostics.Tags.CmdHadSecrets, plan.Secrets.Count > 0);
            if (sourceTargetName is not null) span.SetTag(TampDiagnostics.Tags.CmdSourceTarget, sourceTargetName);
        }
        var cmdStartTicks = Stopwatch.GetTimestamp();
        long stdoutBytes = 0, stderrBytes = 0;

        using var process = new Process { StartInfo = psi };

        // stdout + stderr are read on separate threads (BeginOutputReadLine /
        // BeginErrorReadLine) and typically write to the SAME sink (the executor
        // passes one CapturingTextWriter as both). Those writers are single-threaded
        // by contract, and one of them is the secret-redaction writer — so serialize
        // the two handlers here. Per-line, so a secret can't be split across
        // interleaved concurrent writes (TAM/#31).
        var ioLock = new object();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            System.Threading.Interlocked.Add(ref stdoutBytes, System.Text.Encoding.UTF8.GetByteCount(e.Data) + System.Environment.NewLine.Length);
            lock (ioLock) stdoutSink.WriteLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            System.Threading.Interlocked.Add(ref stderrBytes, System.Text.Encoding.UTF8.GetByteCount(e.Data) + System.Environment.NewLine.Length);
            lock (ioLock) stderrSink.WriteLine(e.Data);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (plan.StandardInput is not null)
        {
            process.StandardInput.Write(plan.StandardInput);
            process.StandardInput.Close();
        }
        process.WaitForExit();

        // After-exit child-process tags. Reading these requires the Process object
        // to still hold the handle — safe here because we haven't disposed yet.
        long peakWs = 0, privateMem = 0, virtualMem = 0;
        double cpuUserMs = 0, cpuSysMs = 0, cpuTotalMs = 0;
        int threadCount = 0, handleCount = 0;
        try
        {
            peakWs = process.PeakWorkingSet64;
            privateMem = process.PrivateMemorySize64;
            virtualMem = process.VirtualMemorySize64;
            cpuUserMs = process.UserProcessorTime.TotalMilliseconds;
            cpuSysMs = process.PrivilegedProcessorTime.TotalMilliseconds;
            cpuTotalMs = process.TotalProcessorTime.TotalMilliseconds;
            threadCount = process.Threads.Count;
        }
        catch { /* some platforms restrict these; ignore */ }
        try { handleCount = process.HandleCount; } catch { /* Windows-only */ }

        var endTicks = Stopwatch.GetTimestamp();
        var durationNs = (long)((endTicks - cmdStartTicks) * 1_000_000_000.0 / Stopwatch.Frequency);
        var durationMs = durationNs / 1_000_000.0;
        var outcome = process.ExitCode == 0 ? TampDiagnostics.Tags.OutcomeSuccess : TampDiagnostics.Tags.OutcomeFailure;

        if (span is not null)
        {
            span.SetTag(TampDiagnostics.Tags.CmdExitCode, process.ExitCode);
            span.SetTag(TampDiagnostics.Tags.CmdDurationNs, durationNs);
            span.SetTag(TampDiagnostics.Tags.CmdChildPeakWorkingSetBytes, peakWs);
            span.SetTag(TampDiagnostics.Tags.CmdChildPrivateMemoryBytes, privateMem);
            span.SetTag(TampDiagnostics.Tags.CmdChildVirtualMemoryBytes, virtualMem);
            span.SetTag(TampDiagnostics.Tags.CmdChildCpuTimeUserMs, cpuUserMs);
            span.SetTag(TampDiagnostics.Tags.CmdChildCpuTimeSystemMs, cpuSysMs);
            span.SetTag(TampDiagnostics.Tags.CmdChildCpuTimeTotalMs, cpuTotalMs);
            span.SetTag(TampDiagnostics.Tags.CmdChildThreadCount, threadCount);
            span.SetTag(TampDiagnostics.Tags.CmdChildHandleCount, handleCount);
            span.SetTag(TampDiagnostics.Tags.CmdStdoutBytes, stdoutBytes);
            span.SetTag(TampDiagnostics.Tags.CmdStderrBytes, stderrBytes);
            span.SetTag(TampDiagnostics.Tags.OutcomeKey, outcome);
            span.SetStatus(outcome == TampDiagnostics.Tags.OutcomeSuccess ? ActivityStatusCode.Ok : ActivityStatusCode.Error,
                outcome == TampDiagnostics.Tags.OutcomeSuccess ? null : $"exit {process.ExitCode}");
        }
        TampDiagnostics.CommandsExecuted.Add(1,
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.CmdExecutable, plan.Executable),
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, outcome));
        TampDiagnostics.CommandDurationMs.Record(durationMs,
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.CmdExecutable, plan.Executable),
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, outcome));
        TampDiagnostics.CommandPeakMemoryBytes.Record(peakWs,
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.CmdExecutable, plan.Executable),
            new KeyValuePair<string, object?>(TampDiagnostics.Tags.OutcomeKey, outcome));

        return process.ExitCode;
    }

    /// <summary>
    /// Print the plan in dry-run format. This is the format ADR 0004
    /// (deferred) will eventually canonicalize; for now the shape is
    /// readable, secrets-redacted, and unambiguous.
    /// </summary>
    public static void Print(CommandPlan plan, string targetName, string? sourceModule, TextWriter writer)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (writer is null) throw new ArgumentNullException(nameof(writer));

        var moduleSuffix = string.IsNullOrEmpty(sourceModule) ? string.Empty : $" ({sourceModule})";
        writer.WriteLine($"{targetName}{moduleSuffix}");

        var args = plan.Arguments.Count == 0 ? string.Empty : " " + string.Join(' ', plan.Arguments.Select(QuoteIfNeeded));
        writer.WriteLine($"  $ {plan.Executable}{args}");

        if (plan.WorkingDirectory is not null)
            writer.WriteLine($"  cwd: {plan.WorkingDirectory}");

        if (plan.Environment.Count > 0)
        {
            var pairs = string.Join(", ", plan.Environment.Select(kv => $"{kv.Key}={kv.Value}"));
            writer.WriteLine($"  env: {pairs}");
        }

        if (plan.Secrets.Count > 0)
        {
            var names = string.Join(", ", plan.Secrets.Select(s => s.Name));
            writer.WriteLine($"  secrets: {names} (values redacted)");
        }

        if (plan.StandardInput is not null)
        {
            // Don't print the literal stdin content even after redaction —
            // the table may not yet have a registration for everything fed
            // through stdin (callers are not required to declare every byte
            // as a Secret). Indicate presence and length only.
            writer.WriteLine($"  stdin: <{plan.StandardInput.Length} bytes redacted>");
        }

        writer.WriteLine();
    }

    private static string QuoteIfNeeded(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.Contains(' ') || arg.Contains('\t') || arg.Contains('"'))
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        return arg;
    }
}
