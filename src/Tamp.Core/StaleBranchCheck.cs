using System.Diagnostics;
using System.Text;

namespace Tamp;

/// <summary>
/// Structured outcome of a stale-branch check.
/// </summary>
/// <param name="BaseRef">The ref the branch was compared against (e.g. <c>origin/main</c>).</param>
/// <param name="CommitsBehind">How many commits exist on <paramref name="BaseRef"/> that are not on <c>HEAD</c>.</param>
/// <param name="MaxAllowed">Threshold above which the branch is considered stale.</param>
/// <param name="IsStale"><c>true</c> when <paramref name="CommitsBehind"/> &gt; <paramref name="MaxAllowed"/>.</param>
/// <param name="FetchPerformed">Whether a <c>git fetch</c> was run before counting.</param>
public sealed record StaleBranchReport(
    string BaseRef,
    int CommitsBehind,
    int MaxAllowed,
    bool IsStale,
    bool FetchPerformed)
{
    public override string ToString()
        => IsStale
            ? $"HEAD is {CommitsBehind} commits behind {BaseRef} (limit {MaxAllowed}). Pull and merge {BaseRef} before pushing."
            : $"HEAD is {CommitsBehind} commits behind {BaseRef} (limit {MaxAllowed}). Branch is fresh.";
}

/// <summary>
/// Thrown by <see cref="GitRepositoryStaleBranchExtensions.AssertNotStale"/> when the branch
/// is more commits behind the comparison ref than the configured threshold.
/// </summary>
public sealed class StaleBranchException : Exception
{
    public StaleBranchReport Report { get; }

    public StaleBranchException(StaleBranchReport report)
        : base(report.ToString())
    {
        Report = report;
    }
}

/// <summary>
/// Pluggable seam over the small subset of <c>git</c> the staleness check needs. Defaults
/// to shelling out via <see cref="Process"/>; tests inject a fake.
/// </summary>
internal interface IGitRunner
{
    GitCommandResult Run(AbsolutePath repoRoot, IReadOnlyList<string> args, TimeSpan timeout);
}

internal sealed record GitCommandResult(int ExitCode, string StdOut, string StdErr);

internal sealed class ProcessGitRunner : IGitRunner
{
    private readonly ExecutableLocator _locator;

    public ProcessGitRunner(ExecutableLocator? locator = null) => _locator = locator ?? ExecutableLocator.Current;

    public GitCommandResult Run(AbsolutePath repoRoot, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var git = _locator.Resolve("git", repoRoot.Value)
            ?? throw new InvalidOperationException(
                "git not found on PATH (entries that are relative or inside the working directory are not searched). Install git and re-run.");
        var psi = new ProcessStartInfo
        {
            FileName = git,
            WorkingDirectory = repoRoot.Value,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdOut) stdOut.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stdErr) stdErr.AppendLine(e.Data); };

        try { proc.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException("git not found on PATH. Install git and re-run.", ex);
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new GitCommandResult(-1, stdOut.ToString(), "git command timed out");
        }
        return new GitCommandResult(proc.ExitCode, stdOut.ToString(), stdErr.ToString());
    }
}

/// <summary>
/// Stale-branch detection extensions on <see cref="GitRepository"/>. Used to gate pre-push
/// pipelines: fail the build when the working branch is too far behind the base ref.
/// </summary>
/// <remarks>
/// <para>
/// Background: when a branch has diverged from <c>origin/main</c> by N commits, the GitHub /
/// ADO three-way merge can succeed mechanically while resurrecting deleted files, undoing
/// renames, or replaying old logic on top of a new schema. The CI shows green; the diff
/// against current main is misleading. Pulling main into the branch BEFORE pushing forces
/// the conflict to surface locally where it can be resolved.
/// </para>
/// <para>
/// This check is the gate. Wire it into your pre-push or pre-PR target:
/// </para>
/// <code>
/// Target PrePush =&gt; _ =&gt; _
///     .DependsOn(nameof(Test), nameof(Lint))
///     .Executes(() =&gt; Git.AssertNotStale(maxCommitsBehind: 20));
/// </code>
/// </remarks>
public static class GitRepositoryStaleBranchExtensions
{
    /// <summary>Default base ref to compare against — <c>origin/main</c>.</summary>
    public const string DefaultBaseRef = "origin/main";

    /// <summary>Default commits-behind threshold — 20.</summary>
    public const int DefaultMaxCommitsBehind = 20;

    /// <summary>Default timeout for the underlying <c>git</c> calls — 30 seconds (network-bound on fetch).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Counts commits on <paramref name="baseRef"/> not on <c>HEAD</c>; returns a structured report.
    /// Does NOT throw on staleness — call <see cref="AssertNotStale"/> for the gate behavior.
    /// </summary>
    public static StaleBranchReport CheckStaleness(
        this GitRepository repo,
        int maxCommitsBehind = DefaultMaxCommitsBehind,
        string baseRef = DefaultBaseRef,
        bool fetch = true,
        TimeSpan? timeout = null)
        => CheckStalenessCore(repo, maxCommitsBehind, baseRef, fetch, timeout ?? DefaultTimeout, new ProcessGitRunner());

    /// <summary>
    /// Throws <see cref="StaleBranchException"/> when the branch is more than
    /// <paramref name="maxCommitsBehind"/> commits behind <paramref name="baseRef"/>.
    /// </summary>
    public static void AssertNotStale(
        this GitRepository repo,
        int maxCommitsBehind = DefaultMaxCommitsBehind,
        string baseRef = DefaultBaseRef,
        bool fetch = true,
        TimeSpan? timeout = null)
    {
        var report = repo.CheckStaleness(maxCommitsBehind, baseRef, fetch, timeout);
        if (report.IsStale) throw new StaleBranchException(report);
    }

    internal static StaleBranchReport CheckStalenessCore(
        GitRepository repo,
        int maxCommitsBehind,
        string baseRef,
        bool fetch,
        TimeSpan timeout,
        IGitRunner runner)
    {
        if (repo is null) throw new ArgumentNullException(nameof(repo));
        if (runner is null) throw new ArgumentNullException(nameof(runner));
        if (string.IsNullOrWhiteSpace(baseRef)) throw new ArgumentException("Base ref must not be null or whitespace.", nameof(baseRef));
        if (maxCommitsBehind < 0) throw new ArgumentOutOfRangeException(nameof(maxCommitsBehind), "Must be non-negative.");
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "Must be positive.");

        if (fetch)
        {
            // Parse the remote+ref shape; "origin/main" → fetch "main" from "origin".
            // Any ref without a slash (e.g. "main") is treated as already-fetched and we skip.
            var slash = baseRef.IndexOf('/');
            if (slash > 0)
            {
                var remote = baseRef[..slash];
                var branch = baseRef[(slash + 1)..];
                var fetchResult = runner.Run(repo.Root, new[] { "fetch", "--quiet", remote, branch }, timeout);
                if (fetchResult.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"git fetch {remote} {branch} failed (exit {fetchResult.ExitCode}). " +
                        $"stderr: {fetchResult.StdErr.Trim()}");
            }
        }

        var revListResult = runner.Run(repo.Root, new[] { "rev-list", "--count", $"{baseRef}", "^HEAD" }, timeout);
        if (revListResult.ExitCode != 0)
            throw new InvalidOperationException(
                $"git rev-list --count {baseRef} ^HEAD failed (exit {revListResult.ExitCode}). " +
                $"stderr: {revListResult.StdErr.Trim()}");

        if (!int.TryParse(revListResult.StdOut.Trim(), out var behind))
            throw new InvalidOperationException(
                $"git rev-list returned non-numeric output: '{revListResult.StdOut.Trim()}'");

        return new StaleBranchReport(
            BaseRef: baseRef,
            CommitsBehind: behind,
            MaxAllowed: maxCommitsBehind,
            IsStale: behind > maxCommitsBehind,
            FetchPerformed: fetch);
    }
}
