using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tamp;

/// <summary>
/// The build's interrupt state (#125): the cancellation token targets and helpers observe, the signal that
/// cancelled it, and the shutdown grace period. One per <see cref="Executor"/> run; reached through
/// <see cref="TampBuild.BuildCancellation"/>.
/// </summary>
/// <remarks>
/// <para>
/// The first SIGINT (Ctrl+C), SIGTERM or SIGHUP cancels the build token and starts the grace period. The running
/// target is reported <c>cancelled</c>, the remaining targets <c>not_run</c>, and <c>AssuredAfterFailure</c>
/// cleanup targets still run, with a token of their own that cancels when the grace period ends. A second signal,
/// or the grace period running out, forces the exit: every child process tree <see cref="ProcessRunner"/> started
/// is killed and the process exits with <c>128 + signal</c> (130, 143, 129) on every OS.
/// </para>
/// <para>
/// Signals are only handled while a run that asked for it is executing (<c>tamp</c> does; an
/// <see cref="Executor"/> constructed in a test doesn't, unless told to).
/// </para>
/// </remarks>
internal sealed class BuildInterrupt : IDisposable
{
    /// <summary>The default shutdown grace period.</summary>
    internal static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(10);

    // How long past the grace deadline a forced exit waits, so a cleanup target that honours its token can unwind.
    private static readonly TimeSpan ForceSlack = TimeSpan.FromSeconds(2);

    private static readonly AsyncLocal<BuildInterrupt?> Current = new();

    private readonly CancellationTokenSource _build = new();
    private readonly ChildProcesses _children = new();
    private readonly ConcurrentDictionary<ManagedProcess, byte> _managed = new();
    private readonly AsyncLocal<CancellationTokenSource?> _cleanup = new();
    private readonly List<PosixSignalRegistration> _registrations = new();
    private readonly TextWriter _log;
    private readonly Action<int> _exit;
    private readonly Action<string, int>? _onForcedExit;
    private readonly object _gate = new();
    private Timer? _forceTimer;
    private DateTimeOffset _deadline;
    private int _forced;

    internal BuildInterrupt(TimeSpan gracePeriod, TextWriter log, bool handleSignals, Action<string, int>? onForcedExit = null, Action<int>? exit = null)
    {
        if (gracePeriod < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(gracePeriod), gracePeriod, "The grace period can't be negative.");
        GracePeriod = gracePeriod;
        _log = log;
        _onForcedExit = onForcedExit;
        _exit = exit ?? Environment.Exit;
        if (handleSignals) RegisterSignals();
    }

    /// <summary>The interrupt for the current run's flow, or null outside a run.</summary>
    internal static BuildInterrupt? Active => Current.Value;

    /// <summary>The token to observe right now: the cleanup token inside a cleanup target, else the build token.</summary>
    internal static CancellationToken CurrentToken => Current.Value?.Token ?? CancellationToken.None;

    /// <summary>The shutdown grace period.</summary>
    internal TimeSpan GracePeriod { get; }

    /// <summary>True once a signal (or <see cref="RequestInterrupt"/>) has cancelled the build.</summary>
    internal bool IsInterrupted => _build.IsCancellationRequested;

    /// <summary>The signal's name, e.g. <c>SIGINT</c>, once interrupted.</summary>
    internal string? Signal { get; private set; }

    /// <summary><c>128 + signal number</c>, once interrupted.</summary>
    internal int ExitCode { get; private set; }

    /// <summary>The reason recorded on the stream, e.g. <c>signal: SIGINT</c>.</summary>
    internal string Reason => $"signal: {Signal}";

    /// <summary>The build token, or the cleanup token while <see cref="EnterCleanup"/> is in effect.</summary>
    internal CancellationToken Token => _cleanup.Value?.Token ?? _build.Token;

    /// <summary>Register a <see cref="ManagedProcess"/> of this run, so the executor can stop it if target code doesn't.</summary>
    internal IDisposable RegisterManaged(ManagedProcess process)
    {
        _managed.TryAdd(process, 0);
        return new Restore(() => _managed.TryRemove(process, out _));
    }

    /// <summary>
    /// Stop every managed process still running (the end-of-run backstop: a service started in one target may serve
    /// later ones, but none outlives the build). Returns how many were stopped.
    /// </summary>
    internal int StopManagedProcesses(string reason)
    {
        var running = _managed.Keys.ToList();
        foreach (var p in running) p.Stop(reason);
        return running.Count;
    }

    /// <summary>Track a child process of this run, so a forced exit kills its tree. Dispose when it has exited.</summary>
    internal IDisposable TrackChild(Process process) => _children.Track(process);

    /// <summary>Make this the interrupt for the current flow; dispose to restore the previous one.</summary>
    internal IDisposable Activate()
    {
        var previous = Current.Value;
        Current.Value = this;
        return new Restore(() => Current.Value = previous);
    }

    /// <summary>
    /// Run the code that follows (a cleanup target after an interrupt) with a fresh token that cancels when the
    /// grace period ends, instead of the already-cancelled build token.
    /// </summary>
    internal IDisposable EnterCleanup()
    {
        var remaining = _deadline - DateTimeOffset.UtcNow;
        var cts = remaining > TimeSpan.Zero ? new CancellationTokenSource(remaining) : new CancellationTokenSource();
        if (remaining <= TimeSpan.Zero) cts.Cancel();
        var previous = _cleanup.Value;
        _cleanup.Value = cts;
        return new Restore(() => { _cleanup.Value = previous; cts.Dispose(); });
    }

    /// <summary>
    /// Interrupt the build as if <paramref name="signal"/> had arrived. The first call cancels the build token and
    /// starts the grace period; a second forces the exit. Called by the signal handlers, and by tests.
    /// </summary>
    internal void RequestInterrupt(string signal, int signalNumber)
    {
        bool first;
        lock (_gate)
        {
            first = !_build.IsCancellationRequested;
            if (first)
            {
                Signal = signal;
                ExitCode = 128 + signalNumber;
                _deadline = DateTimeOffset.UtcNow + GracePeriod;
            }
        }

        if (!first)
        {
            ForceExit($"second interrupt ({signal})");
            return;
        }

        WriteLine($"tamp: {signal} received; stopping (cleanup targets get {GracePeriod.TotalSeconds:0.#}s). Press Ctrl+C again to force.");
        _forceTimer = new Timer(_ => ForceExit($"the {GracePeriod.TotalSeconds:0.#}s shutdown grace period ran out"), null, GracePeriod + ForceSlack, Timeout.InfiniteTimeSpan);
        // Token callbacks (ProcessRunner's tree kills) run synchronously here; they're quick.
        try { _build.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException ex) { WriteLine($"tamp: a cancellation callback failed: {ex.InnerException?.Message}"); }
    }

    private void ForceExit(string why)
    {
        if (Interlocked.Exchange(ref _forced, 1) == 1) return;
        WriteLine($"tamp: forcing exit: {why}.");
        _children.KillAll();
        try { _onForcedExit?.Invoke(Reason, ExitCode); }
        catch (Exception ex) { WriteLine($"tamp: could not record the forced exit: {ex.Message}"); }
        try { _log.Flush(); } catch (ObjectDisposedException) { }
        _exit(ExitCode);
    }

    private void RegisterSignals()
    {
        foreach (var (signal, name, number) in new[]
                 {
                     (PosixSignal.SIGINT, "SIGINT", 2),
                     (PosixSignal.SIGTERM, "SIGTERM", 15),
                     (PosixSignal.SIGHUP, "SIGHUP", 1),
                 })
        {
            try
            {
                _registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    // Take over from the default action (terminate): the build stops in an orderly way, and a
                    // second signal or the grace period forces it.
                    context.Cancel = true;
                    RequestInterrupt(name, number);
                }));
            }
            catch (PlatformNotSupportedException) { }
        }
    }

    private void WriteLine(string line)
    {
        try { lock (_log) _log.WriteLine(line); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        foreach (var r in _registrations) r.Dispose();
        _registrations.Clear();
        _forceTimer?.Dispose();
        _build.Dispose();
    }

    private sealed class Restore : IDisposable
    {
        private Action? _restore;
        public Restore(Action restore) => _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}

/// <summary>
/// Child processes <see cref="ProcessRunner"/> has running in one build, so a forced exit can kill their trees rather
/// than leave them behind (#125). Per run, so one build's forced exit never touches another's children.
/// </summary>
internal sealed class ChildProcesses
{
    private readonly ConcurrentDictionary<Process, byte> _all = new();

    internal IDisposable Track(Process process)
    {
        _all.TryAdd(process, 0);
        return new Untrack(_all, process);
    }

    internal void KillAll()
    {
        foreach (var p in _all.Keys) KillTree(p);
    }

    internal static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already exited, or not ours to kill.
        }
    }

    private sealed class Untrack : IDisposable
    {
        private readonly ConcurrentDictionary<Process, byte> _running;
        private readonly Process _process;
        public Untrack(ConcurrentDictionary<Process, byte> running, Process process) { _running = running; _process = process; }
        public void Dispose() => _running.TryRemove(_process, out _);
    }
}
