using Xunit;

namespace Tamp.Sarif.Tests;

/// <summary>
/// tamp#166 on the 1.20.x line: a declared property default must survive deserialization on
/// EVERY supported runtime, not just net11.
/// </summary>
/// <remarks>
/// <para>
/// A property initializer is only honoured if the deserializer leaves absent JSON members alone.
/// net11's System.Text.Json source generator does; net8, net9 and net10 all overwrite the member
/// with <c>default(T)</c>. <c>default(SarifLevel)</c> is <c>None</c>, so a result whose scanner
/// legally omitted <c>level</c> was read as the LEAST severe value and written back into the
/// evidence file as <c>"level": "none"</c> — a finding of unknown severity recorded as benign.
/// </para>
/// <para>
/// These tests are deliberately asserted on behaviour that must be IDENTICAL on net8.0, net9.0 and
/// net10.0. That is the whole point: a test that only passes on the newest runtime would not have
/// caught the original bug, which is exactly how 1.20.0 shipped with it while green.
/// </para>
/// <para>
/// This line fixes it by normalising the document in <see cref="SarifReader"/> rather than by the
/// accessor-over-readonly-field shape main uses, because that shape cannot run on net8/net9 at all
/// — see the remarks on <c>SarifReader.Normalise</c>.
/// </para>
/// </remarks>
public class SarifDefaultsTests
{
    private const string MinimalLog = """
    {
      "runs": [{
        "tool": { "driver": { "name": "probe" } },
        "results": [{ "ruleId": "R1", "message": { "text": "finding" } }]
      }]
    }
    """;

    [Fact]
    public void A_Result_With_No_Level_Is_A_Warning_Not_A_None()
    {
        var result = SarifReader.Parse(MinimalLog).Runs[0].Results![0];

        // The bug: this read SarifLevel.None on every runtime before net11.
        Assert.Equal(SarifLevel.Warning, result.Level);
    }

    [Fact]
    public void An_Explicit_None_Level_Stays_None()
    {
        // Normalisation must fill only an ABSENT member. A scanner that states "none" means it.
        var json = """
        {
          "runs": [{
            "tool": { "driver": { "name": "probe" } },
            "results": [{ "ruleId": "R1", "level": "none", "message": { "text": "f" } }]
          }]
        }
        """;

        Assert.Equal(SarifLevel.None, SarifReader.Parse(json).Runs[0].Results![0].Level);
    }

    [Fact]
    public void An_Explicit_Level_Is_Never_Rewritten()
    {
        var json = """
        {
          "runs": [{
            "tool": { "driver": { "name": "probe" } },
            "results": [{ "ruleId": "R1", "level": "error", "message": { "text": "f" } }]
          }]
        }
        """;

        Assert.Equal(SarifLevel.Error, SarifReader.Parse(json).Runs[0].Results![0].Level);
    }

    [Fact]
    public void An_Absent_Level_Becomes_An_Explicit_Warning_On_Round_Trip()
    {
        // DELIBERATE DIFFERENCE FROM MAIN (1.21.0+), which preserves the absence.
        //
        // Per SARIF 2.1.0 §3.27.10 an absent level defers to the rule's defaultConfiguration,
        // which itself defaults to warning — so "absent" and "warning" are semantically the same
        // claim, and materialising it is a faithful rewrite. It is in any case strictly better
        // than the "none" this line writes today, which is a DIFFERENT and wrong claim.
        //
        // Asserted rather than left implicit so the difference is a decision on the record.
        var once = SarifWriter.Serialize(SarifReader.Parse(MinimalLog));

        Assert.Contains("\"level\": \"warning\"", once, StringComparison.Ordinal);
        Assert.DoesNotContain("\"level\": \"none\"", once, StringComparison.Ordinal);

        // And it is stable: a second trip does not drift.
        var twice = SarifWriter.Serialize(SarifReader.Parse(once));
        Assert.Equal(once, twice);
        Assert.Equal(SarifLevel.Warning, SarifReader.Parse(twice).Runs[0].Results![0].Level);
    }

    [Fact]
    public void A_Rule_Configuration_With_No_Level_Is_A_Warning()
    {
        // The member an absent result-level defers TO. Reading it as None inverts the deferral.
        var json = """
        {
          "runs": [{
            "tool": { "driver": {
              "name": "probe",
              "rules": [{ "id": "R1", "defaultConfiguration": { "enabled": true } }]
            } },
            "results": []
          }]
        }
        """;

        var rule = SarifReader.Parse(json).Runs[0].Tool.Driver.Rules![0];
        Assert.Equal(SarifLevel.Warning, rule.DefaultConfiguration!.Level);
    }

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
        // Before the fix, absent "runs" deserialised to null and log.Runs[0] threw an NRE —
        // an unreadable SARIF file reported as a crash rather than as empty.
        var log = SarifReader.Parse("{}");

        Assert.Equal("2.1.0", log.Version);
        Assert.NotNull(log.Runs);
        Assert.Empty(log.Runs);
    }

    [Fact]
    public void A_Run_With_No_Tool_Does_Not_Throw()
    {
        var log = SarifReader.Parse("""{ "runs": [{ "results": [] }] }""");

        Assert.NotNull(log.Runs[0].Tool);
        Assert.NotNull(log.Runs[0].Tool.Driver);
        Assert.Equal("", log.Runs[0].Tool.Driver.Name);
    }

    [Fact]
    public void A_Log_Without_A_Schema_Is_Tagged_When_Written()
    {
        var json = SarifWriter.Serialize(SarifReader.Parse(MinimalLog));

        Assert.Contains(SarifLog.CanonicalSchema, json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Explicit_Schema_Is_Preserved_Rather_Than_Overwritten()
    {
        var log = SarifReader.Parse(MinimalLog) with { Schema = "https://example/custom.json" };
        var json = SarifWriter.Serialize(log);

        Assert.Contains("https://example/custom.json", json, StringComparison.Ordinal);
        Assert.DoesNotContain(SarifLog.CanonicalSchema, json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Absent_Schema_Reads_As_Null_So_Merge_Can_Still_Defer()
    {
        // Schema stays nullable on purpose: SarifMerge picks the first non-empty value across
        // logs, which needs "this log did not state one" to be distinguishable.
        Assert.Null(SarifReader.Parse(MinimalLog).Schema);
    }

    [Fact]
    public void A_Result_Constructed_In_Code_Still_Defaults_To_Warning()
    {
        // The public API is unchanged from 1.20.0: Level remains a settable SarifLevel, so a
        // satellite that builds SARIF (Tamp.McpScanner, Tamp.AxeCore) still compiles. A patch
        // release must not break that.
        Assert.Equal(SarifLevel.Warning, new SarifResult().Level);
        Assert.Equal(SarifLevel.Error, new SarifResult { Level = SarifLevel.Error }.Level);
    }
}
