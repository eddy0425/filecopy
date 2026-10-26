using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileCopy.Client;

public sealed class Settings
{
    /// <summary>%AppData%\FileCopy: settings.json, state-*.json and log.txt (kept out of Documents, which OneDrive may sync). Settable so tests can redirect it.</summary>
    public static string DataDir { get; internal set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileCopy");

    public string ServerUrl { get; set; } = "http://";
    public string LocalFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FileCopy", "data");
    public bool AutoConnect { get; set; }
    /// <summary>Identifies this machine; names its backup archive, e.g. XZT1-CB.</summary>
    public string EquipmentId { get; set; } = "";
    /// <summary>
    /// The server had two-step verification enabled at the last ping: the window then asks for a code
    /// at startup and after being closed to the tray. Remembered so the lock also applies when starting offline.
    /// </summary>
    public bool ServerTotpEnabled { get; set; }
    /// <summary>Token encrypted with DPAPI (current user), base64.</summary>
    public string ProtectedToken { get; set; } = "";

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    public string GetToken()
    {
        if (string.IsNullOrEmpty(ProtectedToken)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            return "";
        }
    }

    public void SetToken(string token) =>
        ProtectedToken = Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

    public static Settings Load()
    {
        Directory.CreateDirectory(DataDir); // log.txt is appended here before settings are ever saved
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception)
        {
            // Corrupt settings: start fresh.
        }
        return new Settings();
    }

    /// <summary>Written to a temp file first: a write cut short (crash, power loss) must not lose the settings.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
