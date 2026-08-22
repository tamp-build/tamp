using System.Text.Json.Serialization;

namespace Tamp.Sarif;

/// <summary>
/// SARIF 2.1.0 root object (§3.13). Every SARIF file deserialises to this.
/// </summary>
public sealed record SarifLog
{
    public string Version { get; init; } = "2.1.0";

    [JsonPropertyName("$schema")]
    public string? Schema { get; init; } = "https://json.schemastore.org/sarif-2.1.0.json";

    public IReadOnlyList<SarifRun> Runs { get; init; } = [];
}

/// <summary>SARIF 2.1.0 run (§3.14). One per tool invocation.</summary>
public sealed record SarifRun
{
    public SarifTool Tool { get; init; } = new();
    public IReadOnlyList<SarifResult>? Results { get; init; }

    /// <summary>
    /// SARIF 2.1.0 run.taxonomies. Standard taxonomies the run's rules
    /// classify against — CWE being the one that matters for security
    /// evidence. Each entry is a toolComponent whose Taxa holds the
    /// individual taxon definitions (e.g. CWE-79).
    /// </summary>
    public IReadOnlyList<SarifToolComponent>? Taxonomies { get; init; }
}

/// <summary>SARIF 2.1.0 tool (§3.18). Wraps the driver component.</summary>
public sealed record SarifTool
{
    public SarifToolComponent Driver { get; init; } = new();
}

/// <summary>SARIF 2.1.0 toolComponent (§3.19). Describes the analyser.</summary>
public sealed record SarifToolComponent
{
    public string Name { get; init; } = "";
    public string? Version { get; init; }
    public string? SemanticVersion { get; init; }
    public string? InformationUri { get; init; }
    public IReadOnlyList<SarifRule>? Rules { get; init; }

    /// <summary>
    /// SARIF 2.1.0 toolComponent.taxa. Taxon definitions when this component
    /// is a taxonomy (see SarifRun.Taxonomies).
    /// </summary>
    public IReadOnlyList<SarifRule>? Taxa { get; init; }

    /// <summary>Stable id for cross-references from a reportingDescriptorReference.</summary>
    public string? Guid { get; init; }
}

/// <summary>SARIF 2.1.0 reportingDescriptor (§3.49). Rule metadata.</summary>
public sealed record SarifRule
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    public SarifMultiformatMessage? ShortDescription { get; init; }
    public SarifMultiformatMessage? FullDescription { get; init; }
    public string? HelpUri { get; init; }
    public SarifReportingConfiguration? DefaultConfiguration { get; init; }

    /// <summary>
    /// SARIF 2.1.0 reportingDescriptor.properties. Carries the tags array
    /// scanners use to sub-classify a rule — Trivy folds vulnerability /
    /// misconfiguration / secret findings under one tool name and
    /// distinguishes them here.
    /// </summary>
    public SarifPropertyBag? Properties { get; init; }

    /// <summary>
    /// SARIF 2.1.0 reportingDescriptor.relationships. Links this rule to taxa
    /// in SarifRun.Taxonomies — how a scanner declares "this rule detects
    /// CWE-79".
    /// </summary>
    public IReadOnlyList<SarifReportingDescriptorRelationship>? Relationships { get; init; }

    /// <summary>Stable id targeted by a reportingDescriptorReference.</summary>
    public string? Guid { get; init; }
}

/// <summary>SARIF 2.1.0 reportingConfiguration (§3.50). Per-rule defaults.</summary>
public sealed record SarifReportingConfiguration
{
    public SarifLevel Level { get; init; } = SarifLevel.Warning;
}

/// <summary>SARIF 2.1.0 result (§3.27). A single finding.</summary>
public sealed record SarifResult
{
    public string? RuleId { get; init; }
    public SarifLevel Level { get; init; } = SarifLevel.Warning;
    public SarifMessage Message { get; init; } = new();
    public IReadOnlyList<SarifLocation>? Locations { get; init; }

    /// <summary>
    /// SARIF 2.1.0 result.properties. Free-form per-finding data; dynamic
    /// scanners put the attack payload and a confidence rating here.
    /// </summary>
    public SarifPropertyBag? Properties { get; init; }

    /// <summary>
    /// SARIF 2.1.0 result.webRequest. The HTTP request that produced this
    /// finding — the single most actionable field a dynamic scanner emits,
    /// and absent from static analysis output.
    /// </summary>
    public SarifWebRequest? WebRequest { get; init; }

    /// <summary>SARIF 2.1.0 result.webResponse. The response that evidenced the finding.</summary>
    public SarifWebResponse? WebResponse { get; init; }

    /// <summary>
    /// SARIF 2.1.0 result.partialFingerprints. Scanner-supplied stable
    /// identity components, intended to survive edits that move code.
    /// Consumers needing cross-run finding identity should prefer these over
    /// reconstructing one from path + snippet.
    /// </summary>
    public IReadOnlyDictionary<string, string>? PartialFingerprints { get; init; }

    /// <summary>SARIF 2.1.0 result.fingerprints. Fully stable identity, when the tool can supply one.</summary>
    public IReadOnlyDictionary<string, string>? Fingerprints { get; init; }

    /// <summary>
    /// SARIF 2.1.0 result.taxa. Direct taxonomy references for this finding —
    /// a shorter path to a CWE than walking rule relationships.
    /// </summary>
    public IReadOnlyList<SarifReportingDescriptorReference>? Taxa { get; init; }
}

/// <summary>
/// SARIF 2.1.0 propertyBag. <c>tags</c> is called out because it is the one
/// well-known member; everything else a tool writes is preserved verbatim in
/// <see cref="AdditionalProperties"/> rather than dropped on a round trip.
/// </summary>
public sealed record SarifPropertyBag
{
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// Every other member of the bag, kept as raw JSON. This is what stops
    /// tool-specific payloads — a dynamic scanner's attack string, a
    /// confidence score — from being silently lost.
    /// </summary>
    /// <remarks>
    /// <c>set</c> rather than <c>init</c>, unlike every other member in this
    /// file: System.Text.Json binds init-only members through the record's
    /// deserialisation constructor, and an extension-data property cannot bind
    /// to a constructor parameter — it throws
    /// <c>ExtensionDataCannotBindToCtorParam</c> at serializer-configuration
    /// time, which takes down parsing of the whole log, not just property
    /// bags. Leave this settable.
    /// </remarks>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>SARIF 2.1.0 webRequest.</summary>
public sealed record SarifWebRequest
{
    public string? Protocol { get; init; }
    public string? Version { get; init; }
    public string? Target { get; init; }
    public string? Method { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
    public SarifArtifactContent? Body { get; init; }
}

/// <summary>SARIF 2.1.0 webResponse.</summary>
public sealed record SarifWebResponse
{
    public string? Protocol { get; init; }
    public string? Version { get; init; }
    public int? StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public SarifArtifactContent? Body { get; init; }
    public bool? NoResponseReceived { get; init; }
}

/// <summary>
/// SARIF 2.1.0 reportingDescriptorReference. Points at a rule or taxon by id
/// or guid, optionally naming the component it lives in.
/// </summary>
public sealed record SarifReportingDescriptorReference
{
    public string? Id { get; init; }
    public string? Guid { get; init; }
    public int? Index { get; init; }
    public SarifToolComponentReference? ToolComponent { get; init; }
}

/// <summary>SARIF 2.1.0 toolComponentReference.</summary>
public sealed record SarifToolComponentReference
{
    public string? Name { get; init; }
    public string? Guid { get; init; }
    public int? Index { get; init; }
}

/// <summary>
/// SARIF 2.1.0 reportingDescriptorRelationship. Kinds is typically
/// ["superset"] or ["relevant"] when a rule maps to a CWE taxon.
/// </summary>
public sealed record SarifReportingDescriptorRelationship
{
    public SarifReportingDescriptorReference Target { get; init; } = new();
    public IReadOnlyList<string>? Kinds { get; init; }
}

/// <summary>SARIF 2.1.0 location (§3.28). Wraps a physical/logical location.</summary>
public sealed record SarifLocation
{
    public SarifPhysicalLocation? PhysicalLocation { get; init; }
}

/// <summary>SARIF 2.1.0 physicalLocation (§3.29). File + region.</summary>
public sealed record SarifPhysicalLocation
{
    public SarifArtifactLocation? ArtifactLocation { get; init; }
    public SarifRegion? Region { get; init; }
}

/// <summary>SARIF 2.1.0 artifactLocation (§3.4). Points at a file (URI-form).</summary>
public sealed record SarifArtifactLocation
{
    public string Uri { get; init; } = "";
    public string? UriBaseId { get; init; }
}

/// <summary>SARIF 2.1.0 region (§3.30). Line/column span within an artifact.</summary>
public sealed record SarifRegion
{
    public int? StartLine { get; init; }
    public int? StartColumn { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }

    /// <summary>
    /// SARIF 2.1.0 region.snippet. The source (or response) text at this
    /// region. Its absence is why finding identity has had to be
    /// reconstructed from path + line, which is brittle across edits.
    /// </summary>
    public SarifArtifactContent? Snippet { get; init; }

    /// <summary>Byte offset of the region — dynamic scanners use this to point into a response body.</summary>
    public int? ByteOffset { get; init; }

    /// <summary>Byte length of the region.</summary>
    public int? ByteLength { get; init; }
}

/// <summary>SARIF 2.1.0 artifactContent. Text, base64 binary, or a rendered form.</summary>
public sealed record SarifArtifactContent
{
    public string? Text { get; init; }
    public string? Binary { get; init; }
    public SarifMultiformatMessage? Rendered { get; init; }
}

/// <summary>SARIF 2.1.0 message (§3.11). Either plain text or markdown.</summary>
public sealed record SarifMessage
{
    public string? Text { get; init; }
    public string? Markdown { get; init; }
}

/// <summary>SARIF 2.1.0 multiformatMessageString (§3.12). Text required, markdown optional.</summary>
public sealed record SarifMultiformatMessage
{
    public string Text { get; init; } = "";
    public string? Markdown { get; init; }
}
