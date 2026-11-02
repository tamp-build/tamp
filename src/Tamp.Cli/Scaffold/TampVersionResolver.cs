using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Tamp.Cli.Scaffold;

/// <summary>
/// Lists the published versions of <c>Tamp.Core</c> (#119). Injectable so the
/// resolver is unit-testable without a network.
/// </summary>
internal interface ITampVersionFeed
{
    /// <summary>Human-readable feed location, used in <c>tamp init</c> output.</summary>
    string DisplayName { get; }

    /// <summary>Every published version string, in any order. Throws on failure.</summary>
    Task<IReadOnlyList<string>> GetVersionsAsync(CancellationToken ct);
}

/// <summary>
/// Reads <c>{base}/tamp.core/index.json</c> from a NuGet v3 flat-container
/// endpoint (nuget.org by default). The base URL can be redirected with the
/// <see cref="FeedUrlEnvironmentVariable"/> environment variable (a mirror, an
/// internal proxy, or an unroutable address to exercise the offline fallback).
/// </summary>
internal sealed class NuGetFlatContainerVersionFeed : ITampVersionFeed
{
    public const string DefaultBaseUrl = "https://api.nuget.org/v3-flatcontainer/";
    public const string FeedUrlEnvironmentVariable = "TAMP_INIT_FEED_URL";

    private readonly Uri _indexUri;

    public NuGetFlatContainerVersionFeed(string baseUrl)
    {
        var b = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        BaseUrl = b;
        _indexUri = new Uri(new Uri(b, UriKind.Absolute), "tamp.core/index.json");
    }

    public string BaseUrl { get; }

    public string DisplayName => BaseUrl;

    /// <summary>The feed <c>tamp init</c> uses: <see cref="FeedUrlEnvironmentVariable"/> if set, else nuget.org.</summary>
    public static NuGetFlatContainerVersionFeed FromEnvironment()
    {
        var env = Environment.GetEnvironmentVariable(FeedUrlEnvironmentVariable);
        return new NuGetFlatContainerVersionFeed(string.IsNullOrWhiteSpace(env) ? DefaultBaseUrl : env.Trim());
    }

    public async Task<IReadOnlyList<string>> GetVersionsAsync(CancellationToken ct)
    {
        // The caller bounds the whole call with a token; HttpClient.Timeout is a backstop.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.GetAsync(_indexUri, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseIndex(json);
    }

    /// <summary>Parse a flat-container <c>index.json</c> (<c>{"versions":[...]}</c>).</summary>
    internal static IReadOnlyList<string> ParseIndex(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("versions", out var versions) ||
            versions.ValueKind != JsonValueKind.Array)
            throw new FormatException("response has no 'versions' array");

        var list = new List<string>();
        foreach (var v in versions.EnumerateArray())
            if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) list.Add(s);
        return list;
    }
}

/// <summary>Where the scaffolded Tamp version came from.</summary>
internal enum TampVersionSource
{
    /// <summary>Explicit <c>--tamp-version</c>.</summary>
    Flag,
    /// <summary>Newest stable <c>Tamp.Core</c> on the feed.</summary>
    Feed,
    /// <summary>The running CLI's own version (feed unreachable, or nothing newer on it).</summary>
    Cli,
}

/// <summary>Outcome of <see cref="TampVersionResolver.ResolveAsync"/>.</summary>
/// <param name="Version">Version to pin for Tamp.Core, Tamp.NetCli.V10 and dotnet-tamp.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="FeedName">Feed that was consulted, if any.</param>
/// <param name="FailureReason">Why the feed lookup failed; null when it succeeded or was skipped.</param>
internal sealed record TampVersionResolution(
    string Version,
    TampVersionSource Source,
    string? FeedName = null,
    string? FailureReason = null);

/// <summary>
/// Picks the Tamp version <c>tamp init</c> scaffolds (#119). Tamp.Core,
/// Tamp.NetCli.V10 and dotnet-tamp are packed from one tag with one
/// <c>&lt;Version&gt;</c> (Directory.Build.props, RELEASING.md), so a single
/// lookup of <c>Tamp.Core</c> decides all three pins.
/// </summary>
internal static class TampVersionResolver
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    // SemVer 2.0 core with optional prerelease / build metadata. Leading 'v' is not accepted.
    private static readonly Regex SemVerPattern = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant);

    /// <summary>True if <paramref name="value"/> is a valid SemVer 2.0 version (e.g. <c>1.17.5</c> or <c>1.18.0-rc.1</c>).</summary>
    public static bool IsValidVersion(string value) => SemVerPattern.IsMatch(value);

    /// <summary>
    /// Resolve the version to scaffold. Never throws for feed problems: any
    /// failure (timeout, network, bad payload) falls back to <paramref name="cliVersion"/>
    /// with <see cref="TampVersionResolution.FailureReason"/> set.
    /// </summary>
    public static async Task<TampVersionResolution> ResolveAsync(
        string cliVersion,
        string? pinnedVersion,
        ITampVersionFeed feed,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (pinnedVersion is not null)
            return new TampVersionResolution(pinnedVersion, TampVersionSource.Flag);

        IReadOnlyList<string> versions;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeout);
            try
            {
                // WaitAsync guarantees we return on time even if the feed ignores the token.
                versions = await feed.GetVersionsAsync(cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Fallback($"timed out after {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Fallback(Describe(ex));
            }
        }

        var latest = PickLatestStable(versions, cliVersion);
        if (latest is null)
            return versions.Any(v => TryParseStable(v, out _))
                ? new TampVersionResolution(cliVersion, TampVersionSource.Cli, feed.DisplayName)   // CLI is already the newest
                : Fallback("no stable Tamp.Core versions in the response");
        return new TampVersionResolution(latest, TampVersionSource.Feed, feed.DisplayName);

        TampVersionResolution Fallback(string reason)
            => new(cliVersion, TampVersionSource.Cli, feed.DisplayName, reason);
    }

    /// <summary>
    /// Highest stable (non-prerelease) version in <paramref name="versions"/> that is
    /// &gt;= <paramref name="floor"/>; null if there is none. Never returns a version
    /// older than the floor (the running CLI), so <c>init</c> can't scaffold backwards.
    /// </summary>
    internal static string? PickLatestStable(IEnumerable<string> versions, string floor)
    {
        TryParseStable(floor, out var floorVersion);   // unparseable floor → no floor (null)
        Version? best = null;
        string? bestText = null;
        foreach (var v in versions)
        {
            if (!TryParseStable(v, out var parsed)) continue;
            if (floorVersion is not null && parsed < floorVersion) continue;
            if (best is null || parsed > best) { best = parsed; bestText = Normalize(v); }
        }
        return bestText;
    }

    /// <summary>Parse <c>X.Y.Z</c> (or <c>X.Y.Z.W</c>) with no prerelease label; build metadata is ignored.</summary>
    internal static bool TryParseStable(string text, out Version? version)
    {
        version = null;
        var s = Normalize(text);
        if (s.Contains('-', StringComparison.Ordinal)) return false;
        if (!Version.TryParse(s, out var v) || v.Build < 0) return false;
        version = v;
        return true;
    }

    private static string Normalize(string text)
    {
        var s = text.Trim();
        var plus = s.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? s : s[..plus];
    }

    private static string Describe(Exception ex)
    {
        // Innermost cause is the useful bit (e.g. "Name or service not known").
        var inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        var msg = inner.Message.ReplaceLineEndings(" ").Trim().TrimEnd('.');
        return msg.Length > 160 ? msg[..160] + "…" : msg;
    }
}
