using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// A better name for a music or video file, as TeraTool's "Smart rename" suggested it (spec section 8.2):
/// a music file with an artist and a title in its tags becomes <c>Artist - Title.mp3</c>; a video whose name holds a
/// year (1900-2199) and/or an episode number (<c>s01e02</c>) becomes <c>Title (Year).mkv</c> or <c>Title - s01e02.mkv</c>
/// (both: <c>Title (Year) - s01e02.mkv</c>). Only a suggestion: the user sees it in the Rename box and can undo it.
/// </summary>
public static class SmartName
{
    // The year is a number of four digits that is not part of a longer number; the episode is sNNeNN in any case.
    private static readonly Regex YearRx    = new(@"(?<!\d)(19\d\d|20\d\d|21\d\d)(?!\d)", RegexOptions.Compiled);
    private static readonly Regex EpisodeRx = new(@"(?<![a-z0-9])s(\d{1,2})e(\d{1,3})(?![a-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Whether a file of this name can have a smart name at all: a video, or a music file whose tags are read.</summary>
    public static bool Applies(string fileName) => MediaPlayerLauncher.IsVideo(fileName) || AudioTagReader.HasTags(fileName);

    /// <summary>The suggested file name (with its extension) for a file on the disk; null when there is nothing better to offer.</summary>
    public static string? Suggest(string path)
    {
        var name = Path.GetFileName(path);
        var ext  = Path.GetExtension(name);
        var baseName = Path.GetFileNameWithoutExtension(name);

        string? suggested = null;
        if (MediaPlayerLauncher.IsVideo(name))
            suggested = SuggestVideoBase(baseName);
        else if (AudioTagReader.HasTags(name))
        {
            var tags = AudioTagReader.Read(path);
            if (tags != null && tags.Artist.Length > 0 && tags.Title.Length > 0)
                suggested = Clean($"{tags.Artist} - {tags.Title}");
        }

        if (string.IsNullOrWhiteSpace(suggested)) return null;
        var result = suggested + ext;
        return string.Equals(result, name, StringComparison.Ordinal) ? null : result;
    }

    /// <summary>The new name (without extension) for a video's name (without extension); null when it has no year or episode.</summary>
    public static string? SuggestVideoBase(string baseName)
    {
        // The episode first: a year before it belongs to the show ("Show.2019.S01E02").
        var episode = EpisodeRx.Match(baseName);

        // The last year with something in front of it: "2001.A.Space.Odyssey.1968" gives 1968, "1917.2019" gives 2019.
        Match? year = null;
        foreach (Match m in YearRx.Matches(baseName))
            if (m.Index > 0 && (!episode.Success || m.Index < episode.Index)) year = m;

        if (year == null && !episode.Success) return null;

        var cut = int.MaxValue;
        if (year != null)       cut = Math.Min(cut, year.Index);
        if (episode.Success)    cut = Math.Min(cut, episode.Index);

        var title = Regex.Replace(baseName[..cut].Replace('.', ' ').Replace('_', ' '), @"\s+", " ").Trim(' ', '-', '(', ')', '[', ']');
        if (title.Length == 0) return null;

        var result = title;
        if (year != null)       result += $" ({year.Value})";
        if (episode.Success)    result += $" - s{int.Parse(episode.Groups[1].Value):00}e{int.Parse(episode.Groups[2].Value):00}";
        return Clean(result);
    }

    private static readonly Regex TvNameRx    = new(@"^(?<title>.+?)(?: \((?:19|20|21)\d\d\))? - s\d{2}e\d{2,3}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MovieNameRx = new(@"^.+ \((?:19|20|21)\d\d\)$", RegexOptions.Compiled);

    /// <summary>
    /// Where a video with this name (without extension) belongs in the Videos folder, as TeraTool did after a smart rename:
    /// "Title - s01e02" goes to <c>TV Shows\Title</c> (a folder for the show), "Title (Year)" to <c>Movies</c> itself (no folder of
    /// its own: <c>FolderName</c> is empty); null for any other name.
    /// </summary>
    public static (string Subfolder, string FolderName)? MoveTarget(string baseName)
    {
        var tv = TvNameRx.Match(baseName);
        if (tv.Success) return ("TV Shows", Clean(tv.Groups["title"].Value));
        if (MovieNameRx.IsMatch(baseName)) return ("Movies", "");
        return null;
    }

    /// <summary>Leaves out what a file name cannot hold.</summary>
    private static string Clean(string text)
    {
        var bad = Path.GetInvalidFileNameChars();
        return Regex.Replace(string.Concat(text.Where(c => Array.IndexOf(bad, c) < 0)), @"\s+", " ").Trim();
    }

    // ── Case buttons of the Rename box ────────────────────────────────────────

    public static string ToUpper(string text)  => text.ToUpper(CultureInfo.CurrentCulture);
    public static string ToLower(string text)  => text.ToLower(CultureInfo.CurrentCulture);
    public static string ToProper(string text) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower(CultureInfo.CurrentCulture));
}
