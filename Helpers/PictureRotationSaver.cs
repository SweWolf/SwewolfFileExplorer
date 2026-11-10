using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Saves a picture that was turned in the viewer. Two kinds of file, each the safest way for it (decided 2026-11-10):
/// <list type="bullet">
/// <item><b>JPEG:</b> only the EXIF Orientation tag is changed (TagLibSharp): no pixel is touched, so nothing is lost and a big photo is saved at once.
/// The viewer, Windows and every common program turn the picture by that tag.</item>
/// <item><b>PNG:</b> a PNG has no orientation that programs honor, so the pixels are turned (exact for quarter turns) and written again, and
/// every other chunk of the old file (the text chunks with an AI picture's prompt and seed, the color profile, the dates) is carried over to the new file.</item>
/// </list>
/// Other types (GIF, WebP, BMP, TIFF, RAW, PSD, SVG ...) are not saved: the turn stays a view only, as before.
/// The new file is made beside the old one under a temporary name, read back and checked (size), and only then moved into place;
/// on any failure the old file is untouched.
/// </summary>
public static class PictureRotationSaver
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Can a turn of this picture be saved? A JPEG or a PNG (by the first bytes) in a file on the disk that is not read-only.</summary>
    public static bool CanSave(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;   // inside an archive
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) return false;
            string ext = Path.GetExtension(path);
            bool jpgName = ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
            bool pngName = ext.Equals(".png", StringComparison.OrdinalIgnoreCase);
            if (!jpgName && !pngName) return false;

            var head = new byte[8];
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (f.Read(head, 0, 8) < 8) return false;
            if (jpgName) return head[0] == 0xFF && head[1] == 0xD8;
            return head.AsSpan().SequenceEqual(PngSignature);
        }
        catch { return false; }
    }

    /// <summary>
    /// Saves the picture turned <paramref name="quarterTurns"/> quarter turns clockwise (relative to how the viewer showed it).
    /// <paramref name="expectedWidth"/> and <paramref name="expectedHeight"/> are the size the viewer shows now; the saved file must give it.
    /// Returns null when done, else a text saying why not. Call from a background thread.
    /// </summary>
    public static string? Save(string path, int quarterTurns, int expectedWidth, int expectedHeight)
    {
        quarterTurns = ((quarterTurns % 4) + 4) % 4;
        if (quarterTurns == 0) return null;
        if (!CanSave(path)) return "This kind of picture cannot be saved after turning it.";

        string temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".swewolf-part");
        try
        {
            string? problem = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? WritePng(path, temp, quarterTurns)
                : WriteJpeg(path, temp, quarterTurns);
            if (problem != null) return problem;

            // Read the new file back: it must give the picture the viewer shows now. (The temporary name has no picture extension,
            // so it is read by content.)
            var check = PictureLoader.Load(temp);
            if (check == null || check.Width != expectedWidth || check.Height != expectedHeight)
                return "The saved picture did not read back as expected, so the file was left as it was.";

            // The new file replaces the old one, so it would get today's creation date (and lose the attributes): the old file's are put back.
            // The modified date is meant to change: the file was changed.
            var created = File.GetCreationTimeUtc(path);
            var attributes = File.GetAttributes(path);
            File.Move(temp, path, overwrite: true);
            try { File.SetCreationTimeUtc(path, created); } catch { }
            try { File.SetAttributes(path, attributes); } catch { }
            return null;
        }
        catch (Exception ex)
        {
            return ex is UnauthorizedAccessException ? "You do not have permission to change this file." : ex.Message;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    // ── JPEG: the Orientation tag ─────────────────────────────────────────────

    // An orientation value as (flip, turns): the picture is mirrored left-right first if flip, then turned clockwise by turns quarter turns
    // (the same meaning as PictureLoader.ApplyOrientation).
    private static (bool Flip, int Turns) Decode(int orientation) => orientation switch
    {
        2 => (true, 0), 3 => (false, 2), 4 => (true, 2), 5 => (true, 3),
        6 => (false, 1), 7 => (true, 1), 8 => (false, 3), _ => (false, 0),
    };

    private static int Encode(bool flip, int turns) => (flip, turns) switch
    {
        (false, 0) => 1, (false, 1) => 6, (false, 2) => 3, (false, 3) => 8,
        (true, 0) => 2, (true, 1) => 7, (true, 2) => 4, _ => 5,
    };

    /// <summary>The orientation value that shows the picture turned <paramref name="quarterTurns"/> more than <paramref name="current"/> does.</summary>
    internal static int Compose(int current, int quarterTurns)
    {
        var (flip, turns) = Decode(current);
        return Encode(flip, (turns + quarterTurns) % 4);
    }

    private static string? WriteJpeg(string path, string temp, int quarterTurns)
    {
        File.Copy(path, temp, overwrite: true);

        // Best: change the two bytes of the Orientation value inside the photo's own EXIF block, and nothing else (found by a test:
        // TagLibSharp writes the whole EXIF block again and lost about 29 KB of a Pentax photo's metadata).
        if (PatchExifOrientation(temp, quarterTurns)) return null;

        // The photo has no Orientation tag yet: TagLibSharp makes one.
        using (var file = TagLib.File.Create(new TagLib.File.LocalFileAbstraction(temp), "taglib/jpg", TagLib.ReadStyle.Average))
        {
            if (file is not TagLib.Image.File image) return "This file is not a picture that can carry an orientation.";

            image.GetTag(TagLib.TagTypes.TiffIFD, true);   // the EXIF block (made if the photo has none)
            int current = (int?)image.ImageTag.Orientation ?? 1;
            image.ImageTag.Orientation = (TagLib.Image.ImageOrientation)Compose(current, quarterTurns);
            image.Save();
        }
        return null;
    }

    /// <summary>
    /// Finds the EXIF block (an APP1 segment near the start of the JPEG), finds the Orientation entry (tag 0x0112) in its first directory
    /// and overwrites its 2-byte value in the file. False when the photo has no such entry (nothing was changed).
    /// </summary>
    private static bool PatchExifOrientation(string file, int quarterTurns)
    {
        using var f = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var b = new byte[(int)Math.Min(f.Length, 1 << 20)];
        int read = f.Read(b, 0, b.Length);
        if (read < 4 || b[0] != 0xFF || b[1] != 0xD8) return false;

        int at = 2;
        while (at + 4 <= read && b[at] == 0xFF)
        {
            byte marker = b[at + 1];
            if (marker == 0xDA || marker == 0xD9) break;   // the picture data begins: no EXIF before it
            if (marker == 0xFF) { at++; continue; }       // padding
            int length = (b[at + 2] << 8) | b[at + 3];
            int body = at + 4;
            int end = at + 2 + length;
            if (length < 2 || end > read) return false;

            if (marker == 0xE1 && end - body >= 14 && b[body] == 'E' && b[body + 1] == 'x' && b[body + 2] == 'i' && b[body + 3] == 'f' && b[body + 4] == 0 && b[body + 5] == 0)
            {
                int tiff = body + 6;
                bool little = b[tiff] == 'I' && b[tiff + 1] == 'I';
                if (!little && !(b[tiff] == 'M' && b[tiff + 1] == 'M')) return false;
                int U16(int p) => little ? b[p] | (b[p + 1] << 8) : (b[p] << 8) | b[p + 1];
                long U32(int p) => little ? (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24))
                                          : (uint)((b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]);

                long ifd = U32(tiff + 4);
                if (ifd < 8 || tiff + ifd + 2 > end) return false;
                int count = U16(tiff + (int)ifd);
                for (int i = 0; i < count; i++)
                {
                    int entry = tiff + (int)ifd + 2 + 12 * i;
                    if (entry + 12 > end) return false;
                    if (U16(entry) != 0x0112) continue;
                    if (U16(entry + 2) != 3 || U32(entry + 4) != 1) return false;   // not a single SHORT: do not guess

                    int current = U16(entry + 8);
                    int value = Compose(current is >= 1 and <= 8 ? current : 1, quarterTurns);
                    var bytes = little ? new[] { (byte)value, (byte)0 } : new[] { (byte)0, (byte)value };
                    f.Seek(entry + 8, SeekOrigin.Begin);
                    f.Write(bytes, 0, 2);
                    return true;
                }
                return false;   // EXIF without an Orientation entry
            }
            at = end;
        }
        return false;
    }

    // ── PNG: turn the pixels, keep the other chunks ───────────────────────────

    private sealed record Chunk(string Type, byte[] Data);

    private static List<Chunk>? ReadChunks(byte[] bytes)
    {
        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(PngSignature)) return null;
        var list = new List<Chunk>();
        int at = 8;
        while (at + 12 <= bytes.Length)
        {
            int length = (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];
            if (length < 0 || at + 12 + length > bytes.Length) return null;
            string type = System.Text.Encoding.ASCII.GetString(bytes, at + 4, 4);
            list.Add(new Chunk(type, bytes.AsSpan(at + 8, length).ToArray()));
            at += 12 + length;
            if (type == "IEND") break;
        }
        return list;
    }

    private static string? WritePng(string path, string temp, int quarterTurns)
    {
        byte[] source = File.ReadAllBytes(path);
        var old = ReadChunks(source);
        if (old == null) return "The PNG file is damaged.";
        if (old.Any(c => c.Type is "acTL" or "fcTL" or "fdAT")) return "An animated PNG cannot be saved after turning it.";

        BitmapSource frame;
        using (var stream = new MemoryStream(source))
            frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var turned = new TransformedBitmap(frame, new RotateTransform(quarterTurns * 90));

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(turned));
        byte[] fresh;
        using (var memory = new MemoryStream()) { encoder.Save(memory); fresh = memory.ToArray(); }
        var made = ReadChunks(fresh);
        if (made == null) return "The turned picture could not be written.";

        // From the new encoding: the header, the palette and transparency, the pixel data. Everything else comes from the old file;
        // what describes the old pixels' layout goes (header, palette, transparency, background, histogram, pixel data).
        var drop = new HashSet<string> { "IHDR", "PLTE", "tRNS", "bKGD", "hIST", "sPLT", "IDAT", "IEND" };
        using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(PngSignature);

        WriteChunk(output, made.First(c => c.Type == "IHDR"));
        foreach (var c in old.Where(c => !drop.Contains(c.Type)))
        {
            if (c.Type == "pHYs" && c.Data.Length == 9 && quarterTurns % 2 == 1)
            {
                var swapped = (byte[])c.Data.Clone();
                Array.Copy(c.Data, 4, swapped, 0, 4);
                Array.Copy(c.Data, 0, swapped, 4, 4);
                WriteChunk(output, new Chunk("pHYs", swapped));
            }
            else WriteChunk(output, c);
        }
        foreach (var c in made.Where(c => c.Type is "PLTE" or "tRNS")) WriteChunk(output, c);
        foreach (var c in made.Where(c => c.Type == "IDAT")) WriteChunk(output, c);
        WriteChunk(output, new Chunk("IEND", Array.Empty<byte>()));
        return null;
    }

    private static void WriteChunk(Stream output, Chunk chunk)
    {
        var length = new byte[4];
        length[0] = (byte)(chunk.Data.Length >> 24); length[1] = (byte)(chunk.Data.Length >> 16);
        length[2] = (byte)(chunk.Data.Length >> 8);  length[3] = (byte)chunk.Data.Length;
        var type = System.Text.Encoding.ASCII.GetBytes(chunk.Type);

        uint crc = Crc32(Crc32(0xFFFFFFFF, type), chunk.Data) ^ 0xFFFFFFFF;
        output.Write(length);
        output.Write(type);
        output.Write(chunk.Data);
        output.Write(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(uint crc, byte[] data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
