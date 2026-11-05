using System.Text.Json;
using Xunit;

namespace Tamp.Sarif.Tests;

/// <summary>
/// #155 — the default dedup key has no component or version term, so a dependency scanner's results
/// collapse: distinct affected versions of one package share the advisory id, the lockfile URI, the
/// line and column, and the logical name, differing only in the message text.
/// </summary>
/// <remarks>
/// The fixtures reproduce the shape measured on grype 0.112.0's native SARIF, where 299 results
/// collapse to 268 under the default key. The point of these tests is that the collapse is pinned as
/// known behaviour, the escape hatch works, and the condition under which the default key *is*
/// sufficient is spelled out.
/// </remarks>
public class SarifDedupKeySelectorTests
{
    private const string LockFile = "frontend/pnpm-lock.yaml";

    /// <summary>
    /// A dependency-scanner result as grype's native SARIF emits one: the advisory as the rule id,
    /// the lockfile at 1:1, a logical location naming the <em>path</em>, and the affected version
    /// only in the message.
    /// </summary>
    private static SarifResult PackageFinding(
        string advisory, string package, string version, string uri = LockFile, string? purl = null)
        => new()
        {
            RuleId = $"{advisory}-{package}",
            Level = SarifLevel.Error,
            Message = new SarifMessage { Text = $"A high vulnerability in npm package: {package}, version {version} was found at: {uri}" },
            Locations =
            [
                new SarifLocation
                {
                    PhysicalLocation = new SarifPhysicalLocation
                    {
                        ArtifactLocation = new SarifArtifactLocation { Uri = uri },
                        Region = new SarifRegion { StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 1 },
                    },
                    LogicalLocations =
                    [
                        new SarifLogicalLocation
                        {
                            // grype native: the path. Tamp.Grype 0.2.0: the purl.
                            Name = purl ?? uri,
                            FullyQualifiedName = purl ?? $".:/{uri}",
                        },
                    ],
                },
            ],
            Properties = new SarifPropertyBag
            {
                AdditionalProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["grype.purl"] = JsonSerializer.SerializeToElement($"pkg:npm/{package}@{version}"),
                    ["grype.version"] = JsonSerializer.SerializeToElement(version),
                },
            },
        };

    private static SarifLog Log(params SarifResult[] results)
        => new() { Runs = [new SarifRun { Tool = new SarifTool { Driver = new SarifToolComponent { Name = "grype" } }, Results = results }] };

    private static int Count(SarifLog log) => log.Runs.Sum(r => r.Results?.Count ?? 0);

    /// <summary>The composition pattern the docs recommend: the default key, plus component identity.</summary>
    private static string PackageAware(SarifResult result)
    {
        var purl = result.Properties?.AdditionalProperties is { } properties
            && properties.TryGetValue("grype.purl", out var value)
                ? value.GetString()
                : null;
        return $"{SarifDedup.DefaultKey(result)}|{purl ?? "(no-purl)"}";
    }

    // ─── the defect, pinned ───────────────────────────────────────────────

    [Fact]
    public void Two_Affected_Versions_Collapse_Under_The_Default_Key()
    {
        // Every component of the default key matches; only the message differs. This is known
        // behaviour rather than a bug in Distinct — the key cannot see a version that is only prose.
        var log = Log(
            PackageFinding("GHSA-587h-7wmj-h5pm", "vite", "5.4.21"),
            PackageFinding("GHSA-587h-7wmj-h5pm", "vite", "7.3.1"));

        Assert.Equal(2, Count(log));
        Assert.Equal(1, Count(SarifDedup.Distinct(log)));
        Assert.Equal(
            SarifDedup.DefaultKey(log.Runs[0].Results![0]),
            SarifDedup.DefaultKey(log.Runs[0].Results![1]));
    }

    [Fact]
    public void A_Key_Selector_Keeps_Them_Distinct()
    {
        var log = Log(
            PackageFinding("GHSA-587h-7wmj-h5pm", "vite", "5.4.21"),
            PackageFinding("GHSA-587h-7wmj-h5pm", "vite", "7.3.1"));

        Assert.Equal(2, Count(SarifDedup.Distinct(log, PackageAware)));
    }

    [Fact]
    public void Component_Identity_In_The_Logical_Location_Is_Already_Enough()
    {
        // The condition the docs state: the default key is sufficient exactly when the producer puts
        // component identity in fullyQualifiedName. Tamp.Grype 0.2.0 puts the purl there, which is
        // why its output survives CombineDistinct unchanged.
        var log = Log(
            PackageFinding("GHSA-587h-7wmj-h5pm", "vite", "5.4.21", purl: "pkg:npm/vite@5.4.21"),
            PackageFinding("GHSA-587h-7wmj-h5pm", "vite", "7.3.1", purl: "pkg:npm/vite@7.3.1"));

        Assert.Equal(2, Count(SarifDedup.Distinct(log)));
    }

    [Fact]
    public void A_Selector_Still_Collapses_Genuine_Duplicates()
    {
        // The escape hatch must not become "no dedup at all": the same package version reported twice
        // is still one finding.
        var log = Log(
            PackageFinding("GHSA-587h-7wmj-h5pm", "vite", "5.4.21"),
            PackageFinding("GHSA-587h-7wmj-h5pm", "vite", "5.4.21"));

        Assert.Equal(1, Count(SarifDedup.Distinct(log, PackageAware)));
    }

    [Fact]
    public void The_Same_Package_Version_In_Two_Lock_Files_Stays_Distinct()
    {
        // Two ecosystems vendoring one package is two things to fix, in two files. The URI term of
        // the default key already separates these.
        var log = Log(
            PackageFinding("GHSA-587h-7wmj-h5pm", "vite", "5.4.21", uri: "frontend/pnpm-lock.yaml"),
            PackageFinding("GHSA-587h-7wmj-h5pm", "vite", "5.4.21", uri: "api/package-lock.json"));

        Assert.Equal(2, Count(SarifDedup.Distinct(log)));
        Assert.Equal(2, Count(SarifDedup.Distinct(log, PackageAware)));
    }

    // ─── producer fingerprints are not a safe default ─────────────────────

    [Fact]
    public void Keying_On_A_Producers_Fingerprints_Can_Collapse_Far_Worse()
    {
        // Why the default key does not prefer fingerprints/partialFingerprints even though SARIF
        // nominates them as identity. grype's primaryLocationLineHash identifies the package-version,
        // so every advisory against one version shares it: on the real 299-result document that is
        // 62 distinct fingerprints. A caller can still opt into this; it must not be the default.
        var shared = new Dictionary<string, string> { ["primaryLocationLineHash"] = "abc123:1" };
        var log = Log(
            PackageFinding("GHSA-aaaa", "hono", "4.12.3") with { PartialFingerprints = shared },
            PackageFinding("GHSA-bbbb", "hono", "4.12.3") with { PartialFingerprints = shared },
            PackageFinding("GHSA-cccc", "hono", "4.12.3") with { PartialFingerprints = shared });

        // Three genuinely different advisories.
        Assert.Equal(3, Count(SarifDedup.Distinct(log, PackageAware)));
        // One fingerprint, so keying on it loses two of them.
        Assert.Equal(1, Count(SarifDedup.Distinct(log, r =>
            r.PartialFingerprints?["primaryLocationLineHash"] ?? SarifDedup.DefaultKey(r))));
    }

    // ─── the seam on SarifMerge ───────────────────────────────────────────

    [Fact]
    public void CombineDistinct_Accepts_A_Key_Selector()
    {
        var a = Log(PackageFinding("GHSA-587h-7wmj-h5pm", "vite", "5.4.21"));
        var b = Log(PackageFinding("GHSA-587h-7wmj-h5pm", "vite", "7.3.1"));

        Assert.Equal(1, Count(SarifMerge.CombineDistinct([a, b])));
        Assert.Equal(2, Count(SarifMerge.CombineDistinct([a, b], PackageAware)));
    }

    [Fact]
    public void Combine_Collapses_Nothing()
    {
        // The documented alternative for package findings.
        var a = Log(PackageFinding("GHSA-587h-7wmj-h5pm", "vite", "5.4.21"));
        var b = Log(PackageFinding("GHSA-587h-7wmj-h5pm", "vite", "7.3.1"));

        Assert.Equal(2, Count(SarifMerge.Combine([a, b])));
    }

    // ─── attribution, pinned so it cannot change silently ─────────────────

    [Fact]
    public void A_Finding_Two_Tools_Reported_Is_Attributed_To_The_First_Run()
    {
        // Documented, not desired: tracked separately. Pinned here so a behavioural change to it is
        // a deliberate test edit rather than a surprise.
        var finding = PackageFinding("GHSA-587h-7wmj-h5pm", "vite", "5.4.21");
        var toolA = new SarifLog { Runs = [new SarifRun { Tool = new SarifTool { Driver = new SarifToolComponent { Name = "toolA" } }, Results = [finding] }] };
        var toolB = new SarifLog { Runs = [new SarifRun { Tool = new SarifTool { Driver = new SarifToolComponent { Name = "toolB" } }, Results = [finding] }] };

        var merged = SarifMerge.CombineDistinct([toolA, toolB]);

        // Both runs survive, so "which tools ran" is answerable...
        Assert.Equal(["toolA", "toolB"], merged.Runs.Select(r => r.Tool.Driver.Name));
        // ...but "which tools found this" is not.
        Assert.Single(merged.Runs[0].Results!);
        Assert.Empty(merged.Runs[1].Results!);

        // Combine keeps the corroboration.
        var combined = SarifMerge.Combine([toolA, toolB]);
        Assert.All(combined.Runs, r => Assert.Single(r.Results!));
    }

    // ─── the public default key ───────────────────────────────────────────

    [Fact]
    public void The_Default_Key_Is_Composable()
    {
        var result = PackageFinding("GHSA-1", "vite", "5.4.21");
        var key = SarifDedup.DefaultKey(result);

        Assert.StartsWith("GHSA-1-vite|frontend/pnpm-lock.yaml|1:1|", key, StringComparison.Ordinal);
        Assert.StartsWith(key, PackageAware(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Null_Arguments_Are_Refused()
    {
        Assert.Throws<ArgumentNullException>(() => SarifDedup.DefaultKey(null!));
        Assert.Throws<ArgumentNullException>(() => SarifDedup.Distinct(null!, SarifDedup.DefaultKey));
        Assert.Throws<ArgumentNullException>(() => SarifDedup.Distinct(new SarifLog(), null!));
        Assert.Throws<ArgumentNullException>(() => SarifMerge.CombineDistinct(null!, SarifDedup.DefaultKey));
    }

    [Fact]
    public void A_Run_With_Null_Results_Survives_A_Selector_Pass()
    {
        var log = new SarifLog { Runs = [new SarifRun { Tool = new(), Results = null }] };

        Assert.Single(SarifDedup.Distinct(log, PackageAware).Runs);
        Assert.Null(SarifDedup.Distinct(log, PackageAware).Runs[0].Results);
    }
}
