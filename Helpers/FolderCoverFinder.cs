using System.IO;
using System.Windows.Media.Imaging;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The picture that stands for a folder, shown under the folder tree.
/// <see cref="Path"/> is the picture file (or the picture inside an archive); it is null for a cover
/// that is embedded in a music file, where <see cref="Data"/> holds the bytes of the picture instead.
/// </summary>
public sealed record FolderCover(BitmapSource Image, string? Path, byte[]? Data);

/// <summary>
/// Finds the picture for a folder, in this order (a starting point, meant to be tweaked):
///  0. The picture the user chose for the folder (right-click the preview: Set as Folder Picture), kept in the library by name.
///  1. A picture in the folder with a cover-like name, best first: Folder, Cover, Front, Poster, AlbumArt (also
///     Windows Media Player's AlbumArt_{...}_Large), Album, Thumb, Thumbnail, or a name equal to the folder's.
///     (TeraTool only looked for Folder.jpg.) Hidden files count: Folder.jpg is usually hidden.
///  1b. The folder's own icon from its desktop.ini (IconResource), for a folder Windows gave an icon of its own (Downloads ...).
///  2. The cover embedded in the first music files of the folder (up to <see cref="MaxMusicFilesTried"/> of them).
///  3. In an archive (zip, cbz, rar ...): the same names (stepping into a single folder inside it, up to 3 levels);
///     for a comic book (.cbz / .cbr) the first picture, which is page 1.
/// There is no "first picture of the folder" fallback on purpose: in a folder of photos it would be a random one.
/// Nothing is found for most folders, and that is normal. Safe to call from a background thread.
///
/// Modeled on TeraTool's ShowFolderPicture and GetCoverPictureFromZipFile (read-only reference).
/// A cover the user chooses for a folder is a later step (it belongs in the per-folder database).
/// </summary>
public static class FolderCoverFinder
{
    /// <summary>Base names of a cover picture, best first.</summary>
    private static readonly string[] CoverNames = { "folder", "cover", "front", "poster", "albumart", "album", "thumb", "thumbnail" };

    private const int MaxMusicFilesTried = 5;
    private const int MaxArchiveDepth = 3;   // folders to step into inside an archive

    public static FolderCover? Find(string folder, int decodeWidth, CancellationToken token)
    {
        try
        {
            return ArchiveSupport.IsInside(folder)
                ? FromArchive(folder, decodeWidth)
                : FromFolder(folder, decodeWidth, token);
        }
        catch
        {
            return null;   // a folder that is gone, no access, a damaged archive: no picture
        }
    }

    private static FolderCover? FromFolder(string folder, int decodeWidth, CancellationToken token)
    {
        if (!Directory.Exists(folder)) return null;

        // 0. The picture the user chose for this folder (Set as Folder Picture; the library keeps its name only). If it is gone
        //    or cannot be read, the automatic search below goes on as if nothing was chosen.
        var choice = LibraryStore.GetFolderPicture(folder);
        if (choice == "") return null;   // "No Folder Picture": the user wants none
        if (choice is { Length: > 0 } chosen)
        {
            var file = Path.Combine(folder, chosen);   // a name in the folder, or a full path (Combine keeps a rooted one)
            if (File.Exists(file) && ThumbnailLoader.Load(file, decodeWidth) is { } picked)
                return new FolderCover(picked, file, null);
        }

        var files = Directory.EnumerateFiles(folder).ToList();   // hidden files included
        var name  = Path.GetFileName(folder.TrimEnd('\\', '/'));

        // 1. A picture with a cover-like name. If the best one cannot be read, the next one is tried.
        var pictures = files.Where(ThumbnailLoader.IsImage).ToList();

        // Windows Media Player leaves AlbumArt_{GUID}_Large.jpg for every album it has seen. With more than one in a folder (a music
        // library kept in one folder) none of them is the picture of the folder itself, so they are not used.
        var wmpArt = pictures.Where(IsWmpAlbumArt).ToList();
        if (wmpArt.Select(WmpAlbumGuid).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            pictures = pictures.Where(f => !Path.GetFileNameWithoutExtension(f).StartsWith("albumart", StringComparison.OrdinalIgnoreCase)).ToList();   // AlbumArtSmall.jpg too

        foreach (var picture in Ranked(pictures, name, f => SafeLength(f)))
        {
            token.ThrowIfCancellationRequested();
            var image = ThumbnailLoader.Load(picture, decodeWidth);
            if (image != null) return new FolderCover(image, picture, null);
        }

        // 1b. The folder's own icon from its desktop.ini (what Explorer shows for it, e.g. the green arrow of Downloads): after a picture
        //     with a cover-like name, before a cover embedded in a music file.
        if (FromDesktopIni(folder, files) is { } icon) return icon;

        // 2. The cover embedded in a music file.
        // (The tracks of an audio CD count: their cover is the album's front picture, from MusicBrainz, kept on this computer.)
        var music = files.Where(f => AudioTagReader.HasTags(f) || AudioTagReader.IsCdTrackName(f))
            .OrderBy(f => System.IO.Path.GetFileName(f), Comparer<string>.Create(FileItemComparer.NaturalCompare))
            .Take(MaxMusicFilesTried);
        foreach (var song in music)
        {
            token.ThrowIfCancellationRequested();
            var data = AudioTagReader.ReadCover(song);
            if (data == null) continue;

            var image = ThumbnailLoader.LoadBytes(data, decodeWidth);
            if (image != null) return new FolderCover(image, null, data);
        }

        return null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    /// <summary>
    /// The icon a folder's <c>desktop.ini</c> names: <c>IconResource=path,index</c> (or <c>IconFile=</c> and <c>IconIndex=</c>) in
    /// [.ShellClassInfo]; a negative index is a resource id, as in <c>imageres.dll,-184</c> (the Downloads icon). Drawn at 256 pixels.
    /// Null when the folder has no desktop.ini, it names no icon, or the icon cannot be read.
    /// </summary>
    private static FolderCover? FromDesktopIni(string folder, List<string> files)
    {
        try
        {
            var ini = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), "desktop.ini", StringComparison.OrdinalIgnoreCase));
            if (ini == null || new FileInfo(ini).Length is 0 or > 256 * 1024) return null;

            string? resource = null, iconFile = null, iconIndex = null;
            bool inSection = false;
            foreach (var raw in TextFiles.ReadAllText(ini).Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inSection = line.Equals("[.ShellClassInfo]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim().Trim('"');
                if (key.Equals("IconResource", StringComparison.OrdinalIgnoreCase)) resource = val;
                else if (key.Equals("IconFile", StringComparison.OrdinalIgnoreCase)) iconFile = val;
                else if (key.Equals("IconIndex", StringComparison.OrdinalIgnoreCase)) iconIndex = val;
            }

            string? path; int index = 0;
            if (!string.IsNullOrEmpty(resource))
            {
                var comma = resource.LastIndexOf(',');
                path = comma > 0 ? resource[..comma] : resource;
                if (comma > 0) int.TryParse(resource[(comma + 1)..].Trim(), out index);
            }
            else if (!string.IsNullOrEmpty(iconFile))
            {
                path = iconFile;
                int.TryParse(iconIndex, out index);
            }
            else return null;

            path = Environment.ExpandEnvironmentVariables(path!.Trim());
            if (!Path.IsPathRooted(path))   // next to the desktop.ini, else in System32 (imageres.dll)
            {
                var local = Path.Combine(folder, path);
                path = File.Exists(local) ? local : Path.Combine(Environment.SystemDirectory, path);
            }
            if (!File.Exists(path)) return null;

            return ExtractIcon(path, index) is { } image ? new FolderCover(image, null, null) : null;
        }
        catch
        {
            return null;   // locked, damaged, no such resource
        }
    }

    /// <summary>An icon of an exe / dll / ico at 256 pixels (a negative index is a resource id); null if it cannot be read.</summary>
    private static System.Windows.Media.Imaging.BitmapSource? ExtractIcon(string path, int index)
    {
        try
        {
            var icons = new IntPtr[1];
            var ids   = new int[1];
            if (PrivateExtractIcons(path, index, 256, 256, icons, ids, 1, 0) < 1 || icons[0] == IntPtr.Zero) return null;
            try
            {
                var image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icons[0], System.Windows.Int32Rect.Empty,
                                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally { DestroyIcon(icons[0]); }
        }
        catch { return null; }
    }

    /// <summary>
    /// The picture of a place that is no folder: "pc" (This PC, Windows' own computer icon), "star" (Quick Access, Windows' star) and
    /// "bookmark" (Bookmarks, a blue ribbon drawn here: Windows has none). Call it on the UI thread (the ribbon is drawn with WPF).
    /// </summary>
    public static FolderCover? ForVirtual(string? kind)
    {
        var imageres = Path.Combine(Environment.SystemDirectory, "imageres.dll");
        System.Windows.Media.Imaging.BitmapSource? image = kind switch
        {
            "pc"       => ExtractIcon(imageres, -109),
            "star"     => ExtractIcon(imageres, -1024),
            "bookmark" => DrawBookmark(),
            _          => null,
        };
        return image == null ? null : new FolderCover(image, null, null);
    }

    private static System.Windows.Media.Imaging.BitmapSource? DrawBookmark()
    {
        try
        {
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new System.Windows.Media.ScaleTransform(16, 16));   // the same 16 x 16 drawing as the small icon in the tree
                dc.DrawGeometry(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2F, 0x6F, 0xD6)), null,
                                System.Windows.Media.Geometry.Parse("M3.5,1.5 H12.5 V14.5 L8,10.8 L3.5,14.5 Z"));
            }
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(256, 256, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private static readonly System.Text.RegularExpressions.Regex WmpArt =
        new(@"^AlbumArt_(\{[0-9A-Fa-f-]+\})_(Large|Small)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsWmpAlbumArt(string file) => WmpArt.IsMatch(Path.GetFileNameWithoutExtension(file));

    private static string WmpAlbumGuid(string file) => WmpArt.Match(Path.GetFileNameWithoutExtension(file)).Groups[1].Value;

    private static FolderCover? FromArchive(string folder, int decodeWidth)
    {
        var trimmed = folder.TrimEnd('\\', '/');
        var last    = Path.GetFileName(trimmed);
        var name    = ArchiveSupport.IsArchiveName(last) ? Path.GetFileNameWithoutExtension(last) : last;

        // The pictures of this folder of the archive, in page order (1, 2, ... 10). Most comic books keep their
        // pages in one folder inside the archive: with no picture here but a single folder, look inside it.
        var pictures = new List<VirtualEntry>();
        var current  = folder;
        for (int depth = 0; depth <= MaxArchiveDepth; depth++)
        {
            var entries = ArchiveSupport.List(current);
            pictures = entries
                .Where(e => !e.IsDirectory && ThumbnailLoader.IsImage(e.Name))
                .OrderBy(e => e.Name, Comparer<string>.Create(FileItemComparer.NaturalCompare))
                .ToList();
            if (pictures.Count > 0) break;

            var folders = entries.Where(e => e.IsDirectory).ToList();
            if (folders.Count != 1) break;
            current = folders[0].FullPath;
        }

        var byPath = pictures.ToDictionary(e => e.FullPath, e => e.Size);
        var candidates = Ranked(pictures.Select(e => e.FullPath), name, p => byPath[p]).ToList();

        // A comic book has no cover picture: its first page is the cover.
        if (candidates.Count == 0 && IsComic(folder))
            candidates = pictures.Select(e => e.FullPath).Take(3).ToList();

        foreach (var picture in candidates)
        {
            var image = ThumbnailLoader.Load(picture, decodeWidth);
            if (image != null) return new FolderCover(image, picture, null);
        }

        return null;
    }

    /// <summary>The pictures that look like a cover, best first: by their name, and among equals the biggest file.</summary>
    private static IEnumerable<string> Ranked(IEnumerable<string> pictures, string folderName, Func<string, long> sizeOf)
    {
        return pictures
            .Select(p => (Path: p, Rank: Rank(Path.GetFileNameWithoutExtension(p), folderName)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenByDescending(x => sizeOf(x.Path))   // AlbumArt_..._Large before ..._Small
            .Select(x => x.Path);
    }

    /// <summary>0 is the best name; -1 is not a cover name.</summary>
    private static int Rank(string baseName, string folderName)
    {
        for (int i = 0; i < CoverNames.Length; i++)
            if (baseName.Equals(CoverNames[i], StringComparison.OrdinalIgnoreCase)) return i;

        // Windows Media Player: AlbumArt_{GUID}_Large.jpg, AlbumArtSmall.jpg
        if (baseName.StartsWith("albumart", StringComparison.OrdinalIgnoreCase)) return Array.IndexOf(CoverNames, "albumart");

        // TeraTool's rule for a zip: a picture named like the folder (or ending in "cover").
        if (baseName.Equals(folderName, StringComparison.OrdinalIgnoreCase)) return CoverNames.Length;
        if (baseName.EndsWith("cover", StringComparison.OrdinalIgnoreCase)) return CoverNames.Length + 1;

        return -1;
    }

    private static bool IsComic(string path) =>
        path.Contains(".cbz", StringComparison.OrdinalIgnoreCase) || path.Contains(".cbr", StringComparison.OrdinalIgnoreCase);

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch { return 0; }
    }
}
