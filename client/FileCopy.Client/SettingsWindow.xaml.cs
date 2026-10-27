using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using FileCopy.Client.Api;
using FileCopy.Client.Local;
using Microsoft.Win32;

namespace FileCopy.Client;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private bool _tokenCleared; // locked and the server address changed: the saved token was taken out of the box

    /// <param name="locked">
    /// Opened from the lock prompt: changing the server address clears the token box, so pointing the client at a
    /// server of one's own neither receives the saved token nor unlocks the window with it.
    /// </param>
    public SettingsWindow(Settings settings, bool locked = false)
    {
        InitializeComponent();
        _settings = settings;
        ServerUrlBox.Text = settings.ServerUrl;
        TokenBox.Password = settings.GetToken();
        if (locked)
            ServerUrlBox.TextChanged += (_, _) =>
            {
                if (_tokenCleared || !ServerChanged(settings.ServerUrl, ServerUrlBox.Text.Trim())) return;
                _tokenCleared = true;
                TokenBox.Clear();
            };
        LocalFolderBox.Text = settings.LocalFolder;
        EquipmentIdBox.Text = settings.EquipmentId;
        CloseToTrayBox.IsChecked = settings.CloseToTray;
        AutoConnectBox.IsChecked = settings.AutoConnect;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择本地保留文件夹" };
        if (Directory.Exists(LocalFolderBox.Text)) dlg.InitialDirectory = LocalFolderBox.Text;
        if (dlg.ShowDialog(this) == true) LocalFolderBox.Text = dlg.FolderName;
    }

    /// <summary>Checks the fields a connection needs; shows the first problem and returns false.</summary>
    private bool CheckConnectionFields(string url, string token, string equipmentId)
    {
        if (_tokenCleared && token.Length == 0)
        {
            MessageBox.Show(this, "已更改服务器地址，请重新输入该服务器的 Token。", "FileCopy");
            TokenBox.Focus();
            return false;
        }
        if (ConnectionError(url, token, equipmentId) is not { } error) return true;
        MessageBox.Show(this, error, "FileCopy");
        return false;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var url = ServerUrlBox.Text.Trim();
        var token = TokenBox.Password.Trim();
        var equipmentId = EquipmentIdBox.Text.Trim();
        if (!CheckConnectionFields(url, token, equipmentId)) return;
        TestButton.IsEnabled = false;
        string result;
        try
        {
            result = await TestConnectionAsync(url, token, equipmentId);
        }
        catch (Exception ex)
        {
            result = "连接失败: " + SyncService.ErrorText(ex);
        }
        if (!IsLoaded) return; // closed while testing
        TestButton.IsEnabled = true;
        MessageBox.Show(this, result, "FileCopy");
    }

    /// <summary>Pings the server with the entered values; the success text, or throws like <see cref="ServerApi.PingAsync"/>.</summary>
    internal static async Task<string> TestConnectionAsync(string url, string token, string equipmentId)
    {
        using var api = new ServerApi(url, token, equipmentId);
        var ping = await api.PingAsync();
        var result = "连接成功。\n" + (ping.Totp ? "服务器已启用二步认证。" : "服务器未启用二步认证。");
        if (!string.IsNullOrEmpty(ping.EquipmentId) && ping.EquipmentId != equipmentId)
            result += $"\n服务器记录的设备 ID 为 {ping.EquipmentId}，连接后将自动改用。";
        return result;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var url = ServerUrlBox.Text.Trim();
        var token = TokenBox.Password.Trim();
        var folder = LocalFolderBox.Text.Trim();
        var equipmentId = EquipmentIdBox.Text.Trim();
        if (!CheckConnectionFields(url, token, equipmentId)) return;
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            MessageBox.Show(this, "请选择本地保留文件夹（完整路径）。", "FileCopy");
            return;
        }

        _settings.ServerUrl = url;
        _settings.SetToken(token);
        _settings.LocalFolder = folder;
        _settings.EquipmentId = equipmentId;
        _settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        _settings.AutoConnect = AutoConnectBox.IsChecked == true;
        _settings.Save();
        DialogResult = true;
    }

    /// <summary>Why the server address, token or EquipmentId cannot be used to connect, or null.</summary>
    internal static string? ConnectionError(string url, string token, string equipmentId)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return "请输入有效的服务器地址，例如 https://files.example.com";
        if (token.Length == 0) return "请填写 Token。";
        return EquipmentIdError(equipmentId);
    }

    /// <summary>Whether <paramref name="url"/> addresses another server than <paramref name="savedUrl"/> (ignoring case and a trailing slash).</summary>
    internal static bool ServerChanged(string savedUrl, string url) =>
        !string.Equals(savedUrl.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Why <paramref name="id"/> cannot be an EquipmentId, or null. The server applies the same rules.</summary>
    public static string? EquipmentIdError(string id)
    {
        // Characters as the server counts them: an emoji is one, not two UTF-16 units.
        var length = id.EnumerateRunes().Count();
        if (length == 0 || length > 64)
            return "请填写设备 ID（例如 XZT1-DB1，最多 64 个字符），服务器用它区分设备。";
        // ".7z" is appended to form the archive name, so a trailing dot is fine, but reserved names stay reserved.
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains('\u007f')
            || Regex.IsMatch(id, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase))
            return "设备 ID 不能包含 / \\ : * ? \" < > | 等字符，也不能是 CON、NUL 等系统保留名。";
        return null;
    }
}
