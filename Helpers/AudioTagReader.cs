using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>What the tags of a music file say.</summary>
public record AudioTags(string Artist, string Title, uint TrackNumber);

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

    /// <summary>The artist, title and track number of a music file; null if it has none or cannot be read.</summary>
    public static AudioTags? Read(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.None);   // None: the tags only, not the audio properties
            var tag = file.Tag;

            // The artist; many files (an album ripped to flac) only have the album artist, which Explorer shows too.
            var artist = Join(tag.Performers);
            if (artist.Length == 0) artist = Join(tag.AlbumArtists);

            var title = (tag.Title ?? "").Trim();
            var track = tag.Track;

            return artist.Length == 0 && title.Length == 0 && track == 0
                ? null
                : new AudioTags(artist, title, track);
        }
        catch
        {
            return null;   // not a file TagLib knows, damaged, locked or gone: the cells stay empty
        }
    }

    private static string Join(IEnumerable<string>? names) =>
        string.Join("; ", (names ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()));
}
