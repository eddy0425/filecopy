using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FileCopy.Client.Local;
using FileCopy.Client.Transfers;

namespace FileCopy.Client.Tests;

/// <summary>Per-run temp area; also redirects Settings.DataDir so state files stay out of %AppData%.</summary>
internal static class TestEnv
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "FileCopyTests", Guid.NewGuid().ToString("N")[..8]);

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Root);
        Settings.DataDir = Path.Combine(Root, "appdata");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteDir(Root);
    }

    public static string NewDir(string name)
    {
        var dir = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
    }
}

/// <summary>Builds the Go server once per test run.</summary>
internal static class ServerBinary
{
    private static readonly Lazy<string> Exe = new(Build);

    public static string Path => Exe.Value;

    private static string Build()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "server", "go.mod"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("server/go.mod not found above " + AppContext.BaseDirectory);

        var exe = System.IO.Path.Combine(TestEnv.Root, "filecopy-server.exe");
        var psi = new ProcessStartInfo("go", ["build", "-o", exe, "."])
        {
            WorkingDirectory = System.IO.Path.Combine(dir.FullName, "server"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("go build failed: " + err.Result);
        return exe;
    }
}

/// <summary>A running FileCopy server on a free port with its own data directory.</summary>
public sealed class TestServer : IDisposable
{
    public const string Token = "test-token-0123456789";
    public const string EquipmentId = "TEST-PC";

    private readonly Process _process;
    private readonly string _dir;

    public string Url { get; }
    public string DataDir { get; }

    /// <param name="state">Written as the server's .filecopy/state.json before it starts (read-only folders, TOTP secret, ...).</param>
    public TestServer(object? state = null)
    {
        _dir = TestEnv.NewDir("server");
        DataDir = System.IO.Path.Combine(_dir, "data");
        if (state != null)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(DataDir, ".filecopy"));
            File.WriteAllText(System.IO.Path.Combine(DataDir, ".filecopy", "state.json"), JsonSerializer.Serialize(state));
        }
        int port = FreePort();
        Url = $"http://127.0.0.1:{port}";
        var config = System.IO.Path.Combine(_dir, "config.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new { listen = $"127.0.0.1:{port}", dataDir = DataDir, token = Token }));

        var psi = new ProcessStartInfo(ServerBinary.Path, ["-config", config])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        _process = Process.Start(psi)!;
        _process.ErrorDataReceived += (_, _) => { }; // drain request log
        _process.OutputDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();
        WaitForPort(port);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private void WaitForPort(int port)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (_process.HasExited) throw new InvalidOperationException("server exited with " + _process.ExitCode);
            try
            {
                using var c = new TcpClient();
                c.Connect(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException) { Thread.Sleep(50); }
        }
        throw new TimeoutException("server did not start");
    }

    public string PathOf(string rel) => System.IO.Path.Combine(DataDir, rel.Replace('/', '\\'));

    /// <summary>Writes a file directly into the server's store (as if another client uploaded it).</summary>
    public long Put(string rel, string content, DateTime? mtimeUtc = null)
    {
        var p = PathOf(rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        // Whole milliseconds, like the server stores them.
        var t = mtimeUtc ?? DateTime.UtcNow.AddMinutes(-10);
        t = DateTimeOffset.FromUnixTimeMilliseconds(new DateTimeOffset(t).ToUnixTimeMilliseconds()).UtcDateTime;
        File.SetLastWriteTimeUtc(p, t);
        return new DateTimeOffset(t).ToUnixTimeMilliseconds();
    }

    public string? Read(string rel) => File.Exists(PathOf(rel)) ? File.ReadAllText(PathOf(rel)) : null;

    public bool Exists(string rel) => File.Exists(PathOf(rel)) || Directory.Exists(PathOf(rel));

    /// <summary>Server files (not folders) whose name starts with <paramref name="prefix"/>, as relative paths.</summary>
    public List<string> FilesLike(string folder, string prefix)
    {
        var dir = PathOf(folder);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, prefix + "*").Select(f => System.IO.Path.GetFileName(f)).ToList();
    }

    public void Dispose()
    {
        try { _process.Kill(entireProcessTree: true); _process.WaitForExit(5000); } catch (Exception) { }
        _process.Dispose();
        TestEnv.TryDeleteDir(_dir);
    }
}

/// <summary>A connected client: SyncService over its own mirror folder.</summary>
public sealed class TestClient : IDisposable
{
    public ConcurrentQueue<string> Logs { get; } = new();
    public SyncService Sync { get; }
    public string Root { get; }

    public TestClient(TestServer server, string? root = null)
    {
        Root = root ?? TestEnv.NewDir("mirror");
        Sync = new SyncService(server.Url, TestServer.Token, TestServer.EquipmentId, Root, new TransferQueue(), Logs.Enqueue);
    }

    public string LogText => string.Join(Environment.NewLine, Logs);

    public string Local(string rel) => Sync.LocalPath(rel);

    public void Write(string rel, string content)
    {
        var p = Local(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        // Make sure the mtime differs from anything recorded earlier, even on coarse clocks.
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000)));
    }

    public string? Read(string rel) => File.Exists(Local(rel)) ? File.ReadAllText(Local(rel)) : null;

    public void Dispose()
    {
        Sync.Dispose();
        TestEnv.TryDeleteDir(Root);
    }
}

internal static class TaskTimeout
{
    /// <summary>Fails a test instead of hanging it when a task never completes.</summary>
    public static Task T(this Task t, int seconds = 30) => t.WaitAsync(TimeSpan.FromSeconds(seconds));
    public static Task<TR> T<TR>(this Task<TR> t, int seconds = 30) => t.WaitAsync(TimeSpan.FromSeconds(seconds));
}
