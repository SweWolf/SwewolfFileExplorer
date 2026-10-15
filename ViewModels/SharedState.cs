using System.IO;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.ViewModels;

/// <summary>What all tabs share: the cut/copy clipboard and the sort order chosen per folder.</summary>
public class SharedState
{
    /// <summary>Whether the file list shows folders (off by default; they are in the tree).</summary>
    public bool ShowFolders
    {
        get => Settings.ShowFolders;
        set => Settings.ShowFolders = value;
    }

    /// <summary>The saved settings (folders shown, font sizes); save them again after a change.</summary>
    public AppSettings Settings { get; } = AppSettingsStore.Current;

    // The clipboard is local to the app, not the system clipboard.
    public List<string>? ClipboardPaths { get; set; }
    public bool ClipboardIsCut { get; set; }

    /// <summary>Sort order per folder, for this run. Meant to be saved later (a small database file).</summary>
    public Dictionary<string, (SortColumn Column, bool Ascending)> FolderSorts { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How the subfolders of a folder are sorted in the navigation tree, per folder, for this run.
    /// Only Name, DateCreated and DateModified are used. Meant to be saved later, with <see cref="FolderSorts"/>.
    /// </summary>
    public Dictionary<string, (SortColumn Column, bool Ascending)> TreeSorts { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tree sort order of the subfolders of <paramref name="path"/>. If none was chosen: Name, ascending;
    /// inside an archive no sort (<see cref="SortColumn.None"/>): its folders show in the order they are stored in it.
    /// </summary>
    public (SortColumn Column, bool Ascending) GetTreeSort(string path) =>
        TreeSorts.TryGetValue(Key(path), out var s)
            ? s
            : (ArchiveSupport.IsInside(path) ? SortColumn.None : SortColumn.Name, true);

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

    /// <summary>The key of a folder in <see cref="FolderSorts"/>: its full path without a trailing slash.</summary>
    public static string Key(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
