using System.IO;
using System.Windows;
using Microsoft.Win32;
using SwewolfFileExplorer.Helpers;
using SwewolfFileExplorer.ViewModels;
using SwewolfFileExplorer.Views;

namespace SwewolfFileExplorer;

/// <summary>
/// The playlist buttons in the info line, right of "File 14/15", while an M3U playlist is shown: Add Files, Move Up, Move Down, Save Playlist, Save Playlist As
/// (as in TeraTool). Moving files gives the playlist an order of its own: the sort by a column ends. Deleting takes entries out of the list
/// (the files stay) and pasting adds links to the pasted files (see FolderTabViewModel, "Editing a playlist").
/// </summary>
public partial class MainWindow
{
    private void UpdatePlaylistBar()
    {
        bool inPlaylist = VM.InPlaylist;
        PlaylistBar.Visibility = inPlaylist ? Visibility.Visible : Visibility.Collapsed;   // the buttons right of "File 14/15"
        PlaylistSaveButton.IsEnabled = VM.PlaylistDirty;
        UpdatePlaylistMoveButtons();
    }

    private void UpdatePlaylistMoveButtons()
    {
        bool any = FileListView.SelectedItems.Count > 0;
        PlaylistUpButton.IsEnabled   = any;
        PlaylistDownButton.IsEnabled = any;
    }

    // ── Create Playlist... (the right-click menus of the list and of the tree) ──────────────────

    private void CtxCreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        var folder = ListMenuFolder();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        // Folder mode (a folder row or the empty area): the playlist is for the folder, not for what happens to be selected.
        CreatePlaylist(folder, null);
    }

    private void TreeCtxCreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode == null || !Directory.Exists(_treeMenuNode.FullPath)) return;
        CreatePlaylist(_treeMenuNode.FullPath, null);
    }

    /// <summary>
    /// Makes an M3U playlist (a .m3u8 file, UTF-8) in <paramref name="folder"/>. First the question what it starts with: all the files that can
    /// be played in the folder (pictures, music and video; in the order of the list when the folder is the one shown), the selected files
    /// (<paramref name="selected"/>; the list's menu only), or none; then its name. The new playlist is opened, so files can be added at once.
    /// </summary>
    private void CreatePlaylist(string folder, List<FileItemViewModel>? selected)
    {
        bool isShown = string.Equals(folder.TrimEnd('\\'), VM.CurrentPath?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        bool canSelect = selected is { Count: > 0 };

        var ask = new ChoiceDialog($"Which files should the new playlist start with?\n\nIt is made in this folder:\n{folder}",
            new[] { "All Files in the Folder", "Selected Files", "No Files", "Cancel" },
            defaultIndex: canSelect ? 1 : 0, cancelIndex: 3, fontSize: 16)
        {
            Owner = this,
            Title = "Create Playlist",
        };
        ask.SetButtonEnabled(1, canSelect);
        ask.ShowDialog();
        if (ask.Result is < 0 or 3) return;

        var files = new List<string>();
        if (ask.Result == 0)
        {
            if (isShown)
                files = PlaylistInList();   // the list as shown: sorted, filtered, playable files only
            else
            {
                try
                {
                    files = new DirectoryInfo(folder).EnumerateFiles()
                        .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0
                                    && (ThumbnailLoader.IsImage(f.Name) || MediaPlayerLauncher.IsMedia(f.Name)))
                        .Select(f => f.FullName)
                        .OrderBy(f => Path.GetFileName(f), Comparer<string?>.Create(FileItemComparer.NaturalCompare))
                        .ToList();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"The folder could not be read: {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
        }
        else if (ask.Result == 1)
        {
            files = selected!.Select(i => i.FullPath).ToList();
        }

        var name = new InputDialog("Create Playlist", "Name of the playlist:", SuggestPlaylistName(folder), n => CheckPlaylistName(folder, n)) { Owner = this };
        if (name.ShowDialog() != true) return;

        var path = Path.Combine(folder, PlaylistFileName(name.InputText));
        try
        {
            PlaylistFile.Write(path, files.Select(f => new PlaylistEntry(f, true)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The playlist could not be made: {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        VM.ActiveTab.RefreshFolder(folder);   // the playlist shows in the tree
        VM.Navigate(path);                    // and is opened, ready for more files
        FlashTitle($"{BaseTitle} - Playlist made: {Path.GetFileName(path)}");
    }

    /// <summary>The name of the folder (only its last part) is the suggested name; "(2)", "(3)" ... if a playlist of that name is there already. "Playlist" for a drive.</summary>
    private static string SuggestPlaylistName(string folder)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        name = string.Concat(name.Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0)).Trim();
        if (name.Length == 0) name = "Playlist";

        var candidate = name;
        for (int i = 2; File.Exists(Path.Combine(folder, PlaylistFileName(candidate))); i++) candidate = $"{name} ({i})";
        return candidate;
    }

    /// <summary>"Name" becomes "Name.m3u8"; a name that ends in .m3u or .m3u8 is kept.</summary>
    private static string PlaylistFileName(string typed)
    {
        typed = typed.Trim();
        return PlaylistFile.IsPlaylistName(typed) ? typed : typed + ".m3u8";
    }

    private static string? CheckPlaylistName(string folder, string typed)
    {
        typed = typed.Trim();
        if (typed.Length == 0) return "Type a name.";
        if (typed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "A file name cannot contain any control characters or any of the following characters:\n\\ / : * ? \" < > |";
        return File.Exists(Path.Combine(folder, PlaylistFileName(typed))) ? "A playlist with this name exists already." : null;
    }

    private void PlaylistAdd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add Files to the Playlist",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "Music and Video Files|*.mp3;*.flac;*.m4a;*.aac;*.ogg;*.oga;*.opus;*.wma;*.wav;*.aif;*.aiff;*.sid;"
                   + "*.mp4;*.m4v;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.mpg;*.mpeg;*.ts;*.m2ts;*.mts;*.3gp;*.ogv|All Files|*.*",
        };
        var folder = VM.ActiveTab.CurrentPath;
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(Path.GetDirectoryName(folder))) dialog.InitialDirectory = Path.GetDirectoryName(folder);
        if (dialog.ShowDialog(this) != true) return;

        AddToPlaylistAfterSelection(dialog.FileNames);
    }

    private void AddToPlaylistAfterSelection(IEnumerable<string> files)
    {
        var last = FileListView.SelectedItems.OfType<FileItemViewModel>().OrderBy(i => VM.ActiveTab.CurrentItems.IndexOf(i)).LastOrDefault();
        VM.ActiveTab.AddToPlaylist(files, last);
    }

    private void PlaylistUp_Click(object sender, RoutedEventArgs e) => MovePlaylistSelection(-1);

    private void PlaylistDown_Click(object sender, RoutedEventArgs e) => MovePlaylistSelection(+1);

    private void MovePlaylistSelection(int direction)
    {
        var selected = FileListView.SelectedItems.OfType<FileItemViewModel>().ToList();
        if (selected.Count == 0) return;

        // The first move gives the playlist an order of its own: the order shown now (sorted by a column or not) is kept, and the sort ends.
        if (_sortColumn != SortColumn.None)
        {
            VM.ActiveTab.SetPlaylistOrderFrom(new FileItemComparer(_sortColumn, _sortAscending));
            ApplySort(SortColumn.None, true);
            VM.SetSort(SortColumn.None, true);
        }

        if (!VM.ActiveTab.MovePlaylistItems(selected, direction)) return;

        // The items moved: the selection follows them.
        FileListView.SelectedItems.Clear();
        foreach (var item in selected) FileListView.SelectedItems.Add(item);
        FileListView.ScrollIntoView(direction < 0 ? selected.OrderBy(i => FileListView.Items.IndexOf(i)).First() : selected.OrderBy(i => FileListView.Items.IndexOf(i)).Last());
        FileListView.Focus();
    }

    private void PlaylistSave_Click(object sender, RoutedEventArgs e)
    {
        if (VM.ActiveTab.SavePlaylist()) FlashTitle($"{BaseTitle} - Playlist saved");
    }

    private void PlaylistSaveAs_Click(object sender, RoutedEventArgs e)
    {
        var current = VM.ActiveTab.CurrentPath;
        var dialog = new SaveFileDialog
        {
            Title = "Save Playlist As",
            // The name of the folder the playlist is in (only the last part of the path), with the extension of this playlist.
            FileName = (Path.GetFileName(Path.GetDirectoryName(current)) is { Length: > 0 } folderName ? folderName : Path.GetFileNameWithoutExtension(current))
                       + Path.GetExtension(current),
            InitialDirectory = Path.GetDirectoryName(current),
            DefaultExt = ".m3u8",
            AddExtension = true,
            OverwritePrompt = true,
            Filter = "Playlists (*.m3u8;*.m3u)|*.m3u8;*.m3u",
        };
        if (dialog.ShowDialog(this) != true) return;

        var tab = VM.ActiveTab;
        if (string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
        {
            if (tab.SavePlaylist()) FlashTitle($"{BaseTitle} - Playlist saved");
            return;
        }

        // The new file is written from what is shown; the old playlist keeps its file as it was. Then the new one is opened.
        if (!tab.SavePlaylist(dialog.FileName)) return;
        tab.Navigate(dialog.FileName);
        FlashTitle($"{BaseTitle} - Playlist saved: {Path.GetFileName(dialog.FileName)}");
    }
}
