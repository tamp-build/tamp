using System.Diagnostics;

namespace Tamp;

/// <summary>
/// Resolves the <see cref="BuildEvent.WorkerId"/> — who produced a build's events, an agent or a
/// human — so attribution is threaded by construction (ADR 0019, Pillar 5 / Invariant #4).
///
/// Precedence (#19), mirroring the <see cref="CiHost.Detect"/> layering:
/// <list type="number">
///   <item><c>TAMP_WORKER_ID</c> — explicit override, verbatim (agent ids like <c>agent:pool/3</c>).</item>
///   <item>CI actor — GitHub <c>GITHUB_ACTOR</c>, Azure <c>BUILD_REQUESTEDFOR</c> → <c>human:&lt;actor&gt;</c>.</item>
///   <item>Git author — <c>git config user.email</c> (fallback <c>user.name</c>) → <c>human:&lt;value&gt;</c>.</item>
///   <item>OS login → <c>human:&lt;login&gt;</c>, else <c>human:unknown</c>.</item>
/// </list>
/// </summary>
internal static class WorkerIdResolver
{
    private static string? _default;

    /// <summary>The resolved worker id for this process, computed once and cached (avoids re-shelling git per <see cref="Executor"/>).</summary>
    public static string ResolveDefault() => _default ??= Resolve();

    /// <summary>
    /// Resolve the worker id. All external inputs are injectable so the precedence is unit-testable
    /// without a real CI environment or git. Never cached — callers wanting the per-process value
    /// use <see cref="ResolveDefault"/>.
    /// </summary>
    public static string Resolve(
        Func<string, string?>? getEnv = null,
        Func<string?>? ciActor = null,
        Func<string?>? gitAuthor = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;

        // 1. Explicit override — verbatim (supports agent ids and any custom scheme).
        var explicitId = getEnv("TAMP_WORKER_ID");
        if (!string.IsNullOrWhiteSpace(explicitId)) return explicitId!.Trim();

        // 2. CI actor — the identity that triggered the CI run.
        var actor = (ciActor ?? (() => ResolveCiActor(getEnv)))();
        if (!string.IsNullOrWhiteSpace(actor)) return Human(actor!);

        // 3. Git author — the commit identity configured for this worktree.
        var author = (gitAuthor ?? ResolveGitAuthor)();
        if (!string.IsNullOrWhiteSpace(author)) return Human(author!);

        // 4. OS login (or unknown).
        var login = SafeUserName();
        return Human(string.IsNullOrWhiteSpace(login) ? "unknown" : login!);
    }

    private static string Human(string value) => $"human:{value.Trim()}";

    /// <summary>CI actor for the active vendor; null off-CI or when the vendor exposes no actor.</summary>
    internal static string? ResolveCiActor(Func<string, string?> getEnv)
        => CiHost.Detect(getEnv) switch
        {
            GitHubActionsHost gh => NullIfBlank(gh.Actor),
            AzureDevOpsHost az => NullIfBlank(az.RequestedFor),
            _ => null,
        };

    /// <summary>Best-effort git commit identity (email, else name) for the current worktree; null when git is absent/unconfigured.</summary>
    private static string? ResolveGitAuthor()
        => RunGit("config", "user.email") ?? RunGit("config", "user.name");

    private static string? RunGit(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var outText = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(2000)) { try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ } return null; }
            if (proc.ExitCode != 0) return null;
            return NullIfBlank(outText);
        }
        catch { return null; }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();

    private static string? SafeUserName()
    {
        try { return Environment.UserName; }
        catch { return null; }
    }
}
