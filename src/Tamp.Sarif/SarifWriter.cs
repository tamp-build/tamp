using System.Text.Json;

namespace Tamp.Sarif;

/// <summary>
/// Serialises SARIF 2.1.0 logs to JSON strings and files.
/// </summary>
public static class SarifWriter
{
    /// <summary>Serialise a SARIF log to a JSON string (indented, camelCase).</summary>
    /// <remarks>
    /// A log with no <c>$schema</c> is tagged with <see cref="SarifLog.CanonicalSchema"/> on the way
    /// out, so every SARIF file Tamp writes identifies the schema it conforms to. The tag is applied
    /// here rather than defaulted on the property because a property default is overwritten on
    /// deserialization by every runtime before net11 (tamp#166).
    /// </remarks>
    public static string Serialize(SarifLog log)
    {
        if (log is null) throw new ArgumentNullException(nameof(log));
        if (string.IsNullOrEmpty(log.Schema)) log = log with { Schema = SarifLog.CanonicalSchema };
        return JsonSerializer.Serialize(log, SarifJsonContext.Default.SarifLog);
    }

    /// <summary>Write a SARIF log to a file path. Overwrites if present.</summary>
    public static void WriteToFile(SarifLog log, AbsolutePath path)
    {
        if (log is null) throw new ArgumentNullException(nameof(log));
        if (path is null) throw new ArgumentNullException(nameof(path));
        File.WriteAllText(path, Serialize(log));
    }

    /// <summary>Write a SARIF log to a file path asynchronously. Overwrites if present.</summary>
    public static Task WriteToFileAsync(SarifLog log, AbsolutePath path, CancellationToken cancellationToken = default)
    {
        if (log is null) throw new ArgumentNullException(nameof(log));
        if (path is null) throw new ArgumentNullException(nameof(path));
        return File.WriteAllTextAsync(path, Serialize(log), cancellationToken);
    }
}
