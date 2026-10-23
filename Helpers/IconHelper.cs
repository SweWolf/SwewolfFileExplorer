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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetStockIconInfo(int siid, uint uFlags, ref SHSTOCKICONINFO psii);

    private const int SIID_RECYCLER = 31, SIID_RECYCLERFULL = 32;
    public const int SIID_HELP = 23, SIID_WARNING = 78, SIID_INFO = 79, SIID_ERROR = 80;

    /// <summary>The Recycle Bin's own icon, empty or full as Windows draws it (for the window of a delete); null if Windows has none.</summary>
    public static ImageSource? GetRecycleBinIcon(bool full) => GetStockIcon(full ? SIID_RECYCLERFULL : SIID_RECYCLER);

    /// <summary>One of Windows' stock icons (SIID_*: the Recycle Bin, the icons of a message box ...), 32 px; null if Windows has none.</summary>
    public static ImageSource? GetStockIcon(int siid)
    {
        var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
        if (SHGetStockIconInfo(siid, 0x100 /* SHGSI_ICON */, ref info) != 0 || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally { DestroyIcon(info.hIcon); }
    }

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

    // Does a file of this kind have an icon of its own, inside the file (an exe, an icon, a shortcut)? Then the icon has to be
    // read from the file. All other files show the icon of their type, which does not touch the disk at all: Explorer does
    // the same. (Reading 618 icons from a folder on a cloud drive, all at once, held up everything else on that drive for
    // seconds.) Decided from the registry once per extension: the icon of the type is "%1" (the file itself).
    private static readonly Dictionary<string, bool> OwnIcons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AlwaysOwnIcon = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".ico", ".cur", ".ani", ".lnk", ".url", ".scr", ".icl", ".cpl", ".msc" };

    public static bool HasOwnIcon(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        if (ext.Length == 0) return false;
        if (AlwaysOwnIcon.Contains(ext)) return true;

        lock (OwnIcons)
        {
            if (OwnIcons.TryGetValue(ext, out var known)) return known;
        }

        bool own = false;
        try
        {
            using var extKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(ext);
            if (extKey?.GetValue(null) is string progId && progId.Length > 0)
            {
                using var iconKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(progId + @"\DefaultIcon");
                var value = (iconKey?.GetValue(null) as string)?.Trim().Trim('"');
                own = value != null && (value == "%1" || value == "%L");
            }
        }
        catch { /* unknown: the type's icon is used */ }

        lock (OwnIcons) OwnIcons[ext] = own;
        return own;
    }

    // Icons and type names of things that are not on the disk (the content of an archive): found from the name alone,
    // and kept per extension, so a zip with thousands of files asks the shell a few times and not thousands.
    private static readonly Dictionary<string, ImageSource?> NameIcons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> NameTypes = new(StringComparer.OrdinalIgnoreCase);

    private static string NameKey(string name, bool isDirectory) =>
        isDirectory ? "<folder>" : Path.GetExtension(name);

    /// <summary>The icon of this kind of file if it has been looked up already (instant); false if it has not.</summary>
    public static bool TryGetCachedIconForName(string name, bool isDirectory, out ImageSource? icon)
    {
        lock (NameIcons) return NameIcons.TryGetValue(NameKey(name, isDirectory), out icon);
    }

    private static readonly Dictionary<string, Task<ImageSource?>> NameIconTasks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The icon of this kind of file, looked up in the background. One lookup per kind (extension) however many files ask:
    /// the first lookup of a kind can take a while (the shell loads the program that owns it), and a folder of 500 files
    /// has a few dozen kinds.
    /// </summary>
    public static Task<ImageSource?> GetIconForNameAsync(string name, bool isDirectory)
    {
        string key = NameKey(name, isDirectory);
        lock (NameIconTasks)
        {
            if (!NameIconTasks.TryGetValue(key, out var task))
                NameIconTasks[key] = task = Task.Run(() => GetIconForName(name, isDirectory));
            return task;
        }
    }

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

    private static bool _useRegisteredTypeNames;

    /// <summary>
    /// Settings > "Show the Registered Name of the File Type": the Type column says "JPEG Image" (what the extension's own class is called)
    /// instead of what Windows shows for the user's default app, which can be the plain "JPG File". Changing it forgets the names found.
    /// </summary>
    public static bool UseRegisteredTypeNames
    {
        get => _useRegisteredTypeNames;
        set
        {
            if (_useRegisteredTypeNames == value) return;
            _useRegisteredTypeNames = value;
            lock (NameTypes) NameTypes.Clear();
        }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, System.Text.StringBuilder output, uint length, IntPtr reserved);

    /// <summary>
    /// The name the extension's own class gives the type: HKEY_CLASSES_ROOT\.ext names the class (jpegfile), whose FriendlyTypeName or
    /// default value is the text ("JPEG Image"). Null if the extension has no class or the class has no name.
    /// </summary>
    private static string? RegisteredTypeName(string extension)
    {
        try
        {
            if (extension.Length < 2) return null;
            using var ext = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(extension);
            if (ext?.GetValue("") is not string progId || progId.Length == 0) return null;

            using var cls = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(progId);
            if (cls == null) return null;

            if (cls.GetValue("FriendlyTypeName") is string friendly && friendly.Length > 0)
            {
                if (!friendly.StartsWith('@')) return friendly;
                var text = new System.Text.StringBuilder(260);   // "@C:\Windows\System32\shell32.dll,-30596": a string in a resource
                if (SHLoadIndirectString(friendly, text, (uint)text.Capacity, IntPtr.Zero) == 0 && text.Length > 0) return text.ToString();
            }
            return cls.GetValue("") is string plain && plain.Length > 0 ? plain : null;
        }
        catch { return null; }
    }

    /// <summary>"JPG File", "Text Document" ...: the type of a file with this name, without it being on the disk.</summary>
    public static string GetTypeNameForName(string name)
    {
        string key = Path.GetExtension(name);
        lock (NameTypes)
        {
            if (NameTypes.TryGetValue(key, out var known)) return known;
        }

        string? type = _useRegisteredTypeNames ? RegisteredTypeName(key) : null;
        if (type == null)   // the usual way (and the way when the extension has no class of its own)
        {
            var shfi = new SHFILEINFO();
            SHGetFileInfo("file" + key, FILE_ATTRIBUTE_NORMAL, ref shfi, (uint)Marshal.SizeOf(shfi), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
            type = shfi.szTypeName ?? "";
        }

        lock (NameTypes) NameTypes[key] = type;
        return type;
    }

    /// <summary>
    /// The type of a file or folder on the disk. It asks the disk whether the path is a folder, which is slow for every file
    /// of a big folder on a cloud drive: for files whose name is known use <see cref="GetTypeNameForName"/> (the list does).
    /// </summary>
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
