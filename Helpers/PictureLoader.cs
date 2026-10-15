using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyFileExplorer.Helpers;

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
    /// <summary>Call from any thread. Returns null if the file cannot be read as a picture.</summary>
    public static LoadedPicture? Load(string path)
    {
        try
        {
            byte[] bytes;
            using (var file = ArchiveSupport.OpenAnyFile(path))   // a file, or a picture inside an archive
            {
                bytes = new byte[file.Length];
                file.ReadExactly(bytes);
            }

            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;

            if (decoder is GifBitmapDecoder gif && gif.Frames.Count > 1)
            {
                var animated = LoadGif(gif);
                if (animated != null) return animated;
            }

            var frame = decoder.Frames[0];
            BitmapSource image = ApplyOrientation(frame);
            if (image.CanFreeze) image.Freeze();
            return new LoadedPicture(image);
        }
        catch
        {
            return null;   // not a picture, a damaged file, or one that was deleted meanwhile
        }
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

    /// <summary>Turns a photo the right way up, as its camera said (EXIF orientation 1-8).</summary>
    private static BitmapSource ApplyOrientation(BitmapFrame frame)
    {
        int orientation = Query(frame.Metadata, "System.Photo.Orientation", 1);

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
