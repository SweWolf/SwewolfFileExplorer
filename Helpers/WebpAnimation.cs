using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Animated WebP for the picture viewer. Windows' own WebP codec gives only the first frame, so the frames are decoded by
/// SkiaSharp (already in the program for SVG). Every frame is put together as a whole picture, which is shown instead of the
/// one before it (disposal 2, the same as GifAnimator uses for GIF), so the viewer's animation code can be used as it is.
/// A still WebP, or a file that cannot be read this way, gives null and goes the normal way.
/// History: 2026-11-24 first version.
/// </summary>
public static class WebpAnimation
{
    private const long MaxBytes = 1_500_000_000;   // all frames together; a bigger animation is shown as a still picture

    /// <summary>True when the bytes are a WebP file that holds an animation (the "ANIM" chunk of the extended format).</summary>
    public static bool IsAnimated(byte[] b)
    {
        if (b.Length < 30 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'E' || b[10] != 'B' || b[11] != 'P')
            return false;
        // The first chunk of an animated file is VP8X, with the animation flag (bit 1) in its first byte.
        return b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'X' && (b[20] & 0x02) != 0;
    }

    public static LoadedPicture? Load(byte[] bytes)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec == null || codec.FrameCount < 2) return null;

            int w = codec.Info.Width, h = codec.Info.Height;
            int count = codec.FrameCount;
            long frameBytes = (long)w * h * 4;
            if (frameBytes * count > MaxBytes) return null;

            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            var infos = codec.FrameInfo;
            var pixels = new byte[count][];
            var frames = new List<GifFrame>(count);

            for (int i = 0; i < count; i++)
            {
                var buffer = new byte[frameBytes];
                int required = infos[i].RequiredFrame;
                if (required >= 0) Array.Copy(pixels[required], buffer, buffer.Length);

                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var result = codec.GetPixels(info, handle.AddrOfPinnedObject(), w * 4, new SKCodecOptions(i, required));
                    if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput) return null;
                }
                finally { handle.Free(); }

                pixels[i] = buffer;
                var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buffer, w * 4);
                bitmap.Freeze();

                int delay = infos[i].Duration;
                if (delay <= 10) delay = 100;   // as a browser does
                frames.Add(new GifFrame(bitmap, new Int32Rect(0, 0, w, h), delay, 2));
            }

            return new LoadedPicture(frames[0].Source, frames, w, h);
        }
        catch
        {
            return null;
        }
    }
}
