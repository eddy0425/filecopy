using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileCopy.Client.Local;

/// <summary>What a kept file looked like on both sides at the last successful sync.</summary>
public sealed class FileSnapshot
{
    public long ServerMtime { get; set; }
    public long Size { get; set; }
    public long LocalMtime { get; set; }
    /// <summary>"Always keep on this device" (vs. merely downloaded by opening it).</summary>
    public bool Pinned { get; set; } = true;
}

/// <summary>
/// Persisted record of which items are kept on this device. One state file per
/// (server, local folder) pair, stored in %AppData%\FileCopy.
/// </summary>
public sealed class LocalState
{
    public HashSet<string> KeptFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FileSnapshot> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private string _path = "";
    private readonly object _lock = new();

    public static LocalState Load(string serverUrl, string localFolder)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            serverUrl.TrimEnd('/').ToLowerInvariant() + "|" + Path.GetFullPath(localFolder).TrimEnd('\\').ToLowerInvariant())))[..16];
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileCopy", $"state-{key}.json");

        LocalState? state = null;
        try
        {
            if (File.Exists(path))
                state = JsonSerializer.Deserialize<LocalState>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            // Corrupt state: start fresh; files are re-adopted on the next refresh.
        }
        state ??= new LocalState();
        // Deserialization loses the comparer; restore case-insensitive lookups.
        state.KeptFolders = new HashSet<string>(state.KeptFolders, StringComparer.OrdinalIgnoreCase);
        state.Files = new Dictionary<string, FileSnapshot>(state.Files, StringComparer.OrdinalIgnoreCase);
        state._path = path;
        return state;
    }

    public void Save()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    // All accessors lock: the state is touched from transfer tasks and the UI thread.

    public FileSnapshot? GetFile(string rel)
    {
        lock (_lock) return Files.GetValueOrDefault(rel);
    }

    public void SetFile(string rel, FileSnapshot snap)
    {
        lock (_lock) Files[rel] = snap;
    }

    public void RemoveFile(string rel)
    {
        lock (_lock) Files.Remove(rel);
    }

    public List<string> FileKeys()
    {
        lock (_lock) return Files.Keys.ToList();
    }

    public (int Count, long Bytes) Usage()
    {
        lock (_lock) return (Files.Count, Files.Values.Sum(f => f.Size));
    }

    public void AddKeptFolder(string rel)
    {
        lock (_lock) KeptFolders.Add(rel);
    }

    /// <summary>True if <paramref name="rel"/> is a kept folder or lies inside one.</summary>
    public bool IsInKeptFolder(string rel)
    {
        lock (_lock)
        {
            for (var p = rel; p.Length > 0; p = Parent(p))
                if (KeptFolders.Contains(p)) return true;
            return false;
        }
    }

    /// <summary>True if the item should always stay on this device.</summary>
    public bool IsPinned(string rel)
    {
        lock (_lock) return Files.GetValueOrDefault(rel)?.Pinned == true || IsInKeptFolder(rel);
    }

    /// <summary>
    /// Stops always-keeping <paramref name="rel"/> and everything under it. Kept folders
    /// above it are dissolved, but their other files stay pinned individually.
    /// </summary>
    public void UnkeepFolderTree(string rel)
    {
        lock (_lock)
        {
            foreach (var ancestor in KeptFolders.Where(k => IsSameOrUnder(rel, k) && !IsSameOrUnder(k, rel)).ToList())
                foreach (var (path, snap) in Files)
                    if (IsSameOrUnder(path, ancestor)) snap.Pinned = true;
            KeptFolders.RemoveWhere(k => IsSameOrUnder(k, rel) || IsSameOrUnder(rel, k));
            foreach (var (path, snap) in Files)
                if (IsSameOrUnder(path, rel)) snap.Pinned = false;
        }
    }

    /// <summary>Removes all records at or under <paramref name="rel"/>.</summary>
    public void RemoveTree(string rel)
    {
        lock (_lock)
        {
            KeptFolders.RemoveWhere(k => IsSameOrUnder(k, rel));
            foreach (var k in Files.Keys.Where(k => IsSameOrUnder(k, rel)).ToList()) Files.Remove(k);
        }
    }

    /// <summary>Re-keys all records at or under <paramref name="from"/> to <paramref name="to"/>.</summary>
    public void MoveTree(string from, string to)
    {
        lock (_lock)
        {
            foreach (var k in KeptFolders.Where(k => IsSameOrUnder(k, from)).ToList())
            {
                KeptFolders.Remove(k);
                KeptFolders.Add(to + k[from.Length..]);
            }
            foreach (var (k, v) in Files.Where(f => IsSameOrUnder(f.Key, from)).ToList())
            {
                Files.Remove(k);
                Files[to + k[from.Length..]] = v;
            }
        }
    }

    public static string Parent(string rel)
    {
        int i = rel.LastIndexOf('/');
        return i < 0 ? "" : rel[..i];
    }

    public static bool IsSameOrUnder(string path, string folder) =>
        folder.Length == 0 ||
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
}
