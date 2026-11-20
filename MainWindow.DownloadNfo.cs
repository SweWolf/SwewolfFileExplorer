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

        if (ask.BatchAll)
        {
            await RunNfoBatchAsync(path, folder, ask.Kind, ask.IncludeAdult);
            return;
        }

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
                double? fileMinutes = folder ? null : await Task.Run(() => TmdbBatch.VideoMinutes(path));
                var pick = new PickTmdbResultWindow(hits, search.YearIgnored ? $"Nothing was found for {ask.Year}, so these are from all years. Which one is it?" : null, fileMinutes) { Owner = this };
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
            try { posterProblem = await Tmdb.SaveFolderPosterAsync(path, target); }
            finally { Mouse.OverrideCursor = null; }
        }

        // The folder is read again (the selection stays); the plot, the preview and the folder picture are read again.
        var selected = FileListView.SelectedItems.OfType<FileItemViewModel>().Select(i => i.FullPath).ToList();
        // The tiles of the Icons view keep the picture they had: a poster may be new for the video (or the videos of a TV show folder).
        ThumbnailService.InvalidateFolder(folder ? path : System.IO.Path.GetDirectoryName(path) ?? "", VM.ActiveTab.CurrentItems);
        VM.RefreshCommand.Execute(null);
        ReselectInList(selected);
        UpdateInfoPanel();
        if (folder) VM.RefreshFolderPicture();
        FlashTitle($"{BaseTitle} - Saved {System.IO.Path.GetFileName(target)}");
        if (posterProblem != null)
            MessageBox.Show(this, $"\"{System.IO.Path.GetFileName(target)}\" was saved, but the poster was not: {posterProblem}", "Download NFO File", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// "Look up all ... in this folder": every video (or, for TV shows, every folder) without an .nfo file is looked up and saved when exactly one
    /// TMDB hit matches its title (and year) (<see cref="TmdbBatch"/>). A progress window with Cancel; one message at the end.
    /// </summary>
    private async Task RunNfoBatchAsync(string path, bool folder, Tmdb.Kind kind, bool includeAdult)
    {
        // A selected folder in the list is one item among its neighbours: "all folders in this folder" means the folder it lies in
        // (the TV Shows folder), not the season folders inside the selected show.
        if (folder && System.IO.Path.GetDirectoryName(path.TrimEnd('\\', '/')) is { Length: > 0 } parent)
            path = parent;

        Mouse.OverrideCursor = Cursors.Wait;
        List<TmdbBatch.Item> items;
        try { items = await Task.Run(() => TmdbBatch.ItemsFor(path, folder, kind)); }
        finally { Mouse.OverrideCursor = null; }

        if (items.Count == 0)
        {
            MessageBox.Show(this, "There is nothing to look up: everything of that kind in this folder has an .nfo file already.",
                            "Download NFO Files", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var outcome = WaitWindow.RunWithProgress(this, "Download NFO Files", "Looking up on The Movie Database...", items.Count, "items",
            (ct, progress, _) => TmdbBatch.RunAsync(items, kind, includeAdult, progress, ct));
        if (outcome == null)
        {
            MessageBox.Show(this, "The lookup stopped unexpectedly.", "Download NFO Files", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The list, the plot, the previews and the tiles are read again for what was saved.
        var selected = FileListView.SelectedItems.OfType<FileItemViewModel>().Select(i => i.FullPath).ToList();
        string shown = folder ? path : System.IO.Path.GetDirectoryName(path) ?? "";
        ThumbnailService.InvalidateFolder(shown, VM.ActiveTab.CurrentItems);
        VM.RefreshCommand.Execute(null);
        ReselectInList(selected);
        UpdateInfoPanel();
        if (outcome.Posters > 0) VM.RefreshFolderPicture();

        MessageBox.Show(this, TmdbBatch.SummaryText(outcome, items.Count), "Download NFO Files", MessageBoxButton.OK,
                        outcome.Fatal != null ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}
