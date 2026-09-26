using System.Text.Json;

namespace Tamp;

/// <summary>
/// A tiny per-worktree store of the last <em>successful</em> input hash per target,
/// backing the #17 would-skip cache advisory. Best-effort: load/save failures are
/// swallowed (the advisory is diagnostic, never load-bearing). Never used to skip
/// work — that's the deferred enforcing phase.
/// </summary>
internal sealed class HashCache
{
    private readonly AbsolutePath _path;
    private readonly Dictionary<string, string> _map;

    private HashCache(AbsolutePath path, Dictionary<string, string> map)
    {
        _path = path;
        _map = map;
    }

    public static HashCache Load(AbsolutePath path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (path.FileExists())
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path.Value));
                if (loaded is not null) map = new Dictionary<string, string>(loaded, StringComparer.Ordinal);
            }
        }
        catch { /* corrupt/unreadable cache → start empty */ }
        return new HashCache(path, map);
    }

    public string? Get(string target) => _map.TryGetValue(target, out var h) ? h : null;

    public void Set(string target, string hash) => _map[target] = hash;

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path.Value);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path.Value, JsonSerializer.Serialize(_map));
        }
        catch { /* best effort */ }
    }
}
