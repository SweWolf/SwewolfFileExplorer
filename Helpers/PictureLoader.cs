using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SwewolfFileExplorer.Helpers;

/// <summary>A picture ready to show: the image (turned the right way up), and the frames if it is an animated GIF.</summary>
public sealed class LoadedPicture
{
    public LoadedPicture(BitmapSource image, IReadOnlyList<GifFrame>? frames = null, int width = 0, int height = 0)
    {
        Image  = image;
        Frames = frames;
        Width  = width  > 0 ? width  : image.PixelWidth;
        Height = height > 0 ? height : image.PixelHeight;
    }

    /// <summary>The picture, or the first frame of an animated GIF.</summary>
    public BitmapSource Image { get; }

    /// <summary>The frames of an animated GIF; null for anything else.</summary>
    public IReadOnlyList<GifFrame>? Frames { get; }

    /// <summary>The size in pixels (for a GIF: of the whole canvas).</summary>
    public int Width { get; }
    public int Height { get; }
}

/// <summary>Reads a picture for the viewer. The file is read into memory at once and closed, so it is never locked.</summary>
public static class PictureLoader
{
    private const int ViewerSvgWidth = 3000;
    private const int ViewerPsdWidth = 8192;   // a bigger Photoshop picture is averaged down to this (whole-number steps)

    /// <summary>Call from any thread. Returns null if the file cannot be read as a picture.</summary>
    public static LoadedPicture? Load(string path)
    {
        try
        {
            byte[] bytes;
            using (var file = ArchiveSupport.OpenAnyFile(path))   // a file, or a picture inside an archive
            {
                // A RAW photo (NEF): the biggest JPEG built into it, in a few milliseconds and with the camera's own look;
                // only the bytes of that JPEG are read. Without one, the file is tried like any other (Windows may have a codec).
                if (RawPreview.IsRaw(path) && RawPreview.Read(file) is { } raw)
                    return LoadRaw(raw);

                // Photoshop: the flattened picture, read straight from the file (a PSD can be hundreds of megabytes, mostly layers).
                if (PsdSupport.IsPsd(path))
                    return PsdSupport.ToBitmap(file, ViewerPsdWidth) is { } psdPicture ? new LoadedPicture(psdPicture) : null;

                if (file.CanSeek) file.Position = 0;
                bytes = new byte[file.Length];
                file.ReadExactly(bytes);
            }

            // SVG: drawn inside the program, 3000 pixels wide (about a screen, and sharp when zoomed a little), on white.
            if (SvgSupport.IsSvg(path))
                return SvgSupport.ToBitmap(bytes, ViewerSvgWidth) is { } svgPicture ? new LoadedPicture(svgPicture) : null;

            // JPEG XL: through ImageMagick if it is there, which also turns an HDR picture into a normal one (the codec of Windows
            // shows such a picture burnt out). The picture comes out turned the right way up. Without ImageMagick, or if it cannot
            // read the file, the codec of Windows (Microsoft's JPEG XL Image Extension) is tried below like for any other format.
            if (JxlSupport.IsJxl(path) && JxlSupport.FindMagick() != null
                && JxlSupport.ToBitmap(bytes) is { } jxlPicture)
                return new LoadedPicture(jxlPicture);

            // Animated WebP: Windows' codec shows only the first frame, so SkiaSharp decodes the frames.
            if (WebpAnimation.IsAnimated(bytes) && WebpAnimation.Load(bytes) is { } webpAnimation)
                return webpAnimation;

            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;

            if (decoder is GifBitmapDecoder gif && gif.Frames.Count > 1)
            {
                var animated = LoadGif(gif);
                if (animated != null) return animated;
            }

            // An .ico holds several sizes: show the biggest one.
            var frame = decoder is IconBitmapDecoder ? ThumbnailLoader.BiggestFrame(decoder)! : decoder.Frames[0];
            BitmapSource image = ApplyOrientation(frame, Query(frame.Metadata, "System.Photo.Orientation", 1));
            if (image.CanFreeze) image.Freeze();
            return new LoadedPicture(image);
        }
        catch
        {
            return null;   // not a picture, a damaged file, or one that was deleted meanwhile
        }
    }

    // ── RAW photo: the JPEG inside it ────────────────────────────────────────

    private static LoadedPicture? LoadRaw(RawPreviewImage raw)
    {
        using var stream = new MemoryStream(raw.Jpeg);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) return null;

        var frame = decoder.Frames[0];

        // The camera's orientation is in the RAW file; the JPEG inside it usually says nothing (if it does, one of them is used, not both).
        int orientation = raw.Orientation > 1 ? raw.Orientation : Query(frame.Metadata, "System.Photo.Orientation", 1);
        BitmapSource image = ApplyOrientation(frame, orientation);
        if (image.CanFreeze) image.Freeze();
        return new LoadedPicture(image);
    }

    // ── Animated GIF ─────────────────────────────────────────────────────────

    private static LoadedPicture? LoadGif(GifBitmapDecoder decoder)
    {
        int width  = Query(decoder.Metadata, "/logscrdesc/Width",  decoder.Frames[0].PixelWidth);
        int height = Query(decoder.Metadata, "/logscrdesc/Height", decoder.Frames[0].PixelHeight);

        var frames = new List<GifFrame>();
        foreach (var frame in decoder.Frames)
        {
            var meta = frame.Metadata as BitmapMetadata;

            int left  = Query(meta, "/imgdesc/Left", 0);
            int top   = Query(meta, "/imgdesc/Top", 0);
            int delay = Query(meta, "/grctlext/Delay", 10) * 10;   // in hundredths of a second
            if (delay <= 10) delay = 100;                           // 0 and 1 mean "as fast as possible": browsers show 10 per second

            if (frame.CanFreeze) frame.Freeze();
            frames.Add(new GifFrame(frame,
                new Int32Rect(left, top, frame.PixelWidth, frame.PixelHeight),
                delay,
                Query(meta, "/grctlext/Disposal", 0)));
        }

        return new LoadedPicture(frames[0].Source, frames, width, height);
    }

    private static int Query(object? metadata, string query, int fallback)
    {
        try
        {
            if (metadata is BitmapMetadata m && m.GetQuery(query) is { } value)
                return Convert.ToInt32(value);
        }
        catch
        {
            // no such property in this file
        }
        return fallback;
    }

    // ── EXIF orientation ─────────────────────────────────────────────────────

    /// <summary>The EXIF orientation (1-8) of the first picture in a stream, read from the metadata only; 1 when there is none or it cannot be read.</summary>
    internal static int ReadOrientation(Stream stream)
    {
        try
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            return decoder.Frames.Count > 0 ? Query(decoder.Frames[0].Metadata, "System.Photo.Orientation", 1) : 1;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>Turns a photo the right way up, as its camera said (EXIF orientation 1-8).</summary>
    internal static BitmapSource ApplyOrientation(BitmapSource frame, int orientation)
    {
        var transform = new TransformGroup();
        switch (orientation)
        {
            case 2: transform.Children.Add(new ScaleTransform(-1, 1)); break;                                       // mirrored
            case 3: transform.Children.Add(new RotateTransform(180)); break;
            case 4: transform.Children.Add(new ScaleTransform(1, -1)); break;
            case 5: transform.Children.Add(new ScaleTransform(-1, 1)); transform.Children.Add(new RotateTransform(270)); break;
            case 6: transform.Children.Add(new RotateTransform(90)); break;
            case 7: transform.Children.Add(new ScaleTransform(-1, 1)); transform.Children.Add(new RotateTransform(90)); break;
            case 8: transform.Children.Add(new RotateTransform(270)); break;
            default: return frame;
        }

        return new TransformedBitmap(frame, transform);
    }
}
