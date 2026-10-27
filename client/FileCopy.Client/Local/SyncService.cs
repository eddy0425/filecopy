using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using FileCopy.Client.Api;
using FileCopy.Client.Backup;
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

    private readonly ServerApi _api;
    private readonly TransferQueue _transfers;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrentFiles);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _busy = new(StringComparer.OrdinalIgnoreCase);
    // Copy-on-write: readers take the reference once; replacements hold _remoteLock, since uploads
    // finish on thread-pool threads and must not drop each other's entries (or a newer tree).
    private volatile Dictionary<string, RemoteEntry> _remote = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _remoteLock = new();
    private volatile ChildIndex? _children; // for List; rebuilt when _remote is replaced
    private volatile bool _treeLoaded;
    private Task? _started;

    public string Root { get; }
    public LocalState State { get; }

    /// <summary>Raised (on a worker thread) whenever listings or statuses may have changed.</summary>
    public event Action? Changed;

    /// <summary>Result of the latest successful ping (null before the first one).</summary>
    public PingResult? LastPing { get; private set; }

    public SyncService(string serverUrl, string token, string equipmentId, string root, TransferQueue transfers, Action<string> log)
    {
        Root = Path.GetFullPath(root).TrimEnd('\\');
        Directory.CreateDirectory(Root);
        _api = new ServerApi(serverUrl, token, equipmentId);
        _transfers = transfers;
        _log = log;
        State = LocalState.Load(serverUrl, Root);
    }

    /// <summary>Connects and does the first refresh. Repeated calls return the same task.</summary>
    public Task StartAsync() => _started ??= StartCoreAsync();

    private async Task StartCoreAsync()
    {
        await PingAsync();
        // No periodic refresh: syncing only happens at startup, on "刷新"/F5, on backup, and after file actions.
        await RefreshAsync(throwOnTreeError: true);
        // RefreshAsync is skipped when another refresh already holds the lock; callers of StartAsync
        // rely on the server tree being loaded, so make sure of it (and throw) here.
        if (!_treeLoaded) SetRemote(await _api.GetTreeAsync(_cts.Token));
    }

    private Dictionary<string, RemoteEntry> SetRemote(IEnumerable<RemoteEntry> tree)
    {
        var remote = new Dictionary<string, RemoteEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in tree) remote.TryAdd(e.Path, e); // server may be case-sensitive; first wins
        lock (_remoteLock) _remote = remote;
        _treeLoaded = true;
        Changed?.Invoke();
        return remote;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _api.Dispose();
        State.Flush();
    }

    // ---------- paths & status ----------

    public string LocalPath(string rel) => Path.Combine(Root, rel.Replace('/', '\\'));

    private string ToRel(string full) => Path.GetRelativePath(Root, full).Replace('\\', '/');

    private static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;

    private static long MtimeMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private static bool IsTemp(string path) => path.EndsWith(".fcpart", StringComparison.OrdinalIgnoreCase);

    /// <summary>A "*.7z" directly in the backup folder (not in a subfolder).</summary>
    private static bool IsBackupArchive(string rel) =>
        rel.StartsWith(BackupService.RemoteBackupDir + "/", StringComparison.OrdinalIgnoreCase)
        && rel.IndexOf('/', BackupService.RemoteBackupDir.Length + 1) < 0
        && rel.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);

    public bool IsBusy(string rel) => _busy.ContainsKey(rel);

    public RemoteEntry? GetRemote(string rel) => _remote.GetValueOrDefault(rel);

    /// <summary>Files (remote and local-only) at or under a folder, as relative paths.</summary>
    public IEnumerable<RemoteEntry> RemoteFilesUnder(string folder) =>
        _remote.Values.Where(e => !e.IsDir && LocalState.IsSameOrUnder(e.Path, folder));

    public FileStatus GetFileStatus(string rel) => GetFileStatus(rel, new FileInfo(LocalPath(rel)));

    /// <summary>Status from an already known local file (e.g. from listing its folder); null when there is none.</summary>
    public FileStatus GetFileStatus(string rel, FileInfo? local)
    {
        if (_busy.ContainsKey(rel)) return FileStatus.Transferring;
        var remote = _remote.GetValueOrDefault(rel);
        if (local is not { Exists: true }) return FileStatus.CloudOnly;
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

    /// <param name="LocalFiles">The folder's local files by relative path, to get statuses without touching the disk again.</param>
    public record Listing(List<RemoteEntry> Remote, List<string> LocalOnlyFiles, List<string> LocalOnlyDirs,
        Dictionary<string, FileInfo> LocalFiles);

    private sealed record ChildIndex(Dictionary<string, RemoteEntry> Remote, ILookup<string, RemoteEntry> Children);

    private ILookup<string, RemoteEntry> ChildrenOf(Dictionary<string, RemoteEntry> remote)
    {
        var index = _children;
        if (index?.Remote != remote)
            _children = index = new ChildIndex(remote, remote.Values.ToLookup(e => LocalState.Parent(e.Path), StringComparer.OrdinalIgnoreCase));
        return index.Children;
    }

    /// <summary>Direct children of a folder: everything on the server plus local-only items.</summary>
    public Listing List(string folder)
    {
        var remoteTree = _remote;
        var remote = ChildrenOf(remoteTree)[folder].ToList();
        var localFiles = new List<string>();
        var localDirs = new List<string>();
        var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var dir = new DirectoryInfo(LocalPath(folder));
        if (dir.Exists)
        {
            foreach (var item in dir.EnumerateFileSystemInfos())
            {
                var rel = ToRel(item.FullName);
                if (item is FileInfo fi)
                {
                    if (IsTemp(fi.Name)) continue;
                    files[rel] = fi;
                    if (!remoteTree.ContainsKey(rel)) localFiles.Add(rel);
                }
                else if (!remoteTree.ContainsKey(rel)) localDirs.Add(rel);
            }
        }
        return new Listing(remote, localFiles, localDirs, files);
    }

    // ---------- refresh / reconcile ----------

    /// <summary>Fetches the server tree and brings kept items up to date in both directions.</summary>
    /// <param name="throwOnTreeError">Rethrow (instead of logging) when the server tree cannot be fetched.</param>
    public async Task RefreshAsync(bool throwOnTreeError = false)
    {
        if (!await _refreshLock.WaitAsync(0)) return;
        var jobs = new List<Task>();
        var treeFetched = false;
        try
        {
            var remote = SetRemote(await _api.GetTreeAsync(_cts.Token));
            treeFetched = true;
            // Off the UI thread: a mirror with thousands of files takes a while to scan.
            await Task.Run(() => Reconcile(remote, jobs));
        }
        catch (Exception ex) when (IsCancelled(ex)) { }
        catch (Exception ex) when (treeFetched || !throwOnTreeError)
        {
            _log("刷新失败: " + ErrorText(ex));
        }
        finally
        {
            _refreshLock.Release();
        }
        Changed?.Invoke();
        await Task.WhenAll(jobs);
    }

    /// <summary>Brings the mirror and the server in step; transfers it starts are added to <paramref name="jobs"/>.</summary>
    private void Reconcile(Dictionary<string, RemoteEntry> remote, List<Task> jobs)
    {
        // One walk for files and folders; the FileInfos come with size and time already read.
        var files = new List<FileInfo>();
        var dirs = new List<DirectoryInfo>();
        foreach (var item in new DirectoryInfo(Root).EnumerateFileSystemInfos("*", System.IO.SearchOption.AllDirectories))
        {
            if (item is FileInfo f) { if (!IsTemp(f.Name)) files.Add(f); }
            else dirs.Add((DirectoryInfo)item);
        }

        // 1. Local files.
        var localSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fi in files)
        {
            var rel = ToRel(fi.FullName);
            localSet.Add(rel);
            if (_busy.ContainsKey(rel)) continue;
            var r = remote.GetValueOrDefault(rel);
            var snap = State.GetFile(rel);

            // Read-only folders: the server copy wins, local edits and new files are never uploaded.
            var editedLocally = r is { IsDir: false } && (snap == null ? !SameAsRemote(fi, r) : LocalChanged(fi, snap));
            if (IsReadOnly(rel) && (r == null || editedLocally))
            {
                if (r == null && snap != null && !LocalChanged(fi, snap))
                {
                    if (!UploadedSince(rel)) RecycleLocal(rel, "云端已删除");
                    continue;
                }
                RecycleLocal(rel, r == null ? "只读文件夹，不能新增文件" : "只读文件夹，不能修改，已恢复云端版本");
                if (r != null) jobs.Add(DownloadAsync(r, snap?.Pinned ?? true));
                continue;
            }

            if (r == null || r.IsDir)
            {
                if (r != null) continue; // name clash with a remote folder; leave alone
                if (snap != null && !LocalChanged(fi, snap))
                {
                    // Deleted on the server: move our copy to the Recycle Bin (recoverable).
                    if (!UploadedSince(rel)) RecycleLocal(rel, "云端已删除");
                    continue;
                }
                // New local file, or changed after the server deleted it.
                KeepNewLocalFolder(rel, remote);
                jobs.Add(UploadAsync(rel, baseMtime: 0));
                continue;
            }

            if (snap == null)
            {
                if (SameAsRemote(fi, r)) State.SetFile(rel, Snapshot(r, fi, pinned: true));
                // Server keeps a conflict copy, except for backup archives, which always replace it.
                else jobs.Add(UploadAsync(rel, baseMtime: IsBackupArchive(rel) ? r.Mtime : 0));
            }
            else if (LocalChanged(fi, snap))
            {
                // Backup archives always replace the server copy (no conflict copy), as above.
                jobs.Add(UploadAsync(rel, IsBackupArchive(rel) ? r.Mtime : snap.ServerMtime));
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
            // Being downloaded: the file may have landed (with its snapshot) after the scan above.
            if (_busy.ContainsKey(r.Path)) continue;
            // A snapshot without a local file means the copy was deleted outside the app.
            State.RemoveFile(r.Path);
            if (State.IsInKeptFolder(r.Path)) jobs.Add(DownloadAsync(r, pinned: true));
        }

        // 3. Snapshots of files that vanished on both sides.
        foreach (var rel in State.FileKeys())
            if (!localSet.Contains(rel) && !remote.ContainsKey(rel) && !_busy.ContainsKey(rel)) State.RemoveFile(rel);

        RemoveEmptyStaleDirs(remote, dirs);
        State.Save();
    }

    /// <summary>
    /// Missing from the tree a scan works on, but uploaded meanwhile (the upload adds it to the current tree):
    /// not deleted on the server. The scan takes a while, and transfers finish during it.
    /// </summary>
    private bool UploadedSince(string rel) => _remote.ContainsKey(rel);

    /// <summary>
    /// The upload of a local file creates its missing folders on the server. Its files are kept (pinned),
    /// so keep the topmost new folder too; otherwise it would show as cloud-only.
    /// </summary>
    private void KeepNewLocalFolder(string rel, Dictionary<string, RemoteEntry> remote)
    {
        string? top = null;
        for (var p = LocalState.Parent(rel); p.Length > 0 && !remote.ContainsKey(p); p = LocalState.Parent(p)) top = p;
        if (top != null && !State.IsInKeptFolder(top)) State.AddKeptFolder(top);
    }

    /// <summary>Removes empty local folders that no longer exist on the server; <paramref name="dirs"/> are all mirror folders.</summary>
    private void RemoveEmptyStaleDirs(Dictionary<string, RemoteEntry> remote, List<DirectoryInfo> dirs)
    {
        foreach (var d in dirs.Select(d => d.FullName).OrderByDescending(d => d.Length))
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
                await _api.DownloadToFileAsync(e, full, item, _cts.Token,
                    canReplace: () => CanReplaceLocal(e), onRetry: RetryReporter(item));
                State.SetFile(e.Path, Snapshot(e, new FileInfo(full), pinned));
                State.Save();
                item.Complete();
            }
            catch (Exception ex)
            {
                item.Fail(IsCancelled(ex) ? "已取消" : ErrorText(ex));
                if (!IsCancelled(ex)) _log($"下载 {e.Path} 失败: {ErrorText(ex)}");
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

    /// <summary>
    /// A download may only replace a local copy that is still the last synced version (or identical to
    /// the server file). The decision to download was made earlier; the user may have edited or created
    /// the file since, and that must not be overwritten.
    /// </summary>
    private bool CanReplaceLocal(RemoteEntry e)
    {
        var local = new FileInfo(LocalPath(e.Path));
        if (!local.Exists || SameAsRemote(local, e)) return true;
        var snap = State.GetFile(e.Path);
        return snap != null && !LocalChanged(local, snap);
    }

    /// <summary>Cancelled by Dispose, as opposed to a request timeout (also an OperationCanceledException).</summary>
    private bool IsCancelled(Exception ex) => ex is OperationCanceledException && _cts.IsCancellationRequested;

    /// <summary>
    /// User-facing text for a transfer/connection error. Reports any <see cref="OperationCanceledException"/> as a timeout,
    /// relying on <see cref="ServerApi"/>'s per-request timeouts being the only other source of cancellation:
    /// rule out cancellation by <see cref="Dispose"/> first (<see cref="IsCancelled"/> here; outside this class,
    /// check that the service has not been disposed or replaced).
    /// </summary>
    public static string ErrorText(Exception ex) => ex is OperationCanceledException ? "连接超时" : ex.Message;

    private static Action<int, Exception> RetryReporter(TransferItem item) =>
        (attempt, ex) => item.Retrying(attempt, ServerApi.MaxRetries, ErrorText(ex));

    /// <summary>Checks that the server is reachable (used for the connection heartbeat).</summary>
    public async Task<PingResult> PingAsync() => LastPing = await _api.PingAsync(_cts.Token);

    /// <summary>Checks a two-step verification code with the server (to unlock the window).</summary>
    public Task<bool> VerifyTotpAsync(string code) => _api.VerifyTotpAsync(code, _cts.Token);

    /// <summary>Uses an EquipmentId assigned in the control panel from now on.</summary>
    public void SetEquipmentId(string equipmentId) => _api.SetEquipmentId(equipmentId);

    // ---------- read-only folders ----------

    /// <summary>
    /// Whether rel is (or would be created) inside a folder set read-only in the control panel.
    /// The nearest known server entry decides: the server marks everything at or under such a folder.
    /// </summary>
    public bool IsReadOnly(string rel)
    {
        var remote = _remote;
        for (var p = rel; p.Length > 0; p = LocalState.Parent(p))
            if (remote.TryGetValue(p, out var e)) return e.ReadOnly;
        return false;
    }

    /// <summary>Like <see cref="IsReadOnly"/>, but also true when rel contains a read-only folder (deleting or moving it would take that along).</summary>
    private bool TouchesReadOnly(string rel) =>
        IsReadOnly(rel) || _remote.Values.Any(e => e.ReadOnly && LocalState.IsSameOrUnder(e.Path, rel));

    private static void ThrowIf(bool readOnly)
    {
        if (readOnly) throw new ReadOnlyFolderException();
    }

    /// <summary>Uploads a file from the mirror folder and records the synced state.</summary>
    private Task UploadAsync(string rel, long baseMtime) => UploadFromAsync(LocalPath(rel), rel, baseMtime, trackAsKept: true);

    /// <summary>
    /// Uploads <paramref name="source"/> to <paramref name="rel"/>. With <paramref name="trackAsKept"/>
    /// the source is the mirror copy and its snapshot is recorded.
    /// </summary>
    private async Task<RemoteEntry?> UploadFromAsync(string source, string rel, long baseMtime, bool trackAsKept)
    {
        if (!_busy.TryAdd(rel, 0))
        {
            _log($"{rel} 正在传输中，本次跳过（稍后刷新会继续同步）");
            return null;
        }
        Changed?.Invoke();
        try
        {
            await _slots.WaitAsync(_cts.Token);
            TransferItem? item = null;
            try
            {
                // Inside the try: the source may have vanished (FileInfo.Length throws), and the slot must still be released.
                var before = new FileInfo(source);
                item = _transfers.Start(rel, TransferDirection.Upload, before.Length);
                var entry = await _api.UploadAsync(source, rel, baseMtime, item, _cts.Token, RetryReporter(item));
                lock (_remoteLock)
                    _remote = new Dictionary<string, RemoteEntry>(_remote, StringComparer.OrdinalIgnoreCase) { [rel] = entry };
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
                item?.Fail(IsCancelled(ex) ? "已取消" : ErrorText(ex));
                if (!IsCancelled(ex)) _log($"上传 {rel} 失败: {ErrorText(ex)}");
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
            var local = new FileInfo(LocalPath(rel));
            if (snap != null && local.Exists)
            {
                snap.Pinned = true;
                State.Save();
                Changed?.Invoke();
            }
            else if (local.Exists && e is { IsDir: false })
            {
                // A local copy that was never synced (e.g. copied into the mirror folder): adopt it
                // or upload it like RefreshAsync does, never overwrite it with the server version.
                if (SameAsRemote(local, e))
                {
                    State.SetFile(rel, Snapshot(e, local, pinned: true));
                    State.Save();
                    Changed?.Invoke();
                }
                else if (e.ReadOnly)
                {
                    RecycleLocal(rel, "只读文件夹，不能修改，已恢复云端版本");
                    await DownloadAsync(e, pinned: true);
                }
                else await UploadAsync(rel, baseMtime: IsBackupArchive(rel) ? e.Mtime : 0);
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
                    if (IsTemp(f)) TryDelete(f);
                    else FreeUpFile(fr, f);
                }
                RemoveEmptyStaleDirsUnder(full);
            }
        }
        else if (!_busy.ContainsKey(rel))
        {
            FreeUpFile(rel, full);
        }
        State.Save();
        _log($"已释放空间: {rel}");
        Changed?.Invoke();
    }

    /// <summary>
    /// Deletes the local copy of a server file. Local-only files are kept, and so is their snapshot:
    /// a snapshot without a server file means "deleted on the server", and dropping it would make the
    /// next refresh re-upload the file as new.
    /// </summary>
    private void FreeUpFile(string rel, string full)
    {
        if (!_remote.ContainsKey(rel)) return;
        // Keep the snapshot if the copy stays (e.g. locked), or the next refresh would re-adopt it as pinned.
        if (TryDelete(full)) State.RemoveFile(rel);
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

    private bool TryDelete(string full)
    {
        try { File.Delete(full); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"无法删除 {full}: {ex.Message}");
            return false;
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
        ThrowIf(targetFolder.Length > 0 && IsReadOnly(targetFolder));
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

    /// <summary>Uploads one local file to an exact server path, overwriting the server copy. Returns false on failure.</summary>
    public async Task<bool> UploadFileAsync(string source, string rel)
    {
        // Until the first refresh has loaded the server tree, baseMtime would be 0
        // and the server would keep the previous version as a conflict copy.
        try { await StartAsync(); }
        catch (Exception) { return false; } // connection failure is already logged by the caller of StartAsync
        var ok = await UploadExternalAsync(source, rel);
        await RefreshAsync();
        return ok;
    }

    private async Task<bool> UploadExternalAsync(string source, string rel)
    {
        bool fromMirror = string.Equals(Path.GetFullPath(source), LocalPath(rel), StringComparison.OrdinalIgnoreCase);
        var previous = _remote.GetValueOrDefault(rel); // UploadFromAsync replaces it with the new entry
        var entry = await UploadFromAsync(source, rel, previous?.Mtime ?? 0, trackAsKept: fromMirror);
        // Uploading into a kept folder: put a copy in the mirror so it is not downloaded again.
        if (entry != null && !fromMirror && State.IsInKeptFolder(rel))
        {
            var full = LocalPath(rel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                KeepUnsyncedAsConflictCopy(rel, new FileInfo(source), previous);
                File.Copy(source, full, overwrite: true);
                // Cosmetic only: the snapshot below records whatever mtime the copy ends up with, so a
                // failure here must not skip it (the copy would then look edited and be uploaded again).
                try { File.SetLastWriteTimeUtc(full, DateTimeOffset.FromUnixTimeMilliseconds(entry.Mtime).UtcDateTime); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                State.SetFile(rel, Snapshot(entry, new FileInfo(full), pinned: true));
                State.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The upload succeeded; the next refresh reconciles the local copy.
                _log($"已上传 {rel}，但本地副本未能更新（可能被占用）；若本地有未上传的修改，" +
                     $"下次刷新后刚上传的版本会保留为冲突副本: {ex.Message}");
            }
        }
        return entry != null;
    }

    /// <summary>
    /// Before the mirror copy of <paramref name="rel"/> is replaced by <paramref name="incoming"/>: if it holds
    /// changes that never reached the server, renames it like the server names conflict copies
    /// ("name (conflict 20260102-150405).ext"). The next refresh uploads it as a new file.
    /// <paramref name="previous"/> is the server entry before the upload: a copy without a snapshot that
    /// matches it is merely not adopted yet, not changed.
    /// </summary>
    private void KeepUnsyncedAsConflictCopy(string rel, FileInfo incoming, RemoteEntry? previous)
    {
        var local = new FileInfo(LocalPath(rel));
        if (!local.Exists) return;
        if (local.Length == incoming.Length && local.LastWriteTimeUtc == incoming.LastWriteTimeUtc) return;
        var snap = State.GetFile(rel);
        if (snap != null ? !LocalChanged(local, snap) : previous is { IsDir: false } && SameAsRemote(local, previous)) return;

        var stem = Path.Combine(local.DirectoryName!, Path.GetFileNameWithoutExtension(local.Name))
                   + $" (conflict {DateTime.Now:yyyyMMdd-HHmmss})";
        var copy = stem + local.Extension;
        for (int i = 2; File.Exists(copy) || Directory.Exists(copy); i++) copy = $"{stem} {i}{local.Extension}";
        File.Move(local.FullName, copy);
        _log($"{rel} 的本地修改尚未上传，已另存为 {ToRel(copy)}");
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
                await _api.DownloadToFileAsync(e, dest, item, _cts.Token, onRetry: RetryReporter(item));
                item.Complete();
            }
            catch (Exception ex)
            {
                item.Fail(IsCancelled(ex) ? "已取消" : ErrorText(ex));
                if (!IsCancelled(ex)) _log($"下载 {e.Path} 失败: {ErrorText(ex)}");
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
        ThrowIf(IsReadOnly(rel));
        await _api.MkdirAsync(rel, _cts.Token);
        if (State.IsInKeptFolder(rel)) Directory.CreateDirectory(LocalPath(rel));
        _log($"已新建文件夹: {rel}");
        await RefreshAsync();
    }

    public async Task RenameAsync(string rel, string newName, bool isDir)
    {
        var target = Join(LocalState.Parent(rel), newName);
        ThrowIf(TouchesReadOnly(rel) || IsReadOnly(target));
        var from = LocalPath(rel);
        var to = LocalPath(target);
        // Refuse early (the server also answers 409); this also catches local-only items the server
        // cannot see. A case-only rename is the same item.
        if (!target.Equals(rel, StringComparison.OrdinalIgnoreCase) &&
            (_remote.ContainsKey(target) || File.Exists(to) || Directory.Exists(to)))
            throw new IOException($"已存在同名项: {newName}");
        // Individually pinned files (at or under rel) that no kept folder would re-download, as server
        // entries under their new names (the rename keeps size and mtime).
        var pinnedTargets = State.FileKeys()
            .Where(k => LocalState.IsSameOrUnder(k, rel) && State.GetFile(k)?.Pinned == true && !State.IsInKeptFolder(k))
            .Select(k => _remote.GetValueOrDefault(k) is { IsDir: false } e ? e with { Path = target + k[rel.Length..] } : null)
            .OfType<RemoteEntry>()
            .ToList();
        await _api.MoveAsync(rel, target, _cts.Token);
        var movedLocally = true;
        try
        {
            if (isDir && Directory.Exists(from)) Directory.Move(from, to);
            else if (!isDir && File.Exists(from)) File.Move(from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            movedLocally = false;
            _log($"本地重命名失败（下次刷新时会重新同步）: {ex.Message}");
        }
        // Move what is not locked one by one, so local-only files are not re-uploaded under the old name.
        if (!movedLocally && isDir && Directory.Exists(from))
        {
            foreach (var f in Directory.EnumerateFiles(from, "*", System.IO.SearchOption.AllDirectories).Where(f => !IsTemp(f)).ToList())
            {
                var fr = ToRel(f);
                var tr = target + fr[rel.Length..];
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LocalPath(tr))!);
                    File.Move(f, LocalPath(tr));
                    State.MoveTree(fr, tr);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log($"无法移动 {fr}（下次刷新时会处理）: {ex.Message}");
                }
            }
        }
        // If the local copy stayed behind, so do its snapshots: the next refresh then treats the old
        // path as deleted on the server (recycled, or re-uploaded if edited) instead of as new files.
        State.MoveTree(rel, target, includeFiles: movedLocally);
        State.Save();
        _log($"已重命名: {rel} → {target}");
        await RefreshAsync();
        // Fetch the individually pinned files under their new names. Not via KeepAsync: the refresh above
        // returns early if another one is running, and _remote would not know the new names yet.
        if (!movedLocally)
            await Task.WhenAll(pinnedTargets.Where(e => !File.Exists(LocalPath(e.Path))).Select(e => DownloadAsync(e, pinned: true)));
    }

    /// <summary>Deletes on the server (into its recycle bin); local copies go to the Recycle Bin.</summary>
    public Task DeleteAsync(string rel, bool isDir) => DeleteManyAsync([(rel, isDir)]);

    /// <summary>
    /// <see cref="DeleteAsync"/> for several items, refreshing once at the end. Stops at the first
    /// failure; what was deleted until then is still synced.
    /// </summary>
    public async Task DeleteManyAsync(IReadOnlyList<(string Rel, bool IsDir)> items)
    {
        try
        {
            foreach (var (rel, isDir) in items)
            {
                ThrowIf(TouchesReadOnly(rel));
                await _api.DeleteAsync(rel, _cts.Token);
                var full = LocalPath(rel);
                var deletedLocally = true;
                try
                {
                    if (isDir && Directory.Exists(full))
                        FileSystem.DeleteDirectory(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    else if (!isDir && File.Exists(full))
                        FileSystem.DeleteFile(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
                catch (Exception ex)
                {
                    deletedLocally = false;
                    _log($"删除本地副本失败（下次刷新时会重试）: {ex.Message}");
                }
                // A copy left behind (e.g. open in another program) keeps its snapshots: the next refresh then
                // sees it as deleted on the server and recycles it, instead of uploading it again as a new file.
                State.RemoveTree(rel, includeFiles: deletedLocally);
                _log($"已删除: {rel}");
            }
        }
        finally
        {
            State.Save();
            await RefreshAsync();
        }
    }
}
