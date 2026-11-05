namespace Tamp.Sarif;

/// <summary>
/// Combines multiple SARIF logs into one. Preserves run boundaries (each
/// input run survives as a distinct run in the output), so a consumer can still
/// see every tool that ran.
/// </summary>
/// <remarks>
/// <see cref="Combine"/> keeps every result on its own run, so the originating tool of every finding
/// stays identifiable. <see cref="CombineDistinct(IEnumerable{SarifLog})"/> preserves the runs but
/// keeps one result per finding: a finding two tools both reported stays on whichever run came first
/// in input order, with the others named in <see cref="SarifDedup.AlsoReportedByProperty"/>. See its
/// own remarks.
/// </remarks>
public static class SarifMerge
{
    /// <summary>
    /// Combine the runs of every input log into a single output log. The
    /// output's <c>version</c> and <c>$schema</c> are taken from the first
    /// non-null/non-empty value encountered; defaults are used when none are
    /// present.
    /// </summary>
    /// <remarks>
    /// Nothing is collapsed, so this is the safe default when results from different tools may
    /// overlap and the overlap is information — two independent scanners agreeing on a finding is a
    /// signal, and it survives here.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The input sequence is null.</exception>
    public static SarifLog Combine(IEnumerable<SarifLog> logs)
    {
        if (logs is null) throw new ArgumentNullException(nameof(logs));

        var materialised = logs.Where(static l => l is not null).ToList();
        var runs = materialised.SelectMany(static l => l.Runs).ToList();

        var version = materialised
            .Select(static l => l.Version)
            .FirstOrDefault(static v => !string.IsNullOrEmpty(v))
            ?? "2.1.0";

        var schema = materialised
            .Select(static l => l.Schema)
            .FirstOrDefault(static s => !string.IsNullOrEmpty(s))
            ?? "https://json.schemastore.org/sarif-2.1.0.json";

        return new SarifLog
        {
            Version = version,
            Schema = schema,
            Runs = runs,
        };
    }

    /// <summary>
    /// Combine the inputs (see <see cref="Combine"/>) and then collapse duplicate results via
    /// <see cref="SarifDedup.Distinct(SarifLog)"/>. The common path for stitching together SARIF
    /// from <strong>code</strong> scanners that re-analyse the same source — the multi-TFM Roslyn
    /// case, where <c>/p:ErrorLog</c> emits one copy of each source-level finding per target
    /// framework.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Not for package-level findings unless the producer carries component identity.</strong>
    /// The default dedup key has no component or version term of its own; the only slot that can hold
    /// one is <c>logicalLocations.fullyQualifiedName</c>. A dependency or container scanner typically
    /// reports the advisory id as <c>ruleId</c>, the lockfile as the URI, the same line and column for
    /// every result, and the affected version only in the message text — so <strong>distinct affected
    /// versions of one package collapse into a single result</strong>, and the report under-reports in
    /// the unsafe direction. Measured on grype 0.112.0's native SARIF: 299 results → 268.
    /// </para>
    /// <para>
    /// Use <see cref="Combine"/> for those, or pass a key selector to
    /// <see cref="CombineDistinct(IEnumerable{SarifLog}, Func{SarifResult, string})"/>. A producer
    /// that does put component identity in <c>fullyQualifiedName</c> — <c>Tamp.Grype</c> 0.2.0 puts
    /// the purl there — is already safe under the default key; the same document passes through
    /// 299 → 299. Worked example:
    /// <see href="https://github.com/tamp-build/tamp-grype/issues/6">tamp-grype#6</see>.
    /// </para>
    /// <para>
    /// <strong>Attribution.</strong> Duplicates are collapsed across runs, not within them, so a
    /// finding reported by two tools is kept on the first run in input order and the other tool's run
    /// shows nothing for it. The corroboration survives as
    /// <see cref="SarifDedup.AlsoReportedByProperty"/> on the kept result, listing every other tool
    /// that reported it (tamp#156). What does not survive is a result per reporting tool — use
    /// <see cref="Combine"/> when a consumer needs that rather than a property.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">The input sequence is null.</exception>
    public static SarifLog CombineDistinct(IEnumerable<SarifLog> logs)
        => SarifDedup.Distinct(Combine(logs));

    /// <summary>
    /// <see cref="CombineDistinct(IEnumerable{SarifLog})"/> with a caller-supplied identity, for
    /// results the default key cannot tell apart — most often package findings, where the affected
    /// version lives in the message or the property bag.
    /// </summary>
    /// <param name="logs">The logs to combine.</param>
    /// <param name="keySelector">
    /// Identity for a result; see <see cref="SarifDedup.Distinct(SarifLog, Func{SarifResult, string})"/>
    /// for the composition pattern against <see cref="SarifDedup.DefaultKey"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static SarifLog CombineDistinct(IEnumerable<SarifLog> logs, Func<SarifResult, string> keySelector)
        => SarifDedup.Distinct(Combine(logs), keySelector);
}
