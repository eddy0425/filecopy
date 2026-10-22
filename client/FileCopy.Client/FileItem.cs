using System.ComponentModel;
using System.Windows.Media;
using FileCopy.Client.Local;

namespace FileCopy.Client;

/// <summary>One row in the file list, presented like an Explorer row with OneDrive status.</summary>
public sealed class FileItem : INotifyPropertyChanged
{
    // Segoe MDL2 Assets glyphs, as used by OneDrive's status column.
    private const string GlyphCloud = "\uE753";
    private const string GlyphSync = "\uE895";
    private const string GlyphCompletedSolid = "\uEC61";
    private const string GlyphCompleted = "\uE930";

    private static readonly Brush Blue = Frozen("#0078D4");
    private static readonly Brush Green = Frozen("#2DA44E");

    private FileStatus _status;
    private bool _pinned;
    private long _size;
    private long _mtime;

    public FileItem(string rel, bool isDir)
    {
        Rel = rel;
        IsDir = isDir;
        (Icon, TypeName) = ShellIcons.Get(Name, isDir);
    }

    public string Rel { get; }
    public bool IsDir { get; }
    public string Name => Rel[(Rel.LastIndexOf('/') + 1)..];
    public ImageSource? Icon { get; }
    public string TypeName { get; }

    public FileStatus Status => _status;
    public bool Pinned => _pinned;
    public long Size => _size;
    public long Mtime => _mtime;

    /// <summary>True when the item has (or, for folders, is set to have) a local copy.</summary>
    public bool IsLocal => _status != FileStatus.CloudOnly;

    private bool Syncing => _status is FileStatus.Transferring or FileStatus.PendingUpload
        or FileStatus.PendingDownload or FileStatus.LocalOnly;

    public string StatusGlyph =>
        Syncing ? GlyphSync :
        _status == FileStatus.CloudOnly ? GlyphCloud :
        _pinned ? GlyphCompletedSolid : GlyphCompleted;

    public Brush StatusBrush => Syncing || _status == FileStatus.CloudOnly ? Blue : Green;

    public string StatusText => _status switch
    {
        FileStatus.CloudOnly => "联机时可用",
        FileStatus.Kept when _pinned => "始终在此设备上可用",
        FileStatus.Kept => "在此设备上可用",
        FileStatus.PendingUpload or FileStatus.LocalOnly => "等待上传",
        FileStatus.PendingDownload => "等待下载",
        FileStatus.Transferring => "正在同步",
        _ => "",
    };

    // Explorer style: whole KB rounded up, with thousands separators; blank for folders.
    public string SizeText => IsDir ? "" : $"{(_size + 1023) / 1024:N0} KB";
    public string MtimeText => _mtime == 0 ? "" : DateTimeOffset.FromUnixTimeMilliseconds(_mtime).LocalDateTime.ToString("yyyy'/'MM'/'dd HH:mm");

    public void Update(FileStatus status, bool pinned, long size, long mtime)
    {
        if (status == _status && pinned == _pinned && size == _size && mtime == _mtime) return;
        _status = status;
        _pinned = pinned;
        _size = size;
        _mtime = mtime;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); // refresh all bindings
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static Brush Frozen(string color)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(color)!;
        b.Freeze();
        return b;
    }
}
