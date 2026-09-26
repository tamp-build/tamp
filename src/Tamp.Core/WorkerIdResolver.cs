namespace Tamp;

/// <summary>
/// Resolves the <see cref="BuildEvent.WorkerId"/> — who produced a build's events,
/// an agent or a human. `#0a` ships the field plus this minimal resolver
/// (<c>TAMP_WORKER_ID</c> → <c>human:&lt;login&gt;</c>); the full precedence order
/// (env → CI actor → git author) lands with attribution work in #19.
/// </summary>
internal static class WorkerIdResolver
{
    public static string Resolve(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var explicitId = getEnv("TAMP_WORKER_ID");
        if (!string.IsNullOrWhiteSpace(explicitId)) return explicitId!.Trim();

        var login = SafeUserName();
        return $"human:{(string.IsNullOrWhiteSpace(login) ? "unknown" : login)}";
    }

    private static string? SafeUserName()
    {
        try { return Environment.UserName; }
        catch { return null; }
    }
}
