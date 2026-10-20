using System.IO;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.ViewModels;

/// <summary>What all tabs share: the sort order chosen per folder, and which hidden folders the tree shows.</summary>
public class SharedState
{
    /// <summary>Whether the file list shows folders (off by default; they are in the tree).</summary>
    public bool ShowFolders
    {
        get => Settings.ShowFolders;
        set => Settings.ShowFolders = value;
    }

    /// <summary>
    /// True when the list shows (or is sorted by) a column that needs the tags of the music files.
    /// Only then are they read, in the background: reading costs something for every file.
    /// </summary>
    public bool TagColumnsVisible { get; set; }

    /// <summary>The saved settings (folders shown, font sizes); save them again after a change.</summary>
    public AppSettings Settings { get; } = AppSettingsStore.Current;

    // ── Sort orders, per folder ──────────────────────────────────────────────
    // What the user chose is kept in the library database (table FolderPrefs), so it is still there after the program is
    // closed. A folder's saved order is read once per run, the first time it is asked for, and kept here; a change is
    // written in the background. Used from the UI thread and, for the tree, by the next-folder search: one lock.

    private readonly object _sortGate = new();
    private readonly Dictionary<string, (SortColumn Column, bool Ascending)> _folderSorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (SortColumn Column, bool Ascending)> _treeSorts   = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _prefsLoaded = new(StringComparer.OrdinalIgnoreCase);

    private static SortColumn? ParseColumn(string? text) =>
        text != null && Enum.TryParse<SortColumn>(text, out var column) && Enum.IsDefined(column) ? column : null;

    // Takes over what the database says about a folder, unless this run already knows something newer. Call inside the lock.
    private void LoadPrefs(string path, string key)
    {
        if (!_prefsLoaded.Add(key)) return;

        var prefs = LibraryStore.GetFolderPrefs(path);
        if (prefs == null) return;

        if (ParseColumn(prefs.SortColumn) is { } column && !_folderSorts.ContainsKey(key))
            _folderSorts[key] = (column, prefs.SortAscending);
        if (ParseColumn(prefs.TreeSortColumn) is { } treeColumn && !_treeSorts.ContainsKey(key))
            _treeSorts[key] = (treeColumn, prefs.TreeSortAscending);
    }

    /// <summary>How the user sorted the list of <paramref name="path"/>, if they ever did (in this run or an earlier one).</summary>
    public bool TryGetFolderSort(string path, out (SortColumn Column, bool Ascending) sort)
    {
        sort = default;
        if (string.IsNullOrWhiteSpace(path)) return false;

        lock (_sortGate)
        {
            var key = Key(path);
            LoadPrefs(path, key);
            return _folderSorts.TryGetValue(key, out sort);
        }
    }

    /// <summary>The user sorted the list of <paramref name="path"/> this way: used now, and remembered for the next run.</summary>
    public void SetFolderSort(string path, SortColumn column, bool ascending)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        lock (_sortGate)
        {
            var key = Key(path);
            _prefsLoaded.Add(key);   // what is in the database is older than this
            _folderSorts[key] = (column, ascending);
        }
        LibraryService.Queue(() => LibraryStore.SetFolderSort(path, column.ToString(), ascending));
    }

    /// <summary>
    /// The tree sort order of the subfolders of <paramref name="path"/>. If none was chosen: Name, ascending;
    /// inside a zip / cbz no sort (<see cref="SortColumn.None"/>): its folders show in the order they are stored in it (RAR, cbr, 7z: by name).
    /// </summary>
    public (SortColumn Column, bool Ascending) GetTreeSort(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (SortColumn.Name, true);

        lock (_sortGate)
        {
            var key = Key(path);
            LoadPrefs(path, key);
            if (_treeSorts.TryGetValue(key, out var sort)) return sort;
        }
        return (ArchiveSupport.ShowsStoredOrder(path) ? SortColumn.None : SortColumn.Name, true);
    }

    /// <summary>The user chose this order for the subfolders of <paramref name="path"/> in the tree: used now, and remembered.</summary>
    public void SetTreeSort(string path, SortColumn column, bool ascending)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        lock (_sortGate)
        {
            var key = Key(path);
            _prefsLoaded.Add(key);
            _treeSorts[key] = (column, ascending);
        }
        LibraryService.Queue(() => LibraryStore.SetFolderTreeSort(path, column.ToString(), ascending));
    }

    // ── Hidden folders in the tree ───────────────────────────────────────────

    /// <summary>
    /// The folders whose hidden and system subfolders the navigation tree shows, for this run.
    /// A folder is in here until the user hides them again; the keys come from <see cref="Key"/>.
    /// </summary>
    public HashSet<string> TreeShownHidden { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True if the tree shows the hidden subfolders of <paramref name="path"/>.</summary>
    public bool ShowsHiddenInTree(string path)
    {
        try { return TreeShownHidden.Contains(Key(path)); }
        catch { return false; }   // not a path
    }

    /// <summary>The key of a folder in the sort orders: its full path without a trailing slash.</summary>
    public static string Key(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
