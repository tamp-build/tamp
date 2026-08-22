using Xunit;

namespace Tamp.Sarif.Tests;

/// <summary>
/// TAM-279: the fields a dynamic scanner emits and the minimal model used to
/// drop. Each test parses a fragment shaped like real scanner output and
/// asserts the value survives both the read and the write — a field that
/// deserialises but is lost on re-serialisation is still a data-loss bug for
/// any pipeline that merges or rewrites SARIF.
/// </summary>
public class SarifDastFieldsTests
{
    // Shaped after ZAP's documented SARIF report output.
    private const string ZapResultJson = """
    {
      "version": "2.1.0",
      "runs": [{
        "tool": { "driver": {
          "name": "ZAP",
          "rules": [{
            "id": "40012",
            "name": "Cross Site Scripting (Reflected)",
            "properties": { "tags": ["OWASP_2021_A03", "WSTG-v42-INPV-01"] },
            "relationships": [{
              "target": { "id": "79", "toolComponent": { "name": "CWE", "guid": "cwe-guid" } },
              "kinds": ["superset"]
            }]
          }]
        }},
        "taxonomies": [{
          "name": "CWE",
          "guid": "cwe-guid",
          "taxa": [{ "id": "79", "name": "Improper Neutralization of Input" }]
        }],
        "results": [{
          "ruleId": "40012",
          "level": "error",
          "message": { "text": "Cross Site Scripting (Reflected)" },
          "properties": { "attack": "</p><script>alert(1);</script><p>", "confidence": "Medium" },
          "partialFingerprints": { "zapFingerprint": "abc123" },
          "taxa": [{ "id": "79", "toolComponent": { "name": "CWE" } }],
          "webRequest": {
            "protocol": "https",
            "version": "1.1",
            "target": "https://127.0.0.1:8080/greeting?name=x",
            "method": "GET",
            "headers": { "Host": "127.0.0.1:8080", "User-Agent": "ZAP" },
            "body": { "text": "" }
          },
          "webResponse": {
            "protocol": "https",
            "version": "1.1",
            "statusCode": 200,
            "reasonPhrase": "OK",
            "headers": { "Content-Type": "text/html" },
            "body": { "text": "<html>reflected</html>" }
          },
          "locations": [{
            "physicalLocation": {
              "artifactLocation": { "uri": "https://127.0.0.1:8080/greeting?name=x" },
              "region": {
                "startLine": 10,
                "byteOffset": 512,
                "byteLength": 34,
                "snippet": { "text": "</p><script>alert(1);</script><p>" }
              }
            }
          }]
        }]
      }]
    }
    """;

    private static SarifResult ZapResult() => SarifReader.Parse(ZapResultJson).Runs[0].Results![0];

    // ------------------------------------------------------------------
    // The DAST payload
    // ------------------------------------------------------------------

    [Fact]
    public void Web_request_is_parsed()
    {
        var req = ZapResult().WebRequest;

        Assert.NotNull(req);
        Assert.Equal("GET", req!.Method);
        Assert.Equal("https", req.Protocol);
        Assert.Equal("https://127.0.0.1:8080/greeting?name=x", req.Target);
        Assert.Equal("ZAP", req.Headers!["User-Agent"]);
    }

    [Fact]
    public void Web_response_is_parsed()
    {
        var resp = ZapResult().WebResponse;

        Assert.NotNull(resp);
        Assert.Equal(200, resp!.StatusCode);
        Assert.Equal("OK", resp.ReasonPhrase);
        Assert.Equal("text/html", resp.Headers!["Content-Type"]);
        Assert.Contains("reflected", resp.Body!.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public void Region_snippet_and_byte_span_are_parsed()
    {
        // The snippet is the evidence string, and its absence is what forced
        // finding identity onto path + line in the first place.
        var region = ZapResult().Locations![0].PhysicalLocation!.Region!;

        Assert.Equal("</p><script>alert(1);</script><p>", region.Snippet!.Text);
        Assert.Equal(512, region.ByteOffset);
        Assert.Equal(34, region.ByteLength);
    }

    [Fact]
    public void Partial_fingerprints_are_parsed()
    {
        Assert.Equal("abc123", ZapResult().PartialFingerprints!["zapFingerprint"]);
    }

    // ------------------------------------------------------------------
    // Property bags — the extension-data contract
    // ------------------------------------------------------------------

    [Fact]
    public void Result_properties_preserve_unknown_members()
    {
        // The attack payload has no well-known SARIF member, so it can only
        // survive via extension data. If this breaks, every tool-specific
        // field is silently dropped.
        var props = ZapResult().Properties;

        Assert.NotNull(props);
        Assert.Equal("</p><script>alert(1);</script><p>",
            props!.AdditionalProperties!["attack"].GetString());
        Assert.Equal("Medium", props.AdditionalProperties["confidence"].GetString());
    }

    [Fact]
    public void Rule_properties_expose_tags()
    {
        // Trivy folds vulnerability / misconfiguration / secret findings under
        // one tool name and distinguishes them purely by these tags.
        var rule = SarifReader.Parse(ZapResultJson).Runs[0].Tool.Driver.Rules![0];

        Assert.Equal(["OWASP_2021_A03", "WSTG-v42-INPV-01"], rule.Properties!.Tags);
    }

    [Fact]
    public void Tags_and_extension_data_coexist_in_one_bag()
    {
        const string json = """
        { "version": "2.1.0", "runs": [{ "tool": { "driver": { "name": "t" } }, "results": [{
          "ruleId": "r", "message": { "text": "m" },
          "properties": { "tags": ["a"], "custom": 42 } }] }] }
        """;

        var props = SarifReader.Parse(json).Runs[0].Results![0].Properties!;

        Assert.Equal(["a"], props.Tags);
        Assert.Equal(42, props.AdditionalProperties!["custom"].GetInt32());
        // tags is a modelled member, so it must NOT also land in extension data
        Assert.DoesNotContain("tags", props.AdditionalProperties.Keys);
    }

    // ------------------------------------------------------------------
    // Taxonomies / CWE
    // ------------------------------------------------------------------

    [Fact]
    public void Run_taxonomies_and_taxa_are_parsed()
    {
        var taxonomy = SarifReader.Parse(ZapResultJson).Runs[0].Taxonomies![0];

        Assert.Equal("CWE", taxonomy.Name);
        Assert.Equal("79", taxonomy.Taxa![0].Id);
    }

    [Fact]
    public void Result_taxa_reference_the_cwe_directly()
    {
        var taxa = ZapResult().Taxa!;

        Assert.Equal("79", taxa[0].Id);
        Assert.Equal("CWE", taxa[0].ToolComponent!.Name);
    }

    [Fact]
    public void Rule_relationships_map_to_taxonomy_entries()
    {
        var rel = SarifReader.Parse(ZapResultJson).Runs[0].Tool.Driver.Rules![0].Relationships![0];

        Assert.Equal("79", rel.Target.Id);
        Assert.Equal("CWE", rel.Target.ToolComponent!.Name);
        Assert.Equal(["superset"], rel.Kinds);
    }

    // ------------------------------------------------------------------
    // Round trip — parse alone isn't enough
    // ------------------------------------------------------------------

    [Fact]
    public void The_whole_dast_document_round_trips_losslessly()
    {
        // Merge and dedup rewrite logs, so a field that reads but doesn't
        // write is still lost in any real pipeline.
        var once = SarifWriter.Serialize(SarifReader.Parse(ZapResultJson));
        var twice = SarifWriter.Serialize(SarifReader.Parse(once));

        JsonAssert.Equivalent(once, twice);
        JsonAssert.Equivalent(ZapResultJson, once);
    }

    [Fact]
    public void Static_analysis_output_is_unaffected_by_the_new_members()
    {
        // Every new field is optional. A SARIF log from a static scanner must
        // serialise byte-identically to how it did before TAM-279 — no null
        // members appearing in the output.
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

    [Fact]
    public void Dedup_key_composition_is_unchanged_by_the_new_members()
    {
        // TAM-279 deliberately does NOT change dedup semantics. Adopting
        // partialFingerprints as the identity would alter collapsing
        // behaviour for every tool that emits them (CodeQL does) — that's a
        // separate, opt-in decision.
        var withFingerprint = ZapResult();
        var withoutFingerprint = withFingerprint with { PartialFingerprints = null };

        Assert.Equal(
            SarifDedup.ComputeKey(withFingerprint),
            SarifDedup.ComputeKey(withoutFingerprint));
    }
}
