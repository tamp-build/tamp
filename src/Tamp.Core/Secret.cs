namespace Tamp;

/// <summary>
/// A typed wrapper around a sensitive value (API key, password, token).
/// The type system makes accidental leaks harder: <see cref="ToString"/> never
/// returns the value, and the value is reachable only via <see cref="Reveal"/>
/// which is internal-only — visible to the runner's process-spawn path and to
/// tests, but not to wrappers, build scripts, or arbitrary library code.
/// </summary>
/// <remarks>
/// What this type prevents: accidental inclusion of the secret in log output,
/// dry-run output, error messages, and standard <see cref="object.ToString"/>
/// calls.
/// What it does NOT prevent: a child process exposing the secret in its
/// argument list to the OS process table while it runs (an OS-level concern),
/// or runtime crash dumps if the process is terminated abnormally. Tool
/// wrappers should prefer stdin / file-based secret passing where the wrapped
/// tool supports it (<c>docker login --password-stdin</c>, etc.).
/// </remarks>
public sealed class Secret
{
    private readonly string _value;

    public Secret(string name, string value)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Secret name must be non-empty.", nameof(name));
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Identifier for this secret. Safe to log; this is not the value.</summary>
    public string Name { get; }

    /// <summary>
    /// Returns the underlying secret value. Made <see langword="public"/> as of Tamp.Core 1.6.0
    /// (TAM-196) — the previous <see langword="internal"/>-with-IVT gate had become friction
    /// without protection. The real masking lives in <see cref="ToString"/>, the
    /// <see cref="CommandPlan.Secrets"/> collection (process-trace masking), and the runner's
    /// env-var masking — none of which depend on this method's visibility.
    /// </summary>
    /// <remarks>
    /// Call sites that are NOT building command-line arguments, env vars, or otherwise plumbing
    /// the value to a child process should be considered suspect. The <c>TAMP004</c> Roslyn
    /// analyzer (ships bundled in <c>Tamp.Core</c> as of 1.6.0) flags <c>Reveal()</c> calls
    /// outside of approved contexts (classes ending in <c>Settings</c> / <c>SettingsBase</c>,
    /// and Tamp framework internals).
    /// </remarks>
    public string Reveal() => _value;

    /// <summary>
    /// Produce a <b>derived secret</b> — a new <see cref="Secret"/> whose value is
    /// <paramref name="transform"/> applied to this one's — so a wrapper that transmits a secret in a
    /// <em>transformed</em> shape (a base64 basic-auth header, a signed URL, a hashed token) can
    /// register the transmitted literal too.
    /// </summary>
    /// <remarks>
    /// Redaction matches registered values <b>literally</b>: registering only the raw secret leaves a
    /// transformed form (e.g. <c>base64(":" + pat)</c>) unredacted, because it is a different string.
    /// Put both in <see cref="CommandPlan.Secrets"/> so either can be scrubbed:
    /// <code>Secrets = new[] { pat, pat.Derive(AdoGit.BuildAuthHeader) };</code>
    /// The transform runs inside the framework boundary, so the call site never has to
    /// <see cref="Reveal"/> the raw value itself (no TAMP004 suppression needed at the call site).
    /// </remarks>
    /// <param name="transform">Maps the raw value to its transmitted form. Must not return null.</param>
    /// <param name="name">Name for the derived secret; defaults to <c>"{Name}.derived"</c>.</param>
    public Secret Derive(Func<string, string> transform, string? name = null)
    {
        if (transform is null) throw new ArgumentNullException(nameof(transform));
        var derived = transform(_value) ?? throw new InvalidOperationException(
            $"Secret.Derive transform for '{Name}' returned null; a derived secret must have a value.");
        return new Secret(name ?? $"{Name}.derived", derived);
    }

    /// <summary>
    /// Always returns a redacted form. Critically, this is what gets called by
    /// <c>string.Format</c>, string interpolation, structured loggers, and most
    /// debugger displays — so secrets that "leak" to those surfaces will appear
    /// as <c>&lt;Secret:Name&gt;</c> rather than the value.
    /// </summary>
    public override string ToString() => $"<Secret:{Name}>";

    /// <inheritdoc/>
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    /// <inheritdoc/>
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
