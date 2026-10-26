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

    /// <summary>Saves within this time are written together (keeping a folder of 5000 files is one write, not 5000).</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private string _path = "";
    private readonly object _lock = new();
    private readonly object _writeLock = new(); // one writer at a time, so an older snapshot cannot land last
    private bool _dirty;
    private bool _flushPending;

    public static LocalState Load(string serverUrl, string localFolder)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            serverUrl.TrimEnd('/').ToLowerInvariant() + "|" + Path.GetFullPath(localFolder).TrimEnd('\\').ToLowerInvariant())))[..16];
        var path = Path.Combine(Settings.DataDir, $"state-{key}.json");

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
        // Deserialization loses the comparer; restore case-insensitive lookups. A corrupt file may hold
        // nulls or keys that differ only in case (first one wins).
        var files = state.Files ?? [];
        state.KeptFolders = new HashSet<string>((state.KeptFolders ?? []).Where(k => k != null), StringComparer.OrdinalIgnoreCase);
        state.Files = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in files)
            if (v != null) state.Files.TryAdd(k, v);
        state._path = path;
        return state;
    }

    /// <summary>Writes the state to disk soon, in the background (see <see cref="SaveDelay"/>).</summary>
    public void Save()
    {
        lock (_lock)
        {
            _dirty = true;
            if (_flushPending) return;
            _flushPending = true;
        }
        _ = Task.Delay(SaveDelay).ContinueWith(_ => Write(onlyIfDirty: true), TaskScheduler.Default);
    }

    /// <summary>Writes the state to disk now (on exit / disconnect).</summary>
    public void Flush() => Write(onlyIfDirty: false);

    /// <summary>A failed write stays pending: the next Save or Flush retries it.</summary>
    private void Write(bool onlyIfDirty)
    {
        lock (_writeLock)
        {
            string json;
            lock (_lock)
            {
                _flushPending = false;
                if (onlyIfDirty && !_dirty) return;
                _dirty = false;
                json = JsonSerializer.Serialize(this);
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (_lock) _dirty = true;
            }
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

    /// <summary>
    /// Removes all records at or under <paramref name="rel"/>.
    /// Without <paramref name="includeFiles"/> only kept folders go; file snapshots stay.
    /// </summary>
    public void RemoveTree(string rel, bool includeFiles = true)
    {
        lock (_lock)
        {
            KeptFolders.RemoveWhere(k => IsSameOrUnder(k, rel));
            if (!includeFiles) return;
            foreach (var k in Files.Keys.Where(k => IsSameOrUnder(k, rel)).ToList()) Files.Remove(k);
        }
    }

    /// <summary>
    /// Re-keys all records at or under <paramref name="from"/> to <paramref name="to"/>.
    /// Without <paramref name="includeFiles"/> only kept folders move; file snapshots stay put.
    /// </summary>
    public void MoveTree(string from, string to, bool includeFiles = true)
    {
        lock (_lock)
        {
            foreach (var k in KeptFolders.Where(k => IsSameOrUnder(k, from)).ToList())
            {
                KeptFolders.Remove(k);
                KeptFolders.Add(to + k[from.Length..]);
            }
            if (!includeFiles) return;
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
