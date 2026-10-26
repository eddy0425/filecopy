using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using FileCopy.Client.Api;

namespace FileCopy.Client.Tests;

public sealed class ServerApiTests : IDisposable
{
    private readonly TestServer _server = new();
    private readonly ServerApi _api;
    private readonly string _dir = TestEnv.NewDir("api");

    public ServerApiTests() => _api = new ServerApi(_server.Url + "/", TestServer.Token, TestServer.EquipmentId);

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
        using var bad = new ServerApi(_server.Url, "wrong-token-0123456789", TestServer.EquipmentId);
        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => bad.PingAsync());
        Assert.Contains("401", ex.Message);
        var ping = await _api.PingAsync();
        Assert.False(ping.Totp);
        Assert.Equal(TestServer.EquipmentId, ping.EquipmentId);
    }

    /// <summary>RFC 6238 code for a base32 secret (HMAC-SHA1, 30 s, 6 digits).</summary>
    private static string Totp(string secret, DateTimeOffset at)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var key = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in secret)
        {
            buffer = buffer << 5 | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8) key.Add((byte)(buffer >> (bits -= 8)));
        }
        var msg = BitConverter.GetBytes(at.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        var h = System.Security.Cryptography.HMACSHA1.HashData(key.ToArray(), msg);
        int o = h[^1] & 15;
        int v = (h[o] & 0x7f) << 24 | h[o + 1] << 16 | h[o + 2] << 8 | h[o + 3];
        return (v % 1000000).ToString("D6");
    }

    [Fact]
    public async Task Ping_ReportsTotpAndRenamedId_AndVerifiesCodes()
    {
        const string secret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
        using var server = new TestServer(new { totpSecret = secret, renames = new Dictionary<string, string> { ["OLD-ID"] = "NEW-ID" } });
        using var api = new ServerApi(server.Url, TestServer.Token, "OLD-ID");

        var ping = await api.PingAsync();
        Assert.True(ping.Totp);
        Assert.Equal("NEW-ID", ping.EquipmentId); // renamed in the control panel

        var now = DateTimeOffset.UtcNow;
        var valid = new[] { -30, 0, 30 }.Select(d => Totp(secret, now.AddSeconds(d))).ToHashSet();
        var wrong = Enumerable.Range(0, 10).Select(i => $"{i}{i}{i}{i}{i}{i}").First(c => !valid.Contains(c));
        Assert.False(await api.VerifyTotpAsync(wrong));
        Assert.True(await api.VerifyTotpAsync(Totp(secret, now)));
        // Each code works once (it may just have been used to log in to the control panel).
        await Assert.ThrowsAsync<TotpCodeUsedException>(() => api.VerifyTotpAsync(Totp(secret, now)));
    }

    [Fact]
    public async Task SetEquipmentId_AppliesToFollowingRequests()
    {
        Assert.Equal(TestServer.EquipmentId, (await _api.PingAsync()).EquipmentId);
        _api.SetEquipmentId("车间 2 号机");
        Assert.Equal("车间 2 号机", (await _api.PingAsync()).EquipmentId); // non-ASCII survives the header encoding
    }

    [Fact]
    public async Task PausedServer_TransfersFailWithoutRetrying()
    {
        using var server = new TestServer(new { paused = true });
        server.Put("a.bin", "data");
        using var api = new ServerApi(server.Url, TestServer.Token, TestServer.EquipmentId);
        var retries = 0;
        await Assert.ThrowsAsync<ServerPausedException>(() =>
            api.DownloadRangeAsync("a.bin", 0, 4, default, (_, _) => retries++).T());
        Assert.Equal(0, retries);
        await Assert.ThrowsAsync<ServerPausedException>(() => api.UploadAsync(LocalFile("p.bin", 10), "p.bin", 0, null, default).T());
    }

    [Fact]
    public async Task PausedServer_AndReadOnlyFolder_GiveClearErrors()
    {
        using (var paused = new TestServer(new { paused = true }))
        using (var api = new ServerApi(paused.Url, TestServer.Token, TestServer.EquipmentId))
        {
            var ex = await Assert.ThrowsAsync<ServerPausedException>(() => api.PingAsync());
            Assert.Equal("服务器已暂停连接", ex.Message);
        }

        using var server = new TestServer(new { readOnlyFolders = new[] { "ro" } });
        server.Put("ro/a.txt", "a");
        using var ro = new ServerApi(server.Url, TestServer.Token, TestServer.EquipmentId);
        await Assert.ThrowsAsync<ReadOnlyFolderException>(() => ro.DeleteAsync("ro/a.txt"));
        await Assert.ThrowsAsync<ReadOnlyFolderException>(() => ro.MkdirAsync("ro/sub"));
        await Assert.ThrowsAsync<ReadOnlyFolderException>(() => ro.UploadAsync(LocalFile("x.bin", 10), "ro/x.bin", 0, null, default));
        var tree = await ro.GetTreeAsync();
        Assert.True(tree.Single(e => e.Path == "ro/a.txt").ReadOnly);
        Assert.Equal("a", server.Read("ro/a.txt"));
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
        using var api = new ServerApi($"http://127.0.0.1:{((IPEndPoint)reserved.LocalEndPoint!).Port}/", TestServer.Token, TestServer.EquipmentId);
        var attempts = new List<int>();
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            api.DownloadRangeAsync("r.txt", 0, 4, default, (a, _) => attempts.Add(a)).T(60));
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
