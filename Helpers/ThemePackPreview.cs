using System.IO;
using System.Windows.Media.Imaging;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The preview picture of a Windows theme pack (.deskthemepack, .themepack): these are CAB archives that hold the wallpapers of the theme
/// (DesktopBackground\1.jpg ...), and the first picture says what the theme looks like. Read with 7-Zip (<see cref="SevenZipRunner"/>),
/// in memory: nothing is written to the disk. Only these types: for a Word document or any other zip-like file the first picture means nothing.
/// Runs on a background thread. A few results are kept (the user moves up and down a list of them).
/// </summary>
public static class ThemePackPreview
{
    private static readonly string[] Extensions = { ".deskthemepack", ".themepack" };

    public static bool Applies(string path) =>
        Extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static readonly Dictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const int CacheLimit = 30;

    /// <summary>The first picture of the pack (by name, 1.jpg before 2.jpg ...) as a preview, or null if there is none or it cannot be read.</summary>
    public static PreviewPicture? Find(string path, int decodeWidth)
    {
        try
        {
            var info = new FileInfo(path);
            var key  = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

            byte[]? bytes;
            lock (Cache)
            {
                if (!Cache.TryGetValue(key, out bytes))
                {
                    bytes = ReadFirstPicture(path);
                    if (Cache.Count >= CacheLimit) Cache.Clear();
                    Cache[key] = bytes;
                }
            }

            if (bytes == null) return null;
            var image = ThumbnailLoader.LoadBytes(bytes, decodeWidth);
            return image == null ? null : new PreviewPicture(image, null, bytes, Frame: false);
        }
        catch
        {
            return null;   // 7-Zip missing, a damaged pack, a locked file: no preview
        }
    }

    private static byte[]? ReadFirstPicture(string path)
    {
        var first = SevenZipRunner.List(path)
            .Where(e => !e.IsDirectory && !e.Encrypted && ThumbnailLoader.IsImage(e.FullName))
            .OrderBy(e => e.FullName, Comparer<string>.Create(FileItemComparer.NaturalCompare))
            .FirstOrDefault();
        if (first == null) return null;

        using var memory = new MemoryStream();
        SevenZipRunner.Extract(path, first.StoredName ?? first.FullName, memory);
        return memory.Length > 0 ? memory.ToArray() : null;
    }
}
