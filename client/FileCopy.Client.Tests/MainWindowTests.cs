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

    [Fact]
    public void LockRequired_GracePeriodAfterUnlock()
    {
        Assert.False(MainWindow.LockRequired(false, true, true, true, Now.AddMinutes(-4), Now));
        Assert.True(MainWindow.LockRequired(false, true, true, true, Now.AddMinutes(-5), Now));
    }
}
