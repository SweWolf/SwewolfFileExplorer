using System.IO;
using System.Windows;
using Microsoft.Win32;
using SwewolfFileExplorer.Helpers;
using SwewolfFileExplorer.ViewModels;

namespace SwewolfFileExplorer;

/// <summary>
/// The playlist bar above the list while an M3U playlist is shown: Add Files, Move Up, Move Down, Save Playlist, Save Playlist As
/// (as in TeraTool). Moving files gives the playlist an order of its own: the sort by a column ends. Deleting takes entries out of the list
/// (the files stay) and pasting adds links to the pasted files (see FolderTabViewModel, "Editing a playlist").
/// </summary>
public partial class MainWindow
{
    private void UpdatePlaylistBar()
    {
        bool inPlaylist = VM.InPlaylist;
        PlaylistBar.Visibility = inPlaylist ? Visibility.Visible : Visibility.Collapsed;
        PlaylistSaveButton.IsEnabled = VM.PlaylistDirty;
        UpdatePlaylistMoveButtons();
    }

    private void UpdatePlaylistMoveButtons()
    {
        bool any = FileListView.SelectedItems.Count > 0;
        PlaylistUpButton.IsEnabled   = any;
        PlaylistDownButton.IsEnabled = any;
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
            FileName = Path.GetFileName(current),
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
