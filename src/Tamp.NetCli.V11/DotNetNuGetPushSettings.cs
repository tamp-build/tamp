namespace Tamp.NetCli.V11;

/// <summary>
/// <c>dotnet nuget push</c> settings. Supports authenticated pushes to
/// nuget.org or any custom feed; takes the API key as a <see cref="Secret"/>
/// so the value is registered with the runner's redaction table (any
/// log line that happens to echo it gets scrubbed automatically).
/// </summary>
/// <remarks>
/// <para>
/// The API key is briefly visible to the OS process table while the
/// child <c>dotnet</c> process runs — a standard OS-level limitation.
/// For CI publishes, prefer trusted publishing (OIDC-minted short-lived
/// keys) over long-lived API keys; from a build script that runs in
/// both contexts, the same wrapper handles both — just supply the key
/// via different means.
/// </para>
/// <para>
/// <see cref="SkipDuplicate"/> defaults off but is recommended for any
/// non-trivial pipeline: it makes the push idempotent (re-running on a
/// version that's already published is a no-op rather than a failure).
/// </para>
/// </remarks>
public sealed class DotNetNuGetPushSettings : DotNetSettingsBase
{
    /// <summary>Path to the <c>.nupkg</c> file or a glob (e.g., <c>artifacts/*.nupkg</c>).</summary>
    public string? PackagePath { get; set; }

    /// <summary>Push target. Defaults to nuget.org if unset.</summary>
    public string? Source { get; set; }

    /// <summary>API key for the target source. Pass as <see cref="Secret"/> so it's redacted in logs.</summary>
    public Secret? ApiKey { get; set; }

    /// <summary>Optional separate symbol source URL.</summary>
    public string? SymbolSource { get; set; }

    /// <summary>Optional separate API key for the symbol source.</summary>
    public Secret? SymbolApiKey { get; set; }

    /// <summary>Skip pushing the symbol package (.snupkg).</summary>
    public bool NoSymbols { get; set; }

    /// <summary>Treat already-published versions as success rather than failure. Recommended for CI re-runs.</summary>
    public bool SkipDuplicate { get; set; }

    /// <summary>Per-push timeout. Default is 5 minutes when unset.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Disable client-side buffering of the upload (helps for very large packages).</summary>
    public bool DisableBuffering { get; set; }

    /// <summary>Skip appending <c>api/v2/package</c> to the source URL — for feeds that take the URL as-is.</summary>
    public bool NoServiceEndpoint { get; set; }

    /// <summary>Force English output regardless of locale (for predictable CI log parsing).</summary>
    public bool ForceEnglishOutput { get; set; }

    public DotNetNuGetPushSettings SetPackagePath(string path) { PackagePath = path; return this; }
    public DotNetNuGetPushSettings SetSource(string? url) { Source = url; return this; }
    public DotNetNuGetPushSettings SetApiKey(Secret apiKey) { ApiKey = apiKey; return this; }
    public DotNetNuGetPushSettings SetSymbolSource(string? url) { SymbolSource = url; return this; }
    public DotNetNuGetPushSettings SetSymbolApiKey(Secret apiKey) { SymbolApiKey = apiKey; return this; }
    public DotNetNuGetPushSettings SetNoSymbols(bool v) { NoSymbols = v; return this; }
    public DotNetNuGetPushSettings SetSkipDuplicate(bool v) { SkipDuplicate = v; return this; }
    public DotNetNuGetPushSettings SetTimeout(TimeSpan? t) { Timeout = t; return this; }
    public DotNetNuGetPushSettings SetDisableBuffering(bool v) { DisableBuffering = v; return this; }
    public DotNetNuGetPushSettings SetNoServiceEndpoint(bool v) { NoServiceEndpoint = v; return this; }
    public DotNetNuGetPushSettings SetForceEnglishOutput(bool v) { ForceEnglishOutput = v; return this; }
    public DotNetNuGetPushSettings SetWorkingDirectory(string? cwd) { WorkingDirectory = cwd; return this; }

    protected override IEnumerable<string> BuildVerbArguments()
    {
        yield return "nuget";
        yield return "push";

        if (string.IsNullOrEmpty(PackagePath))
            throw new InvalidOperationException("DotNet.NuGetPush requires PackagePath.");
        yield return PackagePath!;

        if (!string.IsNullOrEmpty(Source)) { yield return "--source"; yield return Source!; }
        if (ApiKey is not null) { yield return "--api-key"; yield return ApiKey.Reveal(); }
        if (!string.IsNullOrEmpty(SymbolSource)) { yield return "--symbol-source"; yield return SymbolSource!; }
        if (SymbolApiKey is not null) { yield return "--symbol-api-key"; yield return SymbolApiKey.Reveal(); }
        if (NoSymbols) yield return "--no-symbols";
        if (SkipDuplicate) yield return "--skip-duplicate";
        if (Timeout is { } t)
        {
            yield return "--timeout";
            yield return ((int)t.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (DisableBuffering) yield return "--disable-buffering";
        if (NoServiceEndpoint) yield return "--no-service-endpoint";
        if (ForceEnglishOutput) yield return "--force-english-output";
    }

    protected override IReadOnlyList<Secret> BuildSecrets()
    {
        if (ApiKey is null && SymbolApiKey is null) return Array.Empty<Secret>();
        var list = new List<Secret>(2);
        if (ApiKey is not null) list.Add(ApiKey);
        if (SymbolApiKey is not null) list.Add(SymbolApiKey);
        return list;
    }
}
