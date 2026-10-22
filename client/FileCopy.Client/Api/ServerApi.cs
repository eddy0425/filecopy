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
    [property: JsonPropertyName("mtime")] long Mtime);

/// <summary>
/// HTTP client for the FileCopy server. Large transfers are split into chunks
/// sent over several parallel connections, which matters a lot on high-latency
/// overseas links where a single TCP stream cannot fill the pipe.
/// </summary>
public sealed class ServerApi : IDisposable
{
    public const int ChunkSize = 4 << 20;
    public const int Parallelism = 4;
    private const int MaxRetries = 4;

    private readonly HttpClient _http;

    public ServerApi(string baseUrl, string token)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 32,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.None,
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan, // per-request timeouts are applied below
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public void Dispose() => _http.Dispose();

    private static string Q(string path) => Uri.EscapeDataString(path);

    private static CancellationTokenSource WithTimeout(CancellationToken ct, int seconds = 60)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    public async Task PingAsync(CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct, 15);
        using var resp = await _http.GetAsync("api/ping", cts.Token);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Token 无效 (401)");
        resp.EnsureSuccessStatusCode();
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
            resp.EnsureSuccessStatusCode();
    }

    public async Task MoveAsync(string from, string to, CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct);
        using var resp = await _http.PostAsJsonAsync("api/move", new { from, to }, cts.Token);
        resp.EnsureSuccessStatusCode();
    }

    public async Task MkdirAsync(string path, CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct);
        using var resp = await _http.PostAsJsonAsync("api/mkdir", new { path }, cts.Token);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Downloads [offset, offset+length) of a file into a new buffer, with retries.</summary>
    public async Task<byte[]> DownloadRangeAsync(string path, long offset, int length, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var cts = WithTimeout(ct, 120);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"api/file?path={Q(path)}");
                req.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                resp.EnsureSuccessStatusCode();
                var buf = new byte[length];
                await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
                await s.ReadExactlyAsync(buf, cts.Token);
                return buf;
            }
            catch (Exception) when (attempt < MaxRetries && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }
    }

    /// <summary>
    /// Downloads a byte range as ordered chunks fetched over parallel connections.
    /// <paramref name="onChunk"/> is invoked sequentially, in file order.
    /// </summary>
    public async Task DownloadChunkedAsync(string path, long offset, long length,
        Func<long, byte[], Task> onChunk, CancellationToken ct)
    {
        var pending = new Queue<(long Offset, Task<byte[]> Data)>();
        long next = offset, end = offset + length;

        void StartNext()
        {
            int len = (int)Math.Min(ChunkSize, end - next);
            pending.Enqueue((next, DownloadRangeAsync(path, next, len, ct)));
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
            // Observe abandoned tasks so failures don't surface as unobserved exceptions.
            foreach (var (_, t) in pending) _ = t.ContinueWith(x => _ = x.Exception, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Downloads a whole file to <paramref name="localPath"/> via a ".fcpart" temp file,
    /// then renames it into place and applies the server mtime.
    /// </summary>
    public async Task DownloadToFileAsync(RemoteEntry entry, string localPath, IProgress<long>? progress, CancellationToken ct)
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
                }, ct);
            }
            File.SetLastWriteTimeUtc(part, DateTimeOffset.FromUnixTimeMilliseconds(entry.Mtime).UtcDateTime);
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
        IProgress<long>? progress, CancellationToken ct)
    {
        var info = new FileInfo(localPath);
        long size = info.Length;
        long mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();

        string id;
        int chunkSize;
        using (var cts = WithTimeout(ct))
        using (var resp = await _http.PostAsJsonAsync("api/upload/init", new { path = remotePath, size, mtime }, cts.Token))
        {
            resp.EnsureSuccessStatusCode();
            var init = await resp.Content.ReadFromJsonAsync<InitResponse>(cts.Token)
                       ?? throw new InvalidDataException("bad init response");
            id = init.Id;
            chunkSize = init.ChunkSize > 0 ? init.ChunkSize : ChunkSize;
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
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        using var cts = WithTimeout(token, 120);
                        using var content = new ByteArrayContent(buf);
                        using var resp = await _http.PutAsync($"api/upload/{id}?offset={off}", content, cts.Token);
                        resp.EnsureSuccessStatusCode();
                        break;
                    }
                    catch (Exception) when (attempt < MaxRetries && !token.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 2), token);
                    }
                }
                progress?.Report(Interlocked.Add(ref done, len));
            });

        using (var cts = WithTimeout(ct))
        using (var resp = await _http.PostAsJsonAsync($"api/upload/{id}/complete", new { baseMtime }, cts.Token))
        {
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<RemoteEntry>(cts.Token)
                   ?? throw new InvalidDataException("bad complete response");
        }
    }

    private sealed record InitResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("chunkSize")] int ChunkSize);
}
