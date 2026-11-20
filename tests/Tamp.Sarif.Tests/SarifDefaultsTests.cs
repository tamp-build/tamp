using Tamp.Sarif;
using Xunit;

namespace Tamp.Sarif.Tests;

/// <summary>
/// tamp#166 — the declared default of a property must hold on every supported TFM.
/// </summary>
/// <remarks>
/// A non-nullable property with an initializer is only honoured when the serializer leaves absent
/// JSON members alone. .NET 11's System.Text.Json source generator does; every earlier one
/// (net8/net9/net10) overwrites the member with <c>default(T)</c>. That made the whole schema
/// runtime-dependent, and in the one case that matters most it was silently wrong rather than
/// merely different: a result with no <c>level</c> read as <see cref="SarifLevel.None"/> instead of
/// <see cref="SarifLevel.Warning"/>, which sorts a finding below any "warning and above" gate and
/// wrote <c>"level": "none"</c> back out into the evidence file.
///
/// These tests are the guard. The project multi-targets, so they run once per TFM — which is the
/// whole point: they fail on the runtime that regresses, not on some canonical one.
/// </remarks>
public class SarifDefaultsTests
{
    private const string MinimalLog = """
        { "runs": [ { "results": [ { "ruleId": "R1" } ] } ] }
        """;

    // ─── the one with teeth ───────────────────────────────────────────────

    [Fact]
    public void A_Result_With_No_Level_Is_A_Warning_Not_A_None()
    {
        // SARIF 2.1.0 §3.27.10: an absent level defers to the rule's defaultConfiguration, which
        // itself defaults to warning. None is the *least* severe value, so reading absent as None
        // hides the finding from a severity gate — unknown reported as benign.
        var result = SarifReader.Parse(MinimalLog).Runs[0].Results![0];

        Assert.Equal(SarifLevel.Warning, result.Level);
    }

    [Fact]
    public void An_Explicit_None_Level_Stays_None()
    {
        // The counterpart: the fix must not flatten a deliberate "none" into a warning, or it would
        // trade under-reporting for over-reporting. The nullable raw property is what keeps the two
        // cases apart.
        const string json = """
            { "runs": [ { "results": [ { "ruleId": "R1", "level": "none" } ] } ] }
            """;

        var result = SarifReader.Parse(json).Runs[0].Results![0];

        Assert.Equal(SarifLevel.None, result.Level);
        Assert.Equal(SarifLevel.None, result.LevelRaw);
    }

    [Fact]
    public void An_Absent_Level_Stays_Absent_On_Round_Trip()
    {
        // Writing a level the input never carried would put an invented severity into an evidence
        // file. Absent in, absent out.
        var once = SarifWriter.Serialize(SarifReader.Parse(MinimalLog));

        Assert.DoesNotContain("\"level\"", once, StringComparison.Ordinal);
        Assert.Null(SarifReader.Parse(once).Runs[0].Results![0].LevelRaw);
        Assert.Equal(SarifLevel.Warning, SarifReader.Parse(once).Runs[0].Results![0].Level);
    }

    [Fact]
    public void A_Rule_Configuration_With_No_Level_Is_A_Warning()
    {
        const string json = """
            {
              "runs": [ { "tool": { "driver": { "name": "t", "rules": [
                { "id": "R1", "defaultConfiguration": { } }
              ] } } } ]
            }
            """;

        var rule = SarifReader.Parse(json).Runs[0].Tool.Driver.Rules![0];

        Assert.Equal(SarifLevel.Warning, rule.DefaultConfiguration!.Level);
    }

    // ─── non-nullable means non-null, on every runtime ────────────────────

    [Fact]
    public void A_Minimal_Document_Still_Honours_Every_Declared_Default()
    {
        var log = SarifReader.Parse(MinimalLog);

        Assert.Equal("2.1.0", log.Version);
        Assert.NotNull(log.Runs);
        Assert.NotNull(log.Runs[0].Tool);
        Assert.NotNull(log.Runs[0].Tool.Driver);
        Assert.NotNull(log.Runs[0].Tool.Driver.Name);
        Assert.NotNull(log.Runs[0].Results![0].Message);
    }

    [Fact]
    public void An_Empty_Object_Parses_To_An_Empty_Log_Rather_Than_Nulls()
    {
        var log = SarifReader.Parse("{}");

        Assert.Equal("2.1.0", log.Version);
        Assert.NotNull(log.Runs);
        Assert.Empty(log.Runs);
    }

    // ─── $schema is supplied on the way out, not defaulted on the property ──

    [Fact]
    public void A_Log_Without_A_Schema_Is_Tagged_When_Written()
    {
        var json = SarifWriter.Serialize(new SarifLog());

        Assert.Contains(SarifLog.CanonicalSchema, json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Explicit_Schema_Is_Preserved_Rather_Than_Overwritten()
    {
        var json = SarifWriter.Serialize(new SarifLog { Schema = "https://example/custom.json" });

        Assert.Contains("https://example/custom.json", json, StringComparison.Ordinal);
        Assert.DoesNotContain(SarifLog.CanonicalSchema, json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Absent_Schema_Reads_As_Null_So_Merge_Can_Still_Defer()
    {
        // SarifMerge picks the first non-empty schema across its inputs. That is only expressible
        // while "no schema" survives a parse as null — a property default would erase it.
        Assert.Null(SarifReader.Parse(MinimalLog).Schema);
    }
}
