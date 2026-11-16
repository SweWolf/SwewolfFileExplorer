using System.IO;
using System.Windows;
using System.Windows.Input;
using SwewolfFileExplorer.Helpers;
using SwewolfFileExplorer.ViewModels;
using SwewolfFileExplorer.Views;

namespace SwewolfFileExplorer;

/// <summary>
/// "Download NFO File...": right-click in the plot area under the list. Asks for the name, year and type (<see cref="DownloadNfoWindow"/>),
/// searches TMDB (<see cref="Tmdb"/>), lets the user pick when there are several hits, and saves the details as a hidden .nfo
/// next to the video. An existing .nfo is only replaced after a question.
/// </summary>
public partial class MainWindow
{
    private sealed record NfoOutcome(string? Error, string? Xml, List<Tmdb.Result>? Hits, bool YearIgnored = false);

    private async void PlotDownloadNfo_Click(object sender, RoutedEventArgs e)
    {
        var path = _plotPath;   // a video / music file, or a folder (a TV show: its tvshow.nfo and poster)
        if (path == null) return;
        bool folder = _plotIsFolder;

        if (Tmdb.GetApiKey() == null)
        {
            if (MessageBox.Show(this, "Downloading an NFO file needs your own free TMDB API key." + Environment.NewLine + Environment.NewLine
                    + "Do you want to open Tools > Settings > Scrapers to add it?", "Download NFO File", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                MenuSettings_Click(this, new RoutedEventArgs());
            return;
        }

        var ask = new DownloadNfoWindow(path) { Owner = this };
        if (ask.ShowDialog() != true) return;

        Tmdb.Result chosen;
        bool byId = ask.Id != null || ask.ImdbId != null;
        if (ask.ImdbId != null)
        {
            // An IMDb ID: TMDB finds the movie or TV show that has it.
            var found = WaitWindow.Run(this, "Download NFO File", "Looking up the IMDb ID on The Movie Database...", ask.ImdbId, async ct =>
            {
                try { return new NfoOutcome(null, null, new List<Tmdb.Result?> { await Tmdb.FindByImdbAsync(ask.ImdbId, ask.Kind, ct) }.OfType<Tmdb.Result>().ToList()); }
                catch (InvalidOperationException ex) { return new NfoOutcome(ex.Message, null, null); }
            });
            if (found == null) return;   // canceled
            if (found.Error != null) { MessageBox.Show(this, found.Error, "Download NFO File", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (found.Hits!.Count == 0)
            {
                MessageBox.Show(this, $"TMDB does not know the IMDb ID {ask.ImdbId}.", "Download NFO File", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            chosen = found.Hits[0];
        }
        else if (byId)
        {
            // A typed ID is used as it is: no search. A TV Show is a TV show; Movie and Not Sure are a movie.
            chosen = new Tmdb.Result(ask.Kind == Tmdb.Kind.Tv, ask.Id!.Value, $"ID {ask.Id}", null, "");
        }
        else
        {
            // 1. Search.
            var search = WaitWindow.Run(this, "Download NFO File", "Searching The Movie Database...", ask.ShowName, async ct =>
            {
                try
                {
                    var outcome = await Tmdb.SearchAsync(ask.ShowName, ask.Year, ask.Kind, ask.IncludeAdult, ct);
                    return new NfoOutcome(null, null, outcome.Results, outcome.YearIgnored);
                }
                catch (InvalidOperationException ex) { return new NfoOutcome(ex.Message, null, null); }
            });
            if (search == null) return;   // canceled
            if (search.Error != null) { MessageBox.Show(this, search.Error, "Download NFO File", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            var hits = search.Hits!;
            if (hits.Count == 0)
            {
                MessageBox.Show(this, $"TMDB found nothing for \"{ask.ShowName}\"{(ask.Year.Length > 0 ? $" ({ask.Year})" : "")}." + Environment.NewLine
                    + "Try another spelling, no year, or the other type.", "Download NFO File", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            chosen = hits[0];
            if (hits.Count > 1 || search.YearIgnored)   // a single hit is also shown when the year was dropped to find it
            {
                var pick = new PickTmdbResultWindow(hits, search.YearIgnored ? $"Nothing was found for {ask.Year}, so these are from all years. Which one is it?" : null) { Owner = this };
                if (pick.ShowDialog() != true || pick.Chosen == null) return;
                chosen = pick.Chosen;
            }
        }

        // 2. An existing .nfo is never replaced without asking.
        // A file: its own .nfo (an existing one, else the new <name>.nfo). A folder: tvshow.nfo in it (movie.nfo for a movie).
        string target; bool exists;
        if (folder) { target = System.IO.Path.Combine(path, chosen.IsTv ? "tvshow.nfo" : "movie.nfo"); exists = File.Exists(target); }
        else target = NfoWriter.TargetFile(path, out exists);
        if (exists && MessageBox.Show(this, $"\"{System.IO.Path.GetFileName(target)}\" already exists. Do you want to replace it?",
                "Download NFO File", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;

        // 3. Details and save.
        var episode = chosen.IsTv && !folder ? Tmdb.EpisodeOf(System.IO.Path.GetFileNameWithoutExtension(path)) : null;
        var built = WaitWindow.Run(this, "Download NFO File", "Getting the details from The Movie Database...", chosen.Title, async ct =>
        {
            try { return new NfoOutcome(null, await Tmdb.BuildNfoAsync(chosen, episode, ct), null); }
            catch (InvalidOperationException ex) { return new NfoOutcome(ex.Message, null, null); }
        });
        if (built == null) return;
        if (built.Error != null && byId && built.Error.StartsWith("TMDB has no entry"))
            built = new NfoOutcome($"TMDB has no {(chosen.IsTv ? "TV show" : "movie")} with the ID {chosen.Id}. Movies and TV shows have separate numbers: check the type too.", null, null);
        if (built.Error != null) { MessageBox.Show(this, built.Error, "Download NFO File", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        try { Tmdb.WriteNfo(target, built.Xml!, replace: exists); }
        catch (InvalidOperationException ex) { MessageBox.Show(this, ex.Message, "Download NFO File", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        // A folder (a TV show) also gets its poster, as a hidden poster.jpg in it: the picture under the tree shows it.
        string? posterProblem = null;
        if (folder)
        {
            Mouse.OverrideCursor = Cursors.Wait;
            try { posterProblem = await SaveFolderPosterAsync(path, target); }
            finally { Mouse.OverrideCursor = null; }
        }

        // The folder is read again (the selection stays); the plot, the preview and the folder picture are read again.
        var selected = FileListView.SelectedItems.OfType<FileItemViewModel>().Select(i => i.FullPath).ToList();
        VM.RefreshCommand.Execute(null);
        ReselectInList(selected);
        UpdateInfoPanel();
        if (folder) VM.RefreshFolderPicture();
        FlashTitle($"{BaseTitle} - Saved {System.IO.Path.GetFileName(target)}");
        if (posterProblem != null)
            MessageBox.Show(this, $"\"{System.IO.Path.GetFileName(target)}\" was saved, but the poster was not: {posterProblem}", "Download NFO File", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// Downloads the poster named in the .nfo into the folder as a hidden poster.jpg (poster.png for a PNG): the name Jellyfin and Kodi use,
    /// and one the folder picture already looks for. A poster that is there already is never replaced. Returns null when it is done (or
    /// nothing is to be done), else the reason it was not.
    /// </summary>
    private static async Task<string?> SaveFolderPosterAsync(string folder, string nfo)
    {
        foreach (var existing in new[] { "poster.jpg", "poster.jpeg", "poster.png" })
            if (File.Exists(System.IO.Path.Combine(folder, existing))) return null;

        var address = NfoPoster.Addresses(nfo).FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        if (address == null) return null;   // the .nfo names no poster on the web

        var watch = System.Diagnostics.Stopwatch.StartNew();
        string outcome = "failed";
        try
        {
            var download = await FileDownloader.GetBytesAsync(address, 30L * 1024 * 1024, 30, "poster");
            outcome = $"ok, {download.Bytes.Length:N0} bytes";
            var name = download.ContentType == "image/png" ? "poster.png" : "poster.jpg";
            FileDownloader.WriteNew(System.IO.Path.Combine(folder, name), download.Bytes, hidden: true);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            outcome = "failed: " + ex.Message;
            return ex.Message;
        }
        finally { LogFiles.AppendInternet($"GET\timage.tmdb.org\t{address}\t{outcome}\t{watch.ElapsedMilliseconds} ms"); }
    }
}
