using System.IO;
using System.Text;
using System.Threading.Channels;

namespace FileCopy.Client;

/// <summary>
/// Appends log lines to a file from a background task through one open writer, so logging never waits
/// on the disk. Past <c>maxBytes</c> the file moves to "log.1.txt" (replacing the previous one) and a new one starts.
/// </summary>
public sealed class LogWriter : IDisposable
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly Task _loop;

    public LogWriter(string path, long maxBytes = 5 << 20)
    {
        _path = path;
        _maxBytes = maxBytes;
        _loop = Task.Run(WriteLoopAsync);
    }

    /// <summary>Queues a line; dropped after <see cref="Dispose"/>.</summary>
    public void Write(string line) => _lines.Writer.TryWrite(line);

    /// <summary>Writes what is queued and closes the file.</summary>
    public void Dispose()
    {
        _lines.Writer.TryComplete();
        _loop.Wait(TimeSpan.FromSeconds(3));
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        try
        {
            while (await _lines.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                try
                {
                    writer ??= Open();
                    while (_lines.Reader.TryRead(out var line)) writer.WriteLine(line);
                    writer.Flush();
                    if (writer.BaseStream.Length > _maxBytes)
                    {
                        writer.Dispose();
                        writer = null;
                        File.Move(_path, Path.ChangeExtension(_path, ".1.txt"), overwrite: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Like before: a log line that cannot be written is dropped. Reopen next time.
                    while (_lines.Reader.TryRead(out _)) { }
                    writer?.Dispose();
                    writer = null;
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private StreamWriter Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, new UTF8Encoding(false));
    }
}
