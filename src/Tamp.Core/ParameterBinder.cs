using System.Globalization;
using System.Reflection;
using System.Text;

namespace Tamp;

/// <summary>
/// Resolves <c>[Parameter]</c>-annotated properties on a build instance from
/// command-line arguments, environment variables, and property defaults — in
/// that order of precedence.
/// </summary>
/// <remarks>
/// Secret resolution is intentionally NOT in this binder. Secrets resolve
/// lazily (only when a target that needs them is about to run) and through
/// a separate path that includes CI/local secret stores. See ADR 0005
/// (deferred) for the eventual contract.
/// </remarks>
public static class ParameterBinder
{
    /// <summary>
    /// Bind every <c>[Parameter]</c>-decorated member on <paramref name="build"/>.
    /// </summary>
    /// <param name="build">The build instance whose parameters to populate.</param>
    /// <param name="args">Command-line arguments, e.g., from <c>Main</c>.</param>
    /// <param name="getEnv">
    /// Environment-variable lookup. Pass <see cref="Environment.GetEnvironmentVariable(string)"/>
    /// in production; pass a fake in tests.
    /// </param>
    /// <param name="tolerateInjectionFailures">
    /// When <c>true</c>, per-member exceptions raised by <see cref="ValueInjectionAttribute"/>
    /// resolution are swallowed silently and the member retains its declared default.
    /// Used during <c>--list</c> / <c>--list-tree</c> invocations where target
    /// introspection should not fail because a tool isn't yet on PATH
    /// (HoldFast friction #20 — TAM-209). Default <c>false</c> preserves
    /// the historic fail-fast behavior for normal builds.
    /// </param>
    public static void Bind(
        TampBuild build,
        string[] args,
        Func<string, string?> getEnv,
        bool tolerateInjectionFailures = false)
    {
        if (build is null) throw new ArgumentNullException(nameof(build));
        if (args is null) throw new ArgumentNullException(nameof(args));
        if (getEnv is null) throw new ArgumentNullException(nameof(getEnv));

        var cliValues = ParseCli(args);

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = build.GetType();

        // Phase 1: ValueInjection attributes (Solution, GitRepository, future
        // codegen-style auto-loaders). These run before [Parameter] binding
        // so [Parameter] resolution can read injected values if it ever
        // needs to (none today, but the ordering is intentional).
        BindInjectedValues(build, type, flags, tolerateInjectionFailures);

        foreach (var member in EnumerateAnnotatedMembers(type, flags))
        {
            if (member.Attribute is null) continue;
            var attr = member.Attribute;
            var memberType = member.MemberType;
            var defaultName = ToKebabCase(member.Name);
            var cliKey = attr.Name ?? defaultName;
            var envKey = attr.EnvironmentVariable ?? ToUpperSnakeCase(member.Name);

            string? rawValue = null;
            if (cliValues.TryGetValue(cliKey, out var cliVal)) rawValue = cliVal;
            else
            {
                var fromEnv = getEnv(envKey);
                if (!string.IsNullOrEmpty(fromEnv)) rawValue = fromEnv;
            }

            if (rawValue is null) continue;  // No CLI / env value; keep the property's default.

            object? converted;
            try
            {
                converted = Convert(rawValue, memberType);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to bind parameter '{member.Name}' (type {memberType.Name}) from value '{rawValue}': {ex.Message}",
                    ex);
            }

            member.SetValue(build, converted);
        }
    }

    private static void BindInjectedValues(TampBuild build, Type type, BindingFlags flags, bool tolerateFailures)
    {
        foreach (var p in type.GetProperties(flags))
        {
            var attr = p.GetCustomAttribute<ValueInjectionAttribute>(inherit: true);
            if (attr is null) continue;
            if (!p.CanWrite) continue;
            if (p.GetIndexParameters().Length > 0) continue;
            try
            {
                var value = attr.GetValue(p, p.PropertyType);
                p.SetValue(build, value);
            }
            catch (Exception ex)
            {
                if (tolerateFailures) continue;
                throw new InvalidOperationException(
                    $"Failed to inject value into '{p.Name}' via [{attr.GetType().Name}]: {ex.Message}",
                    ex);
            }
        }
        foreach (var f in type.GetFields(flags))
        {
            var attr = f.GetCustomAttribute<ValueInjectionAttribute>(inherit: true);
            if (attr is null) continue;
            // readonly fields are deliberately allowed: reflection can still
            // write them, and the NUKE-style `readonly Solution Solution;`
            // idiom is what most build scripts use.
            try
            {
                var value = attr.GetValue(f, f.FieldType);
                f.SetValue(build, value);
            }
            catch (Exception ex)
            {
                if (tolerateFailures) continue;
                throw new InvalidOperationException(
                    $"Failed to inject value into '{f.Name}' via [{attr.GetType().Name}]: {ex.Message}",
                    ex);
            }
        }
    }

    /// <summary>
    /// The set of CLI keys (kebab-case, without the <c>--</c>) that bind a declared
    /// <c>[Parameter]</c> member on <paramref name="buildType"/> — an explicit
    /// <c>[Parameter(Name = "…")]</c> wins, else the member name kebab-cased. Used by the
    /// invocation parser to tell a real <c>--param</c> from a typo so unknown flags can
    /// fail closed instead of being silently ignored (#70).
    /// </summary>
    internal static IReadOnlySet<string> CliKeys(Type buildType)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in EnumerateAnnotatedMembers(buildType, flags))
            keys.Add(m.Attribute.Name ?? ToKebabCase(m.Name));
        return keys;
    }

    private static IEnumerable<AnnotatedMember> EnumerateAnnotatedMembers(Type type, BindingFlags flags)
    {
        foreach (var p in type.GetProperties(flags))
        {
            var attr = p.GetCustomAttribute<ParameterAttribute>(inherit: true);
            if (attr is null) continue;
            if (!p.CanWrite) continue;
            if (p.GetIndexParameters().Length > 0) continue;
            yield return new AnnotatedMember(p.Name, p.PropertyType, attr,
                (instance, value) => p.SetValue(instance, value));
        }
        foreach (var f in type.GetFields(flags))
        {
            var attr = f.GetCustomAttribute<ParameterAttribute>(inherit: true);
            if (attr is null) continue;
            // readonly fields are deliberately allowed — reflection can still
            // write them. Match the NUKE-style `[Parameter] readonly string X;`
            // idiom that build scripts conventionally use.
            yield return new AnnotatedMember(f.Name, f.FieldType, attr,
                (instance, value) => f.SetValue(instance, value));
        }
    }

    /// <summary>
    /// Parse <paramref name="args"/> into a flag→value map. Supports both
    /// <c>--name value</c> and <c>--name=value</c> forms; bare flags
    /// (<c>--quiet</c>) become <c>name=true</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseCli(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var tok = args[i];
            if (!tok.StartsWith("--", StringComparison.Ordinal)) continue;

            var rest = tok[2..];
            if (rest.Length == 0) continue;

            var eq = rest.IndexOf('=');
            if (eq >= 0)
            {
                var k = rest[..eq];
                var v = rest[(eq + 1)..];
                if (k.Length > 0) result[k] = v;
                continue;
            }

            // Look ahead for a value; if next token is another flag or
            // missing, treat current as a boolean flag.
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[rest] = args[i + 1];
                i++;
            }
            else
            {
                result[rest] = "true";
            }
        }
        return result;
    }

    /// <summary>Converts a string from CLI/env into the member's declared type.</summary>
    internal static object? Convert(string raw, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying == typeof(string)) return raw;

        if (underlying == typeof(bool))
        {
            if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;
            if (raw == "1") return true;
            if (raw == "0") return false;
            throw new FormatException($"Cannot interpret '{raw}' as a boolean.");
        }

        if (underlying.IsEnum)
        {
            return Enum.Parse(underlying, raw, ignoreCase: true);
        }

        // Common scalar types via Convert.ChangeType (IConvertible).
        if (typeof(IConvertible).IsAssignableFrom(underlying))
        {
            return System.Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException(
            $"No conversion from string to {underlying.FullName} is registered.");
    }

    /// <summary>Convert PascalCase or camelCase to kebab-case (e.g., MyValue → my-value).</summary>
    public static string ToKebabCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>Convert PascalCase to UPPER_SNAKE_CASE (e.g., MyValue → MY_VALUE).</summary>
    public static string ToUpperSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    sb.Append('_');
                sb.Append(c);
            }
            else
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }
        return sb.ToString();
    }

    private sealed record AnnotatedMember(
        string Name,
        Type MemberType,
        ParameterAttribute Attribute,
        Action<object, object?> SetValue);
}
