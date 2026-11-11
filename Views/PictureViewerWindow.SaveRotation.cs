using System.IO;
using System.Windows;
using System.Windows.Input;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

// Turning a picture (Ctrl+K / Ctrl+L, the menu) used to be for viewing only. A small question of our own (not a message box, so the user can go
// on turning) now shows in the lower right corner while the picture is turned. Two kinds:
//  * the turn can be saved in the file (JPEG, PNG, BMP, still GIF, single-page TIFF: Helpers/PictureRotationSaver): "Do you want to save the
//    turned picture?"  Yes saves it and shows the file again, No keeps the turn for viewing only, Cancel puts the picture back.
//  * it cannot (animated GIF / PNG, multi-page TIFF, WebP, RAW, PSD, SVG, JXL, a read-only file, a picture inside a compressed file):
//    "Do you want to save a turned copy of this picture?" with the reason and the promise that the original is not changed. "Save Copy..."
//    asks where in a Save As box (PNG first, JPEG too, the original's name refused); Cancel there comes back to this question.
// Keys: Y = Yes / Save Copy, N or Enter = No (the default), Esc = Cancel. Going on to another picture, or closing, counts as No.
// A later setting may save at once, or never ask.
public partial class PictureViewerWindow
{
    private bool _savingRotation;
    private bool _copyMode;   // the question offers a copy (the file itself cannot be saved)

    /// <summary>Called after every turn: shows the question while the picture is turned, hides it otherwise.</summary>
    private void UpdateRotateSavePanel()
    {
        bool show = _picture != null && _quarterTurns != 0 && !_closed;
        if (show == (RotateSavePanel.Visibility == Visibility.Visible)) return;

        if (show)
        {
            _slideTimer.Stop();   // the question comes first
            RotateYes.IsEnabled = RotateNo.IsEnabled = RotateCancel.IsEnabled = true;
            if (PictureRotationSaver.CanSave(CurrentPath)) ShowSaveQuestion();
            else ShowCopyQuestion(PictureRotationSaver.WhyNotSavable(CurrentPath));
            RotateSavePanel.Visibility = Visibility.Visible;
            ShowCursor();   // the mouse is needed for the buttons
        }
        else HideRotateSavePanel(restartSlideShow: true);
    }

    private void ShowSaveQuestion()
    {
        _copyMode = false;
        RotateQuestion.Text = "Do you want to save the turned picture?";
        RotateHint.Text = "Cancel puts the picture back as it was. Enter means No.";
        RotateYes.Content = "Yes (Y)";
    }

    private void ShowCopyQuestion(string reason)
    {
        _copyMode = true;
        string only = _picture?.Frames != null || reason.Contains("page", StringComparison.OrdinalIgnoreCase)
            ? " The copy holds only the picture on show (the first frame or page)." : "";
        if (RawPreview.IsRaw(CurrentPath)) only += " The copy is made from the picture built into the file.";
        RotateQuestion.Text = "Do you want to save a turned copy of this picture?";
        RotateHint.Text = $"{reason}{only} The original file will not be changed: the copy is a new PNG or JPEG file. " +
                          "Cancel puts the picture back as it was. Enter means No.";
        RotateYes.Content = "Save Copy... (Y)";
    }

    private void HideRotateSavePanel(bool restartSlideShow)
    {
        if (RotateSavePanel.Visibility != Visibility.Visible) return;
        RotateSavePanel.Visibility = Visibility.Collapsed;
        ShowCursor();
        Viewport.Focus();
        if (restartSlideShow) RestartSlideTimer();
    }

    /// <summary>The keys of the question (see Window_PreviewKeyDown). True when the key was used.</summary>
    private bool HandleRotateSaveKey(KeyEventArgs e)
    {
        if (RotateSavePanel.Visibility != Visibility.Visible) return false;
        if (e.Key is not (Key.Y or Key.N or Key.Escape or Key.Return)) return false;

        e.Handled = true;
        if (e.IsRepeat || _savingRotation) return true;   // a save is under way: the keys wait, and Esc must not close the viewer meanwhile

        switch (e.Key)
        {
            case Key.Y:      RotateYes_Click(this, new RoutedEventArgs()); break;
            case Key.Escape: RotateCancel_Click(this, new RoutedEventArgs()); break;
            default:         RotateNo_Click(this, new RoutedEventArgs()); break;   // N and Enter
        }
        return true;
    }

    // A click on the panel is not a click on the picture (next / previous, panning).
    private void RotatePanel_MouseButton(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void RotateNo_Click(object sender, RoutedEventArgs e) => HideRotateSavePanel(restartSlideShow: true);

    private void RotateCancel_Click(object sender, RoutedEventArgs e)
    {
        _quarterTurns = 0;
        _zoom = 1;
        _offsetX = _offsetY = 0;
        UpdateTransform();
        HideRotateSavePanel(restartSlideShow: true);
    }

    private async void RotateYes_Click(object sender, RoutedEventArgs e)
    {
        if (_savingRotation || _picture == null) return;
        if (_copyMode) { await SaveCopyAsync(); return; }

        _savingRotation = true;
        RotateYes.IsEnabled = RotateNo.IsEnabled = RotateCancel.IsEnabled = false;

        string path = CurrentPath;
        int turns = _quarterTurns;
        var (w, h) = TurnedSize;
        int version = _loadVersion;

        string? problem = await Task.Run(() => PictureRotationSaver.Save(path, turns, (int)w, (int)h));
        _savingRotation = false;
        if (_closed) return;

        if (problem != null)
        {
            if (version != _loadVersion) return;
            RotateYes.IsEnabled = RotateNo.IsEnabled = RotateCancel.IsEnabled = true;

            // A limit of this kind of file (several pages, animated ...): offer a copy. Anything else is a real failure.
            if (PictureRotationSaver.IsLimit(problem)) ShowCopyQuestion(problem);
            else MessageBox.Show(this, $"The turned picture could not be saved.\n\n{problem}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Still on the same picture: show it again from the file (now it is turned for real).
        if (version == _loadVersion && string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            HideRotateSavePanel(restartSlideShow: false);
            ShowCurrent("Saved");
        }
    }

    // ── A turned copy ────────────────────────────────────────────────────────

    private async Task SaveCopyAsync()
    {
        if (_picture == null) return;
        string original = CurrentPath;

        // Where the Save As box starts: the folder of the picture; for one inside a compressed file, the folder of that file.
        string? folder = File.Exists(original) ? Path.GetDirectoryName(original)
                       : ArchiveSupport.DiskArchiveOf(original) is { } archive ? Path.GetDirectoryName(archive) : null;
        string stem = Path.GetFileNameWithoutExtension(original);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save a Turned Copy",
            Filter = "PNG Picture (*.png)|*.png|JPEG Picture (*.jpg)|*.jpg",
            FilterIndex = 1,
            DefaultExt = "png",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = stem + " (turned).png",
        };
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) dialog.InitialDirectory = folder;

        if (dialog.ShowDialog(this) != true) return;   // Cancel in the box: back to the question

        string target = dialog.FileName;
        bool confirmed = true;
        string ext = Path.GetExtension(target);
        if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            target += ".png";   // a picture is always saved as PNG or JPEG: keep the name the user typed and add the extension
            confirmed = false;
        }

        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(original), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "That is the name of the original picture, which is not changed here. Please choose another name for the copy.",
                            AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _savingRotation = true;
        RotateYes.IsEnabled = RotateNo.IsEnabled = RotateCancel.IsEnabled = false;
        var image = _picture.Image;
        int turns = _quarterTurns;
        int version = _loadVersion;

        string? problem = await Task.Run(() => PictureRotationSaver.SaveCopy(image, turns, target, confirmed));
        _savingRotation = false;
        if (_closed) return;
        if (version == _loadVersion) RotateYes.IsEnabled = RotateNo.IsEnabled = RotateCancel.IsEnabled = true;

        if (problem != null)
        {
            MessageBox.Show(this, $"The copy could not be saved.\n\n{problem}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The original stays on show, turned for viewing only as with No; the new file is in its folder.
        if (version == _loadVersion)
        {
            HideRotateSavePanel(restartSlideShow: true);
            ShowOverlay($"Saved copy: {Path.GetFileName(target)}");
        }
    }
}
