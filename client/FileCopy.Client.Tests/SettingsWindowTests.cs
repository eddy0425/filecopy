using System.IO;

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
}

public class SettingsTests
{
    [Fact]
    public void SaveAndLoad_RoundTrips_WithoutLeavingTheTempFile()
    {
        var s = new Settings { ServerUrl = "https://files.example.com", EquipmentId = "XZT1-DB1", AutoConnect = true };
        s.SetToken("secret-token-0123456789");
        s.Save();
        s.EquipmentId = "XZT1-CB";
        s.Save(); // replaces the previous file

        var loaded = Settings.Load();
        Assert.Equal("https://files.example.com", loaded.ServerUrl);
        Assert.Equal("XZT1-CB", loaded.EquipmentId);
        Assert.True(loaded.AutoConnect);
        Assert.Equal("secret-token-0123456789", loaded.GetToken());
        Assert.False(File.Exists(Path.Combine(Settings.DataDir, "settings.json.tmp")));
    }
}
