using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>The JPEG found inside a RAW file, and how the camera held the camera (EXIF orientation 1-8; 1 = as is).</summary>
public sealed record RawPreviewImage(byte[] Jpeg, int Orientation, int Width, int Height);

/// <summary>
/// Shows a RAW photo (Nikon NEF) without a RAW decoder: every such file is a TIFF file that also carries ready-made JPEG
/// pictures (a thumbnail, a preview, often a large one); the biggest of them is shown. The file is walked through its
/// TIFF structure (the directories, the sub-directories, the EXIF directory and Nikon's maker note, which holds the
/// preview directory), only the bytes needed are read, and nothing is converted or written. The camera's orientation is
/// in the first directory of the RAW file, not in the JPEG, so it is returned too.
///
/// Only the extensions in <see cref="Extensions"/> are taken as RAW: NEF and DNG were tested with the user's files (the DNG:
/// a Canon "linear" DNG, 3456x2304, whose biggest JPEG is a 1024x683 preview in a SubIFD); the others that are built the
/// same way (NRW, CR2, ARW, ORF, PEF ...) can be added to the list once there are files to try (RW2 is not a plain TIFF).
/// A JPEG that is really the sensor data in a lossless wrapper (SOF3: Canon CR2, DNG) is skipped: it is not a picture.
/// A DNG whose raw data is a tiled lossy JPEG ("Linear Raw") is skipped the same way: it is in tiles, not one JPEG.
/// </summary>
public static class RawPreview
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".nef", ".dng" };

    public static bool IsRaw(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>The biggest JPEG in the RAW file, or null if there is none (or the file is not a TIFF-based RAW file).</summary>
    public static RawPreviewImage? Read(Stream stream)
    {
        try
        {
            if (!stream.CanSeek)
            {
                var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                stream = copy;
            }

            var walk = new Walk(stream);
            if (!walk.Run()) return null;

            Candidate? best = null;
            foreach (var candidate in walk.Candidates)
            {
                var info = walk.Inspect(candidate);
                if (info == null) continue;

                var c = candidate with { Width = info.Value.Width, Height = info.Value.Height };
                if (best == null || (long)c.Width * c.Height > (long)best.Width * best.Height
                    || ((long)c.Width * c.Height == (long)best.Width * best.Height && c.Length > best.Length))
                    best = c;
            }
            if (best == null) return null;

            var jpeg = walk.ReadBytes(best.Offset, best.Length);
            return new RawPreviewImage(jpeg, walk.Orientation, best.Width, best.Height);
        }
        catch
        {
            return null;   // damaged, cut off, or not what it says
        }
    }

    /// <summary>
    /// The size of the photo itself (not of the little JPEGs in the file), for the Dimensions column: the EXIF "pixel dimensions" when the
    /// file has them (a NEF: 6016 x 4000), else the biggest full-resolution directory (a DNG: 3456 x 2304). Null if it cannot be told.
    /// </summary>
    public static (int Width, int Height)? ReadSize(Stream stream)
    {
        try
        {
            var walk = new Walk(stream);
            if (!walk.Run()) return null;
            // A DNG says what the finished picture is (the sensor is a little bigger: masked borders).
            if (walk.CropSize is { } crop) return crop;

            var full = walk.ExifSize ?? walk.FullSize;
            if (full == null) return null;

            // A camera JPEG built into the file that is nearly as big as the sensor data is the camera's own output (a NEF: 6016 x 4000
            // inside a 6080 x 4012 sensor): that is the size of the photo.
            long fullArea = (long)full.Value.Width * full.Value.Height;
            foreach (var candidate in walk.Candidates)
            {
                if (walk.Inspect(candidate) is not { } jpeg) continue;
                long area = (long)jpeg.Width * jpeg.Height;
                if (area <= fullArea && area * 100 >= fullArea * 90) return jpeg;
            }
            return full;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>For tests and diagnostics: every JPEG that can be shown in the file, with its place and size in pixels.</summary>
    public static IReadOnlyList<(long Offset, int Length, int Width, int Height)> ListJpegs(Stream stream)
    {
        var result = new List<(long, int, int, int)>();
        try
        {
            var walk = new Walk(stream);
            if (!walk.Run()) return result;
            foreach (var candidate in walk.Candidates)
                if (walk.Inspect(candidate) is { } info) result.Add((candidate.Offset, candidate.Length, info.Width, info.Height));
        }
        catch { }
        return result;
    }

    private sealed record Candidate(long Offset, int Length, int Width = 0, int Height = 0);

    // Reads a TIFF file (and the TIFF inside Nikon's maker note) by seeking: nothing is read that is not needed.
    private sealed class Walk
    {
        private readonly Stream _s;
        private bool _little;
        private readonly HashSet<long> _seen = new();

        public List<Candidate> Candidates { get; } = new();
        public int Orientation { get; private set; } = 1;

        /// <summary>PixelXDimension / PixelYDimension of the EXIF directory: the size of the finished photo.</summary>
        public (int Width, int Height)? ExifSize { get; private set; }

        /// <summary>The biggest directory that is the full-resolution picture (not a reduced copy), by its own width and length tags.</summary>
        public (int Width, int Height)? FullSize { get; private set; }

        /// <summary>DNG DefaultCropSize: the size of the finished picture.</summary>
        public (int Width, int Height)? CropSize { get; private set; }

        public Walk(Stream s) => _s = s;

        public bool Run()
        {
            if (_s.Length < 16) return false;

            var header = ReadBytes(0, 8);
            if (header[0] == 'I' && header[1] == 'I') _little = true;
            else if (header[0] == 'M' && header[1] == 'M') _little = false;
            else return false;
            if (U16(2) != 42) return false;   // not a TIFF (Panasonic RW2 and a few others differ: not supported)

            ParseIfd(U32(4), 0, isFirst: true, nikonPreview: false, depth: 0);
            return true;
        }

        // ── Reading numbers ──────────────────────────────────────────────────

        public byte[] ReadBytes(long position, int count)
        {
            if (position < 0 || count < 0 || position + count > _s.Length) throw new EndOfStreamException();
            var buffer = new byte[count];
            _s.Seek(position, SeekOrigin.Begin);
            _s.ReadExactly(buffer);
            return buffer;
        }

        private ushort U16(long position)
        {
            var b = ReadBytes(position, 2);
            return _little ? (ushort)(b[0] | b[1] << 8) : (ushort)(b[0] << 8 | b[1]);
        }

        private uint U32(long position)
        {
            var b = ReadBytes(position, 4);
            return _little
                ? (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24)
                : (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        }

        private record struct Entry(ushort Tag, ushort Type, uint Count, long ValuePosition);

        // The values of a SHORT or LONG entry (inline when they fit in four bytes, otherwise at the offset in the entry).
        private uint[] Values(Entry e, long baseOffset)
        {
            int size = e.Type switch { 3 => 2, 4 or 13 => 4, _ => 0 };
            if (size == 0 || e.Count == 0 || e.Count > 4096) return Array.Empty<uint>();

            long at = (long)e.Count * size <= 4 ? e.ValuePosition : baseOffset + U32(e.ValuePosition);
            var values = new uint[e.Count];
            for (int i = 0; i < values.Length; i++)
                values[i] = size == 2 ? U16(at + i * 2) : U32(at + i * 4L);
            return values;
        }

        // SHORT, LONG or RATIONAL values as whole numbers (a rational is divided, rounded).
        private double[] Numbers(Entry e, long baseOffset)
        {
            if (e.Type != 5 || e.Count == 0 || e.Count > 16)
                return Values(e, baseOffset).Select(v => (double)v).ToArray();

            long at = baseOffset + U32(e.ValuePosition);
            var result = new double[e.Count];
            for (int i = 0; i < result.Length; i++)
            {
                double n = U32(at + i * 8L), d = U32(at + i * 8L + 4);
                result[i] = d == 0 ? 0 : Math.Round(n / d);
            }
            return result;
        }

        // ── The directories ──────────────────────────────────────────────────

        private void ParseIfd(long offset, long baseOffset, bool isFirst, bool nikonPreview, int depth)
        {
            long position = baseOffset + offset;
            if (depth > 6 || offset == 0 || position + 2 > _s.Length || !_seen.Add(position)) return;

            int count = U16(position);
            if (count == 0 || count > 2000 || position + 2 + count * 12L > _s.Length) return;

            uint? jpegOffset = null, jpegLength = null, compression = null, width = null, height = null, subFileType = null, exifW = null, exifH = null;
            uint[] stripOffsets = Array.Empty<uint>(), stripCounts = Array.Empty<uint>();
            var subIfds = new List<(long Offset, long Base, bool NikonPreview)>();

            for (int i = 0; i < count; i++)
            {
                long entryAt = position + 2 + i * 12L;
                var e = new Entry(U16(entryAt), U16(entryAt + 2), U32(entryAt + 4), entryAt + 8);

                switch (e.Tag)
                {
                    case 0x0112 when isFirst:                                 // Orientation of the photo (first directory)
                        var o = Values(e, baseOffset);
                        if (o.Length > 0 && o[0] is >= 1 and <= 8) Orientation = (int)o[0];
                        break;
                    case 0x00FE: subFileType  = Values(e, baseOffset).FirstOrDefault(); break;   // 0 = the full-resolution picture, 1 = a reduced copy
                    case 0x0100: width        = Values(e, baseOffset).FirstOrDefault(); break;
                    case 0x0101: height       = Values(e, baseOffset).FirstOrDefault(); break;
                    case 0xC620:                                                                 // DNG DefaultCropSize (two numbers)
                        var crop = Numbers(e, baseOffset);
                        if (crop.Length >= 2 && crop[0] > 0 && crop[1] > 0 && CropSize == null) CropSize = ((int)crop[0], (int)crop[1]);
                        break;
                    case 0xA002: exifW        = Values(e, baseOffset).FirstOrDefault(); break;   // EXIF PixelXDimension
                    case 0xA003: exifH        = Values(e, baseOffset).FirstOrDefault(); break;
                    case 0x0103: compression  = Values(e, baseOffset).FirstOrDefault(); break;
                    case 0x0201: jpegOffset   = Values(e, baseOffset).FirstOrDefault(); break;   // JPEGInterchangeFormat
                    case 0x0202: jpegLength   = Values(e, baseOffset).FirstOrDefault(); break;   // ...Length
                    case 0x0111: stripOffsets = Values(e, baseOffset); break;
                    case 0x0117: stripCounts  = Values(e, baseOffset); break;
                    case 0x014A:                                              // SubIFDs: Nikon keeps its big preview and the raw data here
                        foreach (var v in Values(e, baseOffset)) subIfds.Add((v, baseOffset, false));
                        break;
                    case 0x8769:                                              // the EXIF directory (it holds the maker note)
                        foreach (var v in Values(e, baseOffset)) subIfds.Add((v, baseOffset, false));
                        break;
                    case 0x927C when e.Type == 7:                             // the maker note
                        TryNikonMakerNote(e, baseOffset, depth);
                        break;
                    case 0x0011 when nikonPreview && e.Type is 4 or 13:       // inside Nikon's maker note: the preview directory
                        foreach (var v in Values(e, baseOffset)) subIfds.Add((v, baseOffset, false));
                        break;
                }
            }

            if (exifW is > 0 && exifH is > 0 && ExifSize == null) ExifSize = ((int)exifW.Value, (int)exifH.Value);
            if (!nikonPreview && width is > 0 && height is > 0 && (subFileType ?? 0) == 0
                && (FullSize == null || (long)width.Value * height.Value > (long)FullSize.Value.Width * FullSize.Value.Height))
                FullSize = ((int)width.Value, (int)height.Value);

            // A JPEG in this directory: by offset and length, or as the one strip of JPEG-compressed (6 or 7) data.
            if (jpegOffset is { } jo && jpegLength is { } jl)
                AddCandidate(baseOffset + jo, jl);
            else if (compression is 6 or 7 && stripOffsets.Length == 1 && stripCounts.Length == 1)
                AddCandidate(baseOffset + stripOffsets[0], stripCounts[0]);

            foreach (var (subOffset, subBase, preview) in subIfds)
                ParseIfd(subOffset, subBase, isFirst: false, preview, depth + 1);

            // The next directory in the chain (the second one holds a small thumbnail).
            long next = position + 2 + count * 12L;
            if (next + 4 <= _s.Length) ParseIfd(U32(next), baseOffset, isFirst: false, nikonPreview, depth + 1);
        }

        // Nikon's maker note is "Nikon\0", a version, then a TIFF of its own: its offsets count from its own header.
        private void TryNikonMakerNote(Entry e, long baseOffset, int depth)
        {
            if (e.Count < 18) return;
            long start = baseOffset + U32(e.ValuePosition);
            var head = ReadBytes(start, 6);
            if (head[0] != 'N' || head[1] != 'i' || head[2] != 'k' || head[3] != 'o' || head[4] != 'n') return;

            long tiff = start + 10;
            var inner = ReadBytes(tiff, 8);
            bool innerLittle = inner[0] == 'I' && inner[1] == 'I';
            if (!innerLittle && !(inner[0] == 'M' && inner[1] == 'M')) return;

            bool outer = _little;
            _little = innerLittle;
            try
            {
                if (U16(tiff + 2) == 42) ParseIfd(U32(tiff + 4), tiff, isFirst: false, nikonPreview: true, depth + 1);
            }
            finally { _little = outer; }
        }

        private void AddCandidate(long offset, uint length)
        {
            if (length < 200 || length > int.MaxValue || offset < 0 || offset + length > _s.Length) return;
            Candidates.Add(new Candidate(offset, (int)length));
        }

        // ── Is it a JPEG that can be shown, and how big? ─────────────────────

        /// <summary>The size of the picture in a candidate JPEG; null if it is no JPEG or not a normal one (not the lossless kind).</summary>
        public (int Width, int Height)? Inspect(Candidate candidate)
        {
            int count = (int)Math.Min(candidate.Length, 262144);   // the size comes before the picture data; a long EXIF block may come first
            var b = ReadBytes(candidate.Offset, count);
            if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return null;

            int i = 2;
            while (i + 4 <= b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                int marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; }                 // padding
                if (marker is 0x01 or (>= 0xD0 and <= 0xD8)) { i += 2; continue; }   // markers without a length

                int length = b[i + 2] << 8 | b[i + 3];
                if (marker is 0xC0 or 0xC1 or 0xC2)                   // baseline, extended or progressive: a picture
                {
                    if (i + 9 > b.Length) return null;
                    int height = b[i + 5] << 8 | b[i + 6];
                    int width  = b[i + 7] << 8 | b[i + 8];
                    return width > 0 && height > 0 ? (width, height) : null;
                }
                if (marker is 0xC3 or >= 0xC5 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                    return null;                                       // lossless or arithmetic coding: not for us

                if (marker == 0xDA) return null;                       // the picture data began without a size
                i += 2 + length;
            }
            return null;
        }
    }
}
