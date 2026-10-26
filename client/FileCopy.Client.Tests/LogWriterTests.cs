using System.IO;

namespace FileCopy.Client.Tests;

public class LogWriterTests
{
    [Fact]
    public void Dispose_WritesQueuedLinesInOrder_AndAppends()
    {
        var path = Path.Combine(TestEnv.NewDir("log"), "log.txt");
        File.WriteAllText(path, "old\n");
        var log = new LogWriter(path);
        for (int i = 0; i < 1000; i++) log.Write($"line {i}");
        log.Dispose();
        log.Write("after dispose"); // dropped, no exception

        var lines = File.ReadAllLines(path);
        Assert.Equal(["old", .. Enumerable.Range(0, 1000).Select(i => $"line {i}")], lines);
    }

    [Fact]
    public void LargeFile_IsRotated()
    {
        var path = Path.Combine(TestEnv.NewDir("log"), "log.txt");
        var log = new LogWriter(path, maxBytes: 100);
        log.Write(new string('a', 150)); // over the limit: moved to log.1.txt
        log.Dispose();
        log = new LogWriter(path, maxBytes: 100);
        log.Write("fresh");
        log.Dispose();

        Assert.Equal(new string('a', 150), File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "log.1.txt")).TrimEnd());
        Assert.Equal("fresh", File.ReadAllText(path).TrimEnd());
    }
}
