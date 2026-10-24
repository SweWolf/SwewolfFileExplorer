using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SwewolfFileExplorer.Helpers;

/// <summary>A place on Earth in decimal degrees (north and east are positive).</summary>
public readonly record struct GpsPosition(double Latitude, double Longitude)
{
    private static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>"59.3293, 18.0686": the form Google Maps and most map programs take in a search box.</summary>
    public string Text => $"{Num(Latitude)}, {Num(Longitude)}";

    /// <summary>The address that shows the place on Google Maps (opened only when the user clicks the symbol).</summary>
    public string MapsUrl => $"https://www.google.com/maps?q={Num(Latitude)},{Num(Longitude)}";
}

/// <summary>
/// Where a picture or a video was taken, for the map symbol in the info panel. Read in the background, only for the selected file.
///   Pictures: the EXIF block's GPS directory (tag 0x8825: latitude, longitude and their N/S, E/W letters): JPEG (APP1), TIFF and the RAW
///   formats built on it (DNG, NEF, CR2, ARW ...: the file itself is a TIFF), PNG (eXIf chunk), WebP (EXIF chunk), HEIC / HEIF / AVIF
///   (the Exif item, found by its "Exif" header in the first 8 MB).
///   Videos (MP4, M4V, MOV, 3GP): the ISO 6709 text ("+59.3293+018.0686/") that phones write into the movie header (the "©xyz" atom, or
///   Apple's "com.apple.quicktime.location.ISO6709").
/// Nothing is written, and nothing leaves the computer. TagLibSharp was not used: it cannot read HEIC, the format of an iPhone's pictures.
/// History:
///   2026-10-24  First version (SweWolf File Explorer only).
/// </summary>
public static class GpsReader
{
    private static readonly HashSet<string> TiffExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tif", ".tiff", ".dng", ".nef", ".nrw", ".cr2", ".arw", ".orf", ".pef", ".srw"
    };
    private static readonly HashSet<string> JpegExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jpe", ".jfif" };
    private static readonly HashSet<string> HeifExtensions = new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif", ".avif" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".3gp", ".3g2" };

    private const int MaxMoovSize = 32 * 1024 * 1024;
    private const int HeifSearchSize = 8 * 1024 * 1024;

    /// <summary>Which files can hold a position (decided by the extension only; nothing is read yet).</summary>
    public static bool CanHave(string path)
    {
        var ext = Path.GetExtension(path);
        return JpegExtensions.Contains(ext) || TiffExtensions.Contains(ext) || HeifExtensions.Contains(ext) || VideoExtensions.Contains(ext)
            || ext.Equals(".png", StringComparison.OrdinalIgnoreCase) || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    public static GpsPosition? Read(string path)
    {
        try
        {
            var ext = Path.GetExtension(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (JpegExtensions.Contains(ext)) return FromJpeg(stream);
            if (TiffExtensions.Contains(ext)) return FromTiff(stream, 0);
            if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)) return FromPng(stream);
            if (ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)) return FromWebP(stream);
            if (HeifExtensions.Contains(ext)) return FromHeif(stream);
            if (VideoExtensions.Contains(ext)) return FromVideo(stream);
            return null;
        }
        catch
        {
            return null;   // damaged, locked or gone: no symbol
        }
    }

    // ── Pictures ────────────────────────────────────────────────────────────

    /// <summary>JPEG: the APP1 segments before the picture data; the one that starts "Exif\0\0" holds the TIFF block.</summary>
    private static GpsPosition? FromJpeg(Stream s)
    {
        var b = new byte[6];
        if (s.Read(b, 0, 2) != 2 || b[0] != 0xFF || b[1] != 0xD8) return null;

        while (s.Position + 4 <= s.Length)
        {
            int c;
            do { c = s.ReadByte(); } while (c != 0xFF && c >= 0);
            if (c < 0) return null;
            int marker;
            do { marker = s.ReadByte(); } while (marker == 0xFF);
            if (marker < 0 || marker == 0xD9 || marker == 0xDA) return null;   // end of the picture / start of its data
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;   // markers with no length

            if (s.Read(b, 0, 2) != 2) return null;
            int length = (b[0] << 8) | b[1];
            if (length < 2) return null;
            long next = s.Position - 2 + length;

            if (marker == 0xE1 && length >= 8 && s.Read(b, 0, 6) == 6
                && b[0] == 'E' && b[1] == 'x' && b[2] == 'i' && b[3] == 'f' && b[4] == 0 && b[5] == 0
                && FromTiff(s, s.Position) is { } found)
                return found;

            s.Position = next;
        }
        return null;
    }

    /// <summary>PNG: the eXIf chunk (the TIFF block itself).</summary>
    private static GpsPosition? FromPng(Stream s)
    {
        var h = new byte[8];
        if (s.Read(h, 0, 8) != 8 || h[0] != 0x89 || h[1] != 'P' || h[2] != 'N' || h[3] != 'G') return null;

        while (s.Position + 12 <= s.Length)
        {
            if (s.Read(h, 0, 8) != 8) return null;
            long length = ((long)h[0] << 24) | ((long)h[1] << 16) | ((long)h[2] << 8) | h[3];
            var type = Encoding.ASCII.GetString(h, 4, 4);
            long dataStart = s.Position;
            if (type == "eXIf") return FromTiff(s, dataStart);
            if (type == "IEND") return null;
            s.Position = dataStart + length + 4;   // data + CRC
        }
        return null;
    }

    /// <summary>WebP: a RIFF file; the EXIF chunk holds the TIFF block (some writers put "Exif\0\0" in front of it).</summary>
    private static GpsPosition? FromWebP(Stream s)
    {
        var h = new byte[12];
        if (s.Read(h, 0, 12) != 12 || Encoding.ASCII.GetString(h, 0, 4) != "RIFF" || Encoding.ASCII.GetString(h, 8, 4) != "WEBP") return null;

        while (s.Position + 8 <= s.Length)
        {
            if (s.Read(h, 0, 8) != 8) return null;
            var type = Encoding.ASCII.GetString(h, 0, 4);
            long length = h[4] | ((long)h[5] << 8) | ((long)h[6] << 16) | ((long)h[7] << 24);
            long dataStart = s.Position;
            if (type == "EXIF")
            {
                var prefix = new byte[6];
                int got = s.Read(prefix, 0, 6);
                bool exifPrefix = got == 6 && prefix[0] == 'E' && prefix[1] == 'x' && prefix[2] == 'i' && prefix[3] == 'f';
                return FromTiff(s, exifPrefix ? dataStart + 6 : dataStart);
            }
            s.Position = dataStart + length + (length & 1);   // chunks are padded to an even size
        }
        return null;
    }

    /// <summary>HEIC / HEIF / AVIF: the Exif item starts "Exif\0\0" and the TIFF block follows; found by searching the start of the file.</summary>
    private static GpsPosition? FromHeif(Stream s)
    {
        var buffer = new byte[(int)Math.Min(s.Length, HeifSearchSize)];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }

        var memory = new MemoryStream(buffer, 0, read, writable: false);
        ReadOnlySpan<byte> marker = "Exif\0\0"u8;
        int from = 0;
        while (from < read - 8)
        {
            int at = buffer.AsSpan(from, read - from).IndexOf(marker);
            if (at < 0) return null;
            if (FromTiff(memory, from + at + 6) is { } found) return found;
            from += at + 1;
        }
        return null;
    }

    // ── The TIFF block (EXIF) ───────────────────────────────────────────────

    /// <summary>
    /// Reads the GPS directory of the TIFF block that starts at <paramref name="start"/>: header (II or MM, 42, offset of the first
    /// directory), the first directory's pointer tag 0x8825, and in the GPS directory the tags 1 and 3 (the N/S and E/W letters) and
    /// 2 and 4 (degrees, minutes and seconds as three fractions). Null if there is no usable position.
    /// </summary>
    private static GpsPosition? FromTiff(Stream s, long start)
    {
        var head = new byte[8];
        s.Position = start;
        if (s.Read(head, 0, 8) != 8) return null;

        bool little;
        if (head[0] == 'I' && head[1] == 'I') little = true;
        else if (head[0] == 'M' && head[1] == 'M') little = false;
        else return null;
        if (U16(head, 2, little) != 42) return null;

        long ifd0 = U32(head, 4, little);
        long gpsIfd = 0;
        foreach (var e in ReadDirectory(s, start + ifd0, little))
            if (e.Tag == 0x8825) gpsIfd = U32(e.Field, 0, little);   // a LONG: the offset of the GPS directory
        if (gpsIfd == 0) return null;

        char latRef = '\0', lonRef = '\0';
        double[]? lat = null, lon = null;
        foreach (var e in ReadDirectory(s, start + gpsIfd, little))
        {
            switch (e.Tag)
            {
                case 1: latRef = (char)e.Field[0]; break;
                case 3: lonRef = (char)e.Field[0]; break;
                case 2: lat = ReadDegrees(s, start, e, little); break;
                case 4: lon = ReadDegrees(s, start, e, little); break;
            }
        }
        if (lat == null || lon == null) return null;

        double latitude = lat[0] + lat[1] / 60 + lat[2] / 3600;
        double longitude = lon[0] + lon[1] / 60 + lon[2] / 3600;
        if (latRef is 'S' or 's') latitude = -latitude;
        if (lonRef is 'W' or 'w') longitude = -longitude;
        return Valid(latitude, longitude);
    }

    private readonly record struct Entry(int Tag, int Type, long Count, byte[] Field);

    /// <summary>The entries of one directory: a count, then 12 bytes each (tag, type, count, and 4 bytes that are the value or its offset).</summary>
    private static List<Entry> ReadDirectory(Stream s, long position, bool little)
    {
        var result = new List<Entry>();
        if (position < 0 || position + 2 > s.Length) return result;
        s.Position = position;

        var n = new byte[2];
        if (s.Read(n, 0, 2) != 2) return result;
        int count = U16(n, 0, little);
        if (count == 0 || count > 500) return result;

        var raw = new byte[count * 12];
        if (s.Read(raw, 0, raw.Length) != raw.Length) return result;
        for (int i = 0; i < count; i++)
        {
            int o = i * 12;
            result.Add(new Entry(U16(raw, o, little), U16(raw, o + 2, little), U32(raw, o + 4, little), raw[(o + 8)..(o + 12)]));
        }
        return result;
    }

    /// <summary>Three fractions (degrees, minutes, seconds) at the offset the entry holds; null if it is not that.</summary>
    private static double[]? ReadDegrees(Stream s, long tiffStart, Entry e, bool little)
    {
        if (e.Type != 5 || e.Count < 1 || e.Count > 3) return null;   // RATIONAL: two 32-bit numbers each
        s.Position = tiffStart + U32(e.Field, 0, little);
        var raw = new byte[8 * e.Count];
        if (s.Read(raw, 0, raw.Length) != raw.Length) return null;

        var parts = new double[3];
        for (int i = 0; i < e.Count; i++)
        {
            double numerator = U32(raw, i * 8, little), denominator = U32(raw, i * 8 + 4, little);
            parts[i] = denominator == 0 ? 0 : numerator / denominator;
        }
        return parts;
    }

    private static int U16(byte[] b, int o, bool little) => little ? b[o] | (b[o + 1] << 8) : (b[o] << 8) | b[o + 1];

    private static long U32(byte[] b, int o, bool little) => little
        ? b[o] | ((long)b[o + 1] << 8) | ((long)b[o + 2] << 16) | ((long)b[o + 3] << 24)
        : ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];

    private static GpsPosition? Valid(double latitude, double longitude) =>
        Math.Abs(latitude) <= 90 && Math.Abs(longitude) <= 180 && !(latitude == 0 && longitude == 0)
            ? new GpsPosition(latitude, longitude)
            : null;   // out of range, or the 0, 0 that a camera without a fix writes

    // ── Videos ──────────────────────────────────────────────────────────────

    // "©xyz" (Android and many cameras, in the movie header's user data, or an iTunes-style item): a few bytes of header, then +59.3293+018.0686/
    private static readonly Regex XyzPattern = new(@"©xyz[\s\S]{0,32}?([+-]\d{1,2}\.\d+)([+-]\d{1,3}\.\d+)", RegexOptions.Compiled);
    // Apple's "com.apple.quicktime.location.ISO6709" (and other writers' plain "location" item): the same text, ended by "/"
    private static readonly Regex Iso6709Pattern = new(@"([+-]\d{1,2}\.\d+)([+-]\d{1,3}\.\d+)(?:[+-]\d+(?:\.\d+)?)?(?:CRS\w+)?/", RegexOptions.Compiled);

    /// <summary>MP4 / MOV: the top-level boxes are skipped with seeks until "moov" (the movie header); the text is looked for in it.</summary>
    private static GpsPosition? FromVideo(Stream s)
    {
        var header = new byte[16];
        while (s.Position + 8 <= s.Length)
        {
            long start = s.Position;
            if (s.Read(header, 0, 8) != 8) return null;
            long size = U32(header, 0, little: false);
            var type = Encoding.Latin1.GetString(header, 4, 4);
            int headerSize = 8;
            if (size == 1)
            {
                if (s.Read(header, 8, 8) != 8) return null;
                size = (U32(header, 8, false) << 32) | U32(header, 12, false);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = s.Length - start;
            }
            if (size < headerSize) return null;

            if (type == "moov")
            {
                long bodySize = size - headerSize;
                if (bodySize > MaxMoovSize || start + size > s.Length) return null;
                var body = new byte[bodySize];
                if (s.Read(body, 0, body.Length) != body.Length) return null;
                return FromMovieHeader(Encoding.Latin1.GetString(body));
            }
            s.Position = start + size;
        }
        return null;
    }

    private static GpsPosition? FromMovieHeader(string text)
    {
        if (text.Contains('©') && XyzPattern.Match(text) is { Success: true } xyz
            && Parse(xyz.Groups[1].Value, xyz.Groups[2].Value) is { } a) return a;

        if ((text.Contains("ISO6709", StringComparison.Ordinal) || text.Contains("location", StringComparison.Ordinal)) && Iso6709Pattern.Match(text) is { Success: true } iso
            && Parse(iso.Groups[1].Value, iso.Groups[2].Value) is { } b) return b;

        return null;
    }

    private static GpsPosition? Parse(string latitude, string longitude) =>
        double.TryParse(latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
        && double.TryParse(longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            ? Valid(lat, lon)
            : null;
}
