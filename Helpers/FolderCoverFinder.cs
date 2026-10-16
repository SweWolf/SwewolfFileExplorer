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
///  1. A picture in the folder with a cover-like name, best first: Folder, Cover, Front, Poster, AlbumArt (also
///     Windows Media Player's AlbumArt_{...}_Large), Album, Thumb, Thumbnail, or a name equal to the folder's.
///     (TeraTool only looked for Folder.jpg.) Hidden files count: Folder.jpg is usually hidden.
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

        var files = Directory.EnumerateFiles(folder).ToList();   // hidden files included
        var name  = Path.GetFileName(folder.TrimEnd('\\', '/'));

        // 1. A picture with a cover-like name. If the best one cannot be read, the next one is tried.
        var pictures = files.Where(ThumbnailLoader.IsImage).ToList();
        foreach (var picture in Ranked(pictures, name, f => SafeLength(f)))
        {
            token.ThrowIfCancellationRequested();
            var image = ThumbnailLoader.Load(picture, decodeWidth);
            if (image != null) return new FolderCover(image, picture, null);
        }

        // 2. The cover embedded in a music file.
        var music = files.Where(AudioTagReader.HasTags)
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
