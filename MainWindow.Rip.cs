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

        // ffmpeg does the encoding (the same ffmpeg the preview pictures use: next to this EXE, else in the PATH).
        if (FfmpegFrame.Find() == null)
        {
            MessageBox.Show("Ripping needs FFmpeg, and ffmpeg.exe was not found." + Environment.NewLine + Environment.NewLine +
                            "Install FFmpeg, or put ffmpeg.exe in the same folder as SweWolf File Explorer or in a folder in your PATH. Then try again.",
                            AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // The tracks: the selected ones, else all, in the order of the list.
        var items = FileListView.Items.OfType<FileItemViewModel>()
            .Where(i => !i.IsDirectory && string.Equals(Path.GetExtension(i.FullPath), ".cda", StringComparison.OrdinalIgnoreCase)).ToList();
        var chosen = new HashSet<string>(FileListView.SelectedItems.OfType<FileItemViewModel>().Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
        bool all = !items.Any(i => chosen.Contains(i.FullPath));
        var files = (all ? items : items.Where(i => chosen.Contains(i.FullPath)).ToList()).Select(i => i.FullPath).ToList();
        if (files.Count == 0)
        {
            MessageBox.Show("There are no tracks to rip.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var disc = CdDiscInfo.DriveTitle(Path.GetPathRoot(folder) ?? folder);
        string what = files.Count == 1 ? "the selected track" : all ? $"all {files.Count} tracks" : $"the {files.Count} selected tracks";
        var dialog = RipCdDialog.Ask(this, disc != null
            ? $"Rip {what} of \"{disc}\" to music files."
            : $"Rip {what} of the audio CD to music files.");
        if (dialog == null) return;

        try { Directory.CreateDirectory(dialog.FolderPath); }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot make the folder \"{dialog.FolderPath}\": {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var (jobs, cover) = await Task.Run(() => { var j = CdRipper.Plan(files, out var c); return (j, c); });
        var options = new CdRipper.Options
        {
            Root = Path.GetPathRoot(folder)!, Format = dialog.Format, QualityIndex = dialog.QualityIndex,
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

        ShowRipReport(rip, dialog.FolderPath, jobs.Count, dialog.Verify, all);
    }

    /// <summary>One message about how it went, and the offer to open the output folder in a new tab.</summary>
    private void ShowRipReport(CdRipper.RipResult rip, string folder, int total, bool verify, bool allTracks)
    {
        var done = rip.Tracks.Where(t => t.OutputPath != null).ToList();
        if (rip.Canceled && done.Count == 0)
        {
            FlashTitle($"{BaseTitle} - Ripping canceled");
            return;
        }

        var text = CdRipper.ReportText(rip, folder, total, verify, allTracks, out bool problems);

        var answer = MessageBox.Show(this, text, AppInfo.Name, MessageBoxButton.YesNo,
                                     problems ? MessageBoxImage.Warning : MessageBoxImage.Information, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Yes && done.Count > 0)
            ShowFolderInTab(folder, done.Select(t => t.OutputPath!).ToList());
        else if (answer == MessageBoxResult.Yes)
            VM.OpenNewTab(folder, activate: true);
    }
}
