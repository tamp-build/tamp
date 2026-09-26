using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamp;

/// <summary>
/// Source-generated JSON contract for the canonical <see cref="BuildEvent"/> stream
/// (`#0a`, ADR 0019). AOT-friendly, zero new dependencies. Wire conventions —
/// camelCase, UTC <c>Z</c> timestamps, drop-nulls — deliberately match the
/// <c>tamp-ingest-v1</c> style so the whole ecosystem reads one JSON shape. The
/// sink that writes this to <c>TAMP_EVENTS</c> / fd is #9; this ticket lands the
/// serializer so the shape is pinned.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BuildEvent))]
public partial class BuildEventJsonContext : JsonSerializerContext
{
}

/// <summary>Helpers for rendering a <see cref="BuildEvent"/> as one NDJSON line.</summary>
public static class BuildEventJson
{
    /// <summary>Serialize a single event to its compact JSON form (no trailing newline).</summary>
    public static string Serialize(BuildEvent e)
        => JsonSerializer.Serialize(e, BuildEventJsonContext.Default.BuildEvent);

    /// <summary>Parse a single NDJSON line back into a <see cref="BuildEvent"/>.</summary>
    public static BuildEvent? Deserialize(string json)
        => JsonSerializer.Deserialize(json, BuildEventJsonContext.Default.BuildEvent);
}
