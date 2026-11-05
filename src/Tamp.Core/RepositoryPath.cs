namespace Tamp;

/// <summary>
/// Express a scanner-reported location as a repository-relative path. One implementation for every
/// reporting wrapper, because a finding anchored to an absolute path on a runner points at nothing in
/// the repository — and because each wrapper deriving its own copy of this is how they drift.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is in core.</b> <c>Tamp.Syft</c>'s <c>GitLabSbom</c> and <c>Tamp.Grype</c>'s
/// <c>GrypeSarif</c> each grew a private copy of this logic, and the copies had already diverged in
/// three ways — one handled <c>//</c> UNC prefixes and the other did not, one returned null for an
/// unrooted path while the other returned it unchanged, one treated the root itself as unrooted —
/// and both carried the same <c>"/"</c> bug (tamp-syft#11, tamp-grype#9). Every future reporting
/// wrapper needs exactly this function and would otherwise produce a fourth variant.
/// </para>
/// <para>
/// <b>Mechanism here, policy at the call site.</b> <see cref="Relativize"/> answers one question —
/// "what is this location relative to that root, or is it not under it at all?" — and returns
/// <see langword="null"/> for the second case. What to do about an unrooted location is the
/// wrapper's decision and differs legitimately: an SBOM profiler omits the property rather than emit
/// a path GitLab cannot anchor, while a SARIF writer keeps the awkward absolute path because losing
/// the only location a human has for a vulnerability is worse. Both are right; neither belongs here.
/// </para>
/// <para>
/// <b>Deliberately string-based, not <see cref="Path"/>-based.</b> A scan captured on a Linux runner
/// is routinely turned into a report on Windows and vice versa, so the OS running this code must not
/// decide how the other's paths parse. Nothing here touches the file system.
/// </para>
/// </remarks>
public static class RepositoryPath
{
    /// <summary>
    /// The normalized form of a source root, for passing to <see cref="Relativize"/>: forward
    /// slashes, no trailing slash. The filesystem root <c>/</c> normalizes to <c>"/"</c>, not to the
    /// empty string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That last point is the whole reason this is a named function rather than an inlined
    /// <c>TrimEnd('/')</c>. A naive trim turns <c>"/"</c> into <c>""</c>, which then reads as "no
    /// root supplied" and rejects every absolute location — so the one root value that correctly
    /// describes a scanner's virtual-filesystem output was the one value that could not be used
    /// (tamp-syft#11, tamp-grype#9).
    /// </para>
    /// <para>
    /// <c>/</c> matters because scanners report against the scan's virtual filesystem rather than the
    /// host: syft records a lockfile at the root of the scanned tree as <c>/pnpm-lock.yaml</c>, which
    /// is already repository-relative but for its leading slash.
    /// </para>
    /// </remarks>
    /// <param name="root">A source root. Null, empty or whitespace yields <see langword="null"/>.</param>
    /// <returns>The normalized root, or <see langword="null"/> when none was supplied.</returns>
    public static string? NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;

        var normalized = root!.Trim().Replace('\\', '/');

        // A root of "/", "//" or "///" is the filesystem (or virtual-filesystem) root. Trimming it to
        // "" would make it indistinguishable from an absent root, which is the bug this avoids.
        if (normalized.Trim('/').Length == 0) return "/";

        return normalized.TrimEnd('/');
    }

    /// <summary>
    /// <paramref name="location"/> expressed relative to <paramref name="normalizedRoot"/>, with
    /// forward slashes — or <see langword="null"/> when it is absolute and not under that root.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>
    /// An <b>already-relative</b> location is normalized and returned as-is: the scanner emitted it
    /// relative to the scan root, which is what a consumer wants.
    /// </item>
    /// <item>
    /// With <paramref name="normalizedRoot"/> <see langword="null"/>, nothing can be rooted, so an
    /// absolute location returns <see langword="null"/> and a relative one passes through.
    /// </item>
    /// <item>
    /// Comparison is <b>case-insensitive when either side looks like a Windows path</b>, because
    /// syft's drive-letter casing does not always match the configured root — observed
    /// <c>C:\TEMP\…</c> against a root of <c>C:\Temp</c>. POSIX paths stay case-sensitive, where case
    /// is significant.
    /// </item>
    /// <item>
    /// The root <b>itself</b> yields <see langword="null"/>: a directory is not a file a finding can
    /// anchor to, and an empty relative path would be meaningless to a consumer.
    /// </item>
    /// </list>
    /// </remarks>
    /// <param name="location">The location a scanner reported. Null or whitespace yields <see langword="null"/>.</param>
    /// <param name="normalizedRoot">A root from <see cref="NormalizeRoot"/>.</param>
    public static string? Relativize(string? location, string? normalizedRoot)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;

        var path = Collapse(location!.Replace('\\', '/'));
        if (path.Length == 0) return null;

        if (!IsAbsolute(path)) return path;
        if (normalizedRoot is null || normalizedRoot.Length == 0) return null;

        // The virtual-filesystem root: every absolute path is under it, so strip the leading slash.
        // A location that *is* the root leaves nothing behind, which is not a file — hence null.
        if (normalizedRoot == "/")
        {
            if (!path.StartsWith('/')) return null;
            var stripped = TrimLeadingSlashes(path);
            return stripped.Length == 0 ? null : stripped;
        }

        var comparison = LooksWindows(path) || LooksWindows(normalizedRoot)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (path.Equals(normalizedRoot, comparison)) return null;          // the root is not a file
        if (!path.StartsWith(normalizedRoot + "/", comparison)) return null;

        return path[(normalizedRoot.Length + 1)..];
    }

    /// <summary>
    /// <see cref="Relativize"/> against a raw, un-normalized root — the convenient form when a caller
    /// holds the root as the user supplied it.
    /// </summary>
    public static string? RelativizeAgainst(string? location, string? root)
        => Relativize(location, NormalizeRoot(root));

    /// <summary>
    /// Whether a forward-slashed path is absolute: POSIX (<c>/x</c>), UNC (<c>//server/share</c>) or
    /// Windows drive-rooted (<c>C:/x</c>).
    /// </summary>
    /// <remarks>
    /// A bare drive letter with no separator (<c>C:x</c>) is drive-relative on Windows, not absolute,
    /// and is reported as such.
    /// </remarks>
    public static bool IsAbsolute(string? forwardSlashPath)
    {
        if (string.IsNullOrEmpty(forwardSlashPath)) return false;

        return forwardSlashPath.StartsWith('/')
            || (forwardSlashPath.Length >= 3
                && char.IsLetter(forwardSlashPath[0])
                && forwardSlashPath[1] == ':'
                && forwardSlashPath[2] == '/');
    }

    /// <summary>Whether a path carries a Windows drive letter, so comparison should ignore case.</summary>
    public static bool LooksWindows(string? forwardSlashPath)
        => forwardSlashPath is { Length: >= 2 }
           && char.IsLetter(forwardSlashPath[0])
           && forwardSlashPath[1] == ':';

    /// <summary>
    /// Collapse redundant separators and <c>.</c> segments in a forward-slashed path, preserving a
    /// leading <c>//</c> (UNC) and any trailing-slash-free shape.
    /// </summary>
    /// <remarks>
    /// Not cosmetic. A root joined to a path by string concatenation yields <c>/src/repo//go.mod</c>,
    /// and without collapsing, relativizing that against <c>/src/repo</c> returns <c>/go.mod</c> —
    /// still absolute, which is exactly the thing a caller must never emit into a report.
    /// <c>..</c> is deliberately <b>not</b> resolved: a scanner-reported location containing it is
    /// suspect, and silently resolving it would let a path escape the root it was checked against.
    /// </remarks>
    private static string Collapse(string forwardSlashPath)
    {
        var unc = forwardSlashPath.StartsWith("//", StringComparison.Ordinal);

        var segments = new List<string>();
        foreach (var segment in forwardSlashPath.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            segments.Add(segment);
        }

        var joined = string.Join('/', segments);
        if (unc) return "//" + joined;
        return forwardSlashPath.StartsWith('/') ? "/" + joined : joined;
    }

    private static string TrimLeadingSlashes(string path)
    {
        var index = 0;
        while (index < path.Length && path[index] == '/') index++;
        return index == path.Length ? "" : path[index..];
    }
}
