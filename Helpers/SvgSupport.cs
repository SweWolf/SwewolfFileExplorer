using System.IO;
using System.IO.Compression;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Svg;
using Svg.Model;
using Svg.Skia;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// SVG (.svg, and .svgz which is the same gzipped): a vector drawing, drawn inside the program by Svg.Skia (on SkiaSharp), so
/// nothing has to be installed on the computer. A vector has no size of its own, so it is drawn at the size asked for
/// (the preview's width, or about the screen's for the viewer). It is drawn on a <b>white</b> background: most drawings are
/// made for a white page (a black drawing on a transparent one would vanish on the viewer's black background), as a web
/// browser shows them.
/// </summary>
public static class SvgSupport
{
    /// <summary>Never draw bigger than this on either side (a drawing may say 100000 px).</summary>
    private const int MaxSide = 4096;

    public static bool IsSvg(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".svg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".svgz", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The drawing as a picture <paramref name="width"/> pixels wide (smaller if that would make it taller than <see cref="MaxSide"/>); null if it cannot be read.</summary>
    public static BitmapSource? ToBitmap(byte[] fileBytes, int width)
    {
        var png = ToPng(fileBytes, width);
        return png == null ? null : ThumbnailLoader.LoadBytes(png, 0);
    }

    private static byte[]? ToPng(byte[] fileBytes, int width)
    {
        try
        {
            using var source = new MemoryStream(Unzipped(fileBytes));
            // A drawing may name pictures and style sheets on the web or on the disk: selecting a file must never make the
            // program fetch anything (a tracking address in a file would tell its owner who looked). Only what is inside the
            // file itself (references to its own parts, and data: addresses) is used.
            var parameters = new SvgParameters(null, null)
            {
                LoadOptions = new SvgDocumentLoadOptions { ExternalResources = SvgExternalResourcePolicy.SameDocumentAndDataOnly },
            };

            using var svg = new SKSvg();
            var picture = svg.Load(source, parameters);
            if (picture == null) return null;

            var box = picture.CullRect;
            if (box.Width < 1 || box.Height < 1) return null;

            float scale = Math.Max(width, 16) / box.Width;
            scale = Math.Min(scale, MaxSide / Math.Max(box.Width, box.Height));   // not bigger than MaxSide on either side
            int w = Math.Max(1, (int)Math.Round(box.Width  * scale));
            int h = Math.Max(1, (int)Math.Round(box.Height * scale));

            using var bitmap = new SKBitmap(w, h);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            canvas.Scale(scale);
            canvas.Translate(-box.Left, -box.Top);
            canvas.DrawPicture(picture);
            canvas.Flush();

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch
        {
            return null;   // not an SVG, a damaged one, or the drawing library cannot be loaded
        }
    }

    // .svgz is gzip: the first two bytes say so (the extension is not trusted either way).
    private static byte[] Unzipped(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0x1F || bytes[1] != 0x8B) return bytes;

        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
