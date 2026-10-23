using System.IO;
using System.Windows.Media.Imaging;

namespace SwewolfFileExplorer.Helpers;

/// <summary>Reads a small preview of a picture without keeping the file open or locked.</summary>
public static class ThumbnailLoader
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".jxl", ".svg", ".svgz", ".psd", ".psb"
    };

    /// <summary>
    /// A picture the viewer can show: the usual formats, JPEG XL (through Windows' codec or ImageMagick, see
    /// <see cref="JxlSupport"/>), and RAW photos (NEF) through the JPEG built into them.
    /// </summary>
    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path)) || RawPreview.IsRaw(path);

    /// <summary>The frame with the most pixels (an .ico holds several sizes), or null if there is none.</summary>
    public static BitmapFrame? BiggestFrame(BitmapDecoder decoder) =>
        decoder.Frames.OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight).FirstOrDefault();

    /// <summary>The same for a picture that is in memory (a cover embedded in a music file).</summary>
    public static BitmapSource? LoadBytes(byte[] data, int decodeWidth)
    {
        try
        {
            using var stream = new MemoryStream(data);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource      = stream;
            bitmap.CacheOption       = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth  = decodeWidth;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;   // the bytes are not a picture
        }
    }

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

            // A RAW photo: the small preview comes from the JPEG built into it (fast; no RAW decoder needed).
            if (RawPreview.IsRaw(path) && RawPreview.Read(stream) is { } raw && LoadBytes(raw.Jpeg, decodeWidth) is { } fromRaw)
                return fromRaw;
            if (stream.CanSeek) stream.Position = 0;

            // Photoshop: the flattened picture stored in the file, averaged down while it is read (see PsdSupport).
            if (PsdSupport.IsPsd(path)) return PsdSupport.ToBitmap(stream, decodeWidth);

            // SVG: a drawing, drawn at the width asked for, on white (see SvgSupport).
            if (SvgSupport.IsSvg(path))
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return SvgSupport.ToBitmap(copy.ToArray(), decodeWidth);
            }

            // JPEG XL: through ImageMagick if it is there (it also tones down an HDR picture, see JxlSupport); otherwise,
            // and if it cannot read the file, through the codec of Windows below.
            if (JxlSupport.IsJxl(path) && JxlSupport.FindMagick() != null)
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                if (JxlSupport.ToBitmap(copy.ToArray(), decodeWidth) is { } jxl) return jxl;
                if (stream.CanSeek) stream.Position = 0;
            }

            // An icon holds several sizes and WPF would give the first (often 16 px): take the biggest one.
            if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var best = BiggestFrame(decoder);
                if (best != null)
                {
                    BitmapSource result = best;
                    if (best.PixelWidth > decodeWidth)
                        result = new TransformedBitmap(best, new System.Windows.Media.ScaleTransform((double)decodeWidth / best.PixelWidth, (double)decodeWidth / best.PixelWidth));
                    if (result.CanFreeze) result.Freeze();
                    return result;
                }
                if (stream.CanSeek) stream.Position = 0;
            }

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
