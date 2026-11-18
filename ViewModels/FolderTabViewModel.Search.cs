using System.Diagnostics;
using System.IO;
using System.Windows;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.ViewModels;

// Search results in a tab (2026-11-17, asked by the user; plan: D:\Dev\dev-notes\FileExplorer-search-plan.md).
// A search opens a NEW tab and fills its list while FileSearch walks the folders on background threads, so the rest of the program is
// never locked. The tab is a "virtual folder" like an audio CD or a playlist, but its items are real files: they can be opened, renamed,
// deleted, rated ... as anywhere. It has no folder (HasFolder is false: no Paste, no New Folder), a Location column, and no sort until a
// header is clicked (results come in the order they are found). Any navigation (tree, address bar) leaves the results.

public partial class FolderTabViewModel
{
    private bool _searchMode;
    private SearchCriteria? _searchCriteria;
    private CancellationTokenSource? _searchCts;
    private int _searchVersion;
    private bool _searchRunning;
    private string _searchStatus = "";

    /// <summary>The list holds search results.</summary>
    public bool InSearch => _searchMode;

    /// <summary>The search is still walking folders.</summary>
    public bool SearchRunning => _searchRunning;

    /// <summary>What the search bar says: how many were found, whether it is still going.</summary>
    public string SearchStatus => _searchStatus;

    public SearchCriteria? Criteria => _searchCriteria;

    private void SetSearchStatus(string text, bool running)
    {
        _searchStatus = text;
        _searchRunning = running;
        OnPropertyChanged(nameof(SearchStatus));
        OnPropertyChanged(nameof(SearchRunning));
    }

    /// <summary>Starts a search in this tab (the caller opened a new tab for it). Results stream into the list.</summary>
    public void StartSearch(SearchCriteria criteria)
    {
        CancelSearch();
        ShowNoFolder("search");   // an empty list, no folder, no watchers

        _searchMode = true;
        _searchCriteria = criteria;
        _unavailable = false;
        _baseTitle = "Search: " + criteria.Describe();
        UpdateTitle();
        AddressText = $"Search Results: {criteria.Describe()} in {string.Join("; ", criteria.Roots)}";

        // No sort while the results come in (sorting a growing list is slow); a click on a header sorts.
        SortColumn = SortColumn.None;
        SortAscending = true;
        SortChanged?.Invoke();

        OnPropertyChanged(nameof(InSearch));
        RaiseAllCanExecuteChanged();
        _ = RunSearchAsync(criteria);
    }

    private async Task RunSearchAsync(SearchCriteria criteria)
    {
        var cts = _searchCts = new CancellationTokenSource();
        int version = ++_searchVersion;
        var dispatcher = Application.Current.Dispatcher;
        var clock = Stopwatch.StartNew();
        int shown = 0;
        SetSearchStatus("Searching...", running: true);

        SearchSummary summary;
        using var log = SearchLog.Start(criteria);   // Settings > Troubleshooting > Log Searches
        try
        {
            summary = await FileSearch.RunAsync(criteria, (items, folders, current) =>
            {
                dispatcher.InvokeAsync(() =>
                {
                    if (version != _searchVersion) return;
                    shown += AddFound(items);
                    // The folder read the longest right now: when the search seems stuck, this is where.
                    SetSearchStatus($"Searching... {shown:N0} found, {folders:N0} folders searched" + (current != null ? $". Looking in: {current}" : ""), running: true);
                });
            }, cts.Token, log == null ? null : log.Folder);
        }
        catch (Exception ex)
        {
            LogFiles.AppendError($"Search failed: {ex.GetType().Name}: {ex.Message}");
            if (version == _searchVersion) SetSearchStatus($"The search stopped: {ex.Message}", running: false);
            return;
        }
        log?.Finish(summary, clock.Elapsed);

        // The last batches were queued before this point, so the list holds everything when this runs.
        await dispatcher.InvokeAsync(() =>
        {
            if (version != _searchVersion) return;
            var took = FormatDuration(clock.Elapsed);
            var text = summary.Canceled ? $"Search canceled: {shown:N0} found so far ({took})"
                     : summary.StoppedAtLimit ? $"Stopped after {shown:N0} items ({took})"
                     : summary.Capped ?$"{shown:N0} found: the list is full, narrow the search to see the rest ({took})"
                     : $"Search finished: {shown:N0} found in {summary.FoldersSearched:N0} folders ({took})";
            if (summary.FoldersSkipped > 0)
                text += $". {summary.FoldersSkipped:N0} {(summary.FoldersSkipped == 1 ? "folder" : "folders")} could not be read.";
            if (summary.ExcludedSkipped > 0)
                text += $". {summary.ExcludedSkipped:N0} excluded {(summary.ExcludedSkipped == 1 ? "folder was" : "folders were")} skipped.";
            SetSearchStatus(text, running: false);
            EnsureTags();   // the tag columns, if shown
        });
    }

    /// <summary>"12.3 s" under a minute; from a minute on whole seconds: "44 min 28 s", "1 h 5 min" (no seconds from an hour up).</summary>
    internal static string FormatDuration(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{t.TotalSeconds:0.0} s";
        long total = (long)Math.Round(t.TotalSeconds);
        if (total < 3600) return $"{total / 60} min {total % 60} s";
        return $"{total / 3600} h {total % 3600 / 60} min";
    }

    private int AddFound(List<FileSystemInfo> found)
    {
        int added = 0;
        foreach (var e in found)
        {
            try
            {
                FileItemViewModel vm;
                if (e is DirectoryInfo dir) vm = NewDirItem(dir, HiddenItems.IsHiddenByAttributes(dir.Attributes));
                else if (e is FileInfo file) vm = NewFileItem(file, HiddenItems.IsHiddenFile(file.Attributes, file.Name));
                else continue;

                vm.Location = Path.GetDirectoryName(e.FullName) ?? "";
                CurrentItems.Add(vm);
                vm.LoadIconAsync();
                added++;
            }
            catch { /* gone while searching */ }
        }
        return added;
    }

    /// <summary>Stops the walk (the results found so far stay). Also when the tab is closed.</summary>
    public void CancelSearch() => _searchCts?.Cancel();

    /// <summary>Leaves the search results: the walk is stopped and the tab is an ordinary folder tab again.</summary>
    internal void EndSearch()
    {
        if (!_searchMode) return;
        _searchCts?.Cancel();
        _searchVersion++;
        _searchMode = false;
        _searchCriteria = null;
        SetSearchStatus("", running: false);
        OnPropertyChanged(nameof(InSearch));
    }

    /// <summary>Takes deleted items (and what was inside deleted folders) out of the results.</summary>
    private void RemoveFoundItems(IEnumerable<string> deleted)
    {
        var paths = deleted.Select(p => p.TrimEnd('\\', '/')).ToList();
        if (paths.Count == 0) return;
        for (int i = CurrentItems.Count - 1; i >= 0; i--)
        {
            var p = CurrentItems[i].FullPath;
            if (paths.Any(d => string.Equals(p, d, StringComparison.OrdinalIgnoreCase)
                            || p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)))
                CurrentItems.RemoveAt(i);
        }
        OnPropertyChanged(nameof(FolderStatusLine));
    }

    /// <summary>A renamed result: the same place in the list, the new name.</summary>
    private void ReplaceFoundItem(FileItemViewModel old, string newPath)
    {
        int index = CurrentItems.IndexOf(old);
        try
        {
            FileItemViewModel vm = old.IsDirectory
                ? NewDirItem(new DirectoryInfo(newPath), HiddenItems.IsHiddenByAttributes(new DirectoryInfo(newPath).Attributes))
                : NewFileItem(new FileInfo(newPath), HiddenItems.IsHiddenFile(new FileInfo(newPath).Attributes, Path.GetFileName(newPath)));
            vm.Location = Path.GetDirectoryName(newPath) ?? "";
            if (index >= 0) CurrentItems[index] = vm; else CurrentItems.Add(vm);
            vm.LoadIconAsync();
        }
        catch { if (index >= 0) CurrentItems.RemoveAt(index); }
    }
}
