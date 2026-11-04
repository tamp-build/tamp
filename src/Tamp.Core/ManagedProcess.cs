using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Tamp;

/// <summary>Options for <see cref="ManagedProcess.StartAsync"/>.</summary>
public sealed class ManagedProcessOptions
{
    /// <summary>The service kind on <c>service.ready</c> / <c>service.stopped</c>, e.g. <c>heft-dev-server</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>How to tell the service is usable. Null: ready as soon as the process has started.</summary>
    public Readiness? Readiness { get; init; }

    /// <summary>How long to wait for <see cref="Readiness"/>. Default 5 minutes.</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Delay between readiness checks. Default 1 second.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// On Unix, send SIGTERM and wait this long for the process to exit before killing its tree. Null (the default):
    /// kill straight away. Windows always kills (a console Ctrl event can't target one process reliably).
    /// </summary>
    public TimeSpan? GracefulStopTimeout { get; init; }

    /// <summary>How long to wait for the tree to exit after the kill. Default 10 seconds.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Where the service is, for <c>service.ready</c>'s <c>url</c>. Default: the readiness description.</summary>
    public string? ServiceUrl { get; init; }

    /// <summary>The listening port, for <c>service.ready</c>.</summary>
    public int? Port { get; init; }

    /// <summary>A state file describing the service for readers outside the build, for <c>service.ready</c>.</summary>
    public string? StatePath { get; init; }

    /// <summary>Where the process's output goes (redacted inside a build). Default: the console.</summary>
    public TextWriter? Output { get; init; }

    /// <summary>How many output lines <see cref="ManagedProcess.Tail"/> keeps for failure messages. Default 40.</summary>
    public int TailLines { get; init; } = 40;
}

/// <summary>
/// A long-running child process a target starts and keeps (a dev server, an emulator), with a readiness check, a
/// concurrency-safe stop that kills the whole process tree, and <c>tool.invoked</c> / <c>service.ready</c> /
/// <c>service.stopped</c> / <c>tool.exited</c> on one command span (#125).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lifetime.</strong> <see cref="StartAsync"/> returns once the service is ready; dispose to stop it
/// (<c>service.stopped</c> reason <c>requested</c>). It can outlive the target that started it (start a database in
/// one target, test against it in the next): inside a <c>tamp</c> run the executor stops whatever is still running
/// before the build finishes, and on Ctrl+C / SIGTERM / SIGHUP it is stopped at once (reason <c>signal</c>).
/// </para>
/// <para>
/// <strong>Failure.</strong> If the process exits before it's ready, <see cref="StartAsync"/> throws
/// <see cref="InvalidOperationException"/> with the last lines of its output (reason <c>exited</c>); if readiness
/// times out it throws <see cref="TimeoutException"/> (reason <c>timeout</c>). If it exits on its own later,
/// <c>service.stopped</c> is recorded with reason <c>exited</c> right away and <see cref="HasExited"/> turns true.
/// </para>
/// <para>
/// Stopping is idempotent and safe to call from several threads at once: a second caller waits for the first to
/// finish, so a build can't exit while the tree is still being killed. Events are emitted on the build's stream
/// from whichever thread stops it, parented to the target that started it.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await using var api = await ManagedProcess.StartAsync(Npm.Run(s =&gt; s.SetScript("start")), new ManagedProcessOptions
/// {
///     Kind = "api",
///     Readiness = Readiness.Http(new Uri("http://localhost:5000/health")),
///     ServiceUrl = "http://localhost:5000/",
///     Port = 5000,
/// });
/// </code>
/// </example>
public sealed class ManagedProcess : IAsyncDisposable, IDisposable
{
    private readonly ManagedProcessOptions _options;
    private readonly CommandPlan _plan;
    private readonly Process _process;
    private readonly CapturedEventScope? _events;
    private readonly string? _span;
    private readonly long _startTimestamp;
    private readonly object _ioLock = new();
    private readonly Queue<string> _tail = new();
    private readonly ManualResetEventSlim _finished = new();
    private readonly TextWriter _output;
    private readonly BuildInterrupt? _interrupt;
    private CancellationTokenRegistration _onBuildCancelled;
    private IDisposable? _childTracking;
    private IDisposable? _registration;
    private string? _lastReadinessDetail;
    private int _stopping;
    private int _finishedFlag;
    private volatile bool _ready;

    private ManagedProcess(CommandPlan plan, ManagedProcessOptions options)
    {
        _plan = plan;
        _options = options;
        _output = options.Output ?? Console.Out;
        _interrupt = BuildInterrupt.Active;
        _events = BuildEvents.Capture();

        var psi = new ProcessStartInfo
        {
            FileName = plan.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = plan.StandardInput is not null,
        };
        if (plan.WorkingDirectory is not null) psi.WorkingDirectory = plan.WorkingDirectory;
        foreach (var arg in plan.Arguments) psi.ArgumentList.Add(arg);
        foreach (var (k, v) in plan.Environment) psi.Environment[k] = v;

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) OnLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OnLine(e.Data); };
        _process.Exited += (_, _) => OnExited();

        _span = _events?.ToolInvoked(plan);
        _startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            _process.Start();
        }
        catch
        {
            if (_events is { } ev && _span is { } span)
                ev.Emit(BuildEventTypes.ToolExited, span, new ToolExitedPayload { Tool = plan.Executable, ExitCode = -1, DurationMs = 0 });
            _process.Dispose();
            throw;
        }
        Pid = _process.Id;
        try { ProcessStartTime = new DateTimeOffset(_process.StartTime.ToUniversalTime(), TimeSpan.Zero); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { ProcessStartTime = DateTimeOffset.UtcNow; }
        _childTracking = _interrupt?.TrackChild(_process);
        _registration = _interrupt?.RegisterManaged(this);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        if (plan.StandardInput is not null)
        {
            _process.StandardInput.Write(plan.StandardInput);
            _process.StandardInput.Close();
        }
    }

    /// <summary>The service kind.</summary>
    public string Kind => _options.Kind;

    /// <summary>PID of the root process.</summary>
    public int Pid { get; }

    /// <summary>When the root process started (UTC).</summary>
    public DateTimeOffset ProcessStartTime { get; }

    /// <summary>True once the process has exited (stopped, or on its own).</summary>
    public bool HasExited => Volatile.Read(ref _finishedFlag) == 1 || SafeHasExited();

    /// <summary>The exit code, once it has exited.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>True once a stop has begun.</summary>
    public bool IsStopping => Volatile.Read(ref _stopping) == 1;

    /// <summary>The reason recorded on <c>service.stopped</c>, once stopped.</summary>
    public string? StopReason { get; private set; }

    /// <summary>The last lines of output (see <see cref="ManagedProcessOptions.TailLines"/>).</summary>
    public IReadOnlyList<string> Tail
    {
        get { lock (_ioLock) return _tail.ToArray(); }
    }

    /// <summary>Completes when the process has exited and its stop has been recorded.</summary>
    public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => _finished.Wait(cancellationToken), cancellationToken);

    /// <summary>
    /// Start <paramref name="plan"/> and wait until <see cref="ManagedProcessOptions.Readiness"/> passes. Honours the
    /// build's cancellation (<see cref="TampBuild.BuildCancellation"/>) as well as <paramref name="cancellationToken"/>.
    /// </summary>
    public static async Task<ManagedProcess> StartAsync(CommandPlan plan, ManagedProcessOptions options, CancellationToken cancellationToken = default)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.Kind)) throw new ArgumentException("options.Kind is required.", nameof(options));
        if (options.ReadyTimeout <= TimeSpan.Zero) throw new ArgumentException("ReadyTimeout must be positive.", nameof(options));
        if (options.PollInterval <= TimeSpan.Zero) throw new ArgumentException("PollInterval must be positive.", nameof(options));
        if (options.ShutdownTimeout <= TimeSpan.Zero) throw new ArgumentException("ShutdownTimeout must be positive.", nameof(options));
        if (options.GracefulStopTimeout is { } g && g < TimeSpan.Zero) throw new ArgumentException("GracefulStopTimeout can't be negative.", nameof(options));
        if (options.TailLines < 0) throw new ArgumentException("TailLines can't be negative.", nameof(options));

        var build = TampBuild.BuildCancellation;
        build.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();

        var mp = new ManagedProcess(plan, options);
        // Ctrl+C / SIGTERM / SIGHUP: stop at once, whatever the target is doing.
        mp._onBuildCancelled = build.Register(() => mp.Stop(ServiceStopReason.Signal));
        try
        {
            await mp.WaitUntilReadyAsync(build, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (build.IsCancellationRequested)
        {
            mp.Stop(ServiceStopReason.Signal);
            throw;
        }
        catch (OperationCanceledException)
        {
            mp.Stop(ServiceStopReason.Requested);
            throw;
        }
        catch (TimeoutException)
        {
            mp.Stop(ServiceStopReason.Timeout);
            throw;
        }
        catch
        {
            mp.Stop(ServiceStopReason.Exited);
            throw;
        }
        return mp;
    }

    private async Task WaitUntilReadyAsync(CancellationToken build, CancellationToken caller)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(build, caller);
        var readiness = _options.Readiness;
        if (readiness is not null)
        {
            var ready = await Polling.Until(async ct =>
            {
                if (SafeHasExited()) throw ExitedBeforeReady();
                var result = await readiness.CheckAsync(ct).ConfigureAwait(false);
                if (!result.IsReady) _lastReadinessDetail = result.Detail;
                return result.IsReady;
            }, _options.ReadyTimeout, Backoff.Constant(_options.PollInterval), logger: null, linked.Token).ConfigureAwait(false);

            if (!ready)
                throw new TimeoutException(
                    $"{_options.Kind} was not ready within {_options.ReadyTimeout.TotalSeconds:0.#}s: {readiness.Description} " +
                    $"(last result: {_lastReadinessDetail ?? "none"}).{TailText()}");
        }
        if (SafeHasExited()) throw ExitedBeforeReady();

        _ready = true;
        if (_events is { } ev && _span is { } span)
            ev.Emit(BuildEventTypes.ServiceReady, span, new ServiceReadyPayload
            {
                Kind = _options.Kind,
                Url = ev.Redact(_options.ServiceUrl ?? readiness?.Description ?? _plan.Executable),
                Port = _options.Port,
                Pid = Pid,
                ProcessStartTime = ProcessStartTime,
                ReadinessProbe = readiness is null ? null : ev.Redact(readiness.Description),
                StatePath = _options.StatePath,
            });
    }

    private InvalidOperationException ExitedBeforeReady()
    {
        try { _process.WaitForExit(); } catch (InvalidOperationException) { }
        return new InvalidOperationException($"{_options.Kind} exited with code {ExitCodeText()} before it was ready.{TailText()}");
    }

    /// <summary>Stop the process (reason <c>requested</c>): kill its tree, wait for it to exit, record the stop.</summary>
    public void Dispose() => Stop(ServiceStopReason.Requested);

    /// <inheritdoc cref="Dispose" />
    public ValueTask DisposeAsync()
    {
        Stop(ServiceStopReason.Requested);
        return default;
    }

    /// <summary>Stop with <paramref name="reason"/>. Idempotent; a concurrent caller waits for the first stop to finish.</summary>
    internal void Stop(string reason)
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 1)
        {
            _finished.Wait(Bound(_options.ShutdownTimeout + (_options.GracefulStopTimeout ?? TimeSpan.Zero) + TimeSpan.FromSeconds(5)));
            return;
        }

        try
        {
            if (!SafeHasExited())
            {
                if (_options.GracefulStopTimeout is { } grace && !OperatingSystem.IsWindows() && grace > TimeSpan.Zero)
                {
                    _ = SendTerm(Pid);
                    _process.WaitForExit(Bound(grace));
                }
                ChildProcesses.KillTree(_process);
                _process.WaitForExit(Bound(_options.ShutdownTimeout));
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            Finish(reason);
        }
    }

    // The process exited on its own (not because we stopped it).
    private void OnExited()
    {
        if (IsStopping) return;
        if (Interlocked.Exchange(ref _stopping, 1) == 1) return;
        if (!_ready)
        {
            // Still starting: the readiness loop reports it (and StartAsync stops with reason exited).
            Volatile.Write(ref _stopping, 0);
            return;
        }
        Finish(ServiceStopReason.Exited);
    }

    private void Finish(string reason)
    {
        if (Interlocked.Exchange(ref _finishedFlag, 1) == 1) return;
        try
        {
            bool exited;
            try { exited = _process.WaitForExit(Bound(_options.ShutdownTimeout)); }
            catch (InvalidOperationException) { exited = true; }
            if (exited)
            {
                try { _process.WaitForExit(); } catch (InvalidOperationException) { } // flush the output readers
                try { ExitCode = _process.ExitCode; } catch (InvalidOperationException) { }
            }
            StopReason = reason;
            var durationMs = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            if (_events is { } ev && _span is { } span)
            {
                ev.Emit(BuildEventTypes.ServiceStopped, span, new ServiceStoppedPayload
                {
                    Kind = _options.Kind,
                    Reason = reason,
                    Pid = Pid,
                    ExitCode = ExitCode,
                    DurationMs = durationMs,
                });
                ev.Emit(BuildEventTypes.ToolExited, span, new ToolExitedPayload { Tool = _plan.Executable, ExitCode = ExitCode ?? -1, DurationMs = durationMs });
            }
        }
        finally
        {
            _onBuildCancelled.Dispose();
            _registration?.Dispose();
            _childTracking?.Dispose();
            _options.Readiness?.Dispose();
            _process.Dispose();
            _finished.Set();
        }
    }

    private void OnLine(string line)
    {
        if (_events is { } ev) line = ev.Redact(line);
        try { _options.Readiness?.OnOutputLine(line); }
        catch (Exception) { /* a faulty readiness hook must not break output */ }
        lock (_ioLock)
        {
            if (_options.TailLines > 0)
            {
                _tail.Enqueue(line);
                while (_tail.Count > _options.TailLines) _tail.Dequeue();
            }
            try { _output.WriteLine(line); }
            catch (ObjectDisposedException) { }
        }
    }

    private bool SafeHasExited()
    {
        try { return _process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private string ExitCodeText()
    {
        try { return _process.ExitCode.ToString(CultureInfo.InvariantCulture); }
        catch (InvalidOperationException) { return "unknown"; }
    }

    private string TailText()
    {
        lock (_ioLock)
            return _tail.Count == 0 ? "" : Environment.NewLine + "Last output:" + Environment.NewLine + string.Join(Environment.NewLine, _tail);
    }

    private static int Bound(TimeSpan t) => (int)Math.Clamp(t.TotalMilliseconds, 0, int.MaxValue);

    private static bool SendTerm(int pid)
    {
        try { return SysKill(pid, 15) == 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int sig);
}
