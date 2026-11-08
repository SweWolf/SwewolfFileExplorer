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

    // ── Icon size follows the font size (list and tree each have their own) ───────────────────────────────

    /// <summary>The icon size, in device-independent pixels, of the file list and of the tree: 16 at the normal font, bigger with a bigger font.</summary>
    public static int ListIconSize { get; set; } = 16;
    public static int TreeIconSize { get; set; } = 16;

    /// <summary>The icon that goes with a font size in points: a little bigger than the text is high, never below 16.</summary>
    public static int IconSizeForFont(double fontPoints) => Math.Max(16, (int)Math.Round(fontPoints * 1.25));

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[]? icons, int[]? ids, uint count, uint flags);
    [DllImport("shell32.dll")] private static extern int SHGetImageList(int imageList, ref Guid riid, out IntPtr ppv);
    [DllImport("comctl32.dll")] private static extern bool ImageList_GetIconSize(IntPtr himl, out int cx, out int cy);

    private static readonly Guid IidImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    private static double DpiScale => Math.Max(1.0, GetDpiForSystem() / 96.0);

    /// <summary>The size in device pixels for an icon of this many device-independent pixels.</summary>
    private static int Pixels(int size) => Math.Max(16, (int)Math.Round(size * DpiScale));

    /// <summary>Is this bigger than the shell's small icon (16 px at 100 %)? Then it has to come from one of the bigger image lists.</summary>
    private static bool NeedsBigger(int size) => Pixels(size) > GetSystemMetrics(49 /* SM_CXSMICON */);

    /// <summary>
    /// The icon the shell shows for this path (or, with <paramref name="byAttributes"/>, for this kind of file: only the extension counts),
    /// taken from the smallest of the shell's image lists (32, 48, 256 px) that is big enough and scaled down to the wanted size,
    /// so a bigger font gets a sharp icon and not a stretched small one. Null if the shell gives none.
    /// </summary>
    private static ImageSource? ShellIconAtSize(string path, uint attributes, bool byAttributes, int size)
    {
        try
        {
            int px = Pixels(size);
            var shfi = new SHFILEINFO();
            uint flags = SHGFI_SYSICONINDEX | (byAttributes ? SHGFI_USEFILEATTRIBUTES : 0u);
            if (SHGetFileInfo(path, attributes, ref shfi, (uint)Marshal.SizeOf(shfi), flags) == IntPtr.Zero) return null;

            foreach (int list in new[] { 0 /* SHIL_LARGE */, 2 /* SHIL_EXTRALARGE */, 4 /* SHIL_JUMBO */ })
            {
                var iid = IidImageList;
                if (SHGetImageList(list, ref iid, out var himl) != 0 || himl == IntPtr.Zero) continue;
                try
                {
                    if (!ImageList_GetIconSize(himl, out int cx, out _)) continue;
                    if (cx < px && list != 4) continue;   // too small: the next list

                    var bgra = Bgra(ImageList_GetIcon(himl, shfi.iIcon, 1 /* ILD_TRANSPARENT */));
                    if (bgra == null) return null;
                    if (bgra.PixelWidth == px && bgra.PixelHeight == px) return bgra;

                    var scaled = new TransformedBitmap(bgra, new ScaleTransform((double)px / bgra.PixelWidth, (double)px / bgra.PixelHeight));
                    scaled.Freeze();
                    return scaled;
                }
                finally { Marshal.Release(himl); }
            }
        }
        catch { /* the small icon is used */ }
        return null;
    }

    /// <summary>The icons of the nodes of the tree, at the size that goes with the tree's font.</summary>
    public static ImageSource? TreeIcon(string path) => GetIcon(path, true, TreeIconSize);
    public static ImageSource? TreeIconForName(string name, bool isDirectory) => GetIconForName(name, isDirectory, TreeIconSize);

    /// <summary>The icon of a file or folder on the disk. <paramref name="size"/> is in device-independent pixels (16 = the usual small icon).</summary>
    public static ImageSource? GetIcon(string path, bool small = true, int size = 16)
    {
        // An Internet shortcut with the favicon of its page: read from the .ico itself, at any size. The shell remembers the icon of a path
        // from the first time anybody asked for it (found by a test: a .url rewritten with an icon kept its plain one), which a bookmark made
        // a moment ago may have got stuck with, and its bigger icons can come out empty.
        if (small && path.EndsWith(".url", StringComparison.OrdinalIgnoreCase) && TryGetUrlIcon(path, size) is { } favicon)
            return favicon;

        if (small && NeedsBigger(size) && ShellIconAtSize(path, 0, false, size) is { } big) return big;

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

    // ── Internet shortcuts (.url) with an icon file ───────────────────────────

    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const int  IDO_SHGIOI_LINK    = 0x0FFFFFFE;   // the shortcut arrow, as an overlay of the system image list

    [DllImport("shell32.dll", EntryPoint = "SHGetIconOverlayIndexW", CharSet = CharSet.Unicode)]
    private static extern int SHGetIconOverlayIndex(string? pszIconPath, int iIconIndex);

    [DllImport("comctl32.dll")]
    private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);

    private static System.Windows.Media.Imaging.BitmapSource? Bgra(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            return converted;
        }
        finally { DestroyIcon(hIcon); }
    }

    // The shortcut arrow of the shell, found once per size of image list: the image list draws an icon with and without the link overlay,
    // and what differs is the arrow. (size, the pixels of the icon with the arrow, which pixels are the arrow); null if Windows gives no arrow.
    // list = -1: the small list; 0, 2, 4: the shell's large, extra large and jumbo lists.
    private static readonly Dictionary<int, (int Size, byte[] Pixels, bool[] Mask)?> ArrowCache = new();

    private static (int Size, byte[] Pixels, bool[] Mask)? ArrowFor(int list)
    {
        lock (ArrowCache)
            if (ArrowCache.TryGetValue(list, out var known)) return known;
        var built = BuildArrow(list);
        lock (ArrowCache) ArrowCache[list] = built;
        return built;
    }

    private static (int Size, byte[] Pixels, bool[] Mask)? BuildArrow(int list)
    {
        IntPtr himl = IntPtr.Zero;
        bool release = false;
        try
        {
            var shfi = new SHFILEINFO();
            himl = SHGetFileInfo("x.url", FILE_ATTRIBUTE_NORMAL, ref shfi, (uint)Marshal.SizeOf(shfi),
                                 SHGFI_SYSICONINDEX | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
            if (list >= 0)
            {
                var iid = IidImageList;
                if (SHGetImageList(list, ref iid, out himl) != 0) return null;
                release = true;
            }
            int overlay = SHGetIconOverlayIndex(null, IDO_SHGIOI_LINK);
            if (himl == IntPtr.Zero || overlay <= 0) return null;

            var plain = Bgra(ImageList_GetIcon(himl, shfi.iIcon, 1 /* ILD_TRANSPARENT */));
            var arrow = Bgra(ImageList_GetIcon(himl, shfi.iIcon, 1u | ((uint)overlay << 8)));   // the overlay goes in the flags (INDEXTOOVERLAYMASK)
            if (plain == null || arrow == null || plain.PixelWidth != arrow.PixelWidth || plain.PixelHeight != arrow.PixelHeight) return null;

            int n = plain.PixelWidth;
            var a = new byte[n * n * 4]; var b = new byte[n * n * 4];
            plain.CopyPixels(a, n * 4, 0); arrow.CopyPixels(b, n * 4, 0);
            var mask = new bool[n * n];
            bool any = false;
            for (int i = 0; i < mask.Length; i++)
                any |= mask[i] = a[i * 4] != b[i * 4] || a[i * 4 + 1] != b[i * 4 + 1] || a[i * 4 + 2] != b[i * 4 + 2] || a[i * 4 + 3] != b[i * 4 + 3];
            return any ? (n, b, mask) : null;
        }
        catch { return null; }
        finally { if (release && himl != IntPtr.Zero) Marshal.Release(himl); }
    }

    /// <summary>
    /// The picture of an .ico file nearest <paramref name="size"/> (the smallest one that is big enough, else the biggest). WPF's icon
    /// decoder refuses some icons that Windows itself reads (a PNG inside an .ico, found with the favicon of dn.se), so then the picture
    /// is taken out of the file by hand: the directory of the .ico names where each picture starts, and a PNG picture is decoded alone.
    /// </summary>
    private static BitmapSource? LoadIconFrame(string iconFile, int size)
    {
        byte[] bytes;
        try
        {
            using var file = new FileStream(iconFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            bytes = new byte[file.Length];
            file.ReadExactly(bytes);
        }
        catch { return null; }
        return LoadIconFrame(bytes, size);
    }

    /// <summary>The same for the bytes of an .ico (a file already read, or an icon embedded in the program).</summary>
    private static BitmapSource? LoadIconFrame(byte[] bytes, int size)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames.Where(f => f.PixelWidth >= size).OrderBy(f => f.PixelWidth).FirstOrDefault()
                   ?? decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch { /* the decoder does not like it: by hand */ }

        try
        {
            if (bytes.Length < 22 || bytes[2] != 1 || bytes[3] != 0) return null;

            int count = bytes[4] | bytes[5] << 8;
            var entries = new List<(int Width, int Size, int Offset)>();
            for (int i = 0; i < count && 6 + 16 * (i + 1) <= bytes.Length; i++)
            {
                int at = 6 + 16 * i;
                entries.Add((bytes[at] == 0 ? 256 : bytes[at], BitConverter.ToInt32(bytes, at + 8), BitConverter.ToInt32(bytes, at + 12)));
            }

            foreach (var e in entries.Where(e => e.Width >= size).OrderBy(e => e.Width).Concat(entries.OrderByDescending(e => e.Width)))
            {
                if (e.Offset < 0 || e.Size < 8 || e.Offset + e.Size > bytes.Length) continue;
                if (bytes[e.Offset] != 0x89 || bytes[e.Offset + 1] != 0x50 || bytes[e.Offset + 2] != 0x4E || bytes[e.Offset + 3] != 0x47) continue;   // a PNG picture

                var png = new PngBitmapDecoder(new MemoryStream(bytes, e.Offset, e.Size), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return png.Frames[0];
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// The icon of an Internet shortcut that names an .ico file (<c>IconFile=</c>): the picture in it nearest the size of a list icon,
    /// with the shortcut arrow on it as Explorer draws it. Null for any other shortcut (it then gets the shell's icon).
    /// </summary>
    private static ImageSource? TryGetUrlIcon(string path, int iconSize)
    {
        try
        {
            string? iconFile = null;
            foreach (var line in File.ReadLines(path).Take(40))
                if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) { iconFile = line[9..].Trim(); break; }
            if (iconFile == null || !iconFile.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) || !File.Exists(iconFile)) return null;

            // The arrow comes from the smallest image list that is big enough for the wanted size; the favicon is drawn at that list's size
            // (so the arrow fits it) and the result is scaled down to the wanted size.
            int px = Pixels(iconSize);
            (int Size, byte[] Pixels, bool[] Mask)? overlay = null;
            if (px <= GetSystemMetrics(49 /* SM_CXSMICON */)) overlay = ArrowFor(-1);
            else
                foreach (int list in new[] { 0, 2, 4 })
                    if (ArrowFor(list) is { } cand && (cand.Size >= px || list == 4)) { overlay = cand; break; }
            int size = overlay?.Size ?? px;

            var frame = LoadIconFrame(iconFile, size);
            if (frame == null) return null;

            BitmapSource scaled = frame;
            if (frame.PixelWidth != size || frame.PixelHeight != size)
                scaled = new TransformedBitmap(frame, new ScaleTransform((double)size / frame.PixelWidth, (double)size / frame.PixelHeight));
            var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var pixels = new byte[w * h * 4];
            bgra.CopyPixels(pixels, w * 4, 0);

            if (overlay is { } o && o.Size == w && h == w)
                for (int i = 0; i < o.Mask.Length; i++)
                    if (o.Mask[i]) Array.Copy(o.Pixels, i * 4, pixels, i * 4, 4);

            BitmapSource result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            if (w != px)
                result = new TransformedBitmap(result, new ScaleTransform((double)px / w, (double)px / h));
            result.Freeze();
            return result;
        }
        catch { return null; }   // damaged or unreadable: the shell's icon is used
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
        if (FileTypeOverrides.HasIcon(ext)) return false;   // the user gave this type an icon: it is the icon of the type, not read from each file

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

    // ── The user's own icons (FileTypes.json) ─────────────────────────────────

    /// <summary>
    /// The icon FileTypes.json names for this extension, at the wanted icon size; null when there is none or it cannot be read
    /// (the normal icon is then used, and the reason goes to errors.log). "file,index" reads an icon out of a DLL or EXE, anything
    /// else is a picture file (.ico: the frame nearest the size; .png, .jpg ... scaled).
    /// </summary>
    private static ImageSource? LoadOverrideIcon(string extension, int iconSize)
    {
        var spec = FileTypeOverrides.GetIcon(extension);
        if (spec == null) return null;

        try
        {
            var (file, index) = spec.Value;
            bool builtIn = file.StartsWith(FileTypeOverrides.BuiltInPrefix, StringComparison.OrdinalIgnoreCase);
            if (!builtIn && !File.Exists(file)) throw new FileNotFoundException("The icon file was not found: " + file);

            int size = Pixels(iconSize);
            BitmapSource? frame;

            if (builtIn)
            {
                string name = file[FileTypeOverrides.BuiltInPrefix.Length..].Trim();
                if (name.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                using var res = typeof(IconHelper).Assembly.GetManifestResourceStream($"FileTypeIcons/{name}.ico")
                                ?? throw new FileNotFoundException($"There is no built-in icon named \"{name}\".");
                var bytes = new byte[res.Length];
                res.ReadExactly(bytes);
                frame = LoadIconFrame(bytes, size);
            }
            else if (index != null)
            {
                var icons = new IntPtr[1];
                if (PrivateExtractIcons(file, index.Value, size, size, icons, null, 1, 0) < 1 || icons[0] == IntPtr.Zero)   // at the size wanted, not a fixed one
                    throw new InvalidOperationException($"There is no icon number {index} in {file}");
                try
                {
                    frame = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    frame.Freeze();
                }
                finally { DestroyIcon(icons[0]); }
            }
            else if (file.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                frame = LoadIconFrame(file, size);
            }
            else
            {
                var bytes = File.ReadAllBytes(file);
                var decoder = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frame = decoder.Frames[0];
            }
            if (frame == null) throw new InvalidOperationException("The icon could not be read: " + file);

            BitmapSource scaled = frame;
            if (frame.PixelWidth != size || frame.PixelHeight != size)
                scaled = new TransformedBitmap(frame, new ScaleTransform((double)size / frame.PixelWidth, (double)size / frame.PixelHeight));
            var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            bgra.Freeze();
            return bgra;
        }
        catch (Exception ex)
        {
            if (FileTypeOverrides.IsWin11Icon(extension)) return null;   // the program's own default for Windows 11: no log line if this Windows differs
            try { LogFiles.AppendError($"The icon for .{extension.TrimStart('.')} in FileTypes.json could not be used, so the normal icon is shown: {ex.Message}"); } catch { }
            return null;
        }
    }

    // Icons and type names of things that are not on the disk (the content of an archive): found from the name alone,
    // and kept per extension, so a zip with thousands of files asks the shell a few times and not thousands.
    private static readonly Dictionary<string, ImageSource?> NameIcons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> NameTypes = new(StringComparer.OrdinalIgnoreCase);

    private static string NameKey(string name, bool isDirectory) =>
        isDirectory ? "<folder>" : Path.GetExtension(name);

    /// <summary>The icon of this kind of file if it has been looked up already (instant); false if it has not.</summary>
    public static bool TryGetCachedIconForName(string name, bool isDirectory, out ImageSource? icon, int size = 16)
    {
        lock (NameIcons) return NameIcons.TryGetValue(CacheKey(NameKey(name, isDirectory), size), out icon);
    }

    // The icons of the kinds are kept per size (the list and the tree can have different font sizes, and the font size can change).
    private static string CacheKey(string key, int size) => size == 16 ? key : key + "@" + size;

    private static readonly Dictionary<string, Task<ImageSource?>> NameIconTasks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The icon of this kind of file, looked up in the background. One lookup per kind (extension) however many files ask:
    /// the first lookup of a kind can take a while (the shell loads the program that owns it), and a folder of 500 files
    /// has a few dozen kinds.
    /// </summary>
    public static Task<ImageSource?> GetIconForNameAsync(string name, bool isDirectory, int size = 16)
    {
        string key = CacheKey(NameKey(name, isDirectory), size);
        lock (NameIconTasks)
        {
            if (!NameIconTasks.TryGetValue(key, out var task))
                NameIconTasks[key] = task = Task.Run(() => GetIconForName(name, isDirectory, size));
            return task;
        }
    }

    /// <summary>The large (32 px) icon of this kind of file, from the name alone (no disk access): for a dialog, where the small one would be blurry.</summary>
    public static ImageSource? GetLargeIconForName(string name)
    {
        var ext = Path.GetExtension(name);
        var shfi = new SHFILEINFO();
        var result = SHGetFileInfo("file" + ext, FILE_ATTRIBUTE_NORMAL, ref shfi, (uint)Marshal.SizeOf(shfi), SHGFI_ICON | SHGFI_USEFILEATTRIBUTES);
        if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally { DestroyIcon(shfi.hIcon); }
    }

    /// <summary>The icon for a file or folder with this name, without it being on the disk.</summary>
    public static ImageSource? GetIconForName(string name, bool isDirectory, int size = 16)
    {
        string key = NameKey(name, isDirectory);
        string cacheKey = CacheKey(key, size);
        lock (NameIcons)
        {
            if (NameIcons.TryGetValue(cacheKey, out var known)) return known;
        }

        if (!isDirectory && LoadOverrideIcon(key, size) is { } custom)   // the user's own icon from FileTypes.json
        {
            lock (NameIcons) NameIcons[cacheKey] = custom;
            return custom;
        }

        var shfi = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES;
        uint attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        string lookup = isDirectory ? "folder" : "file" + key;   // only the extension counts with USEFILEATTRIBUTES

        if (NeedsBigger(size) && ShellIconAtSize(lookup, attrs, true, size) is { } big)   // a bigger font: a sharp bigger icon
        {
            lock (NameIcons) NameIcons[cacheKey] = big;
            return big;
        }

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

        lock (NameIcons) NameIcons[cacheKey] = icon;
        return icon;
    }

    /// <summary>The file type definitions changed (the setting for the built-in list, or the user's file): the names and icons found so far are forgotten.</summary>
    public static void ResetFileTypeCaches()
    {
        lock (NameTypes) NameTypes.Clear();
        lock (NameIcons) NameIcons.Clear();
        lock (NameIconTasks) NameIconTasks.Clear();
        lock (OwnIcons) OwnIcons.Clear();
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

        string? type = FileTypeOverrides.GetDescription(key);   // the user's own FileTypes.json, if it names this extension
        if (type == null && _useRegisteredTypeNames) type = RegisteredTypeName(key);
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
