using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

namespace FileCopy.Client;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;

    public SettingsWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        ServerUrlBox.Text = settings.ServerUrl;
        TokenBox.Password = settings.GetToken();
        LocalFolderBox.Text = settings.LocalFolder;
        EquipmentIdBox.Text = settings.EquipmentId;
        AutoConnectBox.IsChecked = settings.AutoConnect;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择本地保留文件夹" };
        if (Directory.Exists(LocalFolderBox.Text)) dlg.InitialDirectory = LocalFolderBox.Text;
        if (dlg.ShowDialog(this) == true) LocalFolderBox.Text = dlg.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var url = ServerUrlBox.Text.Trim();
        var token = TokenBox.Password.Trim();
        var folder = LocalFolderBox.Text.Trim();
        var equipmentId = EquipmentIdBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            MessageBox.Show(this, "请输入有效的服务器地址，例如 https://files.example.com", "FileCopy");
            return;
        }
        if (token.Length == 0)
        {
            MessageBox.Show(this, "请填写 Token。", "FileCopy");
            return;
        }
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            MessageBox.Show(this, "请选择本地保留文件夹（完整路径）。", "FileCopy");
            return;
        }
        if (equipmentId.Length == 0 || equipmentId.Length > 64)
        {
            MessageBox.Show(this, "请填写设备 ID（例如 XZT1-DB1，最多 64 个字符），服务器用它区分设备。", "FileCopy");
            return;
        }
        // ".7z" is appended to form the archive name, so a trailing dot is fine, but reserved names stay reserved.
        if (equipmentId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || Regex.IsMatch(equipmentId, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase))
        {
            MessageBox.Show(this, "设备 ID 不能包含 / \\ : * ? \" < > | 等字符，也不能是 CON、NUL 等系统保留名。", "FileCopy");
            return;
        }

        _settings.ServerUrl = url;
        _settings.SetToken(token);
        _settings.LocalFolder = folder;
        _settings.EquipmentId = equipmentId;
        _settings.AutoConnect = AutoConnectBox.IsChecked == true;
        _settings.Save();
        DialogResult = true;
    }
}
