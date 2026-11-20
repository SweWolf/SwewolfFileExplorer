using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// What the tags of a music file say, and what is known about the file itself: its length, its bitrate (kbps; for a video the
/// whole file's, for a music file the audio's) and, for a video or a picture, its width and height. Zero when not known.
/// </summary>
public record AudioTags(string Artist, string Title, uint TrackNumber,
                        TimeSpan Duration = default, int BitrateKbps = 0, int Width = 0, int Height = 0,
                        string ProductName = "", string ProductVersion = "", DateTime? DateTaken = null, string Comment = "", string Year = "");

/// <summary>
/// Reads the tags of a music file with TagLibSharp (mp3, m4a, flac, ogg, opus, wma, wav, ...), for the
/// Artist, Title and Track Number columns. It reads only the tags, not the audio, and is safe to call
/// from several background threads at once (each call opens its own file).
///
/// Chosen over the Windows property system after a test on 153 of the user's files (2026-10-15): the same
/// files had tags with both, but Windows gave no artist for FLAC/WMA files that only have an Album Artist,
/// and TagLibSharp was about 6 times faster. It can also read from a stream, which is how tags of files
/// inside archives could be read later.
/// </summary>
public static class AudioTagReader
{
    /// <summary>The file types whose tags are looked at (the others have none worth a column).</summary>
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".flac", ".wma", ".ogg", ".oga", ".opus", ".aac", ".wav", ".aif", ".aiff",
        ".wv", ".mka", ".ape", ".mpc"
    };

    public static bool HasTags(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    /// <summary>A file of a kind the Length, Bitrate and Dimensions columns can say something about: music, video or a picture.</summary>
    public static bool HasMediaInfo(string path) =>
        HasTags(path) || MediaPlayerLauncher.IsVideo(path) || ThumbnailLoader.IsImage(path) || HasVersionInfo(path) || IsCdTrackName(path);

    /// <summary>A .cda name: a track of an audio CD (whether it really is on a CD drive is checked when it is read, by <see cref="CdDiscInfo"/>).</summary>
    public static bool IsCdTrackName(string path) => string.Equals(Path.GetExtension(path), ".cda", StringComparison.OrdinalIgnoreCase);

    /// <summary>Programs and libraries carry a product name and version (the Product name and Product version columns).</summary>
    private static readonly HashSet<string> VersionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".ocx", ".scr", ".cpl", ".drv", ".ax", ".mui"
    };

    public static bool HasVersionInfo(string path) => VersionExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// The artist, title and track number of a music file, and its length, bitrate and size in pixels (for the list's columns);
    /// null if nothing is known or the file cannot be read.
    /// </summary>
    public static AudioTags? Read(string path)
    {
        var tags = ReadCore(path);

        // A picture's Date Taken (EXIF), in the same pass: the file is opened a second time, which is cheap next to the picture's own reading.
        if (GpsReader.CanHaveDateTaken(path) && GpsReader.ReadDateTaken(path) is { } taken)
            tags = tags == null ? new AudioTags("", "", 0, DateTaken: taken) : tags with { DateTaken = taken };

        // A video: the year always comes from its .nfo file (the production / release year as the scraper wrote it), and the title
        // when the setting says so. Other kinds of file have no .nfo of this kind.
        if (MediaPlayerLauncher.IsVideo(path))
        {
            var (nfoTitle, nfoYear) = NfoPoster.TitleAndYear(path);
            if (nfoYear.Length > 0 || (nfoTitle.Length > 0 && AppSettingsStore.Current.TitleFromNfo))
            {
                tags ??= new AudioTags("", "", 0);
                if (nfoYear.Length > 0) tags = tags with { Year = nfoYear };
                if (nfoTitle.Length > 0 && AppSettingsStore.Current.TitleFromNfo) tags = tags with { Title = nfoTitle };
            }
        }
        return tags;
    }

    private static AudioTags? ReadCore(string path)
    {
        try
        {
            // A track of an audio CD: the artist, title, length ... come from the disc's entry in MusicBrainz (kept on this computer), not from the file.
            if (IsCdTrackName(path)) return CdDiscInfo.ReadTags(path);

            // A program or library: the product name and version from its version information (as Explorer's Details columns).
            if (HasVersionInfo(path))
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                var name = (info.ProductName ?? "").Trim();
                var version = (info.ProductVersion ?? "").Trim();
                return name.Length == 0 && version.Length == 0 ? null
                    : new AudioTags("", "", 0, default, 0, 0, 0, name, version);
            }

            // A Photoshop file: TagLib does not know it, but its header says how big the picture is.
            if (PsdSupport.IsPsd(path))
                return PsdSupport.ReadSize(path) is { } size ? new AudioTags("", "", 0, default, 0, size.Width, size.Height) : null;

            // A RAW photo: TagLib reports the little thumbnail (a NEF says 160 x 120, a DNG nothing); the real size is in the TIFF tags.
            if (RawPreview.IsRaw(path))
            {
                using var raw = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return RawPreview.ReadSize(raw) is { } rawSize ? new AudioTags("", "", 0, default, 0, rawSize.Width, rawSize.Height) : null;
            }

            // Average: the tags and the properties of the audio / video / picture (read from the start of the file, not all of it).
            using var file = TagLib.File.Create(new SharedReadFile(path), TagLib.ReadStyle.Average);
            var tag = file.Tag;

            // The artist; many files (an album ripped to flac) only have the album artist, which Explorer shows too.
            var artist = Join(tag.Performers);
            if (artist.Length == 0) artist = Join(tag.AlbumArtists);

            var title = (tag.Title ?? "").Trim();
            var track = tag.Track;

            var props    = file.Properties;
            var duration = props?.Duration ?? TimeSpan.Zero;
            int width    = props?.VideoWidth  > 0 ? props.VideoWidth  : props?.PhotoWidth  ?? 0;
            int height   = props?.VideoHeight > 0 ? props.VideoHeight : props?.PhotoHeight ?? 0;

            // A video's bitrate is the whole file's (its size over its length); a music file's is the audio's.
            int kbps = props?.AudioBitrate ?? 0;
            if (props?.VideoWidth > 0 && duration.TotalSeconds >= 1)
                kbps = (int)Math.Round(new FileInfo(path).Length * 8 / duration.TotalSeconds / 1000);

            var comment = ReadComment(file);

            return artist.Length == 0 && title.Length == 0 && track == 0 && duration == TimeSpan.Zero && kbps == 0 && width == 0 && comment.Length == 0
                ? null
                : new AudioTags(artist, title, track, duration, kbps, width, height, Comment: comment);
        }
        catch
        {
            return null;   // not a file TagLib knows, damaged, locked or gone: the cells stay empty
        }
    }

    /// <summary>
    /// The comment of a file, on one line. An MP4 / M4A keeps it in the <c>©cmt</c> atom, sometimes in several pieces (Suno: "made with suno",
    /// "created=...", "id=...") of which TagLib's <c>Tag.Comment</c> returns only the first: the pieces are joined with "; ".
    /// Other formats: TagLib's comment (ID3 COMM, Vorbis COMMENT ...). Cut at 500 characters (an AI video can hold a long JSON text there).
    /// </summary>
    private static string ReadComment(TagLib.File file)
    {
        string text = "";
        try
        {
            if (file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple)
                text = string.Join("; ", apple.GetText("©cmt").Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
        }
        catch { /* fall back to the plain comment */ }

        if (text.Length == 0) text = file.Tag.Comment ?? "";

        var chars = text.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        text = System.Text.RegularExpressions.Regex.Replace(new string(chars), @"\s{2,}", " ").Trim();
        return text.Length > 500 ? text[..500] + "..." : text;
    }

    /// <summary>The picture embedded in a music file (the front cover if there is one, else the first picture); null if none.</summary>
    public static byte[]? ReadCover(string path)
    {
        try
        {
            if (IsCdTrackName(path)) return CdDiscInfo.ReadCover(path);   // the front cover of the album, the same for every track of the disc

            using var file = TagLib.File.Create(new SharedReadFile(path), TagLib.ReadStyle.None);
            var pictures = file.Tag.Pictures;
            if (pictures == null || pictures.Length == 0) return null;

            var picture = pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover && p.Data.Count > 0)
                       ?? pictures.FirstOrDefault(p => p.Data.Count > 0);
            return picture?.Data.Data;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// TagLib's own file opening shares a file for reading only, so while it reads (a slow drive such as Google Drive can take seconds) the file
    /// cannot be renamed, moved or deleted: "being used by another process". This opens it the way a well-behaved reader does.
    /// </summary>
    private sealed class SharedReadFile : TagLib.File.IFileAbstraction
    {
        public SharedReadFile(string path) => Name = path;

        public string Name { get; }

        public Stream ReadStream => new FileStream(Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        public Stream WriteStream => throw new NotSupportedException("The explorer never writes tags.");

        public void CloseStream(Stream stream) => stream.Dispose();
    }

    private static string Join(IEnumerable<string>? names) =>
        string.Join("; ", (names ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()));
}
