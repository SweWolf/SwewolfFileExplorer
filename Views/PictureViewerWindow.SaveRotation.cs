using System.Windows;
using System.Windows.Input;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

// Turning a picture (Ctrl+K / Ctrl+L, the menu) used to be for viewing only. For a JPEG or a PNG on the disk a small question now shows in the
// lower right corner of the viewer (our own panel, not a message box, so the user can go on turning): "Do you want to save the turned
// picture?" Yes saves it (Helpers/PictureRotationSaver), No keeps the turn for viewing only (as before), Cancel puts the picture back as it was.
// Going on to another picture, or closing, counts as No. A later setting may save at once, or never ask.
public partial class PictureViewerWindow
{
    private bool _savingRotation;

    /// <summary>Called after every turn: shows the question while the picture is turned and can be saved, hides it otherwise.</summary>
    private void UpdateRotateSavePanel()
    {
        bool show = _picture != null && _picture.Frames == null && _quarterTurns != 0 && !_closed && PictureRotationSaver.CanSave(CurrentPath);
        if (show == (RotateSavePanel.Visibility == Visibility.Visible)) return;

        if (show)
        {
            _slideTimer.Stop();   // the question comes first
            RotateYes.IsEnabled = RotateNo.IsEnabled = RotateCancel.IsEnabled = true;
            RotateSavePanel.Visibility = Visibility.Visible;
            ShowCursor();   // the mouse is needed for the buttons
        }
        else HideRotateSavePanel(restartSlideShow: true);
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
            if (version == _loadVersion) RotateYes.IsEnabled = RotateNo.IsEnabled = RotateCancel.IsEnabled = true;
            MessageBox.Show(this, $"The turned picture could not be saved.\n\n{problem}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Still on the same picture: show it again from the file (now it is turned for real).
        if (version == _loadVersion && string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            HideRotateSavePanel(restartSlideShow: false);
            ShowCurrent("Saved");
        }
    }
}
