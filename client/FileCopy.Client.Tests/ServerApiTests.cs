using System.IO;
using System.Net;
using System.Net.Sockets;
using FileCopy.Client.Api;

namespace FileCopy.Client.Tests;

public sealed class ServerApiTests : IDisposable
{
    private readonly TestServer _server = new();
    private readonly ServerApi _api;
    private readonly string _dir = TestEnv.NewDir("api");

    public ServerApiTests() => _api = new ServerApi(_server.Url + "/", TestServer.Token);

    public void Dispose()
    {
        _api.Dispose();
        _server.Dispose();
        TestEnv.TryDeleteDir(_dir);
    }

    private string LocalFile(string name, int size)
    {
        var p = Path.Combine(_dir, name);
        var data = new byte[size];
        new Random(size).NextBytes(data);
        File.WriteAllBytes(p, data);
        return p;
    }

    [Fact]
    public async Task Ping_WrongToken_Throws()
    {
        using var bad = new ServerApi(_server.Url, "wrong-token-0123456789");
        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => bad.PingAsync());
        Assert.Contains("401", ex.Message);
        await _api.PingAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ServerApi.ChunkSize)]
    [InlineData(ServerApi.ChunkSize * 2 + 12345)] // several parallel chunks, ragged tail
    public async Task Upload_ThenDownload_RoundTripsContentAndMtime(int size)
    {
        var src = LocalFile($"up{size}.bin", size);
        var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(src)).ToUnixTimeMilliseconds();

        var entry = await _api.UploadAsync(src, "dir/file.bin", 0, null, default).T(60);
        Assert.Equal(("dir/file.bin", (long)size, mtime), (entry.Path, entry.Size, entry.Mtime));
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(_server.PathOf("dir/file.bin")));

        var tree = await _api.GetTreeAsync();
        Assert.Contains(tree, e => e is { Path: "dir", IsDir: true });
        var remote = Assert.Single(tree, e => e.Path == "dir/file.bin");

        var dest = Path.Combine(_dir, "down", "file.bin");
        long reported = 0;
        await _api.DownloadToFileAsync(remote, dest, new SyncProgress(v => reported = Math.Max(reported, v)), default).T(60);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(dest));
        Assert.Equal(mtime, new DateTimeOffset(File.GetLastWriteTimeUtc(dest)).ToUnixTimeMilliseconds());
        Assert.False(File.Exists(dest + ".fcpart"));
        Assert.Equal(size, reported);
    }

    [Fact]
    public async Task DownloadToFile_CanReplaceFalse_KeepsExistingFile()
    {
        _server.Put("r.txt", "server");
        var remote = (await _api.GetTreeAsync()).Single(e => e.Path == "r.txt");
        var dest = Path.Combine(_dir, "keep", "r.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, "local");

        await Assert.ThrowsAsync<IOException>(() => _api.DownloadToFileAsync(remote, dest, null, default, canReplace: () => false));

        Assert.Equal("local", File.ReadAllText(dest));
        Assert.False(File.Exists(dest + ".fcpart"));
    }

    [Theory]
    [InlineData("空格 和 中文/a b.txt")]
    [InlineData("special/#100% & co+.txt")]
    public async Task Upload_ThenDownload_EscapesPaths(string rel)
    {
        var src = LocalFile("special.bin", 100);
        var entry = await _api.UploadAsync(src, rel, 0, null, default).T();
        Assert.Equal(rel, entry.Path);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(_server.PathOf(rel)));

        var dest = Path.Combine(_dir, "special-down.bin");
        await _api.DownloadToFileAsync(entry, dest, null, default).T();
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(dest));

        await _api.DeleteAsync(rel);
        Assert.False(_server.Exists(rel));
    }

    [Fact]
    public async Task DownloadRange_ReturnsRequestedBytes()
    {
        _server.Put("r.txt", "0123456789");
        var buf = await _api.DownloadRangeAsync("r.txt", 3, 4, default);
        Assert.Equal("3456", System.Text.Encoding.ASCII.GetString(buf));
    }

    [Fact]
    public async Task DownloadRange_ServerDown_ReportsRetriesThenThrows()
    {
        // Bound but not listening: connections are refused, and no other test's server can take the port meanwhile.
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var api = new ServerApi($"http://127.0.0.1:{((IPEndPoint)reserved.LocalEndPoint!).Port}/", TestServer.Token);
        var attempts = new List<int>();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            api.DownloadRangeAsync("r.txt", 0, 4, default, (a, _) => attempts.Add(a))).T(60);
        Assert.Equal(Enumerable.Range(1, ServerApi.MaxRetries - 1), attempts);
    }

    [Fact]
    public async Task Upload_WithStaleBaseMtime_KeepsConflictCopy()
    {
        var serverMtime = _server.Put("c.txt", "server");
        var src = Path.Combine(_dir, "c.txt");
        File.WriteAllText(src, "client");

        await _api.UploadAsync(src, "c.txt", serverMtime - 1000, null, default);
        Assert.Equal("client", _server.Read("c.txt"));
        Assert.Single(_server.FilesLike("", "c (conflict "));

        // Matching baseMtime: plain overwrite.
        var e = (await _api.GetTreeAsync()).Single(x => x.Path == "c.txt");
        File.WriteAllText(src, "client2");
        await _api.UploadAsync(src, "c.txt", e.Mtime, null, default);
        Assert.Equal("client2", _server.Read("c.txt"));
        Assert.Single(_server.FilesLike("", "c (conflict "));
    }

    [Fact]
    public async Task MkdirMoveDelete()
    {
        await _api.MkdirAsync("a/b");
        _server.Put("a/b/x.txt", "x");
        await _api.MoveAsync("a", "z");
        Assert.Equal("x", _server.Read("z/b/x.txt"));
        await _api.DeleteAsync("z");
        Assert.False(_server.Exists("z"));
        await _api.DeleteAsync("does/not/exist"); // 404 is not an error
    }

    [Fact]
    public async Task Move_OntoExisting_ThrowsIOException()
    {
        _server.Put("a.txt", "a");
        _server.Put("b.txt", "b");
        var ex = await Assert.ThrowsAsync<IOException>(() => _api.MoveAsync("a.txt", "b.txt"));
        Assert.Contains("b.txt", ex.Message);
        Assert.Equal("b", _server.Read("b.txt"));
    }

    /// <summary>Synchronous IProgress (Progress&lt;T&gt; posts to the thread pool, so values could arrive late).</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
