using System.Windows;
using Forms = System.Windows.Forms;

namespace FileCopy.Client;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private Forms.NotifyIcon? _tray;
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
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "FileCopy",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Items.Add("打开 FileCopy", null, (_, _) => ShowWindow());
        _tray.ContextMenuStrip.Items.Add("打开同步文件夹", null, async (_, _) =>
        {
            if (await _window.UnlockAsync()) _window.OpenSyncFolder();
        });
        _tray.ContextMenuStrip.Items.Add("立即同步", null, (_, _) => _window.SyncNow());
        _tray.ContextMenuStrip.Items.Add("-");
        _tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => ExitApp());
        _tray.DoubleClick += (_, _) => ShowWindow();

        if (_window.StartMinimized) _window.Hide();
        else ShowWindow(); // locked like reopening from the tray, so restarting cannot skip the code
    }

    // Locked at startup and after being closed to the tray when two-step verification is enabled on the server.
    private async void ShowWindow()
    {
        if (!await _window!.UnlockAsync()) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        _window?.Shutdown();
        if (_tray != null) _tray.Visible = false;
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
