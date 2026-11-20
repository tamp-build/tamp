using System.Text.Json.Serialization;

namespace Tamp.Sarif;

/// <summary>
/// SARIF 2.1.0 root object (§3.13). Every SARIF file deserialises to this.
/// </summary>
public sealed record SarifLog
{
    /// <summary>The canonical SARIF 2.1.0 schema URI every log Tamp writes carries.</summary>
    public const string CanonicalSchema = "https://json.schemastore.org/sarif-2.1.0.json";

    // A non-nullable property with an initializer is only honoured when the
    // serializer leaves absent JSON members alone. .NET 11's System.Text.Json
    // source generator does; every earlier one (net8/net9/net10) overwrites the
    // member with default(T) instead, so "version" went missing, "runs" came
    // back null, and an absent "level" read as None rather than Warning. Both
    // TFMs are supported, so the default is enforced in the accessor rather
    // than left to the runtime. See tamp#166.

    private readonly string _version = "2.1.0";
    private readonly IReadOnlyList<SarifRun> _runs = [];

    public string Version
    {
        get => _version;
        init => _version = value ?? "2.1.0";
    }

    /// <summary>
    /// SARIF 2.1.0 $schema. Deliberately has no default: a non-null initializer
    /// here is exactly what diverged between runtimes, because net10 and earlier
    /// overwrite it on deserialization and net11 does not. With no initializer,
    /// an absent $schema reads as <c>null</c> on every supported TFM, and
    /// <see cref="SarifWriter"/> supplies <see cref="CanonicalSchema"/> on the
    /// way out — so every log Tamp writes is schema-tagged without this property
    /// having to lie about what the input contained.
    /// </summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public IReadOnlyList<SarifRun> Runs
    {
        get => _runs;
        init => _runs = value ?? [];
    }
}

/// <summary>SARIF 2.1.0 run (§3.14). One per tool invocation.</summary>
public sealed record SarifRun
{
    private readonly SarifTool _tool = new();

    public SarifTool Tool
    {
        get => _tool;
        init => _tool = value ?? new();
    }
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
    private readonly SarifToolComponent _driver = new();

    public SarifToolComponent Driver
    {
        get => _driver;
        init => _driver = value ?? new();
    }
}

/// <summary>SARIF 2.1.0 toolComponent (§3.19). Describes the analyser.</summary>
public sealed record SarifToolComponent
{
    private readonly string _name = "";

    public string Name
    {
        get => _name;
        init => _name = value ?? "";
    }
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
    private readonly string _id = "";

    public string Id
    {
        get => _id;
        init => _id = value ?? "";
    }
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
    private readonly SarifLevel? _level;

    /// <summary>
    /// SARIF 2.1.0 reportingConfiguration.level (§3.50.3). An absent level means "defer to the rule's
    /// defaultConfiguration", which itself defaults to warning — so an absent
    /// level reads as <see cref="SarifLevel.Warning"/> and never as
    /// <see cref="SarifLevel.None"/>. Reading it as None would silently sort a
    /// finding below any "warning and above" gate (tamp#166).
    /// </summary>
    [JsonIgnore]
    public SarifLevel Level
    {
        get => _level ?? SarifLevel.Warning;
        init => _level = value;
    }

    /// <summary>
    /// The level exactly as it appears in JSON, or <c>null</c> when the document
    /// omits it. Serialized in place of <see cref="Level"/> so an absent level
    /// stays absent on round-trip and stays distinguishable from an explicit
    /// <c>"none"</c> — a distinction a non-nullable property cannot carry.
    /// Prefer <see cref="Level"/> in code.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("level")]
    public SarifLevel? LevelRaw
    {
        get => _level;
        init => _level = value;
    }
}

/// <summary>SARIF 2.1.0 result (§3.27). A single finding.</summary>
public sealed record SarifResult
{
    private readonly SarifMessage _message = new();

    public string? RuleId { get; init; }

    private readonly SarifLevel? _level;

    /// <summary>
    /// SARIF 2.1.0 result.level (§3.27.10). An absent level means "defer to the rule's
    /// defaultConfiguration", which itself defaults to warning — so an absent
    /// level reads as <see cref="SarifLevel.Warning"/> and never as
    /// <see cref="SarifLevel.None"/>. Reading it as None would silently sort a
    /// finding below any "warning and above" gate (tamp#166).
    /// </summary>
    [JsonIgnore]
    public SarifLevel Level
    {
        get => _level ?? SarifLevel.Warning;
        init => _level = value;
    }

    /// <summary>
    /// The level exactly as it appears in JSON, or <c>null</c> when the document
    /// omits it. Serialized in place of <see cref="Level"/> so an absent level
    /// stays absent on round-trip and stays distinguishable from an explicit
    /// <c>"none"</c> — a distinction a non-nullable property cannot carry.
    /// Prefer <see cref="Level"/> in code.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("level")]
    public SarifLevel? LevelRaw
    {
        get => _level;
        init => _level = value;
    }

    public SarifMessage Message
    {
        get => _message;
        init => _message = value ?? new();
    }
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
    private readonly SarifReportingDescriptorReference _target = new();

    public SarifReportingDescriptorReference Target
    {
        get => _target;
        init => _target = value ?? new();
    }
    public IReadOnlyList<string>? Kinds { get; init; }
}

/// <summary>SARIF 2.1.0 location (§3.28). Wraps a physical/logical location.</summary>
public sealed record SarifLocation
{
    public SarifPhysicalLocation? PhysicalLocation { get; init; }

    /// <summary>
    /// SARIF 2.1.0 location.logicalLocations (§3.28.4). Where a finding has no
    /// file to point at — a finding about an advertised MCP tool, a service
    /// endpoint, a named function with no source on disk — the logical location
    /// carries its identity instead of a synthesised file path. A consumer that
    /// needs cross-run finding identity keys on the <see cref="SarifLogicalLocation.FullyQualifiedName"/>
    /// here (which <see cref="SarifDedup"/> now folds into its key when present).
    /// </summary>
    public IReadOnlyList<SarifLogicalLocation>? LogicalLocations { get; init; }
}

/// <summary>
/// SARIF 2.1.0 logicalLocation (§3.33). Names a program element — a function,
/// a namespace, a service, an advertised tool — independent of any file.
/// </summary>
public sealed record SarifLogicalLocation
{
    /// <summary>The bare name of the element, e.g. the tool name "query_database".</summary>
    public string? Name { get; init; }

    /// <summary>
    /// The fully qualified name — the stable, unique identity of the element,
    /// e.g. "https://mcp.example.com/mcp/query_database". This is the field a
    /// consumer dedupes and correlates on.
    /// </summary>
    public string? FullyQualifiedName { get; init; }

    /// <summary>
    /// The kind of element, e.g. "function", "namespace", or a tool-specific
    /// kind such as "mcpTool" / "mcpPrompt" / "mcpResource".
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>A decorated (e.g. mangled) form of the name, when the tool has one.</summary>
    public string? DecoratedName { get; init; }

    /// <summary>Index into run.logicalLocations when the element is cached at the run level.</summary>
    public int? Index { get; init; }

    /// <summary>Index of the parent logical location in run.logicalLocations, for nesting.</summary>
    public int? ParentIndex { get; init; }
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
    private readonly string _uri = "";

    public string Uri
    {
        get => _uri;
        init => _uri = value ?? "";
    }
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
    private readonly string _text = "";

    public string Text
    {
        get => _text;
        init => _text = value ?? "";
    }
    public string? Markdown { get; init; }
}
