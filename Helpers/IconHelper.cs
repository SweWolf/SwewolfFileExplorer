using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SwewolfFileExplorer.Helpers;

public static class IconHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON            = 0x000000100;
    private const uint SHGFI_SMALLICON       = 0x000000001;
    private const uint SHGFI_TYPENAME        = 0x000000400;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL    = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    public static ImageSource? GetIcon(string path, bool small = true)
    {
        var shfi = new SHFILEINFO();
        uint flags = SHGFI_ICON | (small ? SHGFI_SMALLICON : 0u);

        var result = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf(shfi), flags);
        if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                shfi.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(shfi.hIcon);
        }
    }

    // Icons and type names of things that are not on the disk (the content of an archive): found from the name alone,
    // and kept per extension, so a zip with thousands of files asks the shell a few times and not thousands.
    private static readonly Dictionary<string, ImageSource?> NameIcons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> NameTypes = new(StringComparer.OrdinalIgnoreCase);

    private static string NameKey(string name, bool isDirectory) =>
        isDirectory ? "<folder>" : Path.GetExtension(name);

    /// <summary>The icon for a file or folder with this name, without it being on the disk.</summary>
    public static ImageSource? GetIconForName(string name, bool isDirectory)
    {
        string key = NameKey(name, isDirectory);
        lock (NameIcons)
        {
            if (NameIcons.TryGetValue(key, out var known)) return known;
        }

        var shfi = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES;
        uint attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        string lookup = isDirectory ? "folder" : "file" + key;   // only the extension counts with USEFILEATTRIBUTES

        ImageSource? icon = null;
        var result = SHGetFileInfo(lookup, attrs, ref shfi, (uint)Marshal.SizeOf(shfi), flags);
        if (result != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
        {
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                icon = source;
            }
            finally
            {
                DestroyIcon(shfi.hIcon);
            }
        }

        lock (NameIcons) NameIcons[key] = icon;
        return icon;
    }

    /// <summary>"JPG File", "Text Document" ...: the type of a file with this name, without it being on the disk.</summary>
    public static string GetTypeNameForName(string name)
    {
        string key = Path.GetExtension(name);
        lock (NameTypes)
        {
            if (NameTypes.TryGetValue(key, out var known)) return known;
        }

        var shfi = new SHFILEINFO();
        SHGetFileInfo("file" + key, FILE_ATTRIBUTE_NORMAL, ref shfi, (uint)Marshal.SizeOf(shfi), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
        string type = shfi.szTypeName ?? "";

        lock (NameTypes) NameTypes[key] = type;
        return type;
    }

    public static string GetFileType(string path)
    {
        bool isDir = Directory.Exists(path);
        var shfi = new SHFILEINFO();
        uint attrs = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        uint flags = SHGFI_TYPENAME | (isDir ? 0u : SHGFI_USEFILEATTRIBUTES);

        SHGetFileInfo(path, attrs, ref shfi, (uint)Marshal.SizeOf(shfi), flags);
        return shfi.szTypeName;
    }
}
