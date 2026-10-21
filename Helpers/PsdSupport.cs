using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Photoshop files (.psd, and .psb for very large pictures), shown through the flattened picture that Photoshop stores at the end of the
/// file (the "merged image", there when the file was saved with Maximize Compatibility, which is the default). Nothing is installed and no
/// layer is read: the layers, which are most of a big file, are skipped with a seek. The picture is decoded in one pass over the file and
/// averaged down while it is read, so a 12000 x 6800 panorama never needs more than the answer's size in memory (the preview is a few
/// hundred pixels wide, the viewer about 8000 at most).
///
/// Understood: RGB, grayscale, indexed, CMYK and duotone pictures of 8 or 16 bits, stored raw or RLE (PackBits). Not understood (null, so
/// the picture is simply not shown): ZIP-compressed merged data, Lab, bitmap and 32-bit pictures, and files without a real merged image
/// (saved without Maximize Compatibility they only hold a blank page). The color profile is not applied (sRGB is assumed), and an
/// alpha channel is ignored: the picture is shown as its color channels say.
/// </summary>
public static class PsdSupport
{
    public static bool IsPsd(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".psd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".psb", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The size in pixels from the 26-byte header (for the Dimensions column); null if it is not a Photoshop file.</summary>
    public static (int Width, int Height)? ReadSize(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[26];
            file.ReadExactly(head);
            if (head[0] != '8' || head[1] != 'B' || head[2] != 'P' || head[3] != 'S') return null;
            return ((int)BinaryPrimitives.ReadUInt32BigEndian(head[18..]), (int)BinaryPrimitives.ReadUInt32BigEndian(head[14..]));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The merged picture, at most about <paramref name="maxWidth"/> pixels wide (whole-number averaging down: 12042 wide with a limit of 8192
    /// gives 6021). Null if the file is not a Photoshop file this reader understands. A frozen image, usable on any thread.
    /// </summary>
    public static BitmapSource? ToBitmap(Stream input, int maxWidth)
    {
        try
        {
            return Decode(new BufferedStream(input, 1 << 20), maxWidth);
        }
        catch (Exception)
        {
            return null;   // cut off, damaged, or not a Photoshop file
        }
    }

    private static BitmapSource? Decode(Stream s, int maxWidth)
    {
        // ── The header ────────────────────────────────────────────────────────
        Span<byte> head = stackalloc byte[26];
        s.ReadExactly(head);
        if (head[0] != '8' || head[1] != 'B' || head[2] != 'P' || head[3] != 'S') return null;

        int version  = BinaryPrimitives.ReadUInt16BigEndian(head[4..]);
        int channels = BinaryPrimitives.ReadUInt16BigEndian(head[12..]);
        int height   = (int)BinaryPrimitives.ReadUInt32BigEndian(head[14..]);
        int width    = (int)BinaryPrimitives.ReadUInt32BigEndian(head[18..]);
        int depth    = BinaryPrimitives.ReadUInt16BigEndian(head[22..]);
        int mode     = BinaryPrimitives.ReadUInt16BigEndian(head[24..]);

        if (version is not (1 or 2) || width < 1 || height < 1 || width > 300000 || height > 300000) return null;
        if (depth is not (8 or 16)) return null;

        int colorChannels = mode switch { 1 => 1, 2 => 1, 3 => 3, 4 => 4, 8 => 1, _ => 0 };   // grayscale, indexed, RGB, CMYK, duotone
        if (colorChannels == 0 || channels < colorChannels) return null;

        // ── Color mode data (the palette of an indexed picture), image resources, layers: the last two are skipped ──
        byte[]? palette = null;
        long modeLength = ReadU32(s);
        if (mode == 2 && modeLength == 768)
        {
            palette = new byte[768];
            s.ReadExactly(palette);
        }
        else Skip(s, modeLength);

        Skip(s, ReadU32(s));                                          // image resources
        Skip(s, version == 1 ? ReadU32(s) : (long)ReadU64(s));        // layers and masks: most of a big file

        // ── The merged image ──────────────────────────────────────────────────
        int compression = ReadU16(s);
        if (compression is not (0 or 1)) return null;

        int bytesPerSample = depth / 8;
        int rowBytes = width * bytesPerSample;

        // RLE: how many bytes each row takes, for every row of every channel.
        int[]? rowSizes = null;
        if (compression == 1)
        {
            long entries = (long)channels * height;
            if (entries > 100_000_000) return null;
            rowSizes = new int[entries];
            byte[] table = new byte[entries * (version == 1 ? 2 : 4)];
            s.ReadExactly(table);
            for (long i = 0; i < entries; i++)
                rowSizes[i] = version == 1
                    ? BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan((int)i * 2))
                    : (int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan((int)i * 4));
        }

        // Averaging down by a whole number of pixels. An indexed picture is never averaged (an average of palette numbers means nothing).
        int step = mode == 2 || maxWidth <= 0 || width <= maxWidth ? 1 : (width + maxWidth - 1) / maxWidth;
        int outW = (width + step - 1) / step;
        int outH = (height + step - 1) / step;
        long cells = (long)outW * outH;
        if (cells * 3 > int.MaxValue) return null;

        bool rgb = mode == 3;
        byte[] bgr = rgb || colorChannels == 1 || colorChannels == 4 ? new byte[cells * 3] : Array.Empty<byte>();
        byte[][]? planes = rgb ? null : new byte[colorChannels][];
        uint[]? sums = step > 1 ? new uint[cells] : null;

        byte[] row = new byte[rowBytes];
        byte[] packed = new byte[Math.Max(rowBytes * 2 + 16, 1024)];

        for (int plane = 0; plane < colorChannels; plane++)
        {
            byte[]? dest = rgb ? null : (planes![plane] = new byte[cells]);
            if (sums != null) Array.Clear(sums);

            for (int y = 0; y < height; y++)
            {
                ReadRow(s, compression, rowSizes == null ? 0 : rowSizes[(long)plane * height + y], row, rowBytes, ref packed);

                int outRow = y / step;
                long baseIndex = (long)outRow * outW;
                if (step == 1)
                {
                    if (rgb)
                    {
                        long o = baseIndex * 3 + (2 - plane);   // B, G, R in the bitmap
                        for (int x = 0; x < width; x++, o += 3) bgr[o] = row[x * bytesPerSample];
                    }
                    else
                    {
                        for (int x = 0; x < width; x++) dest![baseIndex + x] = row[x * bytesPerSample];
                    }
                }
                else
                {
                    for (int cx = 0, x0 = 0; cx < outW; cx++, x0 += step)
                    {
                        int x1 = Math.Min(width, x0 + step);
                        uint sum = 0;
                        for (int x = x0; x < x1; x++) sum += row[x * bytesPerSample];
                        sums![baseIndex + cx] += sum;
                    }
                }
            }

            if (sums != null)
            {
                for (int cy = 0; cy < outH; cy++)
                {
                    int ch = Math.Min(step, height - cy * step);
                    for (int cx = 0; cx < outW; cx++)
                    {
                        int cw = Math.Min(step, width - cx * step);
                        uint n = (uint)(cw * ch);
                        long index = (long)cy * outW + cx;
                        byte value = (byte)((sums[index] + n / 2) / n);
                        if (rgb) bgr[index * 3 + (2 - plane)] = value;
                        else dest![index] = value;
                    }
                }
            }
        }

        // ── Into the bitmap's own order ───────────────────────────────────────
        if (mode == 2)
        {
            if (palette == null) return null;
            for (long i = 0; i < cells; i++)
            {
                int index = planes![0][i];
                bgr[i * 3]     = palette[512 + index];   // the palette is all reds, then all greens, then all blues
                bgr[i * 3 + 1] = palette[256 + index];
                bgr[i * 3 + 2] = palette[index];
            }
        }
        else if (colorChannels == 1)
        {
            for (long i = 0; i < cells; i++) bgr[i * 3] = bgr[i * 3 + 1] = bgr[i * 3 + 2] = planes![0][i];
        }
        else if (colorChannels == 4)
        {
            // Photoshop stores CMYK upside down (255 = no ink); the usual approximation of the screen color.
            for (long i = 0; i < cells; i++)
            {
                int k = planes![3][i];
                bgr[i * 3]     = (byte)(planes[2][i] * k / 255);
                bgr[i * 3 + 1] = (byte)(planes[1][i] * k / 255);
                bgr[i * 3 + 2] = (byte)(planes[0][i] * k / 255);
            }
        }

        var bitmap = BitmapSource.Create(outW, outH, 96, 96, PixelFormats.Bgr24, null, bgr, outW * 3);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>One row of one channel, into <paramref name="row"/> (the file's bytes: 8-bit samples, or 16-bit big-endian pairs).</summary>
    private static void ReadRow(Stream s, int compression, int storedSize, byte[] row, int rowBytes, ref byte[] packed)
    {
        if (compression == 0)
        {
            s.ReadExactly(row, 0, rowBytes);
            return;
        }

        if (storedSize > packed.Length) packed = new byte[storedSize];
        s.ReadExactly(packed, 0, storedSize);

        // PackBits: a count byte n; 0..127 = copy the next n + 1 bytes; -127..-1 = repeat the next byte 1 - n times; -128 = nothing.
        int i = 0, o = 0;
        while (o < rowBytes && i < storedSize)
        {
            int n = (sbyte)packed[i++];
            if (n >= 0)
            {
                int count = Math.Min(n + 1, Math.Min(rowBytes - o, storedSize - i));
                Buffer.BlockCopy(packed, i, row, o, count);
                i += n + 1;
                o += count;
            }
            else if (n != -128)
            {
                int count = Math.Min(1 - n, rowBytes - o);
                if (i >= storedSize) break;
                Array.Fill(row, packed[i++], o, count);
                o += count;
            }
        }
        if (o < rowBytes) Array.Clear(row, o, rowBytes - o);   // a short row: the rest is black
    }

    private static ushort ReadU16(Stream s)
    {
        Span<byte> b = stackalloc byte[2];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt16BigEndian(b);
    }

    private static uint ReadU32(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    private static ulong ReadU64(Stream s)
    {
        Span<byte> b = stackalloc byte[8];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt64BigEndian(b);
    }

    /// <summary>Moves over <paramref name="count"/> bytes: a seek when the stream can, else reading and throwing away (a file inside an archive).</summary>
    private static void Skip(Stream s, long count)
    {
        if (count <= 0) return;
        if (s.CanSeek)
        {
            s.Seek(count, SeekOrigin.Current);
            return;
        }

        byte[] buffer = new byte[81920];
        while (count > 0)
        {
            int read = s.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read <= 0) throw new EndOfStreamException();
            count -= read;
        }
    }
}
