using System.IO;
using MyFileExplorer.Helpers;

namespace MyFileExplorer.ViewModels;

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
    public AppSettings Settings { get; } = AppSettingsStore.Load();

    // The clipboard is local to the app, not the system clipboard.
    public List<string>? ClipboardPaths { get; set; }
    public bool ClipboardIsCut { get; set; }

    /// <summary>Sort order per folder, for this run. Meant to be saved later (a small database file).</summary>
    public Dictionary<string, (SortColumn Column, bool Ascending)> FolderSorts { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The key of a folder in <see cref="FolderSorts"/>: its full path without a trailing slash.</summary>
    public static string Key(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
