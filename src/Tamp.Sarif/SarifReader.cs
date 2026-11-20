using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tamp.Sarif;

/// <summary>
/// Loads SARIF 2.1.0 logs from JSON strings and files.
/// </summary>
public static class SarifReader
{
    /// <summary>Parse a SARIF log from a JSON string.</summary>
    /// <exception cref="InvalidDataException">The JSON is not a valid SARIF 2.1.0 log.</exception>
    public static SarifLog Parse(string json)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));
        try
        {
            // Normalise before deserialising -- see Normalise for why this is not
            // done with property defaults on the 1.20.x line (tamp#166).
            var node = JsonNode.Parse(json);
            if (node is not JsonObject root) throw new InvalidDataException("SARIF payload deserialised to null.");
            Normalise(root);

            var log = root.Deserialize(SarifJsonContext.Default.SarifLog);
            if (log is null) throw new InvalidDataException("SARIF payload deserialised to null.");
            return log;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not a valid SARIF 2.1.0 log: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Write the SARIF-declared default into any member the document omits, so that
    /// deserialization never has to rely on a C# property initializer surviving.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A property initializer like <c>SarifLevel Level { get; init; } = SarifLevel.Warning</c> is
    /// only honoured if the deserializer leaves absent JSON members alone. .NET 11's
    /// System.Text.Json source generator does; <b>every earlier runtime overwrites the member with
    /// <c>default(T)</c></b>. <c>default(SarifLevel)</c> is <c>None</c>, so on net8/net9/net10 a
    /// result whose scanner legally omitted <c>level</c> was read as the <i>least</i> severe value
    /// and then written back into the evidence file as <c>"level": "none"</c> — a finding of unknown
    /// severity recorded as benign, below any "warning and above" gate.
    /// </para>
    /// <para>
    /// <b>Why this is implemented differently here than on main.</b> main (1.21.0+) enforces each
    /// default in the property accessor over a readonly backing field, with <c>[JsonIgnore]</c> on
    /// the resolved property and the raw nullable exposed under the wire name. That shape
    /// <b>cannot run on net8 or net9</b>: <c>[JsonIgnore]</c> on a property that has an <c>init</c>
    /// accessor makes the .NET 8/9 JSON source generator emit a parameterized-constructor
    /// deserialization path, which throws <c>ConstructorParameterIncompleteBinding</c> on every
    /// parse — the type becomes entirely undeserializable. Verified by isolation: the same records
    /// with plain nullable properties and no <c>[JsonIgnore]</c> parse fine on net8. Since the
    /// 1.20.x line exists precisely to serve net8/net9 consumers, the fix had to be re-implemented
    /// rather than cherry-picked.
    /// </para>
    /// <para>
    /// Normalising the document instead keeps the public API byte-identical to 1.20.0 — <c>Level</c>
    /// stays a settable <c>SarifLevel</c> named <c>level</c>, so code that does
    /// <c>new SarifResult { Level = ... }</c> still compiles. A patch release must not break that.
    /// </para>
    /// <para>
    /// <b>One behavioural difference from main.</b> main preserves the distinction between "absent"
    /// and "explicitly warning" across a round trip; this does not — an absent level is materialised
    /// as <c>"warning"</c> and written back that way. Per SARIF 2.1.0 §3.27.10 an absent level defers
    /// to the rule's <c>defaultConfiguration</c>, which itself defaults to <c>warning</c>, so the two
    /// are semantically equivalent. Writing <c>"warning"</c> is in any case strictly better than the
    /// <c>"none"</c> this line writes today.
    /// </para>
    /// </remarks>
    private static void Normalise(JsonObject root)
    {
        // SarifLog.Version — declared default "2.1.0".
        DefaultString(root, "version", "2.1.0");

        // SarifLog.Schema is deliberately NOT defaulted: it is declared nullable so SarifMerge can
        // tell "this log did not state a schema" from "this log stated one", and the writer stamps
        // the canonical value on the way out instead.

        // SarifLog.Runs — declared default []. Absent "runs" otherwise deserialises to null and
        // log.Runs[0] throws a NullReferenceException rather than an empty-collection error.
        if (root["runs"] is not JsonArray runs)
        {
            root["runs"] = new JsonArray();
            return;
        }

        foreach (var run in runs)
        {
            if (run is not JsonObject runObj) continue;

            // SarifRun.Tool / SarifTool.Driver / SarifToolComponent.Name — all declared non-null.
            NormaliseToolComponent(EnsureObject(EnsureObject(runObj, "tool"), "driver"));

            if (runObj["taxonomies"] is JsonArray taxonomies)
                foreach (var taxonomy in taxonomies)
                    if (taxonomy is JsonObject t) NormaliseToolComponent(t);

            if (runObj["results"] is JsonArray results)
                foreach (var result in results)
                    if (result is JsonObject r) NormaliseResult(r);
        }
    }

    private static void NormaliseToolComponent(JsonObject component)
    {
        DefaultString(component, "name", "");

        if (component["rules"] is not JsonArray rules) return;
        foreach (var rule in rules)
        {
            if (rule is not JsonObject ruleObj) continue;

            // SarifRule.Id — declared default "".
            DefaultString(ruleObj, "id", "");

            // SarifReportingConfiguration.Level — declared default Warning. This is the member
            // SARIF says an absent result-level defers TO, so leaving it None inverts the deferral.
            if (ruleObj["defaultConfiguration"] is JsonObject config) DefaultLevel(config);

            // SarifReportingDescriptorRelationship.Target — declared non-null.
            if (ruleObj["relationships"] is JsonArray relationships)
                foreach (var relationship in relationships)
                    if (relationship is JsonObject rel) EnsureObject(rel, "target");
        }
    }

    private static void NormaliseResult(JsonObject result)
    {
        // SarifResult.Level — declared default Warning. The headline of tamp#166.
        DefaultLevel(result);

        // SarifResult.Message / SarifMultiformatMessage.Text — declared non-null.
        DefaultString(EnsureObject(result, "message"), "text", "");

        if (result["locations"] is not JsonArray locations) return;
        foreach (var location in locations)
        {
            // SarifArtifactLocation.Uri — declared default "".
            if (location is JsonObject loc
                && loc["physicalLocation"]?["artifactLocation"] is JsonObject artifact)
                DefaultString(artifact, "uri", "");
        }
    }

    private static JsonObject EnsureObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    // Only an ABSENT member is filled, never one the document states. An explicit
    // "level": "none" is a deliberate statement by the scanner and is left exactly as written.
    private static void DefaultLevel(JsonObject obj)
    {
        if (!obj.ContainsKey("level")) obj["level"] = JsonValue.Create("warning");
    }

    private static void DefaultString(JsonObject obj, string key, string value)
    {
        if (!obj.ContainsKey(key)) obj[key] = JsonValue.Create(value);
    }

    /// <summary>Load a SARIF log from a file path.</summary>
    public static SarifLog LoadFromFile(AbsolutePath path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Load a SARIF log from a file path asynchronously.</summary>
    public static async Task<SarifLog> LoadFromFileAsync(AbsolutePath path, CancellationToken cancellationToken = default)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }
}
