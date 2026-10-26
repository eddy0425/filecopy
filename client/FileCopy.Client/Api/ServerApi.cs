using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace FileCopy.Client.Api;

public sealed record RemoteEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("isDir")] bool IsDir,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("mtime")] long Mtime,
    // In (or is) a folder set read-only in the control panel: clients may only download.
    [property: JsonPropertyName("readOnly")] bool ReadOnly = false);

/// <param name="Totp">Two-step verification is enabled: the window locks after being closed to the tray.</param>
/// <param name="EquipmentId">This device's ID as the server knows it; differs from ours after a rename in the control panel.</param>
public sealed record PingResult(
    [property: JsonPropertyName("totp")] bool Totp,
    [property: JsonPropertyName("equipmentId")] string EquipmentId);

/// <summary>
/// HTTP client for the FileCopy server. Large transfers are split into chunks
/// sent over several parallel connections, which matters a lot on high-latency
/// overseas links where a single TCP stream cannot fill the pipe.
/// </summary>
public sealed class ServerApi : IDisposable
{
    public const int ChunkSize = 4 << 20;
    public const int Parallelism = 4;
    public const int MaxRetries = 4;
    // A transfer is abandoned when no bytes arrive for this long, rather than after a fixed total
    // time, so a dead connection is noticed quickly while slow-but-alive links still finish.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private volatile string _equipmentId;

    public ServerApi(string baseUrl, string token, string equipmentId)
    {
        _equipmentId = equipmentId;
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 32,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.None,
        };
        _http = new HttpClient(new EquipmentIdHandler(this, handler))
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan, // per-request timeouts are applied below
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Switches to an ID assigned in the control panel; applies to requests started from now on.</summary>
    public void SetEquipmentId(string equipmentId) => _equipmentId = equipmentId;

    /// <summary>Adds X-Equipment-Id per request, so it can change while transfers are running.</summary>
    private sealed class EquipmentIdHandler(ServerApi api, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.Remove("X-Equipment-Id");
            request.Headers.Add("X-Equipment-Id", Uri.EscapeDataString(api._equipmentId));
            return base.SendAsync(request, ct);
        }
    }

    private static string Q(string path) => Uri.EscapeDataString(path);

    /// <summary>Errors that retrying will not fix: bad token, read-only folder, connections paused by the administrator.</summary>
    private static bool IsFatal(Exception ex) =>
        ex is UnauthorizedException or ReadOnlyFolderException or ServerPausedException
        || ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable };

    /// <summary>EnsureSuccessStatusCode with readable errors for the statuses the server uses on purpose.</summary>
    private static void EnsureOk(HttpResponseMessage resp)
    {
        switch (resp.StatusCode)
        {
            case HttpStatusCode.Unauthorized: throw new UnauthorizedException("Token 无效 (401)");
            case HttpStatusCode.Forbidden: throw new ReadOnlyFolderException();
            case HttpStatusCode.ServiceUnavailable: throw new ServerPausedException();
        }
        resp.EnsureSuccessStatusCode();
    }

    private static CancellationTokenSource WithTimeout(CancellationToken ct, int seconds = 60)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    public async Task<PingResult> PingAsync(CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct, 15);
        using var resp = await _http.GetAsync("api/ping", cts.Token);
        if (resp.StatusCode == HttpStatusCode.BadRequest)
            throw new InvalidOperationException("服务器要求填写设备 ID，请在设置中填写");
        EnsureOk(resp);
        return await resp.Content.ReadFromJsonAsync<PingResult>(cts.Token)
               ?? throw new InvalidDataException("bad ping response");
    }

    /// <summary>Checks a two-step verification code (to unlock the window). False when it is wrong.</summary>
    public async Task<bool> VerifyTotpAsync(string code, CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct, 15);
        using var resp = await _http.PostAsJsonAsync("api/totp/verify", new { code }, cts.Token);
        if (resp.StatusCode == HttpStatusCode.Forbidden) return false;
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("尝试次数过多，请 5 分钟后再试");
        EnsureOk(resp);
        return true;
    }

    public async Task<List<RemoteEntry>> GetTreeAsync(CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct, 120);
        return await _http.GetFromJsonAsync<List<RemoteEntry>>("api/tree", cts.Token) ?? [];
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct);
        using var resp = await _http.DeleteAsync($"api/file?path={Q(path)}", cts.Token);
        if (resp.StatusCode != HttpStatusCode.NotFound)
            EnsureOk(resp);
    }

    public async Task MoveAsync(string from, string to, CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct);
        using var resp = await _http.PostAsJsonAsync("api/move", new { from, to }, cts.Token);
        // The server refuses to replace an existing item (e.g. created by another device since the last refresh).
        if (resp.StatusCode == HttpStatusCode.Conflict)
            throw new IOException($"已存在同名项: {Path.GetFileName(to)}");
        EnsureOk(resp);
    }

    public async Task MkdirAsync(string path, CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct);
        using var resp = await _http.PostAsJsonAsync("api/mkdir", new { path }, cts.Token);
        EnsureOk(resp);
    }

    /// <summary>
    /// Downloads [offset, offset+length) of a file into a new buffer, with retries.
    /// <paramref name="onRetry"/> gets the failed attempt number and its error before each retry.
    /// </summary>
    public async Task<byte[]> DownloadRangeAsync(string path, long offset, int length, CancellationToken ct,
        Action<int, Exception>? onRetry = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(StallTimeout);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"api/file?path={Q(path)}");
                req.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                resp.EnsureSuccessStatusCode();
                var buf = new byte[length];
                await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
                for (int read = 0; read < length;)
                {
                    int n = await s.ReadAsync(buf.AsMemory(read), cts.Token);
                    if (n == 0) throw new EndOfStreamException("服务器提前关闭了连接");
                    read += n;
                    cts.CancelAfter(StallTimeout); // data is flowing: restart the stall timer
                }
                return buf;
            }
            catch (Exception ex) when (attempt < MaxRetries && !ct.IsCancellationRequested && !IsFatal(ex))
            {
                onRetry?.Invoke(attempt, ex);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }
    }

    /// <summary>
    /// Downloads a byte range as ordered chunks fetched over parallel connections.
    /// <paramref name="onChunk"/> is invoked sequentially, in file order.
    /// </summary>
    public async Task DownloadChunkedAsync(string path, long offset, long length,
        Func<long, byte[], Task> onChunk, CancellationToken ct, Action<int, Exception>? onRetry = null)
    {
        var pending = new Queue<(long Offset, Task<byte[]> Data)>();
        long next = offset, end = offset + length;
        // Cancelled on exit so chunks abandoned after a failure stop (and stop retrying) right away.
        using var chunksCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        void StartNext()
        {
            int len = (int)Math.Min(ChunkSize, end - next);
            pending.Enqueue((next, DownloadRangeAsync(path, next, len, chunksCts.Token, onRetry)));
            next += len;
        }

        try
        {
            while (pending.Count < Parallelism && next < end) StartNext();
            while (pending.Count > 0)
            {
                var (off, task) = pending.Dequeue();
                var data = await task;
                if (next < end) StartNext();
                await onChunk(off, data);
            }
        }
        finally
        {
            chunksCts.Cancel();
            // Observe abandoned tasks so failures don't surface as unobserved exceptions.
            foreach (var (_, t) in pending) _ = t.ContinueWith(x => _ = x.Exception, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Downloads a whole file to <paramref name="localPath"/> via a ".fcpart" temp file,
    /// then renames it into place and applies the server mtime. <paramref name="canReplace"/> is asked
    /// just before that rename; if it returns false the download is discarded and an IOException thrown.
    /// </summary>
    public async Task DownloadToFileAsync(RemoteEntry entry, string localPath, IProgress<long>? progress, CancellationToken ct,
        Func<bool>? canReplace = null, Action<int, Exception>? onRetry = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var part = localPath + ".fcpart";
        try
        {
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1, true))
            {
                fs.SetLength(entry.Size);
                long done = 0;
                await DownloadChunkedAsync(entry.Path, 0, entry.Size, async (off, data) =>
                {
                    fs.Position = off;
                    await fs.WriteAsync(data, ct);
                    progress?.Report(done += data.Length);
                }, ct, onRetry);
            }
            File.SetLastWriteTimeUtc(part, DateTimeOffset.FromUnixTimeMilliseconds(entry.Mtime).UtcDateTime);
            if (canReplace?.Invoke() == false)
                throw new IOException("目标文件在下载过程中被修改，已放弃本次下载");
            File.Move(part, localPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Uploads a local file with parallel chunks. Returns the server entry.</summary>
    public async Task<RemoteEntry> UploadAsync(string localPath, string remotePath, long baseMtime,
        IProgress<long>? progress, CancellationToken ct, Action<int, Exception>? onRetry = null)
    {
        var info = new FileInfo(localPath);
        long size = info.Length;
        long mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();

        string id;
        int chunkSize;
        using (var cts = WithTimeout(ct))
        using (var resp = await _http.PostAsJsonAsync("api/upload/init", new { path = remotePath, size, mtime }, cts.Token))
        {
            EnsureOk(resp);
            var init = await resp.Content.ReadFromJsonAsync<InitResponse>(cts.Token)
                       ?? throw new InvalidDataException("bad init response");
            id = init.Id;
            chunkSize = init.ChunkSize > 0 ? init.ChunkSize : ChunkSize;
        }

        // Chunks are read one at a time, so a file replaced mid-upload would arrive as a mix of
        // old and new content. Don't send or commit that; the next refresh uploads the new file.
        void ThrowIfChanged()
        {
            var now = new FileInfo(localPath); // fresh instance: FileInfo.Refresh is not safe across parallel chunk workers
            if (now.Length != size || new DateTimeOffset(now.LastWriteTimeUtc).ToUnixTimeMilliseconds() != mtime)
                throw new IOException("上传过程中本地文件被修改，已放弃本次上传（下次刷新会重新上传）");
        }

        long done = 0;
        var offsets = new List<long>();
        for (long o = 0; o < size; o += chunkSize) offsets.Add(o);

        await Parallel.ForEachAsync(offsets, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (off, token) =>
            {
                int len = (int)Math.Min(chunkSize, size - off);
                var buf = new byte[len];
                await using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, true))
                {
                    fs.Position = off;
                    await fs.ReadExactlyAsync(buf, token);
                }
                ThrowIfChanged(); // fail early instead of after the whole file has been sent
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        cts.CancelAfter(StallTimeout);
                        using var content = new StallResetContent(buf, cts);
                        using var resp = await _http.PutAsync($"api/upload/{id}?offset={off}", content, cts.Token);
                        resp.EnsureSuccessStatusCode();
                        break;
                    }
                    catch (Exception ex) when (attempt < MaxRetries && !token.IsCancellationRequested && !IsFatal(ex))
                    {
                        onRetry?.Invoke(attempt, ex);
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 2), token);
                    }
                }
                progress?.Report(Interlocked.Add(ref done, len));
            });

        ThrowIfChanged(); // covers a change after the last chunk was read

        using (var cts = WithTimeout(ct))
        using (var resp = await _http.PostAsJsonAsync($"api/upload/{id}/complete", new { baseMtime }, cts.Token))
        {
            EnsureOk(resp);
            return await resp.Content.ReadFromJsonAsync<RemoteEntry>(cts.Token)
                   ?? throw new InvalidDataException("bad complete response");
        }
    }

    private sealed record InitResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("chunkSize")] int ChunkSize);

    /// <summary>Request body that restarts the stall timer each time a block is handed to the connection.</summary>
    private sealed class StallResetContent(byte[] buf, CancellationTokenSource stall) : HttpContent
    {
        private const int Block = 64 << 10;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            for (int off = 0; off < buf.Length; off += Block)
            {
                await stream.WriteAsync(buf.AsMemory(off, Math.Min(Block, buf.Length - off)), ct);
                stall.CancelAfter(StallTimeout); // data is flowing: restart the stall timer
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = buf.Length;
            return true;
        }
    }
}

/// <summary>The server rejected the token (HTTP 401); retrying will not help.</summary>
public sealed class UnauthorizedException(string message) : InvalidOperationException(message);

/// <summary>The path is in a folder set read-only in the control panel (HTTP 403).</summary>
public sealed class ReadOnlyFolderException() : IOException("只读文件夹，不能修改");

/// <summary>The administrator has paused client connections (HTTP 503).</summary>
public sealed class ServerPausedException() : InvalidOperationException("服务器已暂停连接");
