namespace Tamp.Sarif;

/// <summary>
/// Bridges SARIF results to the canonical <c>diagnostic.emitted</c> event stream
/// (#13, ADR 0019) via the ambient <see cref="Tamp.BuildEvents"/> emitter. Compiler
/// and Roslyn-analyzer diagnostics are captured to a SARIF ErrorLog by MSBuild
/// (<c>/p:ErrorLog=…,version=2.1</c>); call <see cref="EmitFromFile"/> after a build
/// to surface each result as a <c>diagnostic.emitted</c> event.
/// </summary>
/// <remarks>
/// Lives in <c>Tamp.Sarif</c> (not the small core) so <c>Tamp.Core</c> stays free of
/// the SARIF model — Core owns only the event shape + the ambient emitter. No-op when
/// no build run is active (<see cref="Tamp.BuildEvents.IsActive"/> is false).
/// </remarks>
public static class SarifDiagnostics
{
    /// <summary>
    /// Pure mapping: turn a <see cref="SarifLog"/>'s results into
    /// <see cref="Tamp.DiagnosticEmittedPayload"/>s (no emission). Exposed so the
    /// normalization is unit-testable without a running build.
    /// </summary>
    public static IReadOnlyList<Tamp.DiagnosticEmittedPayload> Map(SarifLog log)
    {
        if (log is null) throw new ArgumentNullException(nameof(log));
        var payloads = new List<Tamp.DiagnosticEmittedPayload>();
        foreach (var run in log.Runs)
        {
            if (run.Results is null) continue;
            foreach (var result in run.Results)
            {
                var physical = result.Locations is { Count: > 0 }
                    ? result.Locations[0].PhysicalLocation
                    : null;
                var uri = physical?.ArtifactLocation?.Uri;
                payloads.Add(new Tamp.DiagnosticEmittedPayload
                {
                    RuleId = result.RuleId ?? string.Empty,
                    Level = MapLevel(result.Level),
                    Message = result.Message.Text ?? string.Empty,
                    Location = string.IsNullOrEmpty(uri) ? null : new Tamp.DiagnosticLocation { File = uri!, Line = physical?.Region?.StartLine },
                });
            }
        }
        return payloads;
    }

    /// <summary>Emit one <c>diagnostic.emitted</c> per result in <paramref name="log"/> via <see cref="Tamp.BuildEvents"/>. Returns the count emitted (0 when no build is active).</summary>
    public static int Emit(SarifLog log)
    {
        if (!Tamp.BuildEvents.IsActive) return 0;
        var payloads = Map(log);
        foreach (var p in payloads)
            Tamp.BuildEvents.Diagnostic(p.RuleId, p.Level, p.Message, p.Location?.File, p.Location?.Line, p.FixHint);
        return payloads.Count;
    }

    /// <summary>Read a SARIF file and <see cref="Emit"/> its results. Returns the count emitted.</summary>
    public static int EmitFromFile(AbsolutePath path) => Emit(SarifReader.LoadFromFile(path));

    /// <summary>Map the SARIF level enum to the pinned lowercase vocabulary used by <c>diagnostic.emitted</c>.</summary>
    private static string MapLevel(SarifLevel level) => level switch
    {
        SarifLevel.None => "none",
        SarifLevel.Note => "note",
        SarifLevel.Warning => "warning",
        SarifLevel.Error => "error",
        _ => "warning",
    };
}
