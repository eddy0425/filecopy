using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileCopy.Client.Backup;
using FileCopy.Client.Local;
using FileCopy.Client.Transfers;
using Microsoft.Win32;

namespace FileCopy.Client;

public partial class MainWindow : Window
{
    private const int MaxLogLines = 500;
    private static readonly string LogFile = Path.Combine(Settings.DataDir, "log.txt");

    private readonly Settings _settings = Settings.Load();
    private readonly TransferQueue _transfers = new();
    private readonly ObservableCollection<FileItem> _items = [];
    private readonly CancellationTokenSource _exit = new();
    private SyncService? _sync;
    private Task? _backup; // archive creation in progress, so Shutdown can wait for 7-Zip to be killed
    private string _folder = ""; // current server folder ("" = root)
    private int _reloadQueued;

    public bool StartMinimized { get; }

    private sealed record Crumb(string Name, string Rel, Visibility SeparatorVisibility);

    public MainWindow()
    {
        InitializeComponent();
        FilesGrid.ItemsSource = _items;
        TransfersGrid.ItemsSource = _transfers.Items;
        StartMinimized = _settings.AutoConnect && _settings.GetToken().Length > 0;
        UpdateBreadcrumb();
        Loaded += (_, _) =>
        {
            if (_settings.GetToken().Length == 0) OpenSettings();
            else _ = ConnectAsync();
        };
        if (StartMinimized) _ = ConnectAsync();
    }

    // Closing the window keeps the app in the tray; exit from the tray menu.
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    /// <summary>App exit: stops transfers and any running backup (7-Zip is killed so no orphan keeps writing).</summary>
    public void Shutdown()
    {
        _exit.Cancel();
        try { _backup?.Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { } // the backup task faults with the cancellation
        Disconnect();
    }

    private void Disconnect()
    {
        _sync?.Dispose();
        _sync = null;
    }

    public void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (LogFile)
        {
            try { File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}"); }
            catch (IOException) { }
        }
        Dispatcher.BeginInvoke(() =>
        {
            LogList.Items.Insert(0, line);
            while (LogList.Items.Count > MaxLogLines) LogList.Items.RemoveAt(LogList.Items.Count - 1);
        });
    }

    public void OpenSyncFolder()
    {
        var folder = _sync?.LocalPath(_folder) ?? _settings.LocalFolder;
        if (!Directory.Exists(folder)) folder = _settings.LocalFolder;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    public void SyncNow()
    {
        if (_sync != null) _ = _sync.RefreshAsync();
        else if (_settings.GetToken().Length > 0) _ = ConnectAsync();
    }

    // ---------- connection ----------

    private async Task ConnectAsync()
    {
        if (_sync != null) return; // already connecting/connected
        StatusText.Text = "正在连接...";
        var sync = new SyncService(_settings.ServerUrl, _settings.GetToken(), _settings.LocalFolder, _transfers, Log);
        sync.Changed += QueueReload;
        _sync = sync;
        try
        {
            await sync.StartAsync();
            StatusText.Text = $"已连接 {_settings.ServerUrl}    本地保留文件夹: {sync.Root}";
            Log("已连接 " + _settings.ServerUrl);
        }
        catch (Exception ex)
        {
            if (_sync == sync) _sync = null;
            sync.Dispose();
            StatusText.Text = "连接失败: " + ex.Message;
            Log("连接失败: " + ex.Message);
        }
        Reload();
    }

    private void OpenSettings()
    {
        var dlg = new SettingsWindow(_settings) { Owner = IsVisible ? this : null };
        if (dlg.ShowDialog() == true)
        {
            Disconnect();
            _folder = "";
            UpdateBreadcrumb();
            _ = ConnectAsync();
        }
    }

    // ---------- listing ----------

    /// <summary>Coalesces bursts of Changed events into one UI reload.</summary>
    private void QueueReload()
    {
        if (Interlocked.Exchange(ref _reloadQueued, 1) == 1) return;
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(150);
            Interlocked.Exchange(ref _reloadQueued, 0);
            Reload();
        });
    }

    private void Reload()
    {
        var sync = _sync;
        if (sync == null)
        {
            _items.Clear();
            EmptyHint.Visibility = Visibility.Collapsed;
            return;
        }

        var listing = sync.List(_folder);
        var rows = new List<(string Rel, bool IsDir, FileStatus Status, bool Pinned, long Size, long Mtime)>();
        foreach (var e in listing.Remote)
        {
            var status = e.IsDir
                ? (sync.State.IsInKeptFolder(e.Path) ? FileStatus.Kept : FileStatus.CloudOnly)
                : sync.GetFileStatus(e.Path);
            rows.Add((e.Path, e.IsDir, status, sync.State.IsPinned(e.Path), e.Size, e.Mtime));
        }
        foreach (var d in listing.LocalOnlyDirs) rows.Add((d, true, FileStatus.LocalOnly, false, 0, 0));
        foreach (var f in listing.LocalOnlyFiles)
        {
            var fi = new FileInfo(sync.LocalPath(f));
            var status = sync.IsBusy(f) ? FileStatus.Transferring : FileStatus.LocalOnly;
            rows.Add((f, false, status, false, fi.Exists ? fi.Length : 0, fi.Exists ? new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() : 0));
        }
        rows.Sort((a, b) => a.IsDir != b.IsDir ? (a.IsDir ? -1 : 1) : StringComparer.CurrentCultureIgnoreCase.Compare(a.Rel, b.Rel));

        // Update in place when the set of rows is unchanged, so selection and scroll survive.
        bool same = rows.Count == _items.Count &&
                    rows.Select(r => r.Rel).SequenceEqual(_items.Select(i => i.Rel), StringComparer.OrdinalIgnoreCase);
        if (!same)
        {
            var selected = FilesGrid.SelectedItems.Cast<FileItem>().Select(i => i.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _items.Clear();
            foreach (var r in rows) _items.Add(new FileItem(r.Rel, r.IsDir));
            foreach (var item in _items.Where(i => selected.Contains(i.Rel))) FilesGrid.SelectedItems.Add(item);
        }
        for (int i = 0; i < rows.Count; i++) _items[i].Update(rows[i].Status, rows[i].Pinned, rows[i].Size, rows[i].Mtime);

        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpButton.IsEnabled = _folder.Length > 0;
        UpdateLocalUsage(sync);
    }

    private void UpdateLocalUsage(SyncService sync)
    {
        var (count, bytes) = sync.State.Usage();
        LocalUsageText.Text = $"本机已保留 {count} 个文件，共 {TransferItem.FormatBytes(bytes)}";
    }

    private void Navigate(string folder)
    {
        _folder = folder;
        UpdateBreadcrumb();
        _items.Clear();
        Reload();
    }

    private void UpdateBreadcrumb()
    {
        var crumbs = new List<Crumb> { new("☁ 云盘", "", Visibility.Collapsed) };
        if (_folder.Length > 0)
        {
            var parts = _folder.Split('/');
            for (int i = 0; i < parts.Length; i++)
                crumbs.Add(new Crumb(parts[i], string.Join('/', parts[..(i + 1)]), Visibility.Visible));
        }
        Breadcrumb.ItemsSource = crumbs;
    }

    // ---------- helpers ----------

    /// <summary>Items an action applies to: the selection, or the row whose menu/button was used.</summary>
    private List<FileItem> Targets(object sender)
    {
        var clicked = (sender as FrameworkElement)?.DataContext as FileItem;
        var selected = FilesGrid.SelectedItems.Cast<FileItem>().ToList();
        if (clicked != null && !selected.Contains(clicked)) return [clicked];
        return selected;
    }

    private async Task Run(Func<SyncService, Task> action)
    {
        var sync = _sync;
        if (sync == null)
        {
            MessageBox.Show(this, "尚未连接服务器。", "FileCopy");
            return;
        }
        try
        {
            await action(sync);
        }
        catch (Exception ex)
        {
            Log("操作失败: " + ex.Message);
            MessageBox.Show(this, "操作失败: " + ex.Message, "FileCopy");
        }
        Reload();
    }

    private static string JoinRel(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;

    // ---------- actions ----------

    // Toggles like OneDrive: checking pins (downloading if needed); unchecking keeps the
    // local copy but stops pinning it.
    private async void Keep_Click(object sender, RoutedEventArgs e)
    {
        var items = Targets(sender);
        if (items.Count > 0 && items.All(i => i.Pinned))
        {
            foreach (var i in items) _sync?.Unpin(i.Rel);
            Reload();
            return;
        }
        await Run(s => Task.WhenAll(items.Select(i => s.KeepAsync(i.Rel, i.IsDir))));
    }

    private void FreeUp_Click(object sender, RoutedEventArgs e) => FreeUp(Targets(sender));

    private void FreeUp(List<FileItem> items)
    {
        var sync = _sync;
        if (sync == null || items.Count == 0) return;
        var unsynced = items.SelectMany(i => sync.UnsyncedUnder(i.Rel, i.IsDir)).ToList();
        if (unsynced.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"有 {unsynced.Count} 个文件的本地修改尚未上传：\n\n{string.Join("\n", unsynced.Take(8))}\n\n" +
                "仅在本地的文件会保留；已修改的文件释放后修改将丢失。\n仍要释放空间吗？（可先点“刷新”等待上传完成）",
                "FileCopy", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
        }
        foreach (var i in items) sync.FreeUp(i.Rel, i.IsDir);
        Reload();
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var item = Targets(sender).FirstOrDefault();
        if (item != null) await OpenItem(item);
    }

    private async Task OpenItem(FileItem item)
    {
        if (item.IsDir) Navigate(item.Rel);
        else await Run(s => s.OpenAsync(item.Rel));
    }

    private async void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore double-clicks on the header / empty area / action button.
        if (e.OriginalSource is DependencyObject src && FindParent<DataGridRow>(src) is { Item: FileItem item }
            && FindParent<Button>(src) == null)
            await OpenItem(item);
    }

    private async void FilesGrid_KeyDown(object sender, KeyEventArgs e)
    {
        var selected = FilesGrid.SelectedItems.Cast<FileItem>().ToList();
        switch (e.Key)
        {
            case Key.Enter when selected.Count == 1:
                e.Handled = true;
                await OpenItem(selected[0]);
                break;
            case Key.Back when _folder.Length > 0:
                Navigate(LocalState.Parent(_folder));
                break;
            case Key.Delete when selected.Count > 0:
                Delete_Click(FilesGrid, e);
                break;
            case Key.F2 when selected.Count == 1:
                Rename_Click(FilesGrid, e);
                break;
            case Key.F5:
                SyncNow();
                break;
        }
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private async void DownloadTo_Click(object sender, RoutedEventArgs e)
    {
        var items = Targets(sender);
        if (items.Count == 0) return;
        if (items.Count == 1 && !items[0].IsDir)
        {
            var dlg = new SaveFileDialog { FileName = items[0].Name, Title = "下载到" };
            if (dlg.ShowDialog(this) != true) return;
            await Run(s => s.DownloadToAsync(items[0].Rel, false, dlg.FileName));
            return;
        }
        var folderDlg = new OpenFolderDialog { Title = "下载到文件夹" };
        if (folderDlg.ShowDialog(this) != true) return;
        await Run(s => Task.WhenAll(items.Select(i =>
            s.DownloadToAsync(i.Rel, i.IsDir, Path.Combine(folderDlg.FolderName, i.Name)))));
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var item = Targets(sender).FirstOrDefault();
        if (item == null) return;
        var name = InputDialog.Show(this, "重命名", "新名称：", item.Name);
        if (name == null || name == item.Name) return;
        if (name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            MessageBox.Show(this, "名称不能包含 / \\ : * ? \" < > |", "FileCopy");
            return;
        }
        await Run(s => s.RenameAsync(item.Rel, name, item.IsDir));
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var items = Targets(sender);
        if (items.Count == 0) return;
        var what = items.Count == 1 ? $"“{items[0].Name}”" : $"这 {items.Count} 项";
        var answer = MessageBox.Show(this, $"从服务器上删除{what}？\n\n本机的副本会移到回收站。",
            "FileCopy", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        await Run(async s =>
        {
            foreach (var i in items) await s.DeleteAsync(i.Rel, i.IsDir);
        });
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Show(this, "新建文件夹", "文件夹名称：", "新建文件夹");
        if (name != null) await Run(s => s.MkdirAsync(JoinRel(_folder, name)));
    }

    private async void UploadFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "选择要上传的文件" };
        if (dlg.ShowDialog(this) == true) await Run(s => s.UploadPathsAsync(dlg.FileNames, _folder));
    }

    private async void UploadFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Multiselect = true, Title = "选择要上传的文件夹" };
        if (dlg.ShowDialog(this) == true) await Run(s => s.UploadPathsAsync(dlg.FolderNames, _folder));
    }

    private void FilesGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void FilesGrid_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        // Dropping onto a folder row uploads into that folder.
        var target = e.OriginalSource is DependencyObject src && FindParent<DataGridRow>(src) is { Item: FileItem { IsDir: true } dir }
            ? dir.Rel
            : _folder;
        await Run(s => s.UploadPathsAsync(paths, target));
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_folder.Length > 0) Navigate(LocalState.Parent(_folder));
    }

    private void Crumb_Click(object sender, RoutedEventArgs e) => Navigate((string)((Button)sender).Tag);

    private void Refresh_Click(object sender, RoutedEventArgs e) => SyncNow();

    private void OpenLocal_Click(object sender, RoutedEventArgs e) => OpenSyncFolder();

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var id = _settings.EquipmentId;
        if (string.IsNullOrEmpty(id))
        {
            MessageBox.Show(this, "请先在设置中填写设备 ID。", "FileCopy");
            OpenSettings();
            return;
        }

        var rel = BackupService.RemoteBackupDir + "/" + BackupService.ArchiveName(id);
        var sync = _sync;
        if (sync?.IsBusy(rel) == true)
        {
            MessageBox.Show(this, "上一份备份仍在传输中，请等传输完成后再备份。", "FileCopy");
            return;
        }
        var target = sync?.LocalPath(rel) ?? Path.Combine(_settings.LocalFolder, rel.Replace('/', '\\'));
        BackupButton.IsEnabled = false;
        try
        {
            Log("开始备份...");
            var backup = Task.Run(() => BackupService.CreateArchiveAsync(target, Log, _exit.Token));
            _backup = backup;
            var skipped = await backup;
            _exit.Token.ThrowIfCancellationRequested(); // exiting: don't start an upload or show a dialog
            Log($"备份已生成: {target} ({TransferItem.FormatBytes(new FileInfo(target).Length)})");
            sync = _sync; // compressing can take minutes; the connection may have changed meanwhile
            string result;
            if (sync == null)
                result = "未连接服务器，备份仅保存在本机，连接后自动上传";
            else if (await sync.UploadFileAsync(target, rel))
                result = $"备份完成，已上传到服务器 {rel}";
            else
                result = "备份已保存在本机，但本次上传未完成（可能正在传输或失败），请查看日志和传输列表";
            _exit.Token.ThrowIfCancellationRequested(); // exiting: the upload was aborted by Disconnect, don't show a dialog
            Log(result);
            var warning = skipped.Count > 0 ? $"\n\n有 {skipped.Count} 个文件无法读取（可能被占用），未包含在备份中，详见日志。" : "";
            MessageBox.Show(this, $"{result}\n\n本机: {target}{warning}", "FileCopy");
        }
        catch (OperationCanceledException) when (_exit.IsCancellationRequested)
        {
            Log("备份已取消（程序退出）");
        }
        catch (Exception ex)
        {
            Log("备份失败: " + ex.Message);
            MessageBox.Show(this, "备份失败: " + ex.Message, "FileCopy");
        }
        finally
        {
            _backup = null;
            BackupButton.IsEnabled = true;
            Reload();
        }
    }
}
