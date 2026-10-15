using System.IO;
using System.Windows.Media.Imaging;

namespace MyFileExplorer.Helpers;

/// <summary>Reads a small preview of a picture without keeping the file open or locked.</summary>
public static class ThumbnailLoader
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp"
    };

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// The picture decoded at <paramref name="decodeWidth"/> pixels wide (a frozen image: usable on any thread),
    /// or null if it cannot be read. The file is read into memory and closed again at once, and others may
    /// write to it or delete it meanwhile, so showing the preview never gets in the way of another program.
    /// </summary>
    public static BitmapSource? Load(string path, int decodeWidth)
    {
        try
        {
            using var stream = ArchiveSupport.OpenAnyFile(path);   // a file, or a picture inside an archive

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource      = stream;
            bitmap.CacheOption       = BitmapCacheOption.OnLoad;   // decode now, so the stream can be closed
            bitmap.DecodePixelWidth  = decodeWidth;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;   // a damaged file, no codec, or a file that was deleted meanwhile
        }
    }
}
