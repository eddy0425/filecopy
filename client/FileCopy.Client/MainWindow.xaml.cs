using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileCopy.Client.Api;
using FileCopy.Client.Backup;
using FileCopy.Client.Local;
using FileCopy.Client.Transfers;
using Microsoft.Win32;

namespace FileCopy.Client;

public partial class MainWindow : Window
{
    private const int MaxLogLines = 500;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    // After unlocking, reopening from the tray within this time needs no code (a code is accepted only once).
    private static readonly TimeSpan UnlockGrace = TimeSpan.FromMinutes(5);
    private const int LogBatchMs = 250; // log lines reach the list in batches, not one dispatcher post each

    private readonly Settings _settings = Settings.Load(); // first: creates Settings.DataDir
    private readonly LogWriter _logFile = new(Path.Combine(Settings.DataDir, "log.txt"));
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLog = new();
    private int _logQueued;
    private readonly TransferQueue _transfers = new();
    private readonly ObservableCollection<FileItem> _items = [];
    private readonly CancellationTokenSource _exit = new();
    private SyncService? _sync;
    private Task? _backup; // archive creation in progress, so Shutdown can wait for 7-Zip to be killed
    private string _folder = ""; // current server folder ("" = root)
    private int _reloadQueued;
    private bool? _online; // heartbeat result for _sync; null while not connected
    private bool _pinging;
    private bool _hiddenToTray = true; // started or closed to the tray since the last unlock (not shown yet counts too)
    private DateTime _unlockedAt = DateTime.MinValue;
    private bool _unlocking;
    private bool _settingsShown; // so the first show does not reopen settings the lock prompt already offered
    private bool _exiting; // Shutdown ran: closing the window no longer goes to the tray
    private readonly System.Windows.Media.Brush _normalStatusBrush; // StatusText's foreground as set in XAML

    public bool StartMinimized { get; }
    /// <summary>Paused from the tray: disconnected until resumed. Not saved, a restart syncs again.</summary>
    public bool SyncPaused { get; private set; }
    public bool CloseToTray => _settings.CloseToTray;
    /// <summary>Connected and not paused: the tray offers "暂停同步", otherwise "立即同步".</summary>
    public bool CanPauseSync => !SyncPaused && _sync != null;
    public event Action? SyncPausedChanged;
    /// <summary>The window was closed (and the exit confirmed) while it does not close to the tray.</summary>
    public event Action? ExitRequested;

    private sealed record Crumb(string Name, string Rel, Visibility SeparatorVisibility);

    public MainWindow()
    {
        InitializeComponent();
        _normalStatusBrush = StatusText.Foreground;
        FilesGrid.ItemsSource = _items;
        TransfersGrid.ItemsSource = _transfers.Items;
        StartMinimized = StartsMinimized(_settings.AutoConnect, _settings.CloseToTray, _settings.GetToken().Length > 0);
        IsVisibleChanged += (_, _) => { if (IsVisible) _hiddenToTray = false; };
        UpdateBreadcrumb();
        Loaded += (_, _) =>
        {
            if (_settings.GetToken().Length > 0) _ = ConnectAsync();
            else if (!_settingsShown) OpenSettings();
        };
        if (StartMinimized) _ = ConnectAsync();
        var heartbeat = new System.Windows.Threading.DispatcherTimer { Interval = HeartbeatInterval };
        heartbeat.Tick += Heartbeat_Tick;
        heartbeat.Start();
    }

    /// <summary>Starting into the tray only makes sense when closing keeps the app there.</summary>
    internal static bool StartsMinimized(bool autoConnect, bool closeToTray, bool hasToken) => autoConnect && closeToTray && hasToken;

    // Closing the window keeps the app in the tray if so set; otherwise it exits (App runs the exit).
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        if (!_settings.CloseToTray)
        {
            if (ConfirmExit()) Dispatcher.BeginInvoke(() => ExitRequested?.Invoke()); // not from within Closing
            return;
        }
        Hide();
        _hiddenToTray = true;
    }

    /// <summary>Asks before exiting would cut off transfers or a backup. True to exit.</summary>
    public bool ConfirmExit()
    {
        if (ExitWarning(_transfers.Items.Count(t => !t.IsFinished), _backup != null) is not { } warning) return true;
        var answer = IsVisible
            ? MessageBox.Show(this, warning, "FileCopy", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            : MessageBox.Show(warning, "FileCopy", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        return answer == MessageBoxResult.OK;
    }

    /// <summary>What exiting would interrupt, as the question to ask; null when nothing is running.</summary>
    internal static string? ExitWarning(int transfers, bool backup)
    {
        var running = new List<string>();
        if (transfers > 0) running.Add($"有 {transfers} 个传输未完成");
        if (backup) running.Add("备份进行中");
        return running.Count == 0 ? null : string.Join("，", running) + "，退出将中断，仍要退出吗？";
    }

    /// <summary>
    /// When two-step verification is enabled on the server, asks for a code before the window (or the
    /// local folder) is shown at startup or again after being closed to the tray. Returns false if it stays locked.
    /// </summary>
    public async Task<bool> UnlockAsync()
    {
        if (!LockRequired(IsVisible, _settings.ServerTotpEnabled, _settings.GetToken().Length > 0, _hiddenToTray, _unlockedAt, DateTime.UtcNow)) return true;
        if (_unlocking) return false; // a prompt is already open
        _unlocking = true;
        try
        {
            const string askCode = "请输入二步验证码（验证器 App 中的 6 位数字）：";
            var prompt = askCode;
            // E.g. started offline into the tray: the failed connection is not retried on its own.
            // Connects while the code is being typed: the prompt shows at once, and a slow first sync
            // has less time to outlast the code than when connecting after it was entered.
            var connecting = _sync == null && !SyncPaused && _settings.GetToken().Length > 0 ? ConnectAsync() : Task.CompletedTask;
            while (true)
            {
                var code = InputDialog.Show(null, "FileCopy 已锁定", prompt);
                if (code == null) return false;
                await connecting;
                try
                {
                    if (await VerifyTotpAsync(code))
                    {
                        _unlockedAt = DateTime.UtcNow;
                        _hiddenToTray = false;
                        if (_sync == null && !SyncPaused) _ = ConnectAsync(); // checked over a short-lived connection: the server is back
                        return true;
                    }
                }
                catch (TotpCodeUsedException ex)
                {
                    prompt = ex.Message + "：";
                    continue;
                }
                catch (TotpRateLimitedException ex) // the same server, reachable: the settings are not the problem
                {
                    MessageBox.Show("无法验证: " + ex.Message, "FileCopy");
                    return false;
                }
                catch (Exception ex) // e.g. offline, rejected token, or the server was replaced
                {
                    if (OfferSettings("无法验证: " + SyncService.ErrorText(ex)) is not { } reconnecting) return false;
                    connecting = reconnecting;
                    prompt = askCode;
                    continue;
                }
                Log("解锁失败: 验证码错误");
                prompt = "验证码错误，请重新输入：";
            }
        }
        finally
        {
            _unlocking = false;
        }
    }

    /// <summary>
    /// Checks the code over the connection, or, when there is none (paused, or the connection failed), over a
    /// short-lived one: unlocking while paused does not resume syncing.
    /// </summary>
    private async Task<bool> VerifyTotpAsync(string code)
    {
        if (_sync is { } sync) return await sync.VerifyTotpAsync(code);
        using var api = new ServerApi(_settings.ServerUrl, _settings.GetToken(), _settings.EquipmentId);
        return await api.VerifyTotpAsync(code);
    }

    /// <summary>
    /// The code cannot be checked: offers the settings to check the server address and token (and test them).
    /// Changing the address there needs the token typed in again (see <see cref="SettingsWindow"/>).
    /// Returns the new connection once saved, or null to stay locked.
    /// </summary>
    private Task? OfferSettings(string reason)
    {
        var answer = MessageBox.Show(reason + "\n\n是否打开设置检查服务器地址和 Token？（设置中可测试连接）",
            "FileCopy", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return answer == MessageBoxResult.Yes ? OpenSettings(locked: true) : null;
    }

    /// <summary>
    /// Whether showing the window needs a code: hidden since startup or since being closed to the tray, past the
    /// grace period. Without a token the code cannot be checked and the server is out of reach anyway: the window
    /// opens so the token can be entered in the settings.
    /// </summary>
    internal static bool LockRequired(bool visible, bool totpEnabled, bool hasToken, bool hiddenToTray, DateTime unlockedAt, DateTime now) =>
        !visible && totpEnabled && hasToken && hiddenToTray && now - unlockedAt >= UnlockGrace;

    /// <summary>App exit: stops transfers and any running backup (7-Zip is killed so no orphan keeps writing).</summary>
    public void Shutdown()
    {
        _exiting = true;
        _exit.Cancel();
        try { _backup?.Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { } // the backup task faults with the cancellation
        Disconnect();
        _logFile.Dispose(); // writes the queued lines
    }

    private void Disconnect()
    {
        _sync?.Dispose();
        _sync = null;
        _online = null;
    }

    public void Log(string message)
    {
        var now = DateTime.Now;
        var line = $"{now:HH:mm:ss}  {message}";
        _logFile.Write($"{now:yyyy-MM-dd} {line}");
        _pendingLog.Enqueue(line);
        if (Interlocked.Exchange(ref _logQueued, 1) == 1) return;
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(LogBatchMs);
            Interlocked.Exchange(ref _logQueued, 0);
            while (_pendingLog.TryDequeue(out var l)) LogList.Items.Insert(0, l);
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

    /// <summary>Syncs, or connects when not connected; resumes a paused sync.</summary>
    public void SyncNow()
    {
        if (SyncPaused)
        {
            SetSyncPaused(false);
            Log("已恢复同步");
        }
        if (_sync != null) _ = _sync.RefreshAsync();
        else if (_settings.GetToken().Length > 0) _ = ConnectAsync();
    }

    /// <summary>The tray's "暂停同步" / "立即同步" item (see <see cref="CanPauseSync"/>).</summary>
    public void ToggleSync()
    {
        if (!CanPauseSync)
        {
            SyncNow();
            return;
        }
        Disconnect(); // cancels the running transfers
        SetSyncPaused(true);
        StatusText.Text = "已暂停同步（从托盘菜单选择“立即同步”继续）";
        StatusText.Foreground = _normalStatusBrush;
        Log("已暂停同步");
        Reload();
    }

    private void SetSyncPaused(bool paused)
    {
        if (SyncPaused == paused) return;
        SyncPaused = paused;
        SyncPausedChanged?.Invoke();
    }

    // ---------- connection ----------

    private async Task ConnectAsync()
    {
        if (_sync != null || SyncPaused) return; // already connecting/connected, or paused until resumed
        if (string.IsNullOrWhiteSpace(_settings.EquipmentId))
        {
            StatusText.Text = "未连接：请先在设置中填写设备 ID";
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            if (IsVisible)
            {
                MessageBox.Show(this, "请先在设置中填写设备 ID。", "FileCopy");
                _ = OpenSettings();
            }
            return;
        }
        StatusText.Text = "正在连接...";
        StatusText.Foreground = _normalStatusBrush;
        var sync = new SyncService(_settings.ServerUrl, _settings.GetToken(), _settings.EquipmentId, _settings.LocalFolder, _transfers, Log);
        sync.Changed += QueueReload;
        _sync = sync;
        try
        {
            await sync.StartAsync();
            if (_sync != sync) return; // replaced by a newer connection meanwhile
            if (sync.LastPing != null) ApplyPing(sync, sync.LastPing);
            SetOnline(sync, true, null);
            Log("已连接 " + _settings.ServerUrl);
        }
        catch (Exception ex)
        {
            sync.Dispose();
            if (_sync != sync) return; // replaced by a newer connection (e.g. settings changed): not a failure
            _sync = null;
            StatusText.Text = "连接失败: " + SyncService.ErrorText(ex);
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            Log("连接失败: " + SyncService.ErrorText(ex));
        }
        Reload();
    }

    /// <summary>Pings the server periodically so a dropped connection shows up in the status bar.</summary>
    private async void Heartbeat_Tick(object? sender, EventArgs e)
    {
        var sync = _sync;
        if (sync == null || _online == null || _pinging) return;
        _pinging = true;
        try
        {
            var ping = await sync.PingAsync();
            if (_sync != sync) return;
            ApplyPing(sync, ping);
            SetOnline(sync, true, null);
        }
        catch (Exception ex)
        {
            if (_sync == sync) SetOnline(sync, false, ex);
        }
        finally
        {
            _pinging = false;
        }
    }

    /// <summary>Takes over server-side settings: whether the window locks, and an EquipmentId renamed in the control panel.</summary>
    private void ApplyPing(SyncService sync, PingResult ping)
    {
        var changed = false;
        if (_settings.ServerTotpEnabled != ping.Totp)
        {
            _settings.ServerTotpEnabled = ping.Totp;
            changed = true;
            Log(ping.Totp ? "服务器已启用二步认证：启动或关闭到托盘后再次打开需要验证码" : "服务器已停用二步认证");
        }
        if (!string.IsNullOrEmpty(ping.EquipmentId) && ping.EquipmentId != _settings.EquipmentId)
        {
            Log($"设备 ID 已由服务器改为 {ping.EquipmentId}（原为 {_settings.EquipmentId}）");
            _settings.EquipmentId = ping.EquipmentId;
            sync.SetEquipmentId(ping.EquipmentId);
            changed = true;
        }
        if (changed) _settings.Save();
    }

    private void SetOnline(SyncService sync, bool online, Exception? failure)
    {
        var error = failure == null ? null : SyncService.ErrorText(failure);
        var wasOnline = _online;
        if (online && wasOnline == true) return; // nothing changed; keep the status bar as is
        _online = online;
        if (online)
        {
            StatusText.Text = $"已连接 {_settings.ServerUrl}    本地保留文件夹: {sync.Root}";
            StatusText.Foreground = _normalStatusBrush;
            if (wasOnline == false)
            {
                Log("已重新连接 " + _settings.ServerUrl);
                _ = sync.RefreshAsync(); // re-sync kept items whose transfers failed while offline ("下载到…" jobs are not retried)
            }
        }
        else
        {
            var unauthorized = failure is UnauthorizedException;
            StatusText.Text = unauthorized
                ? $"认证失败 {_settings.ServerUrl}（{error}），请在设置中检查 Token"
                : $"连接已断开 {_settings.ServerUrl}（{error}），正在重试...";
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            if (wasOnline != false) Log(unauthorized ? $"认证失败: {error}" : $"与服务器的连接已断开: {error}");
        }
    }

    /// <summary>Shows the settings; once saved, reconnects (resuming a paused sync) and returns that connection, else null.</summary>
    /// <param name="locked">Opened from the lock prompt (see <see cref="SettingsWindow"/>).</param>
    private Task? OpenSettings(bool locked = false)
    {
        _settingsShown = true;
        var dlg = new SettingsWindow(_settings, locked) { Owner = IsVisible ? this : null };
        if (dlg.ShowDialog() != true) return null;
        Disconnect();
        SetSyncPaused(false);
        _folder = "";
        UpdateBreadcrumb();
        return ConnectAsync();
    }

    // ---------- listing ----------

    /// <summary>
    /// Coalesces bursts of Changed events into one UI reload. Every transfer raises Changed when it starts
    /// and ends, so while transfers run, reloads are spaced further apart.
    /// </summary>
    private void QueueReload()
    {
        if (Interlocked.Exchange(ref _reloadQueued, 1) == 1) return;
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(_transfers.Items.Any(t => !t.IsFinished) ? 500 : 150);
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
        var rows = new List<(string Rel, bool IsDir, FileStatus Status, bool Pinned, long Size, long Mtime, bool ReadOnly)>();
        foreach (var e in listing.Remote)
        {
            var status = e.IsDir
                ? (sync.State.IsInKeptFolder(e.Path) ? FileStatus.Kept : FileStatus.CloudOnly)
                : sync.GetFileStatus(e.Path, listing.LocalFiles.GetValueOrDefault(e.Path));
            rows.Add((e.Path, e.IsDir, status, sync.State.IsPinned(e.Path), e.Size, e.Mtime, e.ReadOnly));
        }
        foreach (var d in listing.LocalOnlyDirs) rows.Add((d, true, FileStatus.LocalOnly, false, 0, 0, sync.IsReadOnly(d)));
        foreach (var f in listing.LocalOnlyFiles)
        {
            var fi = listing.LocalFiles[f];
            var status = sync.IsBusy(f) ? FileStatus.Transferring : FileStatus.LocalOnly;
            rows.Add((f, false, status, false, fi.Length, new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(), sync.IsReadOnly(f)));
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
        for (int i = 0; i < rows.Count; i++)
            _items[i].Update(rows[i].Status, rows[i].Pinned, rows[i].Size, rows[i].Mtime, rows[i].ReadOnly);

        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpButton.IsEnabled = _folder.Length > 0;
        var readOnly = _folder.Length > 0 && sync.IsReadOnly(_folder);
        UploadFilesButton.IsEnabled = UploadFolderButton.IsEnabled = NewFolderButton.IsEnabled = !readOnly;
        ReadOnlyHint.Visibility = readOnly ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = readOnly ? "此文件夹为空" : "此文件夹为空 —— 把文件拖到这里即可上传";
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
            MessageBox.Show(this, SyncPaused ? "同步已暂停，请先从托盘菜单选择“立即同步”。" : "尚未连接服务器。", "FileCopy");
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
        QueueReload(); // merges with the reload the action's Changed events already queued
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
        if (name == null || name == item.Name || !CheckName(name)) return;
        await Run(s => s.RenameAsync(item.Rel, name, item.IsDir));
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var items = Targets(sender);
        if (items.Count == 0) return;
        var what = items.Count == 1 ? $"“{items[0].Name}”" : $"这 {items.Count} 项";
        var answer = MessageBox.Show(this, $"从服务器上删除{what}？\n\n删除后进入服务器回收站，可在控制面板中还原；本机的副本会移到回收站。",
            "FileCopy", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        await Run(s => s.DeleteManyAsync(items.Select(i => (i.Rel, i.IsDir)).ToList()));
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Show(this, "新建文件夹", "文件夹名称：", "新建文件夹");
        if (name != null && CheckName(name)) await Run(s => s.MkdirAsync(JoinRel(_folder, name)));
    }

    private bool CheckName(string name)
    {
        if (NameError(name) is not { } error) return true;
        MessageBox.Show(this, error, "FileCopy");
        return false;
    }

    /// <summary>
    /// Why <paramref name="name"/> cannot name a new file or folder, or null. Kept copies live in a Windows
    /// folder, so Windows' rules apply; "a/b" or ".." would also address a different place on the server.
    /// </summary>
    internal static string? NameError(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "名称不能包含 / \\ : * ? \" < > | 等字符。";
        if (name.EndsWith('.') || name.EndsWith(' '))
            return "名称不能以句点或空格结尾。";
        if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase))
            return "名称不能是 CON、NUL 等系统保留名。";
        return null;
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
        var readOnly = _folder.Length > 0 && _sync?.IsReadOnly(_folder) == true;
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !readOnly ? DragDropEffects.Copy : DragDropEffects.None;
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

    // ---------- transfers / log ----------

    private void ClearTab_Click(object sender, RoutedEventArgs e)
    {
        if (BottomTabs.SelectedIndex == 0) _transfers.ClearFinished();
        else LogList.Items.Clear();
    }

    private void ClearTransfers_Click(object sender, RoutedEventArgs e) => _transfers.ClearFinished();

    // Only the list: log.txt is kept for troubleshooting.
    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogList.Items.Clear();

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var id = _settings.EquipmentId;
        if (string.IsNullOrEmpty(id))
        {
            MessageBox.Show(this, "请先在设置中填写设备 ID。", "FileCopy");
            _ = OpenSettings();
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
                result = SyncPaused ? "同步已暂停，备份仅保存在本机，恢复同步后自动上传" : "未连接服务器，备份仅保存在本机，连接后自动上传";
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
