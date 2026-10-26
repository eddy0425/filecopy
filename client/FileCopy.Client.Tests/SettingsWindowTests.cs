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
