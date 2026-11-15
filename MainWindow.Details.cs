using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SwewolfFileExplorer.Helpers;
using SwewolfFileExplorer.ViewModels;

namespace SwewolfFileExplorer;

/// <summary>
/// The details block of the info panel (left of the preview picture, like Explorer's Details pane): the name of the selected item and a few
/// rows that fit its kind (a picture, music, a video, a program, a folder, or the count and total size of several items). A row without a
/// value is left out. What takes a look into the file (length, size in pixels, tags) is read in the background.
/// </summary>
public partial class MainWindow
{
    private int _detailsVersion;

    /// <summary>Fills the details block for what is selected in the list (nothing selected: the block is hidden).</summary>
    private async void ShowDetails(IList<FileItemViewModel> selected)
    {
        int version = ++_detailsVersion;

        if (!VM.HasFolder || !VM.ShowInfoPanel)   // This PC, or the user hid the panel (the "x" far right)
        {
            DetailsPane.Visibility = Visibility.Collapsed;
            return;
        }

        if (selected.Count == 0)
        {
            // Nothing is selected in the list: a packed file or a playlist that was clicked in the tree is shown (as the "folder" it is)
            // by its own details. Any other folder shows nothing.
            var node = VM.CurrentPath;
            if (!string.IsNullOrEmpty(node) && (IsArchiveFileOnDisk(node) || PlaylistFile.IsPlaylist(node)))
            {
                try
                {
                    var file = new System.IO.FileInfo(node);
                    bool playlist = PlaylistFile.IsPlaylist(node);
                    RenderDetails(file.Name, new List<(string, string)>
                    {
                        ("Type", IconHelper.GetTypeNameForName(file.Name)),
                        (playlist ? "Entries" : "Items", VM.CurrentItems.Count.ToString()),
                        ("Size", DriveRow.FormatSize(file.Length)),
                        ("Date Modified", Converters.DateTimeConverter.Format(file.LastWriteTime)),
                        ("Date Created", Converters.DateTimeConverter.Format(file.CreationTime))
                    }, nodePath: node, fileForViewer: true);
                    return;
                }
                catch { /* gone or no access: nothing to show */ }
            }
            DetailsPane.Visibility = Visibility.Collapsed;
            return;
        }

        if (selected.Count > 1)
        {
            long total = selected.Where(i => !i.IsDirectory && i.Size > 0).Sum(i => i.Size);
            int folders = selected.Count(i => i.IsDirectory);
            RenderDetails($"{selected.Count} items selected", new List<(string, string)>
            {
                ("Folders", folders > 0 ? folders.ToString() : ""),
                ("Files", (selected.Count - folders).ToString()),
                ("Total Size", total > 0 ? DriveRow.FormatSize(total) : "")
            });
            return;
        }

        var item = selected[0];
        var rows = new List<(string, string)>();
        void Row(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) rows.Add((label, value!)); }
        string size     = item.IsDirectory || item.Size < 0 ? "" : DriveRow.FormatSize(item.Size);
        string modified = item.DateModified == default ? "" : Converters.DateTimeConverter.Format(item.DateModified);

        // What needs a look into the file is read in the background (pictures, music, video, programs; not inside an archive).
        AudioTags? tags = null;
        if (!item.IsDirectory && !item.IsInArchive && AudioTagReader.HasMediaInfo(item.FullPath))
        {
            string path = item.FullPath;
            tags = await Task.Run(() => AudioTagReader.Read(path));
            if (version != _detailsVersion) return;   // another item was selected meanwhile
        }

        string length  = tags is { Duration.TotalSeconds: >= 0.5 } ? FormatLength(tags.Duration) : "";
        string bitrate = tags is { BitrateKbps: > 0 } ? $"{tags.BitrateKbps} kbps" : "";
        string dims    = tags is { Width: > 0, Height: > 0 } ? $"{tags.Width} x {tags.Height}" : "";

        if (item.IsDirectory)
        {
            Row("Type", item.TypeName);
            Row("Date Modified", modified);
        }
        else if (AudioTagReader.HasTags(item.FullPath) || AudioTagReader.IsCdTrackName(item.FullPath))   // music (a CD track too)
        {
            Row("Type", item.TypeName);
            Row("Artist", tags?.Artist);
            Row("Title", tags?.Title);
            Row("Track", tags is { TrackNumber: > 0 } ? tags.TrackNumber.ToString() : "");
            Row("Length", length);
            Row("Bitrate", bitrate);
            bool cdTrack = AudioTagReader.IsCdTrackName(item.FullPath);
            Row("Size", cdTrack ? "" : size);   // a CD track's 44-byte stub says nothing
            Row("Date Modified", cdTrack ? "" : modified);   // nor does its date (always 1994-12-31)
        }
        else if (MediaPlayerLauncher.IsVideo(item.FullPath))
        {
            Row("Type", item.TypeName);
            Row("Title", tags?.Title);   // the title in the video file itself (mkv, mp4 ...)
            Row("Length", length);
            Row("Dimensions", dims);
            Row("Bitrate", bitrate);
            Row("Size", size);
            Row("Date Modified", modified);
        }
        else if (ThumbnailLoader.IsImage(item.FullPath))
        {
            Row("Type", item.TypeName);
            Row("Dimensions", dims);
            Row("Size", size);
            Row("Date Taken", tags?.DateTaken is { } taken ? Converters.DateTimeConverter.Format(taken) : "");   // the EXIF date, when the picture has one
            Row("Date Modified", modified);
        }
        else if (AudioTagReader.HasVersionInfo(item.FullPath))   // a program or library
        {
            Row("Type", item.TypeName);
            Row("Product Name", tags?.ProductName);
            Row("Product Version", tags?.ProductVersion);
            Row("Size", size);
            Row("Date Modified", modified);
        }
        else
        {
            Row("Type", item.TypeName);
            Row("Size", size);
            Row("Date Modified", modified);
            Row("Date Created", item.DateCreated == default ? "" : Converters.DateTimeConverter.Format(item.DateCreated));
        }

        Row("Rating", item.RatingText);
        Row("Expectation", item.ExpectationText);

        // A track of a known audio CD is headed "Artist - Title"; the file name (Track03.cda) stays when the disc is not known (yet). The
        // lookup raises CdDiscInfo.Updated, which calls ShowDetails again, so the header follows by itself when the answer comes later.
        string header = item.DisplayName;
        if (!item.IsDirectory && AudioTagReader.IsCdTrackName(item.FullPath) && tags is { } cd && !string.IsNullOrWhiteSpace(cd.Title))
        {
            header = string.IsNullOrWhiteSpace(cd.Artist) ? cd.Title.Trim() : $"{cd.Artist.Trim()} - {cd.Title.Trim()}";
        }

        RenderDetails(header, rows, fileForViewer: !item.IsDirectory);
    }

    private static string FormatLength(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes}:{d.Seconds:00}";

    /// <summary>The name in bold on top (cut with "..." when long, the whole name as a tooltip), then the rows: a grey label and its value.</summary>
    private void RenderDetails(string title, List<(string Label, string Value)> rows, string? nodePath = null, bool fileForViewer = false)
    {
        DetailsPanel.Children.Clear();

        var name = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 4),
                                   TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = title };
        DetailsPanel.Children.Add(name);

        foreach (var (label, value) in rows)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labelText = new TextBlock { Text = label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            labelText.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.GrayTextBrushKey);
            var valueText = new TextBlock { Text = value, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value };
            Grid.SetColumn(valueText, 1);

            grid.Children.Add(labelText);
            grid.Children.Add(valueText);
            DetailsPanel.Children.Add(grid);
        }

        DetailsScroll.ScrollToTop();
        DetailsPane.Visibility = Visibility.Visible;
        UpdateDetailsButtons(nodePath, fileForViewer);
    }

    // ── The small buttons under the details, and hiding them ─────────────────

    /// <summary>The packed file or playlist whose own details are shown (nothing is selected in the list); null for a selection.</summary>
    private string? _detailsNodePath;

    /// <summary>
    /// Properties is there for everything on the disk (not for what is inside a packed file); File Info Viewer only for exactly one file
    /// (or the packed file / playlist shown), and only when that program is found.
    /// </summary>
    private void UpdateDetailsButtons(string? nodePath, bool fileForViewer)
    {
        _detailsNodePath = nodePath;
        DetailsPropertiesButton.Visibility = nodePath != null || !VM.InArchive ? Visibility.Visible : Visibility.Collapsed;

        var exe = fileForViewer ? FileInfoViewerLauncher.FindExe() : null;
        DetailsFileInfoButton.Visibility = fileForViewer ? Visibility.Visible : Visibility.Collapsed;
        if (exe != null) DetailsFileInfoIcon.Source = FileInfoViewerLauncher.GetIcon(exe);
        else if (DetailsFileInfoIcon.Source == null) DetailsFileInfoIcon.Source = IconHelper.GetIconForName("File Info Viewer.exe", false);   // plain program icon until the program is found
    }

    private void DetailsProperties_Click(object sender, RoutedEventArgs e)
    {
        if (_detailsNodePath != null)
        {
            try { PropertiesHelper.Show(new[] { _detailsNodePath }, OnPropertiesChanged); }
            catch (Exception ex) { MessageBox.Show(this, $"Cannot show properties: {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        else ShowProperties();   // the selected items, as the menu's Properties
    }

    private void DetailsFileInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_detailsNodePath != null) { if (System.IO.File.Exists(_detailsNodePath)) FileInfoViewerLauncher.Open(_detailsNodePath); }
        else CtxFileInfo_Click(sender, e);   // the one selected file, as the menu's File Info Viewer
    }

}
