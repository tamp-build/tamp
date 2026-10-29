using Xunit;

namespace Tamp.Sarif.Tests;

/// <summary>
/// logicalLocations support (§3.33) for findings that have no file to point at
/// — an advertised MCP tool, a service endpoint. Each test parses a fragment
/// shaped like such output and asserts the value survives read + write, and
/// that dedup keeps distinct logical locations apart without disturbing the
/// physical-location behaviour every existing scanner relies on.
/// </summary>
public class SarifLogicalLocationTests
{
    // Shaped after a normalised MCP-scanner finding: a tool-poisoning hit on an
    // advertised tool, with no physicalLocation at all.
    private const string McpToolFindingJson = """
    {
      "version": "2.1.0",
      "runs": [{
        "tool": { "driver": { "name": "mcp-scanner" } },
        "results": [{
          "ruleId": "MCP-YARA-TOOL_POISONING",
          "level": "error",
          "message": { "text": "Detected 1 threat: tool poisoning" },
          "partialFingerprints": { "mcpTool": "https://mcp.example.com/mcp/query_database" },
          "locations": [{
            "logicalLocations": [{
              "name": "query_database",
              "fullyQualifiedName": "https://mcp.example.com/mcp/query_database",
              "kind": "mcpTool"
            }]
          }]
        }]
      }]
    }
    """;

    private static SarifResult McpResult() => SarifReader.Parse(McpToolFindingJson).Runs[0].Results![0];

    [Fact]
    public void Logical_location_is_parsed()
    {
        var logical = McpResult().Locations![0].LogicalLocations![0];

        Assert.Equal("query_database", logical.Name);
        Assert.Equal("https://mcp.example.com/mcp/query_database", logical.FullyQualifiedName);
        Assert.Equal("mcpTool", logical.Kind);
    }

    [Fact]
    public void Finding_without_a_physical_location_still_parses()
    {
        // The whole point: no artifactLocation, no region — the identity lives
        // entirely in the logical location.
        Assert.Null(McpResult().Locations![0].PhysicalLocation);
    }

    [Fact]
    public void The_whole_document_round_trips_losslessly()
    {
        var once = SarifWriter.Serialize(SarifReader.Parse(McpToolFindingJson));
        var twice = SarifWriter.Serialize(SarifReader.Parse(once));

        JsonAssert.Equivalent(once, twice);
        JsonAssert.Equivalent(McpToolFindingJson, once);
    }

    [Fact]
    public void Static_analysis_output_is_unaffected_by_the_new_member()
    {
        // A physical-location log must serialise with no logicalLocations member
        // appearing (every field is optional / null-ignored).
        const string json = """
        {
          "version": "2.1.0",
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "runs": [{
            "tool": { "driver": { "name": "OpenGrep" } },
            "results": [{
              "ruleId": "rules.x",
              "level": "warning",
              "message": { "text": "finding" },
              "locations": [{ "physicalLocation": {
                "artifactLocation": { "uri": "src/Foo.cs" },
                "region": { "startLine": 3 } } }]
            }]
          }]
        }
        """;

        JsonAssert.Equivalent(json, SarifWriter.Serialize(SarifReader.Parse(json)));
    }

    // ------------------------------------------------------------------
    // Dedup — the behaviour this change exists to make correct
    // ------------------------------------------------------------------

    [Fact]
    public void Distinct_logical_locations_do_not_collapse()
    {
        // Two findings, same ruleId, no physicalLocation, different tools. Before
        // logical-location-aware keys these shared (ruleId, (no-uri), 0:0) and one
        // was silently dropped.
        var a = McpResult();
        var b = a with
        {
            Locations =
            [
                new SarifLocation
                {
                    LogicalLocations =
                    [
                        new SarifLogicalLocation
                        {
                            Name = "delete_everything",
                            FullyQualifiedName = "https://mcp.example.com/mcp/delete_everything",
                            Kind = "mcpTool",
                        },
                    ],
                },
            ],
        };

        Assert.NotEqual(SarifDedup.ComputeKey(a), SarifDedup.ComputeKey(b));
    }

    [Fact]
    public void Same_logical_location_and_rule_collapses()
    {
        // The genuine duplicate (same tool, same rule, e.g. two scan runs) still folds.
        var a = McpResult();
        var b = McpResult();

        Assert.Equal(SarifDedup.ComputeKey(a), SarifDedup.ComputeKey(b));

        var merged = SarifDedup.Distinct(new SarifLog { Runs = [a.InOneRun(), b.InOneRun()] });
        Assert.Single(merged.Runs.SelectMany(r => r.Results ?? []));
    }

    [Fact]
    public void Physical_location_findings_keep_their_prior_key()
    {
        // A result with no logical location must dedupe exactly as before: the
        // key gains only a constant suffix, so two such results still collapse.
        var withLogical = McpResult();
        var physicalOnly = new SarifResult
        {
            RuleId = "rules.x",
            Message = new SarifMessage { Text = "m" },
            Locations =
            [
                new SarifLocation
                {
                    PhysicalLocation = new SarifPhysicalLocation
                    {
                        ArtifactLocation = new SarifArtifactLocation { Uri = "src/Foo.cs" },
                        Region = new SarifRegion { StartLine = 3 },
                    },
                },
            ],
        };
        var physicalOnlyToo = physicalOnly with { };

        Assert.Equal(SarifDedup.ComputeKey(physicalOnly), SarifDedup.ComputeKey(physicalOnlyToo));
        Assert.NotEqual(SarifDedup.ComputeKey(physicalOnly), SarifDedup.ComputeKey(withLogical));
    }
}

internal static class SarifLogicalLocationTestExtensions
{
    public static SarifRun InOneRun(this SarifResult result) =>
        new() { Tool = new SarifTool { Driver = new SarifToolComponent { Name = "mcp-scanner" } }, Results = [result] };
}
