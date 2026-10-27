using System.Runtime.InteropServices;
using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace FileCopy.Client;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private Forms.NotifyIcon? _tray;
    private Drawing.Icon? _icon;
    private Drawing.Icon? _pausedIcon;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\FileCopy.Client.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("FileCopy 已经在运行（见系统托盘）。", "FileCopy");
            Shutdown();
            return;
        }

        _window = new MainWindow();
        _icon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? Drawing.SystemIcons.Application;
        _pausedIcon = WithPauseBadge(_icon);
        _tray = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "FileCopy",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Items.Add("打开 FileCopy", null, (_, _) => ShowWindow());
        _tray.ContextMenuStrip.Items.Add("打开同步文件夹", null, async (_, _) =>
        {
            if (await _window.UnlockAsync()) _window.OpenSyncFolder();
        });
        var syncItem = _tray.ContextMenuStrip.Items.Add("立即同步", null, (_, _) => _window.ToggleSync());
        _tray.ContextMenuStrip.Opening += (_, _) => syncItem.Text = _window.CanPauseSync ? "暂停同步" : "立即同步";
        _tray.ContextMenuStrip.Items.Add("-");
        _tray.ContextMenuStrip.Items.Add("退出", null, (_, _) =>
        {
            if (_window.ConfirmExit()) ExitApp();
        });
        _tray.DoubleClick += (_, _) => ShowWindow();
        _window.SyncPausedChanged += () =>
        {
            _tray.Icon = _window.SyncPaused ? _pausedIcon : _icon;
            _tray.Text = _window.SyncPaused ? "FileCopy（已暂停同步）" : "FileCopy";
        };
        _window.ExitRequested += ExitApp;

        if (_window.StartMinimized) _window.Hide();
        else StartShown();
    }

    // Locked like reopening from the tray, so restarting cannot skip the code. Without the tray to fall back to,
    // a window that stays locked leaves nothing to use: exit.
    private async void StartShown()
    {
        if (!await ShowWindowAsync() && !_window!.CloseToTray) ExitApp();
    }

    private async void ShowWindow() => await ShowWindowAsync();

    // Locked at startup and after being closed to the tray when two-step verification is enabled on the server.
    private async Task<bool> ShowWindowAsync()
    {
        if (!await _window!.UnlockAsync()) return false;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        return true;
    }

    private void ExitApp()
    {
        if (_tray == null) return; // already exiting
        _window?.Shutdown();
        _tray.Visible = false;
        _tray.Dispose();
        _tray = null;
        _pausedIcon?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>The tray icon with a pause badge (an orange disc with two white bars) in its lower right corner.</summary>
    private static Drawing.Icon WithPauseBadge(Drawing.Icon icon)
    {
        var size = Forms.SystemInformation.SmallIconSize;
        using var bitmap = new Drawing.Bitmap(size.Width, size.Height);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var small = new Drawing.Icon(icon, size)) g.DrawIcon(small, new Drawing.Rectangle(0, 0, size.Width, size.Height));
            float d = size.Width * 0.62f, x = size.Width - d, y = size.Height - d;
            using (var orange = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xF0, 0x8C, 0x00))) g.FillEllipse(orange, x, y, d, d);
            float barWidth = d * 0.16f, barHeight = d * 0.5f, top = y + (d - barHeight) / 2;
            g.FillRectangle(Drawing.Brushes.White, x + d * 0.28f, top, barWidth, barHeight);
            g.FillRectangle(Drawing.Brushes.White, x + d * 0.56f, top, barWidth, barHeight);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Drawing.Icon.FromHandle(handle); // does not own the handle
            return (Drawing.Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);
}
