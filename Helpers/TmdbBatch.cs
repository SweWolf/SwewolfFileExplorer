using System.Globalization;
using System.IO;
using System.Text;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// "Download NFO File" for a whole folder: every video file (or, for TV shows, every folder) that has no .nfo file yet is looked up on TMDB by
/// the name it has on the disk, and the .nfo is saved only when the answer is certain: exactly ONE hit has the same title (and the same
/// year, when the name has one). Several equal hits, or none, mean the item is skipped and listed afterwards: a wrong .nfo is worse than none.
/// Only the folder itself is looked at, not its subfolders. A TMDB problem that would hit every item (no key, a refused key, no network,
/// too many requests) stops the whole run.
/// </summary>
public static class TmdbBatch
{
    /// <summary>A file or a folder to look up.</summary>
    public sealed record Item(string Path, bool IsFolder);

    public sealed record Skipped(string Name, string Reason);

    public sealed class Outcome
    {
        public List<string> Saved { get; } = new();
        public List<Skipped> Skipped { get; } = new();
        public int Posters { get; set; }
        public string? Fatal { get; set; }
        public bool Canceled { get; set; }
        public int Done { get; set; }
    }

    /// <summary>
    /// What the run would look at. <paramref name="path"/> is the file or the folder the dialog was opened on. For a file: the video files in its
    /// folder. For a folder: its subfolders when the type is TV Show, the video files in it when it is Movie, both when it is Not Sure.
    /// Anything that already has a .nfo file is left out.
    /// </summary>
    public static List<Item> ItemsFor(string path, bool pathIsFolder, Tmdb.Kind kind)
    {
        var items = new List<Item>();
        string folder = pathIsFolder ? path : System.IO.Path.GetDirectoryName(path) ?? "";
        if (folder.Length == 0 || !Directory.Exists(folder)) return items;

        bool files = !pathIsFolder || kind != Tmdb.Kind.Tv;
        bool folders = pathIsFolder && kind != Tmdb.Kind.Movie;

        try
        {
            if (folders)
                foreach (var dir in Directory.EnumerateDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var attributes = File.GetAttributes(dir);
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (File.Exists(System.IO.Path.Combine(dir, "tvshow.nfo")) || File.Exists(System.IO.Path.Combine(dir, "movie.nfo"))) continue;
                    items.Add(new Item(dir, true));
                }

            if (files)
                foreach (var file in Directory.EnumerateFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (!MediaPlayerLauncher.IsVideo(file)) continue;
                    if (NfoPoster.NfoFilesOf(file).Any(File.Exists)) continue;
                    items.Add(new Item(file, false));
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return items;
    }

    private sealed record Decision(Tmdb.Result? Hit, string Reason, List<Tmdb.Result>? Candidates = null);

    /// <summary>Lower case, accents gone, only letters and digits left: "Amélie" and "amelie" and "Spider-Man" and "Spider Man" are the same.</summary>
    public static string Simplify(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static async Task<Decision> DecideAsync(string name, string year, Tmdb.Kind kind, bool includeAdult, CancellationToken ct)
    {
        var search = await Tmdb.SearchAsync(name, year, kind, includeAdult, ct);
        if (search.Results.Count == 0) return new Decision(null, "TMDB found nothing");

        // A year in the name must match too, but one year off is accepted: production year and release year often differ.
        // Hits with exactly the right year win over hits that are one year off.
        var want = Simplify(name);
        var sameTitle = search.Results.Where(r => Simplify(r.Title) == want).ToList();
        var exact = sameTitle;
        if (year.Length > 0 && int.TryParse(year, out var wanted))
        {
            int Off(Tmdb.Result r) => int.TryParse(r.Year, out var y) ? Math.Abs(y - wanted) : int.MaxValue;
            exact = sameTitle.Where(r => Off(r) == 0).ToList();
            if (exact.Count == 0) exact = sameTitle.Where(r => Off(r) == 1).ToList();
        }

        // TMDB itself found just one movie for this name (and year, when the name has one): that is the answer, even when its
        // title is spelled a little differently. With several hits, only an exact title counts.
        if (exact.Count == 0 && search.Results.Count == 1 && (year.Length == 0 || !search.YearIgnored))
            return new Decision(search.Results[0], "");

        if (exact.Count == 1) return new Decision(exact[0], "");
        if (exact.Count == 0) return new Decision(null, $"no exact match among {search.Results.Count} hits");

        var shown = string.Join(", ", exact.Take(3).Select(r => $"{r.Title}{(r.Year != null ? $" ({r.Year})" : "")}"));
        return new Decision(null, $"{exact.Count} exact matches ({shown}{(exact.Count > 3 ? ", ..." : "")})", exact);
    }

    /// <summary>Looks up and saves the items one after the other; <paramref name="progress"/> gets the number done.</summary>
    public static async Task<Outcome> RunAsync(IReadOnlyList<Item> items, Tmdb.Kind kind, bool includeAdult, IProgress<int> progress, CancellationToken ct)
    {
        var outcome = new Outcome();
        var decisions = new Dictionary<string, Decision>(StringComparer.OrdinalIgnoreCase);   // the episodes of one show ask TMDB once

        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) { outcome.Canceled = true; break; }

            string label = System.IO.Path.GetFileName(item.Path);
            try
            {
                await DoOneAsync(item, label, kind, includeAdult, decisions, outcome, ct);
            }
            catch (OperationCanceledException) { outcome.Canceled = true; break; }
            catch (TmdbFatalException ex) { outcome.Fatal = ex.Message; break; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                outcome.Skipped.Add(new Skipped(label, ex.Message));
            }

            outcome.Done++;
            progress.Report(outcome.Done);
        }
        return outcome;
    }

    private static async Task DoOneAsync(Item item, string label, Tmdb.Kind kind, bool includeAdult,
                                         Dictionary<string, Decision> decisions, Outcome outcome, CancellationToken ct)
    {
        var (name, year, _) = Tmdb.Guess(item.Path);
        if (name.Length == 0) { outcome.Skipped.Add(new Skipped(label, "no name to look up")); return; }

        string key = $"{name}|{year}|{kind}|{includeAdult}";
        if (!decisions.TryGetValue(key, out var decision))
            decisions[key] = decision = await DecideAsync(name, year, kind, includeAdult, ct);

        var hit = decision.Hit;
        if (hit == null && decision.Candidates is { Count: > 1 } candidates && !item.IsFolder)
        {
            // Several equal hits: the length of the video file can still tell which one it is.
            hit = await PickByLengthAsync(item.Path, candidates, ct);
            if (hit == null) { outcome.Skipped.Add(new Skipped(label, decision.Reason + "; the length of the file did not settle it")); return; }
        }
        if (hit == null) { outcome.Skipped.Add(new Skipped(label, decision.Reason)); return; }

        (int Season, int Episode)? episode = null;
        if (hit.IsTv && !item.IsFolder)
        {
            episode = Tmdb.EpisodeOf(System.IO.Path.GetFileNameWithoutExtension(item.Path));
            if (episode == null) { outcome.Skipped.Add(new Skipped(label, $"it is the TV show \"{hit.Title}\", but the file name has no season and episode")); return; }
        }

        string target = item.IsFolder
            ? System.IO.Path.Combine(item.Path, hit.IsTv ? "tvshow.nfo" : "movie.nfo")
            : NfoWriter.TargetFile(item.Path, out _);
        if (File.Exists(target)) { outcome.Skipped.Add(new Skipped(label, "it has an .nfo file already")); return; }

        var xml = await Tmdb.BuildNfoAsync(hit, episode, ct);
        Tmdb.WriteNfo(target, xml, replace: false);
        outcome.Saved.Add(label);

        if (item.IsFolder)
        {
            var problem = await Tmdb.SaveFolderPosterAsync(item.Path, target);
            if (problem == null) outcome.Posters++;
            else outcome.Skipped.Add(new Skipped(label, "the .nfo was saved, but the poster was not: " + problem));
        }
    }

    // A hit whose length is within this many minutes of the file's counts as the same movie.
    private const double LengthTolerance = 3;

    /// <summary>The length of a video file in minutes, or null when it cannot be read.</summary>
    public static double? VideoMinutes(string path)
    {
        try { var length = AudioTagReader.Read(path)?.Duration ?? TimeSpan.Zero; return length > TimeSpan.Zero ? length.TotalMinutes : null; }
        catch { return null; }
    }

    // Exactly one movie among the hits has the length of the file: that one. Otherwise (none, several, a TV show, no length) null.
    private static async Task<Tmdb.Result?> PickByLengthAsync(string path, List<Tmdb.Result> candidates, CancellationToken ct)
    {
        if (VideoMinutes(path) is not { } mine) return null;

        var same = new List<Tmdb.Result>();
        foreach (var candidate in candidates.Where(c => !c.IsTv))
            if (await Tmdb.RuntimeAsync(candidate, ct) is { } minutes && Math.Abs(minutes - mine) <= LengthTolerance)
                same.Add(candidate);
        return same.Count == 1 ? same[0] : null;
    }

    /// <summary>The message after a run: what was saved and what was left out, and why (the first few).</summary>
    public static string SummaryText(Outcome outcome, int total)
    {
        var sb = new StringBuilder();
        if (outcome.Fatal != null)
            sb.AppendLine(outcome.Fatal).AppendLine().AppendLine($"The run stopped after {outcome.Done} of {total}.");
        else if (outcome.Canceled)
            sb.AppendLine($"Canceled after {outcome.Done} of {total}.");

        int skipped = outcome.Skipped.Count;
        sb.AppendLine(outcome.Saved.Count == 0
            ? "No .nfo files were saved."
            : outcome.Saved.Count == 1 ? "1 .nfo file was saved." : $"{outcome.Saved.Count} .nfo files were saved.");

        if (skipped > 0)
        {
            sb.AppendLine().AppendLine(skipped == 1 ? "1 item was left out:" : $"{skipped} items were left out:");
            foreach (var s in outcome.Skipped.Take(15)) sb.AppendLine($"  {s.Name}: {s.Reason}");
            if (skipped > 15) sb.AppendLine($"  ... and {skipped - 15} more.");
        }
        return sb.ToString().TrimEnd();
    }
}
