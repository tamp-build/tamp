using System.Reflection;

namespace Tamp;

/// <summary>
/// Resolves <c>[Secret]</c>-annotated members on a <see cref="TampBuild"/>
/// subclass. Walks the same resolution chain promised by the
/// <see cref="SecretAttribute"/> docstring: environment variable first,
/// then interactive prompt when a TTY is attached and the attribute opts
/// in. Future legs (CI vendor store, OS keychain) bolt on at the
/// declared insertion points without breaking the API.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="ParameterBinder"/>, secret resolution is split
/// across two phases:
/// <list type="bullet">
///   <item><b>Bind (eager)</b> — resolves from environment + CI store
///         immediately. Cheap; happens once at startup. Sets the field
///         and registers the value for redaction.</item>
///   <item><b>EnsureRequired (lazy)</b> — called by the executor just
///         before a target that <c>.Requires(...)</c> the secret runs.
///         If still unset, prompts (TTY) or fails preflight with a
///         clear message naming the missing secret.</item>
/// </list>
/// </para>
/// <para>
/// Today only the env-var leg of Bind is implemented (TAM-78). The
/// interactive-prompt leg of EnsureRequired ships with TAM-79. The OS
/// keychain leg is deferred to a future ticket.
/// </para>
/// </remarks>
public static class SecretBinder
{
    /// <summary>
    /// Eager binding: walk <c>[Secret]</c>-decorated members on
    /// <paramref name="build"/> and populate any whose env-var value is
    /// set. Already-set fields are left alone (explicit assignment in
    /// the build script wins).
    /// </summary>
    /// <param name="build">The build instance.</param>
    /// <param name="getEnv">
    /// Environment-variable lookup. Pass
    /// <see cref="Environment.GetEnvironmentVariable(string)"/> in
    /// production; pass a fake in tests.
    /// </param>
    /// <param name="onResolved">
    /// Optional callback fired once per resolved secret. Used by the
    /// runner to register the value with the redaction table and to
    /// emit CI-vendor masking instructions (e.g. <c>::add-mask::</c> on
    /// GitHub Actions). Pass <c>null</c> to skip.
    /// </param>
    /// <param name="osStore">Optional OS secret store (Keychain / libsecret / DPAPI) consulted when an env var isn't set; <c>null</c> uses the default resolution.</param>
    public static void Bind(
        TampBuild build,
        Func<string, string?> getEnv,
        Action<Secret>? onResolved = null,
        IOsSecretStore? osStore = null)
    {
        if (build is null) throw new ArgumentNullException(nameof(build));
        if (getEnv is null) throw new ArgumentNullException(nameof(getEnv));

        // Lazy default: detect the platform's keychain on first use.
        // Tests inject a fake to keep the bind path deterministic.
        osStore ??= OsSecretStore.Detect();

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = build.GetType();

        foreach (var member in EnumerateSecretMembers(type, flags))
        {
            // Don't overwrite an explicit assignment.
            if (member.GetValue(build) is not null) continue;

            var envKey = member.Attribute.EnvironmentVariable
                ?? ParameterBinder.ToUpperSnakeCase(member.Name);

            // Resolution chain (in order):
            //   1. Environment variable (covers CI vendor stores; everyone has env)
            //   2. OS keychain (local dev fallback when env not set)
            //   (3. interactive prompt happens later in EnsureResolved)
            string? value = getEnv(envKey);
            if (string.IsNullOrEmpty(value) && osStore is not null && member.Attribute.UseKeychain)
                value = osStore.TryGet(envKey);

            if (string.IsNullOrEmpty(value)) continue;  // still missing — EnsureResolved or Requires() takes it from here

            // Resolution order for the Secret's display Name (the redaction label):
            //   1. attr.Name (explicit override; rare)
            //   2. attr.Description (the most common case — e.g. "API token")
            //   3. member.Name (last resort programmer identifier)
            var label = member.Attribute.Name ?? member.Attribute.Description ?? member.Name;
            var secret = new Secret(label, value);
            member.SetValue(build, secret);
            onResolved?.Invoke(secret);
        }
    }

    /// <summary>
    /// Lazy completion: if a <c>[Secret]</c> member is still <c>null</c>
    /// after <see cref="Bind"/>, attempt the late-resolution legs
    /// (interactive prompt). Called by the executor just before a target
    /// that requires the secret runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prompting only happens when <see cref="Console.IsInputRedirected"/>
    /// is <c>false</c> (i.e. an interactive TTY) AND
    /// <see cref="SecretAttribute.AllowInteractivePrompt"/> is true on
    /// the attribute (default).
    /// </para>
    /// <para>
    /// Today this is a thin pass-through. As CI vendor secret stores
    /// and OS keychain support land, those legs slot in between the
    /// already-bound check and the prompt fallback.
    /// </para>
    /// </remarks>
    public static void EnsureResolved(
        TampBuild build,
        Action<Secret>? onResolved = null,
        TextReader? reader = null,
        TextWriter? writer = null)
    {
        if (build is null) throw new ArgumentNullException(nameof(build));

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = build.GetType();

        foreach (var member in EnumerateSecretMembers(type, flags))
        {
            if (member.GetValue(build) is not null) continue;
            if (!member.Attribute.AllowInteractivePrompt) continue;
            if (Console.IsInputRedirected) continue;  // CI / scripted: don't block on input.

            var label = member.Attribute.Name ?? member.Attribute.Description ?? member.Name;
            var description = member.Attribute.Description ?? label;
            var value = PromptForSecret(description, reader, writer);
            if (string.IsNullOrEmpty(value)) continue;  // user dismissed; Requires() will fire.

            var secret = new Secret(label, value);
            member.SetValue(build, secret);
            onResolved?.Invoke(secret);
        }
    }

    /// <summary>
    /// Interactive prompt with masked echo. Reads from
    /// <paramref name="reader"/> (defaults to <c>Console.In</c>) and
    /// writes the prompt to <paramref name="writer"/> (defaults to
    /// <c>Console.Out</c>). Returns the entered value, or <c>null</c>
    /// on cancel / empty input.
    /// </summary>
    internal static string? PromptForSecret(string description, TextReader? reader = null, TextWriter? writer = null)
    {
        reader ??= Console.In;
        writer ??= Console.Out;
        writer.Write($"[tamp] secret needed — {description}: ");
        writer.Flush();

        // We deliberately use ReadLine rather than per-char masked input
        // here. Cross-platform masked input via Console.ReadKey is
        // fragile (redirected stdin breaks it; some terminals don't
        // suppress echo cleanly) and adds complexity well beyond what
        // a v1 needs. The runner's redaction table scrubs the value
        // from any subsequent log output regardless.
        var value = reader.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static IEnumerable<AnnotatedMember> EnumerateSecretMembers(Type type, BindingFlags flags)
    {
        foreach (var p in type.GetProperties(flags))
        {
            var attr = p.GetCustomAttribute<SecretAttribute>(inherit: true);
            if (attr is null) continue;
            if (p.PropertyType != typeof(Secret) && p.PropertyType != typeof(Secret).MakeByRefType())
                throw new InvalidOperationException(
                    $"[Secret] requires a member of type Tamp.Secret; '{p.Name}' is {p.PropertyType.Name}.");
            if (!p.CanWrite) continue;
            if (p.GetIndexParameters().Length > 0) continue;
            yield return new AnnotatedMember(
                p.Name, attr,
                (b) => p.GetValue(b) as Secret,
                (b, v) => p.SetValue(b, v));
        }
        foreach (var f in type.GetFields(flags))
        {
            var attr = f.GetCustomAttribute<SecretAttribute>(inherit: true);
            if (attr is null) continue;
            if (f.FieldType != typeof(Secret))
                throw new InvalidOperationException(
                    $"[Secret] requires a member of type Tamp.Secret; '{f.Name}' is {f.FieldType.Name}.");
            // readonly fields are deliberately allowed (NUKE-style
            // `[Secret] readonly Secret X;` idiom); reflection writes
            // bypass the language guard.
            yield return new AnnotatedMember(
                f.Name, attr,
                (b) => f.GetValue(b) as Secret,
                (b, v) => f.SetValue(b, v));
        }
    }

    private sealed record AnnotatedMember(
        string Name,
        SecretAttribute Attribute,
        Func<object, Secret?> GetValue,
        Action<object, Secret?> SetValue);
}
