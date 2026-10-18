using System.Diagnostics;
using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// JPEG XL (.jxl). Two ways to read it. (1) <b>ImageMagick</b> (<c>magick.exe</c>, built with libjxl), used first when it is
/// installed, in the same way RAR files are read by 7-Zip's <c>7z.exe</c>: the file is fed through the standard input (no
/// file name to quote, also for a picture inside an archive), the PNG is read from the standard output, nothing is written
/// to the disk; only the first frame of an animated file is used, and the picture comes out turned the right way up.
/// An <b>HDR</b> picture (PQ) is also turned into a normal screen picture here (<see cref="ToneMap"/>). (2) Microsoft's
/// "JPEG XL Image Extension" (a WIC codec, installed on the user's machine): WPF decodes the file like any other picture
/// with it. It is the fallback, because it shows an HDR picture burnt out (white where there is detail), which is why
/// ImageMagick comes first although it takes a fraction of a second more.
///
/// Not done: a setting for where ImageMagick is (it is looked for in the registry, in Program Files and on the PATH),
/// HLG pictures (the other HDR curve: shown as the codec shows them), animated JXL, and a decoder inside the program (that needs a
/// native library, e.g. libjxl through a NuGet package; the user would be asked first).
/// </summary>
public static class JxlSupport
{
    public static bool IsJxl(string path) => string.Equals(Path.GetExtension(path), ".jxl", StringComparison.OrdinalIgnoreCase);

    /// <summary>What to tell the user when a JPEG XL picture cannot be shown.</summary>
    public const string MissingHint = "JPEG XL needs ImageMagick (magick.exe) or Windows' JPEG XL Image Extension";

    private static readonly object FindLock = new();
    private static string? _magick;
    private static bool _searched;

    /// <summary>The place of <c>magick.exe</c>, or null if ImageMagick is not installed. Looked for once per run.</summary>
    public static string? FindMagick()
    {
        lock (FindLock)
        {
            if (_searched) return _magick;
            _searched = true;
            _magick = FindMagickNow();
            return _magick;
        }
    }

    /// <summary>Only for tests: look again (and, with a path, use that one).</summary>
    public static void ResetForTest(string? magick = null)
    {
        lock (FindLock) { _searched = magick != null; _magick = magick; }
    }

    private static string? FindMagickNow()
    {
        // 1. The registry: the installer of ImageMagick writes where it put the program.
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\ImageMagick\Current");
                if (key?.GetValue("BinPath") is string bin)
                {
                    var exe = Path.Combine(bin, "magick.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            catch { }
        }

        // 2. Program Files (the newest version if there are several).
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") }
                     .Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(root!, "ImageMagick*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var exe = Path.Combine(dir, "magick.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            catch { }
        }

        // 3. The PATH. Only magick.exe: Windows has a convert.exe of its own, which is something else.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(dir.Trim().Trim('"'), "magick.exe");
                if (File.Exists(exe)) return exe;
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// The picture ready to show (frozen), or null if there is no ImageMagick or it could not read the data.
    /// An ordinary picture comes back as it is (its colour profile, if any, is for WPF to use). An <b>HDR</b> picture (PQ,
    /// the curve of HDR video: the profile says "PeQ") is turned into a normal screen picture first (<see cref="ToneMap"/>):
    /// shown as it is, WPF would apply the profile as if it were an SDR one and blow the brightness out.
    /// <paramref name="maxWidth"/> above 0 shrinks a bigger picture to that width (much faster, for previews).
    /// Call it from a background thread: it starts a program and waits for it.
    /// </summary>
    public static System.Windows.Media.Imaging.BitmapSource? ToBitmap(byte[] jxl, int maxWidth = 0)
    {
        var png = ToPng(jxl, maxWidth, hdr: false);
        if (png == null) return null;

        try
        {
            if (IsPq(png))
            {
                // The raw 16-bit values, without the profile: the PQ codes themselves.
                var raw = ToPng(jxl, maxWidth, hdr: true);
                if (raw != null)
                {
                    var mapped = ToneMap(raw);
                    if (mapped != null) return mapped;
                }
            }

            using var stream = new MemoryStream(png);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;

            var frame = decoder.Frames[0];
            if (frame.CanFreeze) frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    // ── HDR (PQ) pictures ────────────────────────────────────────────────────

    /// <summary>
    /// True if the PNG carries a colour profile of an HDR picture in the PQ curve. libjxl names the profiles it writes
    /// "RGB_D65_202_Per_PeQ" (Rec.2020 primaries, PQ transfer function): the name is looked for in the profile of the PNG.
    /// </summary>
    internal static bool IsPq(byte[] png)
    {
        try
        {
            // Walk the chunks to the iCCP chunk: name, a zero byte, the method (0), then the zlib-packed profile.
            int pos = 8;
            while (pos + 12 <= png.Length)
            {
                int length = png[pos] << 24 | png[pos + 1] << 16 | png[pos + 2] << 8 | png[pos + 3];
                string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
                if (type == "iCCP")
                {
                    int data = pos + 8, end = data + length;
                    int zero = Array.IndexOf(png, (byte)0, data, Math.Min(80, end - data));
                    if (zero < 0) return false;

                    using var packed = new MemoryStream(png, zero + 2 + 2, end - (zero + 2 + 2));   // skip the 2-byte zlib header
                    using var inflate = new System.IO.Compression.DeflateStream(packed, System.IO.Compression.CompressionMode.Decompress);
                    using var profile = new MemoryStream();
                    inflate.CopyTo(profile);
                    var bytes = profile.ToArray();
                    return Contains(bytes, "PeQ") || Contains(bytes, "PQ_");   // ASCII 'desc' or UTF-16 'mluc' text
                }
                if (type == "IDAT") return false;
                pos += 12 + length;
            }
        }
        catch { }
        return false;
    }

    private static bool Contains(byte[] data, string text)
    {
        var ascii = System.Text.Encoding.ASCII.GetBytes(text);
        var utf16 = System.Text.Encoding.BigEndianUnicode.GetBytes(text);
        return IndexOf(data, ascii) >= 0 || IndexOf(data, utf16) >= 0;
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (int i = 0; i + pattern.Length <= data.Length; i++)
        {
            int j = 0;
            while (j < pattern.Length && data[i + j] == pattern[j]) j++;
            if (j == pattern.Length) return i;
        }
        return -1;
    }

    /// <summary>
    /// Turns a PNG of PQ codes (16 bits per channel, Rec.2020 primaries, no profile) into a picture for an ordinary screen:
    /// PQ code to light (absolute, in nits), 203 nits (the "reference white" of HDR video) is the white of the screen,
    /// Rec.2020 to Rec.709 colors, brightness above about 70 % of white is eased in towards white instead of being cut off
    /// (a soft shoulder, applied to the luminance so the colors keep their balance), then the sRGB curve.
    /// </summary>
    internal static System.Windows.Media.Imaging.BitmapSource? ToneMap(byte[] rawPng)
    {
        using var stream = new MemoryStream(rawPng);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile | System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) return null;

        var source = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoder.Frames[0],
            System.Windows.Media.PixelFormats.Rgb48, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight;

        var codes = new ushort[width * height * 3];
        source.CopyPixels(codes, width * 6, 0);

        // PQ code (0..65535) to light relative to 203 nits.
        var light = new float[65536];
        const double m1 = 0.1593017578125, m2 = 78.84375, c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
        for (int i = 0; i < light.Length; i++)
        {
            double e = Math.Pow(i / 65535.0, 1 / m2);
            double nits = 10000 * Math.Pow(Math.Max(e - c1, 0) / (c2 - c3 * e), 1 / m1);
            light[i] = (float)(nits / 203.0);
        }

        // Light (0..1) to an 8-bit sRGB value.
        const int steps = 4096;
        var srgb = new byte[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            double v = i / (double)steps;
            double s = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            srgb[i] = (byte)Math.Clamp((int)Math.Round(s * 255), 0, 255);
        }

        const double knee = 0.7;
        var output = new byte[width * height * 3];
        for (int p = 0, o = 0; p < codes.Length; p += 3, o += 3)
        {
            double r2 = light[codes[p]], g2 = light[codes[p + 1]], b2 = light[codes[p + 2]];

            // Rec.2020 to Rec.709 (linear light); colors outside Rec.709 are cut off at zero.
            double r = Math.Max(1.6605 * r2 - 0.5876 * g2 - 0.0728 * b2, 0);
            double g = Math.Max(-0.1246 * r2 + 1.1329 * g2 - 0.0083 * b2, 0);
            double b = Math.Max(-0.0182 * r2 - 0.1006 * g2 + 1.1187 * b2, 0);

            // The shoulder, on the luminance: untouched up to the knee, then eased towards 1 without ever cutting off.
            double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            if (y > knee)
            {
                double mapped = knee + (1 - knee) * (1 - Math.Exp(-(y - knee) / (1 - knee)));
                double scale = mapped / y;
                r *= scale; g *= scale; b *= scale;
            }

            // BGR order, for Bgr24.
            output[o]     = srgb[(int)(Math.Clamp(b, 0, 1) * steps)];
            output[o + 1] = srgb[(int)(Math.Clamp(g, 0, 1) * steps)];
            output[o + 2] = srgb[(int)(Math.Clamp(r, 0, 1) * steps)];
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgr24, null, output, width * 3);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// The picture as PNG bytes (8 bits, turned the right way up, first frame), or null if there is no ImageMagick or it could
    /// not read the data. With <paramref name="hdr"/>: 16 bits and without the colour profile (the raw codes of an HDR picture,
    /// for <see cref="ToneMap"/>). <paramref name="maxWidth"/> above 0 shrinks a bigger picture to that width.
    /// </summary>
    internal static byte[]? ToPng(byte[] jxl, int maxWidth, bool hdr)
    {
        var magick = FindMagick();
        if (magick == null) return null;

        try
        {
            var start = new ProcessStartInfo(magick)
            {
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            start.ArgumentList.Add("jxl:-[0]");   // from the standard input, the first frame
            start.ArgumentList.Add("-auto-orient");
            if (maxWidth > 0)
            {
                start.ArgumentList.Add("-resize");
                start.ArgumentList.Add($"{maxWidth}x>");   // only ever smaller
            }
            if (hdr)
            {
                start.ArgumentList.Add("-alpha");
                start.ArgumentList.Add("off");
                start.ArgumentList.Add("-strip");   // no profile: the codes as they are
                start.ArgumentList.Add("-define");
                start.ArgumentList.Add("png:exclude-chunk=gAMA,cHRM,iCCP,sRGB");
            }
            start.ArgumentList.Add("-depth");
            start.ArgumentList.Add(hdr ? "16" : "8");
            start.ArgumentList.Add("png:-");   // to the standard output

            using var process = Process.Start(start);
            if (process == null) return null;

            // The input goes in on its own thread: a big picture would otherwise fill the pipe while we wait for the answer.
            var feeding = Task.Run(() =>
            {
                try { process.StandardInput.BaseStream.Write(jxl, 0, jxl.Length); process.StandardInput.Close(); }
                catch { /* ImageMagick gave up before it read everything: the exit code tells */ }
            });
            var errors = process.StandardError.ReadToEndAsync();   // read, so a chatty program cannot block on a full pipe

            using var output = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(output);

            if (!process.WaitForExit(60_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            feeding.Wait(1000);

            return process.ExitCode == 0 && output.Length > 8 ? output.ToArray() : null;
        }
        catch
        {
            return null;   // could not start it, or the pipe broke
        }
    }
}
