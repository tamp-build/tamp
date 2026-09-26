using Tamp;
using Xunit;

namespace Tamp.Sarif.Tests;

/// <summary>
/// Tests for the SARIF → <c>diagnostic.emitted</c> normalizer (#13). The pure
/// <see cref="SarifDiagnostics.Map"/> is exercised here without a running build;
/// the ambient emission end-to-end is covered in Tamp.Core.Tests.
/// </summary>
public sealed class SarifDiagnosticsTests
{
    [Fact]
    public void Map_Normalizes_Results_Including_Level_Location_And_Missing_Location()
    {
        var log = new SarifLog
        {
            Runs = new[]
            {
                new SarifRun
                {
                    Results = new[]
                    {
                        new SarifResult
                        {
                            RuleId = "CS1002",
                            Level = SarifLevel.Error,
                            Message = new SarifMessage { Text = "; expected" },
                            Locations = new[]
                            {
                                new SarifLocation
                                {
                                    PhysicalLocation = new SarifPhysicalLocation
                                    {
                                        ArtifactLocation = new SarifArtifactLocation { Uri = "src/Foo.cs" },
                                        Region = new SarifRegion { StartLine = 88 },
                                    },
                                },
                            },
                        },
                        new SarifResult
                        {
                            RuleId = "TAMP001",
                            Level = SarifLevel.Warning,
                            Message = new SarifMessage { Text = "unobserved CommandPlan" },
                            // no location
                        },
                    },
                },
            },
        };

        var payloads = SarifDiagnostics.Map(log);

        Assert.Equal(2, payloads.Count);

        Assert.Equal("CS1002", payloads[0].RuleId);
        Assert.Equal("error", payloads[0].Level);              // enum → pinned lowercase vocab
        Assert.Equal("; expected", payloads[0].Message);
        Assert.Equal("src/Foo.cs", payloads[0].Location!.File);
        Assert.Equal(88, payloads[0].Location!.Line);

        Assert.Equal("TAMP001", payloads[1].RuleId);
        Assert.Equal("warning", payloads[1].Level);
        Assert.Null(payloads[1].Location);                     // missing location → null
    }

    [Fact]
    public void Emit_Is_NoOp_Returning_Zero_When_No_Build_Active()
    {
        Assert.False(BuildEvents.IsActive);
        Assert.Equal(0, SarifDiagnostics.Emit(new SarifLog()));
    }
}
