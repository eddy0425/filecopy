using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;

namespace FileCopy.Client.Transfers;

public enum TransferDirection { Upload, Download }

/// <summary>One row in the transfer list. Updated from worker threads, marshalled to the UI.</summary>
public sealed class TransferItem : INotifyPropertyChanged
{
    private const string Running = "进行中";
    // Uploads run chunks in parallel, so progress from healthy chunks would otherwise hide a retry at once.
    private const double MinRetryDisplayMs = 3000;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _statusLock = new(); // Status is set from worker threads and the UI thread
    private long _done;
    private string _status = Running;
    private double _lastUiUpdate;
    private volatile bool _retrying;
    private double _retryAt; // _clock time of the last Retrying call; guarded by _statusLock
    private volatile bool _finished; // completed or failed: late progress/retry reports must not change Status

    public TransferItem(string path, TransferDirection direction, long size)
    {
        Path = path;
        Direction = direction;
        Size = size;
    }

    public string Path { get; }
    public TransferDirection Direction { get; }
    public string DirectionText => Direction == TransferDirection.Upload ? "↑ 上传" : "↓ 下载";
    public long Size { get; }
    public string SizeText => FormatBytes(Size);
    public double Percent => Size == 0 ? 100 : _done * 100.0 / Size;
    public string SpeedText
    {
        get
        {
            var secs = _clock.Elapsed.TotalSeconds;
            return secs < 0.2 ? "" : FormatBytes((long)(_done / secs)) + "/s";
        }
    }
    public string Status { get => _status; private set { _status = value; OnChanged(); } }

    public void Report(long done)
    {
        _done = done;
        if (_retrying)
        {
            lock (_statusLock)
            {
                if (_retrying && !_finished && _clock.Elapsed.TotalMilliseconds - _retryAt >= MinRetryDisplayMs)
                {
                    _retrying = false;
                    Status = Running;
                }
            }
        }
        // Throttle UI updates to ~5/s per item.
        var now = _clock.Elapsed.TotalMilliseconds;
        if (now - _lastUiUpdate < 200 && done < Size) return;
        _lastUiUpdate = now;
        OnChanged(nameof(Percent));
        OnChanged(nameof(SpeedText));
    }

    public void Complete()
    {
        _done = Size;
        _clock.Stop();
        OnChanged(nameof(Percent));
        OnChanged(nameof(SpeedText));
        lock (_statusLock)
        {
            _finished = true;
            Status = "完成";
        }
    }

    /// <summary>Shows that a part of the transfer failed and is about to be retried.</summary>
    public void Retrying(int attempt, int maxAttempts, string reason)
    {
        lock (_statusLock)
        {
            if (_finished) return;
            _retryAt = _clock.Elapsed.TotalMilliseconds;
            _retrying = true;
            Status = $"重试中 ({attempt + 1}/{maxAttempts}): {reason}";
        }
    }

    public void Fail(string message)
    {
        _clock.Stop();
        lock (_statusLock)
        {
            _finished = true;
            Status = "失败: " + message;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int i = 0;
        // Compare the value as displayed (2 decimals), so 1023.999 KB shows as "1 MB", not "1024 KB".
        while (Math.Round(v, 2) >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {units[i]}";
    }
}

/// <summary>Observable list of recent transfers for the UI.</summary>
public sealed class TransferQueue
{
    private const int MaxItems = 300;

    public ObservableCollection<TransferItem> Items { get; } = [];

    public TransferItem Start(string path, TransferDirection direction, long size)
    {
        var item = new TransferItem(path, direction, size);
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Items.Insert(0, item);
            while (Items.Count > MaxItems) Items.RemoveAt(Items.Count - 1);
        });
        return item;
    }
}
