using System.IO;
using FileCopy.Client.Api;

namespace FileCopy.Client.Tests;

public class SettingsWindowTests
{
    [Theory]
    [InlineData("XZT1-DB1", true)]
    [InlineData("车间一号机", true)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("a\u007fb", false)]
    [InlineData("CON", false)]
    [InlineData("con.txt", false)]
    [InlineData("CONSOLE", true)]
    public void EquipmentIdError(string id, bool valid) =>
        Assert.Equal(valid, SettingsWindow.EquipmentIdError(id) == null);

    [Fact]
    public void EquipmentIdError_CountsCharactersLikeTheServer()
    {
        // An emoji is two UTF-16 units but one character: the server accepts 64 of them.
        Assert.Null(SettingsWindow.EquipmentIdError(string.Concat(Enumerable.Repeat("😀", 64))));
        Assert.NotNull(SettingsWindow.EquipmentIdError(string.Concat(Enumerable.Repeat("😀", 65))));
    }

    [Theory]
    [InlineData("https://files.example.com", "secret", "XZT1-DB1", true)]
    [InlineData("files.example.com", "secret", "XZT1-DB1", false)]
    [InlineData("ftp://files.example.com", "secret", "XZT1-DB1", false)]
    [InlineData("https://files.example.com", "", "XZT1-DB1", false)]
    [InlineData("https://files.example.com", "secret", "", false)]
    public void ConnectionError(string url, string token, string equipmentId, bool valid) =>
        Assert.Equal(valid, SettingsWindow.ConnectionError(url, token, equipmentId) == null);

    [Theory]
    [InlineData("https://old.example.com", false)]
    [InlineData("HTTPS://OLD.example.com/", false)] // same server, written differently
    [InlineData("https://old.example.com:8443", true)]
    [InlineData("https://new.example.com", true)]
    public void ServerChanged(string url, bool changed) =>
        Assert.Equal(changed, SettingsWindow.ServerChanged("https://old.example.com", url));

    [Fact]
    public async Task TestConnection_ReportsTheServer()
    {
        using var server = new TestServer(new { renames = new Dictionary<string, string> { ["OLD-ID"] = "NEW-ID" } });

        var ok = await SettingsWindow.TestConnectionAsync(server.Url, TestServer.Token, TestServer.EquipmentId);
        Assert.StartsWith("连接成功", ok);
        Assert.Contains("未启用二步认证", ok);
        Assert.DoesNotContain("设备 ID", ok);

        Assert.Contains("NEW-ID", await SettingsWindow.TestConnectionAsync(server.Url, TestServer.Token, "OLD-ID"));
        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            SettingsWindow.TestConnectionAsync(server.Url, "wrong-token-0123456789", TestServer.EquipmentId));
    }
}

public class SettingsTests
{
    [Fact]
    public void SaveAndLoad_RoundTrips_WithoutLeavingTheTempFile()
    {
        var s = new Settings { ServerUrl = "https://files.example.com", EquipmentId = "XZT1-DB1", AutoConnect = true, CloseToTray = true };
        s.SetToken("secret-token-0123456789");
        s.Save();
        s.EquipmentId = "XZT1-CB";
        s.Save(); // replaces the previous file

        var loaded = Settings.Load();
        Assert.Equal("https://files.example.com", loaded.ServerUrl);
        Assert.Equal("XZT1-CB", loaded.EquipmentId);
        Assert.True(loaded.AutoConnect);
        Assert.True(loaded.CloseToTray);
        Assert.Equal("secret-token-0123456789", loaded.GetToken());
        Assert.False(File.Exists(Path.Combine(Settings.DataDir, "settings.json.tmp")));
    }

    [Fact]
    public void CloseToTray_OffByDefault() => Assert.False(new Settings().CloseToTray);
}
