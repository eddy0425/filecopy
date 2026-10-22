using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using FileCopy.Client.Api;
using FileCopy.Client.Transfers;
using Microsoft.VisualBasic.FileIO;

namespace FileCopy.Client.Local;

public enum FileStatus { CloudOnly, Kept, PendingUpload, PendingDownload, Transferring, LocalOnly }

/// <summary>
/// The local mirror folder holds copies of "kept" items. This service keeps those
/// copies in step with the server and implements the file-manager actions.
/// </summary>
public sealed class SyncService : IDisposable
{
    private const int MaxConcurrentFiles = 3;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly ServerApi _api;
    private readonly TransferQueue _transfers;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrentFiles);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _busy = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private volatile Dictionary<string, RemoteEntry> _remote = new(StringComparer.OrdinalIgnoreCase);

    public string Root { get; }
    public LocalState State { get; }

    /// <summary>Raised (on a worker thread) whenever listings or statuses may have changed.</summary>
    public event Action? Changed;

    public SyncService(string serverUrl, string token, string root, TransferQueue transfers, Action<string> log)
    {
        Root = Path.GetFullPath(root).TrimEnd('\\');
        Directory.CreateDirectory(Root);
        _api = new ServerApi(serverUrl, token);
        _transfers = transfers;
        _log = log;
        State = LocalState.Load(serverUrl, Root);
    }

    public async Task StartAsync()
    {
        await _api.PingAsync(_cts.Token);
        await RefreshAsync();
        _timer = new Timer(_ => _ = RefreshAsync(), null, RefreshInterval, RefreshInterval);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cts.Cancel();
        _api.Dispose();
    }

    // ---------- paths & status ----------

    public string LocalPath(string rel) => Path.Combine(Root, rel.Replace('/', '\\'));

    private string ToRel(string full) => Path.GetRelativePath(Root, full).Replace('\\', '/');

    private static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;

    private static long MtimeMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private static bool IsTemp(string path) => path.EndsWith(".fcpart", StringComparison.OrdinalIgnoreCase);

    public bool IsBusy(string rel) => _busy.ContainsKey(rel);

    public RemoteEntry? GetRemote(string rel) => _remote.GetValueOrDefault(rel);

    /// <summary>Files (remote and local-only) at or under a folder, as relative paths.</summary>
    public IEnumerable<RemoteEntry> RemoteFilesUnder(string folder) =>
        _remote.Values.Where(e => !e.IsDir && LocalState.IsSameOrUnder(e.Path, folder));

    public FileStatus GetFileStatus(string rel)
    {
        if (_busy.ContainsKey(rel)) return FileStatus.Transferring;
        var remote = _remote.GetValueOrDefault(rel);
        var local = new FileInfo(LocalPath(rel));
        if (!local.Exists) return FileStatus.CloudOnly;
        if (remote == null) return FileStatus.LocalOnly;

        var snap = State.GetFile(rel);
        if (snap == null)
            return SameAsRemote(local, remote) ? FileStatus.Kept : FileStatus.PendingUpload;
        if (LocalChanged(local, snap)) return FileStatus.PendingUpload;
        if (remote.Mtime != snap.ServerMtime || remote.Size != snap.Size) return FileStatus.PendingDownload;
        return FileStatus.Kept;
    }

    private static bool SameAsRemote(FileInfo local, RemoteEntry remote) =>
        local.Length == remote.Size && MtimeMs(local.LastWriteTimeUtc) == remote.Mtime;

    private static bool LocalChanged(FileInfo local, FileSnapshot snap) =>
        local.Length != snap.Size || MtimeMs(local.LastWriteTimeUtc) != snap.LocalMtime;

    public record Listing(List<RemoteEntry> Remote, List<string> LocalOnlyFiles, List<string> LocalOnlyDirs);

    /// <summary>Direct children of a folder: everything on the server plus local-only items.</summary>
    public Listing List(string folder)
    {
        var remote = _remote.Values.Where(e => LocalState.Parent(e.Path).Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();
        var localFiles = new List<string>();
        var localDirs = new List<string>();
        var dir = LocalPath(folder);
        if (Directory.Exists(dir))
        {
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var rel = ToRel(d);
                if (!_remote.ContainsKey(rel)) localDirs.Add(rel);
            }
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var rel = ToRel(f);
                if (!IsTemp(f) && !_remote.ContainsKey(rel)) localFiles.Add(rel);
            }
        }
        return new Listing(remote, localFiles, localDirs);
    }

    // ---------- refresh / reconcile ----------

    /// <summary>Fetches the server tree and brings kept items up to date in both directions.</summary>
    public async Task RefreshAsync()
    {
        if (!await _refreshLock.WaitAsync(0)) return;
        var jobs = new List<Task>();
        try
        {
            var tree = await _api.GetTreeAsync(_cts.Token);
            var remote = new Dictionary<string, RemoteEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in tree) remote.TryAdd(e.Path, e); // server may be case-sensitive; first wins
            _remote = remote;
            Changed?.Invoke();

            // 1. Local files.
            var localFiles = Directory.EnumerateFiles(Root, "*", System.IO.SearchOption.AllDirectories)
                .Where(f => !IsTemp(f)).ToList();
            var localSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var full in localFiles)
            {
                var rel = ToRel(full);
                localSet.Add(rel);
                if (_busy.ContainsKey(rel)) continue;
                var r = remote.GetValueOrDefault(rel);
                var snap = State.GetFile(rel);
                var fi = new FileInfo(full);

                if (r == null || r.IsDir)
                {
                    if (r != null) continue; // name clash with a remote folder; leave alone
                    if (snap != null)
                    {
                        // Deleted on the server: move our copy to the Recycle Bin (recoverable).
                        if (LocalChanged(fi, snap)) jobs.Add(UploadAsync(rel, baseMtime: 0));
                        else RecycleLocal(rel, "云端已删除");
                    }
                    else
                    {
                        jobs.Add(UploadAsync(rel, baseMtime: 0)); // new local file
                    }
                    continue;
                }

                if (snap == null)
                {
                    if (SameAsRemote(fi, r)) State.SetFile(rel, Snapshot(r, fi, pinned: true));
                    else jobs.Add(UploadAsync(rel, baseMtime: 0)); // server keeps a conflict copy
                }
                else if (LocalChanged(fi, snap))
                {
                    jobs.Add(UploadAsync(rel, snap.ServerMtime));
                }
                else if (r.Mtime != snap.ServerMtime || r.Size != snap.Size)
                {
                    jobs.Add(DownloadAsync(r, snap.Pinned));
                }
            }

            // 2. Remote files that are not local.
            foreach (var r in remote.Values)
            {
                if (localSet.Contains(r.Path)) continue;
                if (r.IsDir)
                {
                    if (State.IsInKeptFolder(r.Path)) Directory.CreateDirectory(LocalPath(r.Path));
                    continue;
                }
                // A snapshot without a local file means the copy was deleted outside the app.
                State.RemoveFile(r.Path);
                if (State.IsInKeptFolder(r.Path)) jobs.Add(DownloadAsync(r, pinned: true));
            }

            // 3. Snapshots of files that vanished on both sides.
            foreach (var rel in State.FileKeys())
                if (!localSet.Contains(rel) && !remote.ContainsKey(rel)) State.RemoveFile(rel);

            RemoveEmptyStaleDirs(remote);
            State.Save();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log("刷新失败: " + ex.Message);
        }
        finally
        {
            _refreshLock.Release();
        }
        Changed?.Invoke();
        await Task.WhenAll(jobs);
    }

    /// <summary>Removes empty local folders that no longer exist on the server.</summary>
    private void RemoveEmptyStaleDirs(Dictionary<string, RemoteEntry> remote)
    {
        var dirs = Directory.EnumerateDirectories(Root, "*", System.IO.SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length);
        foreach (var d in dirs)
        {
            var rel = ToRel(d);
            if (remote.ContainsKey(rel) || Directory.EnumerateFileSystemEntries(d).Any()) continue;
            try { Directory.Delete(d); } catch (IOException) { }
        }
    }

    private static FileSnapshot Snapshot(RemoteEntry r, FileInfo local, bool pinned) => new()
    {
        ServerMtime = r.Mtime,
        Size = r.Size,
        LocalMtime = MtimeMs(local.LastWriteTimeUtc),
        Pinned = pinned,
    };

    private bool WasPinned(string rel) => State.GetFile(rel)?.Pinned ?? true;

    private void RecycleLocal(string rel, string reason)
    {
        try
        {
            FileSystem.DeleteFile(LocalPath(rel), UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            State.RemoveFile(rel);
            _log($"{reason}，本地副本已移到回收站: {rel}");
        }
        catch (Exception ex)
        {
            _log($"无法删除本地 {rel}: {ex.Message}");
        }
    }

    // ---------- transfers ----------

    /// <summary>Downloads a server file into the mirror folder and records it as kept.</summary>
    private async Task DownloadAsync(RemoteEntry e, bool pinned)
    {
        if (!_busy.TryAdd(e.Path, 0)) return;
        Changed?.Invoke();
        try
        {
            await _slots.WaitAsync(_cts.Token);
            var item = _transfers.Start(e.Path, TransferDirection.Download, e.Size);
            try
            {
                var full = LocalPath(e.Path);
                await _api.DownloadToFileAsync(e, full, new Progress<long>(item.Report), _cts.Token);
                State.SetFile(e.Path, Snapshot(e, new FileInfo(full), pinned));
                State.Save();
                item.Complete();
            }
            catch (Exception ex)
            {
                item.Fail(ex is OperationCanceledException ? "已取消" : ex.Message);
                if (ex is not OperationCanceledException) _log($"下载 {e.Path} 失败: {ex.Message}");
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _busy.TryRemove(e.Path, out _);
            Changed?.Invoke();
        }
    }

    /// <summary>Uploads a file from the mirror folder and records the synced state.</summary>
    private Task UploadAsync(string rel, long baseMtime) => UploadFromAsync(LocalPath(rel), rel, baseMtime, trackAsKept: true);

    /// <summary>
    /// Uploads <paramref name="source"/> to <paramref name="rel"/>. With <paramref name="trackAsKept"/>
    /// the source is the mirror copy and its snapshot is recorded.
    /// </summary>
    private async Task<RemoteEntry?> UploadFromAsync(string source, string rel, long baseMtime, bool trackAsKept)
    {
        if (!_busy.TryAdd(rel, 0)) return null;
        Changed?.Invoke();
        try
        {
            await _slots.WaitAsync(_cts.Token);
            var before = new FileInfo(source);
            var item = _transfers.Start(rel, TransferDirection.Upload, before.Length);
            try
            {
                var entry = await _api.UploadAsync(source, rel, baseMtime, new Progress<long>(item.Report), _cts.Token);
                var remote = new Dictionary<string, RemoteEntry>(_remote, StringComparer.OrdinalIgnoreCase) { [rel] = entry };
                _remote = remote;
                if (trackAsKept)
                {
                    // Snapshot the pre-upload local state: if the file changed meanwhile,
                    // the next refresh sees the difference and uploads again.
                    State.SetFile(rel, new FileSnapshot
                    {
                        ServerMtime = entry.Mtime,
                        Size = entry.Size,
                        LocalMtime = MtimeMs(before.LastWriteTimeUtc),
                        Pinned = WasPinned(rel),
                    });
                    State.Save();
                }
                item.Complete();
                _log($"已上传: {rel}");
                return entry;
            }
            catch (Exception ex)
            {
                item.Fail(ex is OperationCanceledException ? "已取消" : ex.Message);
                if (ex is not OperationCanceledException) _log($"上传 {rel} 失败: {ex.Message}");
                return null;
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _busy.TryRemove(rel, out _);
            Changed?.Invoke();
        }
    }

    // ---------- user actions ----------

    /// <summary>"Always keep on this device".</summary>
    public async Task KeepAsync(string rel, bool isDir)
    {
        if (!isDir)
        {
            var e = _remote.GetValueOrDefault(rel);
            var snap = State.GetFile(rel);
            if (snap != null && File.Exists(LocalPath(rel)))
            {
                snap.Pinned = true;
                State.Save();
                Changed?.Invoke();
            }
            else if (e != null)
            {
                await DownloadAsync(e, pinned: true);
            }
            return;
        }
        State.AddKeptFolder(rel);
        State.Save();
        Directory.CreateDirectory(LocalPath(rel));
        foreach (var d in _remote.Values.Where(e => e.IsDir && LocalState.IsSameOrUnder(e.Path, rel)))
            Directory.CreateDirectory(LocalPath(d.Path));
        var jobs = RemoteFilesUnder(rel).Where(e => !File.Exists(LocalPath(e.Path)))
            .Select(e => DownloadAsync(e, pinned: true)).ToList();
        Changed?.Invoke();
        await Task.WhenAll(jobs);
    }

    /// <summary>Unchecks "Always keep": local copies stay, but are no longer pinned.</summary>
    public void Unpin(string rel)
    {
        State.UnkeepFolderTree(rel);
        State.Save();
        Changed?.Invoke();
    }

    /// <summary>Kept files at or under <paramref name="rel"/> whose local changes are not uploaded yet.</summary>
    public List<string> UnsyncedUnder(string rel, bool isDir)
    {
        IEnumerable<string> files = isDir
            ? (Directory.Exists(LocalPath(rel))
                ? Directory.EnumerateFiles(LocalPath(rel), "*", System.IO.SearchOption.AllDirectories).Where(f => !IsTemp(f)).Select(ToRel)
                : [])
            : File.Exists(LocalPath(rel)) ? [rel] : [];
        return files.Where(f => GetFileStatus(f) is FileStatus.PendingUpload or FileStatus.LocalOnly).ToList();
    }

    /// <summary>"Free up space": delete local copies; the server copy stays.</summary>
    public void FreeUp(string rel, bool isDir)
    {
        State.UnkeepFolderTree(rel);
        var full = LocalPath(rel);
        if (isDir)
        {
            if (Directory.Exists(full))
            {
                foreach (var f in Directory.EnumerateFiles(full, "*", System.IO.SearchOption.AllDirectories).ToList())
                {
                    var fr = ToRel(f);
                    if (_busy.ContainsKey(fr)) continue;
                    if (_remote.ContainsKey(fr) || IsTemp(f)) TryDelete(f);
                    State.RemoveFile(fr);
                }
                RemoveEmptyStaleDirsUnder(full);
            }
        }
        else if (!_busy.ContainsKey(rel))
        {
            TryDelete(full);
            State.RemoveFile(rel);
        }
        State.Save();
        _log($"已释放空间: {rel}");
        Changed?.Invoke();
    }

    private static void RemoveEmptyStaleDirsUnder(string full)
    {
        foreach (var d in Directory.EnumerateDirectories(full, "*", System.IO.SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length).Append(full))
        {
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
            catch (IOException) { }
        }
    }

    private void TryDelete(string full)
    {
        try { File.Delete(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"无法删除 {full}: {ex.Message}");
        }
    }

    /// <summary>Opens a file with its default program, downloading (and keeping) it first if needed.</summary>
    public async Task OpenAsync(string rel)
    {
        var full = LocalPath(rel);
        if (!File.Exists(full))
        {
            var e = _remote.GetValueOrDefault(rel);
            if (e == null) return;
            await DownloadAsync(e, pinned: false);
            if (!File.Exists(full)) return;
        }
        Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
    }

    /// <summary>Uploads files/folders from anywhere (e.g. dragged from Explorer) into a server folder.</summary>
    public async Task UploadPathsAsync(IEnumerable<string> paths, string targetFolder)
    {
        var jobs = new List<Task>();
        foreach (var p in paths)
        {
            var name = Path.GetFileName(p.TrimEnd('\\'));
            var rel = Join(targetFolder, name);
            if (Directory.Exists(p))
            {
                await _api.MkdirAsync(rel, _cts.Token);
                foreach (var d in Directory.EnumerateDirectories(p, "*", System.IO.SearchOption.AllDirectories))
                    await _api.MkdirAsync(Join(rel, Path.GetRelativePath(p, d).Replace('\\', '/')), _cts.Token);
                foreach (var f in Directory.EnumerateFiles(p, "*", System.IO.SearchOption.AllDirectories))
                    jobs.Add(UploadExternalAsync(f, Join(rel, Path.GetRelativePath(p, f).Replace('\\', '/'))));
            }
            else if (File.Exists(p))
            {
                jobs.Add(UploadExternalAsync(p, rel));
            }
        }
        await Task.WhenAll(jobs);
        await RefreshAsync();
    }

    private async Task UploadExternalAsync(string source, string rel)
    {
        bool fromMirror = string.Equals(Path.GetFullPath(source), LocalPath(rel), StringComparison.OrdinalIgnoreCase);
        var entry = await UploadFromAsync(source, rel, _remote.GetValueOrDefault(rel)?.Mtime ?? 0, trackAsKept: fromMirror);
        // Uploading into a kept folder: put a copy in the mirror so it is not downloaded again.
        if (entry != null && !fromMirror && State.IsInKeptFolder(rel))
        {
            var full = LocalPath(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Copy(source, full, overwrite: true);
            File.SetLastWriteTimeUtc(full, DateTimeOffset.FromUnixTimeMilliseconds(entry.Mtime).UtcDateTime);
            State.SetFile(rel, Snapshot(entry, new FileInfo(full), pinned: true));
            State.Save();
        }
    }

    /// <summary>Saves a server file or folder to an arbitrary local path without keeping it.</summary>
    public async Task DownloadToAsync(string rel, bool isDir, string targetPath)
    {
        var files = isDir ? RemoteFilesUnder(rel).ToList() : _remote.TryGetValue(rel, out var one) ? [one] : [];
        var jobs = files.Select(async e =>
        {
            var dest = isDir ? Path.Combine(targetPath, Path.GetRelativePath(rel, e.Path).Replace('/', '\\')) : targetPath;
            await _slots.WaitAsync(_cts.Token);
            var item = _transfers.Start(e.Path, TransferDirection.Download, e.Size);
            try
            {
                await _api.DownloadToFileAsync(e, dest, new Progress<long>(item.Report), _cts.Token);
                item.Complete();
            }
            catch (Exception ex)
            {
                item.Fail(ex.Message);
                _log($"下载 {e.Path} 失败: {ex.Message}");
            }
            finally
            {
                _slots.Release();
            }
        });
        await Task.WhenAll(jobs);
        _log($"已下载到 {targetPath}");
    }

    public async Task MkdirAsync(string rel)
    {
        await _api.MkdirAsync(rel, _cts.Token);
        if (State.IsInKeptFolder(rel)) Directory.CreateDirectory(LocalPath(rel));
        _log($"已新建文件夹: {rel}");
        await RefreshAsync();
    }

    public async Task RenameAsync(string rel, string newName, bool isDir)
    {
        var target = Join(LocalState.Parent(rel), newName);
        await _api.MoveAsync(rel, target, _cts.Token);
        var from = LocalPath(rel);
        var to = LocalPath(target);
        try
        {
            if (isDir && Directory.Exists(from)) Directory.Move(from, to);
            else if (!isDir && File.Exists(from)) File.Move(from, to);
        }
        catch (IOException ex)
        {
            _log($"本地重命名失败（下次刷新时会重新同步）: {ex.Message}");
        }
        State.MoveTree(rel, target);
        State.Save();
        _log($"已重命名: {rel} → {target}");
        await RefreshAsync();
    }

    /// <summary>Deletes on the server; local copies go to the Recycle Bin.</summary>
    public async Task DeleteAsync(string rel, bool isDir)
    {
        await _api.DeleteAsync(rel, _cts.Token);
        var full = LocalPath(rel);
        try
        {
            if (isDir && Directory.Exists(full))
                FileSystem.DeleteDirectory(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else if (!isDir && File.Exists(full))
                FileSystem.DeleteFile(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex)
        {
            _log($"删除本地副本失败: {ex.Message}");
        }
        State.RemoveTree(rel);
        State.Save();
        _log($"已删除: {rel}");
        await RefreshAsync();
    }
}
