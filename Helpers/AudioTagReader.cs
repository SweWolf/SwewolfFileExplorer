using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// What the tags of a music file say, and what is known about the file itself: its length, its bitrate (kbps; for a video the
/// whole file's, for a music file the audio's) and, for a video or a picture, its width and height. Zero when not known.
/// </summary>
public record AudioTags(string Artist, string Title, uint TrackNumber,
                        TimeSpan Duration = default, int BitrateKbps = 0, int Width = 0, int Height = 0);

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
        HasTags(path) || MediaPlayerLauncher.IsVideo(path) || ThumbnailLoader.IsImage(path);

    /// <summary>
    /// The artist, title and track number of a music file, and its length, bitrate and size in pixels (for the list's columns);
    /// null if nothing is known or the file cannot be read.
    /// </summary>
    public static AudioTags? Read(string path)
    {
        try
        {
            // A Photoshop file: TagLib does not know it, but its header says how big the picture is.
            if (PsdSupport.IsPsd(path))
                return PsdSupport.ReadSize(path) is { } size ? new AudioTags("", "", 0, default, 0, size.Width, size.Height) : null;

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

            return artist.Length == 0 && title.Length == 0 && track == 0 && duration == TimeSpan.Zero && kbps == 0 && width == 0
                ? null
                : new AudioTags(artist, title, track, duration, kbps, width, height);
        }
        catch
        {
            return null;   // not a file TagLib knows, damaged, locked or gone: the cells stay empty
        }
    }

    /// <summary>The picture embedded in a music file (the front cover if there is one, else the first picture); null if none.</summary>
    public static byte[]? ReadCover(string path)
    {
        try
        {
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
