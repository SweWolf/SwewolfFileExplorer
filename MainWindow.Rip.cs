using System.IO;
using System.Windows;
using SwewolfFileExplorer.Helpers;
using SwewolfFileExplorer.ViewModels;
using SwewolfFileExplorer.Views;

namespace SwewolfFileExplorer;

/// <summary>
/// The Rip button of the toolbar (to the right of Play), shown while the folder shown is an audio CD: it rips the selected tracks to music
/// files, or all tracks when none is selected (in the order of the list, top to bottom). The questions are <see cref="RipCdDialog"/>, the work
/// is <see cref="CdRipper"/> with the usual progress window and Cancel; afterwards one message says how it went (no read problems, or which
/// tracks may click or skip) and offers to open the output folder in a new tab. Plan: D:\Dev\dev-notes\FileExplorer-audio-cd-ripping-plan.md.
/// </summary>
public partial class MainWindow
{
    private bool _ripping;

    /// <summary>Move/Copy to Folder's "Open This Folder in a New Tab When Finished" box, for the rip that window may start (see <see cref="RipDroppedTracks"/>).</summary>
    private bool _openFolderAfterRip;

    /// <summary>The Rip button is there only while an audio CD is shown (and not while a rip runs: one at a time).</summary>
    private void UpdateRipButton()
    {
        bool show;
        try { show = VM.HasFolder && CdDiscInfo.IsAudioCdFolder(VM.CurrentPath); }
        catch { show = false; }

        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (RipButton.Visibility == visibility) return;
        RipButton.Visibility = visibility;
        Dispatcher.BeginInvoke(new Action(FitAddressBar), System.Windows.Threading.DispatcherPriority.Loaded);   // the address bar gives way
    }

    private async void RipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ripping) return;
        _ripping = true;
        RipButton.IsEnabled = false;
        try { await RipAudioCdAsync(); }
        catch (Exception ex)
        {
            LogFiles.AppendError($"Rip Audio CD failed: {ex}");
            MessageBox.Show($"The audio CD could not be ripped: {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _ripping = false;
            RipButton.IsEnabled = true;
        }
    }

    private async Task RipAudioCdAsync()
    {
        var folder = VM.CurrentPath;
        if (!CdDiscInfo.IsAudioCdFolder(folder))
        {
            UpdateRipButton();
            return;
        }

        // The tracks: the selected ones, else all, in the order of the list.
        var items = FileListView.Items.OfType<FileItemViewModel>()
            .Where(i => !i.IsDirectory && string.Equals(Path.GetExtension(i.FullPath), ".cda", StringComparison.OrdinalIgnoreCase))
            .Select(i => i.FullPath).ToList();
        var chosen = new HashSet<string>(FileListView.SelectedItems.OfType<FileItemViewModel>().Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
        await RipTracksAsync(Path.GetPathRoot(folder) ?? folder, items, items.Where(chosen.Contains).ToList(), null);
    }

    /// <summary>
    /// Audio CD tracks were pasted, dropped or sent (Move/Copy to Folder) into <paramref name="folder"/>: they cannot be copied (a .cda file is only
    /// a 44-byte stub), so the Rip questions are asked, with that folder fixed. The radio buttons still let the user rip the whole disc instead.
    /// </summary>
    private async void RipDroppedTracks(string folder, List<string> tracks)
    {
        bool openAfter = _openFolderAfterRip;   // set by Move/Copy to Folder only; a paste or a drag never opens the folder
        _openFolderAfterRip = false;
        if (_ripping) return;
        _ripping = true;
        RipButton.IsEnabled = false;
        try
        {
            var root = Path.GetPathRoot(tracks[0]) ?? tracks[0];
            List<string> all;
            try { all = Directory.GetFiles(root, "*.cda").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch { all = tracks; }
            if (all.Count == 0) all = tracks;
            await RipTracksAsync(root, all, tracks, folder, openAfter);
        }
        catch (Exception ex)
        {
            LogFiles.AppendError($"Rip Audio CD failed: {ex}");
            MessageBox.Show($"The audio CD could not be ripped: {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _ripping = false;
            RipButton.IsEnabled = true;
        }
    }

    /// <param name="root">The CD drive, such as E:\.</param>
    /// <param name="items">All the tracks of the disc (list order).</param>
    /// <param name="selected">The ones to start with (selected in the list, or dragged); none = all.</param>
    /// <param name="fixedFolder">The folder the tracks were dropped in (the user cannot change it), or null to ask.</param>
    /// <param name="openAfter">With a fixed folder: whether to open it in a new tab afterwards (no question; the user is in or dropped on that folder
    /// already). Null = the Rip button: the report asks.</param>
    private async Task RipTracksAsync(string root, List<string> items, List<string> selected, string? fixedFolder, bool? openAfter = null)
    {
        // ffmpeg does the encoding (the same ffmpeg the preview pictures use: next to this EXE, else in the PATH).
        if (FfmpegFrame.Find() == null)
        {
            MessageBox.Show("Ripping needs FFmpeg, and ffmpeg.exe was not found." + Environment.NewLine + Environment.NewLine +
                            "Install FFmpeg, or put ffmpeg.exe in the same folder as SweWolf File Explorer or in a folder in your PATH. Then try again.",
                            AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (items.Count == 0)
        {
            MessageBox.Show("There are no tracks to rip.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int selectedCount = selected.Count;
        var disc = CdDiscInfo.DriveTitle(root);
        var icon = IconHelper.GetIcon(root, true, 32);   // the same icon as the drive's node in the tree
        var dialog = RipCdDialog.Ask(this, disc, items.Count, selectedCount, icon, fixedFolder);
        if (dialog == null) return;

        // The dialog says which: all the tracks, or the selected ones (offered only when some, not all, are selected).
        bool all = dialog.AllTracks || selectedCount == 0;
        var files = all ? items : selected;

        try { Directory.CreateDirectory(dialog.FolderPath); }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot make the folder \"{dialog.FolderPath}\": {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var (jobs, cover) = await Task.Run(() => { var j = CdRipper.Plan(files, out var c); return (j, c); });
        var options = new CdRipper.Options
        {
            Root = root, Format = dialog.Format, QualityIndex = dialog.QualityIndex,
            Verify = dialog.Verify, OutputFolder = dialog.FolderPath, Cover = cover,
        };

        CdRipper.RipResult? rip = null;
        var target = Path.GetFileName(dialog.FolderPath.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(target)) target = dialog.FolderPath;
        await FolderTabViewModel.RunWithProgressAsync(async (progress, token) =>
        {
            rip = await CdRipper.RunAsync(jobs, options, progress, token);
            return new TransferResult(rip.Tracks.Where(t => t.OutputPath != null).Select(t => (t.Name, t.OutputPath!)).ToList(),
                                      rip.Tracks.Where(t => t.Failed).Select(t => (t.Name, t.Error!)).ToList(), rip.Canceled, jobs.Count);
        }, "Ripping", "", dialog.FolderPath, heading: $"Ripping {(jobs.Count == 1 ? "1 track" : $"{jobs.Count} tracks")} to '{target}'");

        if (rip == null) return;
        foreach (var t in rip.Tracks.Where(t => t.Failed || t.Damaged || t.TagError != null))
            LogFiles.AppendError($"Rip Audio CD, track {t.Number} ({t.Name}): " + (t.Error ?? (t.Damaged
                ? $"read problems: {t.UnreadableSectors} unreadable sectors, {t.UnresolvedChunks} unresolved places (around {t.WhereText})" : $"tags: {t.TagError}")));

        ShowRipReport(rip, dialog.FolderPath, jobs.Count, dialog.Verify, all, fixedFolder != null ? openAfter ?? false : null);
    }

    /// <summary>
    /// One message about how it went. After the Rip button it also offers to open the output folder in a new tab. For tracks copied or dropped
    /// into a folder (<paramref name="openFolder"/> not null) there is no such question: the message is only told, and the folder is opened in a
    /// new tab when Move/Copy to Folder's box said so and at least one file was made (when nothing was made there is nothing to look at).
    /// </summary>
    private void ShowRipReport(CdRipper.RipResult rip, string folder, int total, bool verify, bool allTracks, bool? openFolder = null)
    {
        var done = rip.Tracks.Where(t => t.OutputPath != null).ToList();
        if (rip.Canceled && done.Count == 0)
        {
            FlashTitle($"{BaseTitle} - Ripping canceled");
            return;
        }

        var text = CdRipper.ReportText(rip, folder, total, verify, allTracks, out bool problems, askToOpen: openFolder == null);

        if (openFolder != null)
        {
            MessageBox.Show(this, text, AppInfo.Name, MessageBoxButton.OK, problems ? MessageBoxImage.Warning : MessageBoxImage.Information);
            if (openFolder == true && done.Count > 0) ShowFolderInTab(folder, done.Select(t => t.OutputPath!).ToList());
            return;
        }

        var answer = MessageBox.Show(this, text, AppInfo.Name, MessageBoxButton.YesNo,
                                     problems ? MessageBoxImage.Warning : MessageBoxImage.Information, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Yes && done.Count > 0)
            ShowFolderInTab(folder, done.Select(t => t.OutputPath!).ToList());
        else if (answer == MessageBoxResult.Yes)
            VM.OpenNewTab(folder, activate: true);
    }
}
