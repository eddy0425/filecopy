namespace FileCopy.Client.Tests;

public class MainWindowTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LockRequired_AtStartup()
    {
        // Regression: a restart showed the window without asking for a code.
        Assert.True(MainWindow.LockRequired(visible: false, totpEnabled: true, hasToken: true, hiddenToTray: true, DateTime.MinValue, Now));
    }

    [Theory]
    [InlineData(true, true, true, true)]   // already visible
    [InlineData(false, false, true, true)] // two-step verification off
    [InlineData(false, true, false, true)] // no token: the code cannot be checked, the settings must stay reachable
    [InlineData(false, true, true, false)] // not hidden since the last unlock
    public void LockRequired_NotNeeded(bool visible, bool totpEnabled, bool hasToken, bool hiddenToTray) =>
        Assert.False(MainWindow.LockRequired(visible, totpEnabled, hasToken, hiddenToTray, DateTime.MinValue, Now));

    [Theory]
    [InlineData("报告.docx", true)]
    [InlineData("新建文件夹", true)]
    [InlineData(".gitignore", true)]
    [InlineData("CONSOLE", true)]
    [InlineData("a/b", false)]   // regression: new folders were not checked, "a/b" created two levels
    [InlineData("../x", false)]  // regression: would be created in the parent folder
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("a:b", false)]
    [InlineData("a\tb", false)]
    [InlineData("name.", false)] // Windows drops the dot: the local copy would get another name
    [InlineData("CON", false)]
    [InlineData("nul.txt", false)]
    public void NameError(string name, bool valid) =>
        Assert.Equal(valid, MainWindow.NameError(name) == null);

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)] // closing exits: starting hidden would leave no window to close
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, false)] // no token: the settings must show
    public void StartsMinimized(bool autoConnect, bool closeToTray, bool hasToken, bool minimized) =>
        Assert.Equal(minimized, MainWindow.StartsMinimized(autoConnect, closeToTray, hasToken));

    [Fact]
    public void ExitWarning_NamesWhatWouldBeInterrupted()
    {
        Assert.Null(MainWindow.ExitWarning(0, false));
        Assert.Equal("有 2 个传输未完成，退出将中断，仍要退出吗？", MainWindow.ExitWarning(2, false));
        Assert.Equal("备份进行中，退出将中断，仍要退出吗？", MainWindow.ExitWarning(0, true));
        Assert.Equal("有 1 个传输未完成，备份进行中，退出将中断，仍要退出吗？", MainWindow.ExitWarning(1, true));
    }

    [Fact]
    public void LockRequired_GracePeriodAfterUnlock()
    {
        Assert.False(MainWindow.LockRequired(false, true, true, true, Now.AddMinutes(-4), Now));
        Assert.True(MainWindow.LockRequired(false, true, true, true, Now.AddMinutes(-5), Now));
    }
}
