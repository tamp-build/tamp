using System.Text.Json;
using Xunit;

namespace Tamp.Sarif.Tests;

/// <summary>
/// #156 — collapsing duplicates across runs discarded the corroboration between two independent
/// scanners. Agreement between tools is a signal, and a finding both tools see is worth triaging
/// differently from one only one tool sees; before this, the merged log recorded only that the second
/// tool reported nothing.
/// </summary>
public class SarifDedupAttributionTests
{
    private static SarifResult Finding(string ruleId = "S1", string uri = "src/App.cs", int line = 12)
        => new()
        {
            RuleId = ruleId,
            Level = SarifLevel.Warning,
            Message = new SarifMessage { Text = $"{ruleId} at {uri}:{line}" },
            Locations =
            [
                new SarifLocation
                {
                    PhysicalLocation = new SarifPhysicalLocation
                    {
                        ArtifactLocation = new SarifArtifactLocation { Uri = uri },
                        Region = new SarifRegion { StartLine = line, StartColumn = 1 },
                    },
                },
            ],
        };

    private static SarifLog Log(string tool, params SarifResult[] results)
        => new() { Runs = [Run(tool, results)] };

    private static SarifRun Run(string tool, params SarifResult[] results)
        => new()
        {
            Tool = new SarifTool { Driver = new SarifToolComponent { Name = tool } },
            Results = results,
        };

    /// <summary>The tool names recorded on a result, or null when the property is absent.</summary>
    private static string[]? AlsoReportedBy(SarifResult result)
        => result.Properties?.AdditionalProperties is { } properties
            && properties.TryGetValue(SarifDedup.AlsoReportedByProperty, out var value)
                ? [.. value.EnumerateArray().Select(e => e.GetString()!)]
                : null;

    /// <summary>The recorded tool names, failing with a readable message when the property is absent.</summary>
    private static string[] Recorded(SarifResult result)
    {
        var tools = AlsoReportedBy(result);
        Assert.NotNull(tools);
        return tools;
    }

    // ─── the fix ──────────────────────────────────────────────────────────

    [Fact]
    public void A_Finding_Two_Tools_Reported_Names_The_Other_Tool()
    {
        var merged = SarifMerge.CombineDistinct([Log("opengrep", Finding()), Log("roslyn", Finding())]);

        // Still one result, still on the first run — counts do not move.
        Assert.Single(merged.Runs[0].Results!);
        Assert.Empty(merged.Runs[1].Results!);
        // But the corroboration is now recorded rather than discarded.
        Assert.Equal(["roslyn"], Recorded(merged.Runs[0].Results![0]));
    }

    [Fact]
    public void Three_Tools_Are_All_Named_In_Input_Order()
    {
        var merged = SarifMerge.CombineDistinct(
            [Log("opengrep", Finding()), Log("roslyn", Finding()), Log("trivy", Finding())]);

        Assert.Equal(["roslyn", "trivy"], Recorded(merged.Runs[0].Results![0]));
    }

    [Fact]
    public void A_Finding_Only_One_Tool_Reported_Gets_No_Property()
    {
        // The discriminator the whole property exists for: one tool seeing something is different
        // from two tools agreeing, so an uncorroborated finding must stay visibly uncorroborated.
        var merged = SarifMerge.CombineDistinct([Log("opengrep", Finding("S1")), Log("roslyn", Finding("S2"))]);

        Assert.Null(AlsoReportedBy(merged.Runs[0].Results![0]));
        Assert.Null(AlsoReportedBy(merged.Runs[1].Results![0]));
    }

    [Fact]
    public void The_Same_Tool_Reporting_Twice_Is_Not_Corroboration()
    {
        // The multi-TFM Roslyn case this helper exists for: /p:ErrorLog emits one copy per target
        // framework. "roslyn also reported this roslyn finding" is noise, not a second opinion.
        var merged = SarifMerge.CombineDistinct([Log("roslyn", Finding()), Log("roslyn", Finding())]);

        Assert.Single(merged.Runs[0].Results!);
        Assert.Null(AlsoReportedBy(merged.Runs[0].Results![0]));
    }

    [Fact]
    public void Duplicates_Within_One_Run_Are_Not_Corroboration_Either()
    {
        var log = Log("roslyn", Finding(), Finding());

        var deduped = SarifDedup.Distinct(log);

        Assert.Single(deduped.Runs[0].Results!);
        Assert.Null(AlsoReportedBy(deduped.Runs[0].Results![0]));
    }

    [Fact]
    public void One_Tool_Reporting_It_Twice_Is_Named_Once()
    {
        var merged = SarifMerge.CombineDistinct(
            [Log("opengrep", Finding()), Log("roslyn", Finding()), Log("roslyn", Finding())]);

        Assert.Equal(["roslyn"], Recorded(merged.Runs[0].Results![0]));
    }

    // ─── it must not damage anything it touches ───────────────────────────

    [Fact]
    public void The_Input_Log_Is_Not_Mutated()
    {
        // Results are shared with the caller, so the pass has to copy the property bag rather than
        // edit it. A dedup that silently annotated its input would be a nasty surprise.
        var original = Finding();
        var logs = new[] { Log("opengrep", original), Log("roslyn", Finding()) };

        var merged = SarifMerge.CombineDistinct(logs);

        Assert.NotNull(AlsoReportedBy(merged.Runs[0].Results![0]));
        Assert.Null(AlsoReportedBy(original));
        Assert.Null(original.Properties);
    }

    [Fact]
    public void Existing_Result_Properties_Survive()
    {
        var withProperties = Finding() with
        {
            Properties = new SarifPropertyBag
            {
                Tags = ["security"],
                AdditionalProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["grype.purl"] = JsonDocument.Parse("\"pkg:npm/vite@5.4.21\"").RootElement.Clone(),
                },
            },
        };

        var merged = SarifMerge.CombineDistinct([Log("opengrep", withProperties), Log("roslyn", Finding())]);
        var kept = merged.Runs[0].Results![0];

        Assert.Equal(["roslyn"], Recorded(kept));
        Assert.Equal(["security"], kept.Properties!.Tags);
        Assert.Equal("pkg:npm/vite@5.4.21", kept.Properties.AdditionalProperties!["grype.purl"].GetString());
    }

    [Fact]
    public void An_Existing_Property_Of_The_Same_Name_Is_Replaced_Not_Duplicated()
    {
        // Running a merged log back through Distinct must not accumulate stale corroborators.
        var stale = Finding() with
        {
            Properties = new SarifPropertyBag
            {
                AdditionalProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [SarifDedup.AlsoReportedByProperty] = JsonDocument.Parse("[\"ghost\"]").RootElement.Clone(),
                },
            },
        };

        var merged = SarifMerge.CombineDistinct([Log("opengrep", stale), Log("roslyn", Finding())]);

        Assert.Equal(["roslyn"], Recorded(merged.Runs[0].Results![0]));
    }

    [Fact]
    public void Combine_Still_Keeps_A_Result_Per_Tool()
    {
        // The documented alternative when a consumer needs one result per reporting tool rather than
        // a property naming them.
        var combined = SarifMerge.Combine([Log("opengrep", Finding()), Log("roslyn", Finding())]);

        Assert.All(combined.Runs, r => Assert.Single(r.Results!));
        Assert.All(combined.Runs, r => Assert.Null(AlsoReportedBy(r.Results![0])));
    }

    [Fact]
    public void A_Run_With_An_Unnamed_Tool_Gets_A_Stable_Placeholder()
    {
        // Recording nothing would re-lose the signal for a producer that omits the driver name.
        var merged = SarifMerge.CombineDistinct([Log("opengrep", Finding()), Log("", Finding())]);

        Assert.Equal(["(unnamed tool)"], Recorded(merged.Runs[0].Results![0]));
    }

    [Fact]
    public void A_Key_Selector_Gets_The_Same_Attribution()
    {
        var merged = SarifMerge.CombineDistinct(
            [Log("opengrep", Finding()), Log("roslyn", Finding())],
            SarifDedup.DefaultKey);

        Assert.Equal(["roslyn"], Recorded(merged.Runs[0].Results![0]));
    }

    [Fact]
    public void Runs_With_Null_Results_Are_Untouched()
    {
        var log = new SarifLog
        {
            Runs = [new SarifRun { Tool = new(), Results = null }, Run("roslyn", Finding())],
        };

        var deduped = SarifDedup.Distinct(log);

        Assert.Null(deduped.Runs[0].Results);
        Assert.Single(deduped.Runs[1].Results!);
    }

    [Fact]
    public void The_Property_Round_Trips_Through_Serialisation()
    {
        // It is written into the property bag's extension data, so it has to survive a write/read
        // cycle to be of any use to a consumer.
        var merged = SarifMerge.CombineDistinct([Log("opengrep", Finding()), Log("roslyn", Finding())]);

        var json = SarifWriter.Serialize(merged);
        var reloaded = SarifReader.Parse(json);

        Assert.Equal(["roslyn"], Recorded(reloaded.Runs[0].Results![0]));
    }
}
