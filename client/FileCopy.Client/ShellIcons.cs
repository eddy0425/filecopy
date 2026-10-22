using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileCopy.Client;

/// <summary>
/// Explorer's small icon and type name ("7Z 文件") for a file name, looked up by
/// extension only (the file does not need to exist). Cached per extension. UI thread only.
/// </summary>
internal static class ShellIcons
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_TYPENAME = 0x400;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint fileAttributes, ref SHFILEINFO info, uint cbFileInfo, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    private static readonly Dictionary<string, (ImageSource? Icon, string TypeName)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static (ImageSource? Icon, string TypeName) Get(string name, bool isDir)
    {
        var key = isDir ? "<dir>" : Path.GetExtension(name);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var info = new SHFILEINFO();
        var probe = isDir ? "folder" : "file" + key;
        SHGetFileInfo(probe, isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);

        ImageSource? icon = null;
        if (info.hIcon != 0)
        {
            icon = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            icon.Freeze();
            DestroyIcon(info.hIcon);
        }
        var result = (icon, string.IsNullOrEmpty(info.szTypeName) ? (isDir ? "文件夹" : "文件") : info.szTypeName);
        Cache[key] = result;
        return result;
    }
}
