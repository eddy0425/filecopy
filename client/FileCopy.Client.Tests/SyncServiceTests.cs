using System.IO;
using FileCopy.Client.Api;
using FileCopy.Client.Local;

namespace FileCopy.Client.Tests;

public sealed class SyncServiceTests : IDisposable
{
    private readonly TestServer _server = new();
    private readonly List<IDisposable> _cleanup = [];

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
        _server.Dispose();
    }

    private async Task<TestClient> Connect(string? root = null)
    {
        var c = new TestClient(_server, root);
        _cleanup.Add(c);
        await c.Sync.StartAsync().T();
        return c;
    }

    private static Task Refresh(TestClient c) => c.Sync.RefreshAsync().T();

    private static string StatePath(TestClient c) =>
        (string)typeof(LocalState).GetField("_path", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(c.Sync.State)!;

    // ---------- listing & status ----------

    [Fact]
    public async Task Start_ListsServerFiles_AsCloudOnly()
    {
        _server.Put("docs/a.txt", "a");
        _server.Put("top.txt", "t");
        var c = await Connect();

        var root = c.Sync.List("");
        Assert.Equal(["docs", "top.txt"], root.Remote.Select(e => e.Path).Order());
        Assert.Equal(FileStatus.CloudOnly, c.Sync.GetFileStatus("top.txt"));
        Assert.Equal(["docs/a.txt"], c.Sync.List("docs").Remote.Select(e => e.Path));
        Assert.False(File.Exists(c.Local("top.txt")));
    }

    [Fact]
    public async Task List_ShowsLocalOnlyItems_AndHidesTempFiles()
    {
        _server.Put("docs/a.txt", "a");
        var c = await Connect();
        c.Write("docs/mine.txt", "m");
        c.Write("docs/a.txt.fcpart", "partial download");
        Directory.CreateDirectory(c.Local("docs/local-dir"));

        var listing = c.Sync.List("docs");

        Assert.Equal(["docs/a.txt"], listing.Remote.Select(e => e.Path));
        Assert.Equal(["docs/mine.txt"], listing.LocalOnlyFiles);
        Assert.Equal(["docs/local-dir"], listing.LocalOnlyDirs);
        Assert.Equal(FileStatus.LocalOnly, c.Sync.GetFileStatus("docs/mine.txt"));
    }

    [Fact]
    public async Task Status_ServerEditNotYetDownloaded_IsPendingDownload()
    {
        _server.Put("a.txt", "v1");
        var c = await Connect();
        await c.Sync.KeepAsync("a.txt", false).T();
        _server.Put("a.txt", "v2", DateTime.UtcNow.AddMinutes(-1));

        // The open file cannot be replaced, so the download fails and the update stays pending.
        using (File.Open(c.Local("a.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Refresh(c);
        }
        Assert.Equal("v1", c.Read("a.txt"));
        Assert.False(File.Exists(c.Local("a.txt") + ".fcpart"));
        Assert.Equal(FileStatus.PendingDownload, c.Sync.GetFileStatus("a.txt"));

        await Refresh(c);
        Assert.Equal("v2", c.Read("a.txt"));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("a.txt"));
    }

    [Fact]
    public async Task Start_WithWrongToken_Throws()
    {
        using var bad = new SyncService(_server.Url, "wrong-token-0123456789", TestServer.EquipmentId, TestEnv.NewDir("mirror"), new(), _ => { });
        await Assert.ThrowsAsync<UnauthorizedException>(() => bad.StartAsync().T());
    }

    [Fact]
    public async Task ReadOnlyFolder_RevertsLocalEdits_AndRefusesChanges()
    {
        using var server = new TestServer(new { readOnlyFolders = new[] { "ro" } });
        server.Put("ro/a.txt", "server");
        server.Put("rw.txt", "x");
        var c = new TestClient(server);
        _cleanup.Add(c);
        await c.Sync.StartAsync().T();
        Assert.True(c.Sync.IsReadOnly("ro"));
        Assert.True(c.Sync.IsReadOnly("ro/a.txt"));
        Assert.True(c.Sync.IsReadOnly("ro/new/file.txt")); // not on the server yet: decided by the nearest parent
        Assert.False(c.Sync.IsReadOnly("rw.txt"));

        await c.Sync.KeepAsync("ro", isDir: true).T();
        Assert.Equal("server", c.Read("ro/a.txt"));
        c.Write("ro/a.txt", "edited");
        c.Write("ro/new.txt", "new");
        await Refresh(c);

        Assert.Equal("server", c.Read("ro/a.txt")); // restored from the server
        Assert.False(File.Exists(c.Local("ro/new.txt")));
        Assert.Equal("server", server.Read("ro/a.txt"));
        Assert.False(server.Exists("ro/new.txt"));
        Assert.Contains("只读文件夹", c.LogText);

        await Assert.ThrowsAsync<ReadOnlyFolderException>(() => c.Sync.DeleteAsync("ro/a.txt", false).T());
        await Assert.ThrowsAsync<ReadOnlyFolderException>(() => c.Sync.RenameAsync("ro", "other", true).T());
        await Assert.ThrowsAsync<ReadOnlyFolderException>(() => c.Sync.MkdirAsync("ro/sub").T());
        var local = Path.Combine(TestEnv.NewDir("src"), "up.txt");
        File.WriteAllText(local, "u");
        await Assert.ThrowsAsync<ReadOnlyFolderException>(() => c.Sync.UploadPathsAsync([local], "ro").T());
        Assert.True(server.Exists("ro/a.txt"));
        await c.Sync.DeleteAsync("rw.txt", false).T(); // elsewhere everything still works
        Assert.False(server.Exists("rw.txt"));
    }

    [Fact]
    public async Task ReadOnlyFolder_KeepWithEditedCopy_RestoresServerVersion_AndUploadsFail()
    {
        using var server = new TestServer(new { readOnlyFolders = new[] { "ro" } });
        server.Put("ro/a.txt", "server");
        var c = new TestClient(server);
        _cleanup.Add(c);
        await c.Sync.StartAsync().T();
        c.Write("ro/a.txt", "never synced local copy"); // e.g. copied into the mirror folder by hand

        await c.Sync.KeepAsync("ro/a.txt", isDir: false).T();
        Assert.Equal("server", c.Read("ro/a.txt"));
        Assert.Equal("server", server.Read("ro/a.txt"));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("ro/a.txt"));

        // Uploading to an exact path (what the backup does) reports failure instead of throwing.
        var local = Path.Combine(TestEnv.NewDir("src"), "b.txt");
        File.WriteAllText(local, "b");
        Assert.False(await c.Sync.UploadFileAsync(local, "ro/b.txt").T());
        Assert.False(server.Exists("ro/b.txt"));
        Assert.Contains("只读文件夹，不能修改", c.LogText);
    }

    [Fact]
    public async Task Delete_GoesToServerRecycleBin()
    {
        _server.Put("docs/a.txt", "a");
        var c = await Connect();
        await c.Sync.DeleteAsync("docs", isDir: true).T();

        Assert.False(_server.Exists("docs"));
        var trash = Path.Combine(_server.DataDir, ".filecopy", "trash");
        var item = Assert.Single(Directory.GetDirectories(trash));
        Assert.Equal("a", File.ReadAllText(Path.Combine(item, "item", "a.txt")));
        Assert.Contains(TestServer.EquipmentId, File.ReadAllText(Path.Combine(item, "meta.json")));
        Assert.DoesNotContain(c.Sync.List("").Remote, e => e.Path.StartsWith(".filecopy"));
    }

    // ---------- keep / free up ----------

    [Fact]
    public async Task KeepFolder_DownloadsEverything_AndMarksKept()
    {
        _server.Put("docs/a.txt", "a");
        _server.Put("docs/sub/b.txt", "bb");
        Directory.CreateDirectory(_server.PathOf("docs/empty"));
        var c = await Connect();

        await c.Sync.KeepAsync("docs", isDir: true).T();

        Assert.Equal("a", c.Read("docs/a.txt"));
        Assert.Equal("bb", c.Read("docs/sub/b.txt"));
        Assert.True(Directory.Exists(c.Local("docs/empty")));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("docs/a.txt"));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("docs/sub/b.txt"));
        Assert.True(c.Sync.State.IsPinned("docs/sub/b.txt"));

        // New server files in a kept folder arrive on refresh.
        _server.Put("docs/new.txt", "n");
        await Refresh(c);
        Assert.Equal("n", c.Read("docs/new.txt"));
    }

    [Fact]
    public async Task KeepFile_WithUnsyncedLocalCopy_DoesNotOverwriteIt()
    {
        _server.Put("a.txt", "server");
        var c = await Connect();
        // A file dropped into the mirror folder after the last refresh, under a name that exists on the server.
        c.Write("a.txt", "local edits");

        await c.Sync.KeepAsync("a.txt", isDir: false).T();

        Assert.Equal("local edits", c.Read("a.txt"));
    }

    [Fact]
    public async Task KeepFile_WithIdenticalLocalCopy_AdoptsItWithoutUpload()
    {
        var mtime = _server.Put("a.txt", "same");
        var c = await Connect();
        File.WriteAllText(c.Local("a.txt"), "same");
        File.SetLastWriteTimeUtc(c.Local("a.txt"), DateTimeOffset.FromUnixTimeMilliseconds(mtime).UtcDateTime);

        await c.Sync.KeepAsync("a.txt", false).T();

        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("a.txt"));
        Assert.True(c.Sync.State.IsPinned("a.txt"));
        Assert.DoesNotContain("已上传", c.LogText);
        Assert.Empty(_server.FilesLike("", "a (conflict"));
    }

    [Fact]
    public async Task Unpin_FileInKeptFolder_KeepsSiblingsPinned()
    {
        _server.Put("docs/a.txt", "a");
        _server.Put("docs/b.txt", "b");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();

        c.Sync.Unpin("docs/a.txt");

        Assert.False(c.Sync.State.IsPinned("docs/a.txt"));
        Assert.True(c.Sync.State.IsPinned("docs/b.txt"));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("docs/a.txt")); // the local copy stays

        // The folder is no longer kept: new server files are not downloaded.
        _server.Put("docs/new.txt", "n");
        await Refresh(c);
        Assert.False(File.Exists(c.Local("docs/new.txt")));
        Assert.Equal("b", c.Read("docs/b.txt"));
    }

    [Fact]
    public async Task FreeUp_File_DeletesLocalCopyOnly()
    {
        _server.Put("a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("a.txt", false).T();
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("a.txt"));

        c.Sync.FreeUp("a.txt", false);

        Assert.False(File.Exists(c.Local("a.txt")));
        Assert.Equal(FileStatus.CloudOnly, c.Sync.GetFileStatus("a.txt"));
        Assert.Equal("a", _server.Read("a.txt"));
        await Refresh(c);
        Assert.False(File.Exists(c.Local("a.txt"))); // not re-downloaded
    }

    [Fact]
    public async Task FreeUp_LocalOnlyFile_KeepsIt()
    {
        // The UI promises "仅在本地的文件会保留" when freeing up space.
        var c = await Connect();
        c.Write("only-here.txt", "precious");
        Assert.Equal(FileStatus.LocalOnly, c.Sync.GetFileStatus("only-here.txt"));

        c.Sync.FreeUp("only-here.txt", false);

        Assert.Equal("precious", c.Read("only-here.txt"));
    }

    [Fact]
    public async Task FreeUp_FileDeletedOnServer_IsNotReuploaded()
    {
        _server.Put("a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("a.txt", false).T();
        File.Delete(_server.PathOf("a.txt"));
        using (File.Open(c.Local("a.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Refresh(c); // cannot recycle the locked copy, so it now shows as local-only
        }

        c.Sync.FreeUp("a.txt", false);
        await Refresh(c);

        Assert.False(_server.Exists("a.txt"), "deleted file was re-uploaded:\n" + c.LogText);
    }

    [Fact]
    public async Task FreeUp_Folder_KeepsLocalOnlyFiles()
    {
        _server.Put("docs/a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();
        c.Write("docs/local.txt", "mine");

        c.Sync.FreeUp("docs", true);

        Assert.False(File.Exists(c.Local("docs/a.txt")));
        Assert.Equal("mine", c.Read("docs/local.txt"));
        Assert.False(c.Sync.State.IsInKeptFolder("docs/a.txt"));
    }

    [Fact]
    public async Task Unpin_KeepsLocalCopy()
    {
        _server.Put("docs/a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();
        c.Sync.Unpin("docs");
        Assert.False(c.Sync.State.IsPinned("docs/a.txt"));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("docs/a.txt"));
    }

    // ---------- refresh ----------

    [Fact]
    public async Task Refresh_UploadsLocalEdit_WithoutConflictCopy()
    {
        _server.Put("docs/a.txt", "v1");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();

        c.Write("docs/a.txt", "v2 local");
        Assert.Equal(FileStatus.PendingUpload, c.Sync.GetFileStatus("docs/a.txt"));
        Assert.Equal(["docs/a.txt"], c.Sync.UnsyncedUnder("docs", true));
        await Refresh(c);

        Assert.Equal("v2 local", _server.Read("docs/a.txt"));
        Assert.Empty(_server.FilesLike("docs", "a (conflict"));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("docs/a.txt"));
    }

    [Fact]
    public async Task Refresh_DownloadsServerEdit()
    {
        _server.Put("docs/a.txt", "v1");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();

        _server.Put("docs/a.txt", "v2 from server", DateTime.UtcNow.AddMinutes(-1));
        await Refresh(c);

        Assert.Equal("v2 from server", c.Read("docs/a.txt"));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("docs/a.txt"));
    }

    [Fact]
    public async Task Refresh_BothChanged_ServerKeepsConflictCopy()
    {
        _server.Put("docs/a.txt", "v1");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();

        _server.Put("docs/a.txt", "server edit", DateTime.UtcNow.AddMinutes(-1));
        c.Write("docs/a.txt", "local edit");
        await Refresh(c);

        Assert.Equal("local edit", _server.Read("docs/a.txt"));
        var conflict = Assert.Single(_server.FilesLike("docs", "a (conflict"));
        Assert.Equal("server edit", _server.Read("docs/" + conflict));
    }

    [Fact]
    public async Task Refresh_LocalEditDuringDownload_IsNotOverwritten()
    {
        _server.Put("docs/a.txt", "v1");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();

        _server.Put("docs/a.txt", "server edit", DateTime.UtcNow.AddMinutes(-1));
        // Edit the local copy after the refresh decided to download, before the download replaces it.
        var edited = 0;
        c.Sync.Changed += () =>
        {
            if (c.Sync.IsBusy("docs/a.txt") && Interlocked.Exchange(ref edited, 1) == 0)
                c.Write("docs/a.txt", "local edit");
        };
        await Refresh(c);
        Assert.Equal(1, edited);
        Assert.Equal("local edit", c.Read("docs/a.txt"));

        // The next refresh uploads the edit; the server keeps its version as a conflict copy.
        await Refresh(c);
        Assert.Equal("local edit", _server.Read("docs/a.txt"));
        var conflict = Assert.Single(_server.FilesLike("docs", "a (conflict"));
        Assert.Equal("server edit", _server.Read("docs/" + conflict));
    }

    [Fact]
    public async Task Refresh_NewLocalFileDuringDownload_IsNotOverwritten()
    {
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();

        _server.Put("docs/a.txt", "server");
        var created = 0;
        c.Sync.Changed += () =>
        {
            if (c.Sync.IsBusy("docs/a.txt") && Interlocked.Exchange(ref created, 1) == 0)
                c.Write("docs/a.txt", "local");
        };
        await Refresh(c);
        Assert.Equal(1, created);
        Assert.Equal("local", c.Read("docs/a.txt"));
    }

    [Fact]
    public async Task Refresh_UploadsNewLocalFile()
    {
        var c = await Connect();
        c.Write("new/n.txt", "new");
        await Refresh(c);
        Assert.Equal("new", _server.Read("new/n.txt"));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("new/n.txt"));
    }

    [Fact]
    public async Task Refresh_ServerDelete_RemovesUnchangedLocalCopy()
    {
        _server.Put("docs/a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();

        File.Delete(_server.PathOf("docs/a.txt"));
        await Refresh(c);

        Assert.False(File.Exists(c.Local("docs/a.txt")));
        Assert.Null(c.Sync.State.GetFile("docs/a.txt"));
    }

    [Fact]
    public async Task Refresh_ServerDelete_ReuploadsChangedLocalCopy()
    {
        _server.Put("docs/a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();

        File.Delete(_server.PathOf("docs/a.txt"));
        c.Write("docs/a.txt", "edited");
        await Refresh(c);

        Assert.Equal("edited", _server.Read("docs/a.txt"));
    }

    [Fact]
    public async Task Refresh_LocalDelete_OfPinnedFileOutsideKeptFolder_BecomesCloudOnly()
    {
        _server.Put("a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("a.txt", false).T();
        File.Delete(c.Local("a.txt"));
        await Refresh(c);
        Assert.Equal(FileStatus.CloudOnly, c.Sync.GetFileStatus("a.txt"));
        Assert.Equal("a", _server.Read("a.txt"));
    }

    [Fact]
    public async Task Refresh_BackupArchive_ReplacesServerCopyWithoutConflict()
    {
        _server.Put("backup/DEV1.7z", "old archive");
        var c = await Connect();
        // A newly built archive, never synced on this machine.
        c.Write("backup/DEV1.7z", "new archive");
        await Refresh(c);
        Assert.Equal("new archive", _server.Read("backup/DEV1.7z"));
        Assert.Empty(_server.FilesLike("backup", "DEV1 (conflict"));
    }

    [Fact]
    public async Task Refresh_MirrorsServerFolders_InKeptFolderOnly()
    {
        _server.Put("docs/a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();

        Directory.CreateDirectory(_server.PathOf("docs/new-empty"));
        Directory.CreateDirectory(_server.PathOf("elsewhere"));
        await Refresh(c);
        Assert.True(Directory.Exists(c.Local("docs/new-empty")));
        Assert.False(Directory.Exists(c.Local("elsewhere")));

        // Deleted on the server: the empty local folder goes too.
        Directory.Delete(_server.PathOf("docs/new-empty"));
        await Refresh(c);
        Assert.False(Directory.Exists(c.Local("docs/new-empty")));
        Assert.True(Directory.Exists(c.Local("docs")));
    }

    [Fact]
    public async Task Refresh_AfterStateLoss_ReadoptsIdenticalFiles()
    {
        _server.Put("docs/a.txt", "a");
        var c1 = await Connect();
        await c1.Sync.KeepAsync("docs", true).T();
        c1.Sync.Dispose();
        File.Delete(StatePath(c1));

        var c2 = await Connect(c1.Root);

        Assert.Equal(FileStatus.Kept, c2.Sync.GetFileStatus("docs/a.txt"));
        Assert.NotNull(c2.Sync.State.GetFile("docs/a.txt"));
        Assert.DoesNotContain("已上传", c2.LogText);
        Assert.Empty(_server.FilesLike("docs", "a (conflict"));
    }

    [Fact]
    public async Task TwoClients_PropagateEdits()
    {
        _server.Put("shared/a.txt", "v1");
        var a = await Connect();
        var b = await Connect();
        await a.Sync.KeepAsync("shared", true).T();
        await b.Sync.KeepAsync("shared", true).T();

        a.Write("shared/a.txt", "from A");
        await Refresh(a);
        await Refresh(b);
        Assert.Equal("from A", b.Read("shared/a.txt"));

        b.Write("shared/b.txt", "from B");
        await Refresh(b);
        await Refresh(a);
        Assert.Equal("from B", a.Read("shared/b.txt"));
    }

    [Fact]
    public async Task State_SurvivesRestart()
    {
        _server.Put("docs/a.txt", "a");
        var c1 = await Connect();
        await c1.Sync.KeepAsync("docs", true).T();
        c1.Sync.Dispose();

        // Same server + folder: picks up the saved state; nothing re-uploaded or duplicated.
        var c2 = await Connect(c1.Root);
        Assert.True(c2.Sync.State.IsInKeptFolder("docs/a.txt"));
        Assert.Equal(FileStatus.Kept, c2.Sync.GetFileStatus("docs/a.txt"));
        Assert.Empty(_server.FilesLike("docs", "a (conflict"));
    }

    // ---------- transfers ----------

    [Fact]
    public async Task UploadFile_MissingSource_DoesNotLeakTransferSlots()
    {
        var c = await Connect();
        var missing = Path.Combine(TestEnv.Root, "does-not-exist.bin");
        for (int i = 0; i < 4; i++)
            Assert.False(await c.Sync.UploadFileAsync(missing, $"m{i}.bin").T());

        var src = Path.Combine(TestEnv.NewDir("src"), "ok.txt");
        File.WriteAllText(src, "ok");
        Assert.True(await c.Sync.UploadFileAsync(src, "ok.txt").T(15));
        Assert.Equal("ok", _server.Read("ok.txt"));
    }

    [Fact]
    public async Task UploadFile_OverwritesServerCopy()
    {
        _server.Put("backup/X.7z", "old");
        var c = await Connect();
        var src = Path.Combine(TestEnv.NewDir("src"), "X.7z");
        File.WriteAllText(src, "new");
        Assert.True(await c.Sync.UploadFileAsync(src, "backup/X.7z").T());
        Assert.Equal("new", _server.Read("backup/X.7z"));
        Assert.Empty(_server.FilesLike("backup", "X (conflict"));
    }

    [Fact]
    public async Task UploadPaths_FolderIntoKeptFolder_MirrorsLocally()
    {
        _server.Put("docs/keep.txt", "k");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();

        var src = TestEnv.NewDir("src");
        Directory.CreateDirectory(Path.Combine(src, "proj", "sub", "empty"));
        File.WriteAllText(Path.Combine(src, "proj", "p.txt"), "p");
        File.WriteAllText(Path.Combine(src, "proj", "sub", "s.txt"), "s");
        File.WriteAllText(Path.Combine(src, "single.txt"), "1");

        await c.Sync.UploadPathsAsync([Path.Combine(src, "proj"), Path.Combine(src, "single.txt")], "docs").T();

        Assert.Equal("p", _server.Read("docs/proj/p.txt"));
        Assert.Equal("s", _server.Read("docs/proj/sub/s.txt"));
        Assert.True(Directory.Exists(_server.PathOf("docs/proj/sub/empty")));
        Assert.Equal("1", _server.Read("docs/single.txt"));
        Assert.Equal("s", c.Read("docs/proj/sub/s.txt"));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("docs/proj/sub/s.txt"));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("docs/single.txt"));
    }

    [Fact]
    public async Task UploadPaths_IntoKeptFolder_KeepsUnsyncedLocalEdit()
    {
        _server.Put("docs/a.txt", "v1");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();
        c.Write("docs/a.txt", "local edit"); // not uploaded yet

        var src = Path.Combine(TestEnv.NewDir("src"), "a.txt");
        File.WriteAllText(src, "dragged");
        await c.Sync.UploadPathsAsync([src], "docs").T();
        await Refresh(c);

        // The uploaded file wins; the unsynced edit survives as a conflict copy on both sides.
        Assert.Equal("dragged", _server.Read("docs/a.txt"));
        Assert.Equal("dragged", c.Read("docs/a.txt"));
        var conflict = Assert.Single(_server.FilesLike("docs", "a (conflict"));
        Assert.Equal("local edit", _server.Read("docs/" + conflict));
        Assert.Equal("local edit", c.Read("docs/" + conflict));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("docs/a.txt"));
    }

    [Fact]
    public async Task UploadPaths_IntoKeptFolder_NotYetAdoptedCopy_MakesNoConflictCopy()
    {
        _server.Put("docs/a.txt", "v1");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();
        c.Sync.State.RemoveFile("docs/a.txt"); // e.g. state lost: the copy matches the server but has no snapshot

        var src = Path.Combine(TestEnv.NewDir("src"), "a.txt");
        File.WriteAllText(src, "dragged");
        await c.Sync.UploadPathsAsync([src], "docs").T();
        await Refresh(c);

        Assert.Equal("dragged", c.Read("docs/a.txt"));
        Assert.Equal(["a.txt"], Directory.GetFiles(c.Local("docs")).Select(Path.GetFileName));
        Assert.Empty(_server.FilesLike("docs", "a (conflict"));
    }

    [Fact]
    public async Task UploadPaths_IntoKeptFolder_LockedLocalCopy_DoesNotFailAndLosesNothing()
    {
        _server.Put("docs/a.txt", "v1");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();
        c.Write("docs/a.txt", "local edit");

        var src = Path.Combine(TestEnv.NewDir("src"), "a.txt");
        File.WriteAllText(src, "dragged");
        using (File.Open(c.Local("docs/a.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await c.Sync.UploadPathsAsync([src], "docs").T(); // must not throw: the upload itself succeeded
        }
        Assert.Equal("dragged", _server.Read("docs/a.txt"));
        Assert.Contains("本地副本未能更新", c.LogText);

        // Once unlocked, the edit is uploaded and the dragged version kept as a conflict copy.
        await Refresh(c);
        Assert.Equal("local edit", _server.Read("docs/a.txt"));
        var conflict = Assert.Single(_server.FilesLike("docs", "a (conflict"));
        Assert.Equal("dragged", _server.Read("docs/" + conflict));
    }

    [Fact]
    public async Task UploadPaths_IntoFolderNotKept_DoesNotMirrorLocally()
    {
        _server.Put("docs/a.txt", "a");
        var c = await Connect();
        var src = Path.Combine(TestEnv.NewDir("src"), "b.txt");
        File.WriteAllText(src, "b");

        await c.Sync.UploadPathsAsync([src], "docs").T();

        Assert.Equal("b", _server.Read("docs/b.txt"));
        Assert.False(File.Exists(c.Local("docs/b.txt")));
        Assert.Equal(FileStatus.CloudOnly, c.Sync.GetFileStatus("docs/b.txt"));
    }

    [Fact]
    public async Task DownloadTo_Folder_WritesTreeWithoutKeeping()
    {
        _server.Put("docs/a.txt", "a");
        _server.Put("docs/sub/b.txt", "b");
        var c = await Connect();
        var target = Path.Combine(TestEnv.NewDir("dl"), "docs");

        await c.Sync.DownloadToAsync("docs", true, target).T();

        Assert.Equal("a", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(target, "sub", "b.txt")));
        Assert.Equal(FileStatus.CloudOnly, c.Sync.GetFileStatus("docs/a.txt"));
    }

    [Fact]
    public async Task DownloadTo_SingleFile()
    {
        _server.Put("a.txt", "a");
        var c = await Connect();
        var target = Path.Combine(TestEnv.NewDir("dl"), "renamed.txt");
        await c.Sync.DownloadToAsync("a.txt", false, target).T();
        Assert.Equal("a", File.ReadAllText(target));
    }

    // ---------- file actions ----------

    [Fact]
    public async Task Mkdir_InKeptFolder_CreatesLocalFolder()
    {
        _server.Put("docs/a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();
        await c.Sync.MkdirAsync("docs/new").T();
        Assert.True(Directory.Exists(_server.PathOf("docs/new")));
        Assert.True(Directory.Exists(c.Local("docs/new")));
    }

    [Fact]
    public async Task Rename_KeptFile_MovesLocalCopyAndState()
    {
        _server.Put("docs/a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();

        await c.Sync.RenameAsync("docs/a.txt", "b.txt", false).T();

        Assert.False(_server.Exists("docs/a.txt"));
        Assert.Equal("a", _server.Read("docs/b.txt"));
        Assert.False(File.Exists(c.Local("docs/a.txt")));
        Assert.Equal("a", c.Read("docs/b.txt"));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("docs/b.txt"));
        Assert.Empty(_server.FilesLike("docs", "b (conflict"));
    }

    [Fact]
    public async Task Rename_KeptFolder_KeepsItKept()
    {
        _server.Put("docs/a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();

        await c.Sync.RenameAsync("docs", "papers", true).T();

        Assert.Equal("a", c.Read("papers/a.txt"));
        Assert.False(Directory.Exists(c.Local("docs")));
        Assert.True(c.Sync.State.IsInKeptFolder("papers/a.txt"));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("papers/a.txt"));
        Assert.False(_server.Exists("docs"));
    }

    [Fact]
    public async Task Rename_CaseOnly()
    {
        _server.Put("docs/a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();

        await c.Sync.RenameAsync("docs/a.txt", "A.txt", false).T();

        Assert.Equal(["A.txt"], Directory.GetFiles(c.Local("docs")).Select(Path.GetFileName));
        Assert.Equal(["A.txt"], Directory.GetFiles(_server.PathOf("docs")).Select(Path.GetFileName));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("docs/A.txt"));
    }

    [Fact]
    public async Task Rename_KeptFolder_CaseOnly()
    {
        _server.Put("docs/a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();

        await c.Sync.RenameAsync("docs", "Docs", true).T();
        await Refresh(c);

        Assert.Equal(["Docs"], Directory.GetDirectories(c.Root).Select(Path.GetFileName));
        Assert.Equal(["Docs"], Directory.GetDirectories(_server.DataDir).Select(Path.GetFileName).Where(n => n is not (".uploads" or ".filecopy")));
        Assert.Equal("a", c.Read("Docs/a.txt"));
        Assert.True(c.Sync.State.IsInKeptFolder("Docs/a.txt"));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("Docs/a.txt"));
    }

    [Fact]
    public async Task Rename_OntoExistingName_IsRefused()
    {
        _server.Put("a.txt", "a");
        _server.Put("b.txt", "precious b");
        var c = await Connect();

        await Assert.ThrowsAsync<IOException>(() => c.Sync.RenameAsync("a.txt", "b.txt", false).T());

        Assert.Equal("precious b", _server.Read("b.txt"));
        Assert.Equal("a", _server.Read("a.txt"));
    }

    [Fact]
    public async Task Rename_KeptFolder_WhileLocalFileIsOpen_DoesNotResurrectOldFolder()
    {
        _server.Put("docs/a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();

        using (File.Open(c.Local("docs/a.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // The server rename succeeds; only the local move fails. That must not surface as an error.
            await c.Sync.RenameAsync("docs", "papers", true).T();
        }
        await Refresh(c);

        Assert.False(_server.Exists("docs"), "old folder was re-uploaded:\n" + c.LogText);
        Assert.Equal("a", _server.Read("papers/a.txt"));
        Assert.Equal("a", c.Read("papers/a.txt"));
        Assert.True(c.Sync.State.IsInKeptFolder("papers"));
    }

    [Fact]
    public async Task Rename_KeptFolder_WhileLocalFileIsOpen_MovesLocalOnlyFiles()
    {
        _server.Put("docs/a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();
        c.Write("docs/local.txt", "mine"); // never uploaded

        using (File.Open(c.Local("docs/a.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await c.Sync.RenameAsync("docs", "papers", true).T();
        }
        await Refresh(c);

        Assert.False(_server.Exists("docs"), "old folder was re-created:\n" + c.LogText);
        Assert.Equal("mine", c.Read("papers/local.txt"));
        Assert.Equal("mine", _server.Read("papers/local.txt"));
        Assert.Equal("a", c.Read("papers/a.txt"));
    }

    [Fact]
    public async Task Rename_PinnedFile_WhileLocalFileIsOpen_StaysPinned()
    {
        _server.Put("a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("a.txt", false).T();

        using (File.Open(c.Local("a.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await c.Sync.RenameAsync("a.txt", "b.txt", false).T();
        }
        // The rename's own refresh could not recycle the locked copy; this one does.
        await Refresh(c);

        Assert.False(_server.Exists("a.txt"), "old file was re-uploaded:\n" + c.LogText);
        Assert.Equal("a", c.Read("b.txt"));
        Assert.Equal(FileStatus.Kept, c.Sync.GetFileStatus("b.txt"));
    }

    [Fact]
    public async Task Rename_Folder_WhileLocalFileIsOpen_KeepsPinnedFilesPinned()
    {
        _server.Put("docs/a.txt", "a");
        var c = await Connect();
        await c.Sync.KeepAsync("docs/a.txt", false).T(); // pinned individually, folder not kept

        using (File.Open(c.Local("docs/a.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await c.Sync.RenameAsync("docs", "papers", true).T();
        }
        await Refresh(c);

        Assert.False(_server.Exists("docs"), "old folder was re-uploaded:\n" + c.LogText);
        Assert.Equal("a", c.Read("papers/a.txt"));
        Assert.True(c.Sync.State.IsPinned("papers/a.txt"));
    }

    [Fact]
    public async Task Delete_RemovesServerAndLocalCopies()
    {
        _server.Put("docs/a.txt", "a");
        _server.Put("docs/b.txt", "b");
        var c = await Connect();
        await c.Sync.KeepAsync("docs", true).T();

        await c.Sync.DeleteAsync("docs/a.txt", false).T();
        Assert.False(_server.Exists("docs/a.txt"));
        Assert.False(File.Exists(c.Local("docs/a.txt")));

        await c.Sync.DeleteAsync("docs", true).T();
        Assert.False(_server.Exists("docs"));
        Assert.False(Directory.Exists(c.Local("docs")));
        Assert.False(c.Sync.State.IsInKeptFolder("docs"));
        Assert.Null(c.Sync.State.GetFile("docs/b.txt"));
    }
}
