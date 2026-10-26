using System.IO;
using FileCopy.Client.Local;
using FileCopy.Client.Transfers;

namespace FileCopy.Client.Tests;

public class LocalStateTests
{
    private static LocalState NewState() => LocalState.Load("http://unit.test", TestEnv.NewDir("state"));

    private static FileSnapshot Snap(bool pinned = true) => new() { ServerMtime = 1, Size = 1, LocalMtime = 1, Pinned = pinned };

    [Theory]
    [InlineData("a/b", "a", true)]
    [InlineData("a", "a", true)]
    [InlineData("A/B", "a", true)]
    [InlineData("ab", "a", false)]
    [InlineData("a", "a/b", false)]
    [InlineData("anything", "", true)]
    public void IsSameOrUnder(string path, string folder, bool expected) =>
        Assert.Equal(expected, LocalState.IsSameOrUnder(path, folder));

    [Theory]
    [InlineData("a/b/c.txt", "a/b")]
    [InlineData("c.txt", "")]
    public void Parent(string rel, string expected) => Assert.Equal(expected, LocalState.Parent(rel));

    [Fact]
    public void SaveAndLoad_RoundTrips_AndKeepsCaseInsensitiveLookups()
    {
        var folder = TestEnv.NewDir("state");
        var s = LocalState.Load("http://unit.test/", folder);
        s.AddKeptFolder("Docs");
        s.SetFile("Docs/A.txt", new FileSnapshot { ServerMtime = 5, Size = 6, LocalMtime = 7, Pinned = false });
        s.Flush();

        // Trailing slash / case differences in the key inputs map to the same state file.
        var t = LocalState.Load("HTTP://unit.test", folder + "\\");
        Assert.True(t.IsInKeptFolder("docs/sub/x"));
        var snap = t.GetFile("docs/a.TXT");
        Assert.NotNull(snap);
        Assert.Equal((5L, 6L, 7L, false), (snap!.ServerMtime, snap.Size, snap.LocalMtime, snap.Pinned));
    }

    [Fact]
    public async Task Save_WritesInTheBackground()
    {
        var folder = TestEnv.NewDir("state");
        var s = LocalState.Load("http://later.test", folder);
        for (int i = 0; i < 100; i++)
        {
            s.SetFile($"f{i}", Snap());
            s.Save(); // a burst, as when keeping a large folder
        }

        for (var deadline = DateTime.UtcNow.AddSeconds(10); LocalState.Load("http://later.test", folder).GetFile("f99") == null;)
        {
            Assert.True(DateTime.UtcNow < deadline, "state was not saved in the background");
            await Task.Delay(50);
        }
        Assert.Equal(100, LocalState.Load("http://later.test", folder).FileKeys().Count);
    }

    [Fact]
    public void Flush_WritesPendingChangesNow()
    {
        var folder = TestEnv.NewDir("state");
        var s = LocalState.Load("http://now.test", folder);
        s.SetFile("a", Snap());
        s.Save();
        s.Flush();
        Assert.NotNull(LocalState.Load("http://now.test", folder).GetFile("a"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"Files\":null,\"KeptFolders\":null}")]
    public void Load_CorruptState_StartsFresh(string content)
    {
        var folder = TestEnv.NewDir("state");
        var s = LocalState.Load("http://corrupt.test", folder);
        s.SetFile("x", Snap());
        s.Flush();
        var file = (string)typeof(LocalState).GetField("_path", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(s)!;
        File.WriteAllText(file, content);

        var t = LocalState.Load("http://corrupt.test", folder);
        Assert.Null(t.GetFile("x"));
        t.SetFile("y", Snap()); // usable
        Assert.False(t.IsInKeptFolder("y"));
    }

    [Fact]
    public void Load_NullFolderAndCaseDuplicateKeys_AreTolerated()
    {
        var folder = TestEnv.NewDir("state");
        var s = LocalState.Load("http://dup.test", folder);
        s.Flush();
        var file = (string)typeof(LocalState).GetField("_path", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(s)!;
        File.WriteAllText(file, """{"KeptFolders":["a",null],"Files":{"x":{"Size":1},"X":{"Size":2},"y":null}}""");

        var t = LocalState.Load("http://dup.test", folder);
        Assert.True(t.IsInKeptFolder("a/b"));
        Assert.False(t.IsInKeptFolder("other"));
        Assert.Equal(1, t.GetFile("X")!.Size); // first one wins
        Assert.Equal(["x"], t.FileKeys());
    }

    [Fact]
    public void UnkeepFolderTree_DissolvesAncestor_ButPinsSiblingsIndividually()
    {
        var s = NewState();
        s.AddKeptFolder("a");
        s.SetFile("a/x.txt", Snap(pinned: false));
        s.SetFile("a/b/y.txt", Snap(pinned: true));
        s.SetFile("other.txt", Snap(pinned: false));

        s.UnkeepFolderTree("a/b");

        Assert.False(s.IsInKeptFolder("a/x.txt"));
        Assert.True(s.IsPinned("a/x.txt"));      // sibling stays pinned on its own
        Assert.False(s.IsPinned("a/b/y.txt"));   // the unkept subtree is unpinned
        Assert.False(s.IsPinned("other.txt"));   // unrelated files untouched
    }

    [Fact]
    public void MoveTree_RekeysOnlyTheSubtree()
    {
        var s = NewState();
        s.AddKeptFolder("a/b");
        s.SetFile("a/b/x.txt", Snap());
        s.SetFile("a/bc/y.txt", Snap());

        s.MoveTree("a/b", "a/z");

        Assert.Null(s.GetFile("a/b/x.txt"));
        Assert.NotNull(s.GetFile("a/z/x.txt"));
        Assert.NotNull(s.GetFile("a/bc/y.txt"));
        Assert.True(s.IsInKeptFolder("a/z/x.txt"));
        Assert.False(s.IsInKeptFolder("a/b/x.txt"));
    }

    [Fact]
    public void MoveTree_CaseOnlyRename()
    {
        var s = NewState();
        s.SetFile("a.txt", Snap());
        s.MoveTree("a.txt", "A.txt");
        Assert.Equal(["A.txt"], s.FileKeys());
    }

    [Fact]
    public void RemoveTree_RemovesSubtreeOnly()
    {
        var s = NewState();
        s.AddKeptFolder("a");
        s.SetFile("a/x", Snap());
        s.SetFile("ab", Snap());
        s.RemoveTree("a");
        Assert.Equal(["ab"], s.FileKeys());
        Assert.False(s.IsInKeptFolder("a"));
    }
}

public class TransferItemTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L << 30, "5 GB")]
    [InlineData((1L << 20) - 1, "1 MB")]   // 1023.999 KB must not print as "1024 KB"
    [InlineData((1L << 30) - 1, "1 GB")]
    public void FormatBytes(long bytes, string expected) => Assert.Equal(expected, TransferItem.FormatBytes(bytes));

    [Fact]
    public void Percent_EmptyFile_Is100() => Assert.Equal(100, new TransferItem("x", TransferDirection.Upload, 0).Percent);

    [Fact]
    public void Report_UpdatesPercent_WithoutApplication()
    {
        var item = new TransferItem("x", TransferDirection.Download, 200);
        item.Report(50);
        Assert.Equal(25, item.Percent);
        item.Complete();
        Assert.Equal(100, item.Percent);
        Assert.Equal("完成", item.Status);
    }
}
