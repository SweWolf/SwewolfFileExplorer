using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The files that belong to a video or music file by their names: <c>Movie-poster.png</c>, <c>Movie.srt</c>, <c>Movie.en.srt</c>,
/// <c>Movie.eng.forced.srt</c>, <c>Movie.nfo</c> belong to <c>Movie.mp4</c>. When the video is renamed or moved the program asks
/// whether these follow it (all or none).
///
/// A file belongs to a video when, in the same folder, its name starts with the video's name (without the extension), followed
/// by a separator (. - space _). Never related: other video or music files, folders, and a file that fits another video in the
/// folder better (<c>Movie 2.srt</c> belongs to <c>Movie 2.mp4</c>, not to <c>Movie.mp4</c>) or fits two videos equally (<c>Movie.mp4</c>
/// and <c>Movie.mkv</c> both have <c>Movie.srt</c>: it is left alone, as it is not clear whom it follows).
/// </summary>
public static class RelatedFiles
{
    private static readonly char[] Separators = { '.', '-', ' ', '_' };

    /// <summary>Only video and music files have related files.</summary>
    public static bool HasRelated(string path) => MediaPlayerLauncher.IsMedia(path);

    /// <summary>The full paths of the files that belong to the video or music file <paramref name="mediaPath"/>.</summary>
    public static List<string> Find(string mediaPath)
    {
        var result = new List<string>();
        try
        {
            var folder = Path.GetDirectoryName(mediaPath);
            if (string.IsNullOrEmpty(folder) || !HasRelated(mediaPath) || !Directory.Exists(folder)) return result;

            var baseName = Path.GetFileNameWithoutExtension(mediaPath);
            if (baseName.Length == 0) return result;

            var files = Directory.GetFiles(folder);
            var mediaBases = files.Where(HasRelated)
                                  .Select(f => Path.GetFileNameWithoutExtension(f))
                                  .ToList();

            foreach (var file in files)
            {
                if (string.Equals(file, mediaPath, StringComparison.OrdinalIgnoreCase) || HasRelated(file)) continue;

                var name = Path.GetFileName(file);
                if (!Follows(name, baseName)) continue;

                // The longest video name that this file starts with is its owner; another video with the same name is a tie.
                int longest = mediaBases.Where(b => Follows(name, b)).Select(b => b.Length).DefaultIfEmpty(0).Max();
                int owners  = mediaBases.Count(b => b.Length == longest && Follows(name, b));
                if (longest == baseName.Length && owners == 1) result.Add(file);
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // a folder that cannot be read has no related files
        }
        return result;
    }

    private static bool Follows(string fileName, string baseName) =>
        fileName.Length > baseName.Length
        && fileName.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)
        && Array.IndexOf(Separators, fileName[baseName.Length]) >= 0;

    /// <summary>
    /// The new name of a related file when its video gets a new name: the new name without extension, followed by what came
    /// after the old name (<c>Movie.en.srt</c> + <c>Movie (2010).mp4</c> gives <c>Movie (2010).en.srt</c>).
    /// </summary>
    public static string NewName(string relatedFileName, string oldMediaName, string newMediaName) =>
        Path.GetFileNameWithoutExtension(newMediaName) + relatedFileName.Substring(Path.GetFileNameWithoutExtension(oldMediaName).Length);

    /// <summary>
    /// What the question shows: one line per file with its present name, at most <paramref name="maxLines"/> of them.
    /// Files whose new name is taken already are listed apart (by the name they would get); they are left as they are.
    /// </summary>
    public static string Describe(IReadOnlyList<string> names, IReadOnlyList<string> skipped, int maxLines = 14)
    {
        var lines = names.Take(maxLines).Select(n => "    " + n).ToList();
        if (names.Count > maxLines) lines.Add($"    ... and {names.Count - maxLines} more");

        var text = string.Join("\n", lines);
        if (skipped.Count > 0)
            text += (names.Count > 0 ? "\n\n" : "") + "These are left as they are, as the new name is taken already:\n"
                  + string.Join("\n", skipped.Take(maxLines).Select(s => "    " + s));
        return text;
    }
}
