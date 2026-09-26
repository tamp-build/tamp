namespace Tamp;

/// <summary>
/// What a tool wrapper produces, what the runner dispatches.
/// A plan describes everything the runner needs to execute (or print, in dry-run)
/// without further consultation with the wrapper.
/// </summary>
public sealed record CommandPlan
{
    public required string Executable { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = EmptyEnvironment;
    public string? WorkingDirectory { get; init; }
    public IReadOnlyList<Secret> Secrets { get; init; } = Array.Empty<Secret>();

    /// <summary>
    /// The capability tier this command requires (#20). Wrappers stamp
    /// <see cref="CapabilityTier.SideEffectful"/> for external mutation (registry push,
    /// deploy, …) or <see cref="CapabilityTier.Grey"/> for a worktree-scoped resource;
    /// the default is <see cref="CapabilityTier.Safe"/>. A plan that declares
    /// <see cref="Secrets"/> is treated as side-effectful regardless (secret reveal).
    /// </summary>
    public CapabilityTier RequiredCapability { get; init; } = CapabilityTier.Safe;

    /// <summary>
    /// Optional content fed to the child process's standard input. Used by
    /// wrappers whose underlying tool accepts a sensitive value via stdin
    /// rather than as a command-line argument (<c>docker login
    /// --password-stdin</c>, <c>gh auth login --with-token</c>, etc.) — this
    /// keeps the value out of the OS process table for the lifetime of the
    /// child process. The runner pipes this string verbatim and closes
    /// stdin afterwards.
    /// </summary>
    public string? StandardInput { get; init; }

    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment
        = new Dictionary<string, string>(0);
}
