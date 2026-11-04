using System.Text.RegularExpressions;

namespace Tamp;

/// <summary>
/// Read-only view of the consumer repository's git metadata. Reads
/// <c>.git/HEAD</c>, <c>.git/refs/...</c>, and <c>.git/config</c> directly
/// — no shelling out to <c>git</c>.
/// </summary>
/// <remarks>
/// Surface is intentionally narrow for v1: the things a build script
/// reads constantly (current branch, current commit SHA, origin URL) and
/// nothing else. Authoring or query-the-history operations belong to
/// <c>git</c> proper, invocable via a future <c>Tamp.Git</c> module.
/// </remarks>
public sealed class GitRepository
{
    private GitRepository(AbsolutePath root, string? branch, string commit, string? remoteUrl)
    {
        Root = root;
        Branch = branch;
        Commit = commit;
        RemoteUrl = remoteUrl;
    }

    /// <summary>Working-tree root (the directory containing <c>.git</c>).</summary>
    public AbsolutePath Root { get; }

    /// <summary>Current branch name, or null if HEAD is detached.</summary>
    public string? Branch { get; }

    /// <summary>40-char SHA-1 of the current commit.</summary>
    public string Commit { get; }

    /// <summary>Origin remote URL, or null if no <c>origin</c> remote is configured.</summary>
    public string? RemoteUrl { get; }

    public bool IsDetachedHead => Branch is null;

    /// <summary>
    /// Load by walking up from <paramref name="startDirectory"/> looking for
    /// a <c>.git</c> directory. Throws if none is found.
    /// </summary>
    public static GitRepository Load(AbsolutePath startDirectory)
    {
        var root = LocateGitRoot(startDirectory.Value);
        if (root is null)
            throw new InvalidOperationException(
                $"No .git directory found above '{startDirectory}'.");

        var rootPath = AbsolutePath.Create(root);
        var gitDir = rootPath / ".git";

        var (branch, commit) = ReadHead(gitDir);
        var remoteUrl = ReadOriginUrl(gitDir);

        return new GitRepository(rootPath, branch, commit, remoteUrl);
    }

    private static string? LocateGitRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git"))) return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    private static (string? branch, string commit) ReadHead(AbsolutePath gitDir)
    {
        var headPath = gitDir / "HEAD";
        if (!headPath.FileExists())
            throw new InvalidOperationException($"Missing .git/HEAD at {gitDir}");

        var head = headPath.ReadAllText().Trim();
        if (head.StartsWith("ref:", StringComparison.Ordinal))
        {
            var refPath = head["ref:".Length..].Trim();
            var branch = refPath.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? refPath["refs/heads/".Length..]
                : refPath;
            var commit = ResolveRefSha(gitDir, refPath);
            return (branch, commit);
        }
        // Detached HEAD: HEAD itself contains the SHA.
        if (head.Length == 40 && head.All(IsHexDigit))
            return (null, head);

        // Fallback: try to interpret as a partial SHA — unlikely but harmless.
        return (null, head);
    }

    private static string ResolveRefSha(AbsolutePath gitDir, string refPath)
    {
        // Try loose ref file first.
        var loose = gitDir / refPath;
        if (loose.FileExists())
        {
            var s = loose.ReadAllText().Trim();
            if (s.Length == 40 && s.All(IsHexDigit)) return s;
        }
        // Fall back to packed-refs.
        var packed = gitDir / "packed-refs";
        if (packed.FileExists())
        {
            foreach (var raw in packed.ReadAllLines())
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('^')) continue;
                var space = line.IndexOf(' ');
                if (space < 0) continue;
                var sha = line[..space];
                var name = line[(space + 1)..];
                if (sha.Length == 40 && string.Equals(name, refPath, StringComparison.Ordinal))
                    return sha;
            }
        }
        throw new InvalidOperationException($"Could not resolve ref '{refPath}' in {gitDir}");
    }

    // `.*` then Trim, rather than `\s*(?<url>.+?)\s*$`: the lazy form re-tries `\s*$` at every
    // character and goes quadratic on a long whitespace run. This form is linear.
    private static readonly Regex RemoteUrlRegex = TampRegex.Create(@"^\s*url\s*=(?<url>.*)$");

    private static string? ReadOriginUrl(AbsolutePath gitDir)
    {
        var configPath = gitDir / "config";
        if (!configPath.FileExists()) return null;
        var inOriginSection = false;
        foreach (var raw in configPath.ReadAllLines())
        {
            var line = raw.TrimStart();
            if (line.StartsWith('['))
            {
                // Section header.
                inOriginSection = line.StartsWith("[remote \"origin\"]", StringComparison.Ordinal);
                continue;
            }
            if (!inOriginSection) continue;
            var m = TampRegex.Match(RemoteUrlRegex, line, $"a remote line of {configPath}");
            if (m.Success && m.Groups["url"].Value.Trim() is { Length: > 0 } url) return url;
        }
        return null;
    }

    private static bool IsHexDigit(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}

/// <summary>
/// Auto-inject a <see cref="GitRepository"/> built from the consumer
/// repository's <c>.git</c> directory.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class GitRepositoryAttribute : ValueInjectionAttribute
{
    public override object? GetValue(System.Reflection.MemberInfo member, Type memberType)
        => GitRepository.Load(TampBuild.RootDirectory);
}
