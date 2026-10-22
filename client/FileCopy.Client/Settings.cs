using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileCopy.Client;

public sealed class Settings
{
    public string ServerUrl { get; set; } = "http://";
    public string LocalFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FileCopy");
    public bool AutoConnect { get; set; }
    /// <summary>Token encrypted with DPAPI (current user), base64.</summary>
    public string ProtectedToken { get; set; } = "";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileCopy", "settings.json");

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

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
