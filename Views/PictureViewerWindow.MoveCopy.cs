using System.IO;
using System.Windows;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

// "Move/Copy to Folder..." in the right-click menu: the same window and the same moving and copying as in the Explorer (progress window, questions
// about names that are taken, the library follows). After a move the picture has left the folder, so the viewer goes on to the next one; after
// a copy it stays on the same picture. Not for pictures inside a compressed file or in a playlist.
public partial class PictureViewerWindow
{
    /// <summary>What a Move/Copy to Folder did: the window of the Explorer asks where to, does it, and says what was done (null: nothing).</summary>
    public sealed record MoveCopyOutcome(bool IsMove, string Folder, List<(string From, string To)> Done);

    /// <summary>Set by the Explorer: asks where to (the dialog is owned by the first window), moves or copies the file, returns the outcome.</summary>
    public Func<Window, string, Task<MoveCopyOutcome?>>? MoveCopy { get; set; }

    private bool _moveCopyBusy;

    private void MenuMoveCopy_Click(object sender, RoutedEventArgs e) => MoveOrCopyCurrent();

    private async void MoveOrCopyCurrent()
    {
        if (_files.Count == 0 || _closed || _moveCopyBusy || _trashBusy || MoveCopy == null) return;
        string path = CurrentPath;

        if (IsPlaylist || !File.Exists(path))
        {
            ShowOverlay("Move/Copy to Folder does not work for\npictures in a compressed file or a playlist");
            return;
        }

        _moveCopyBusy = true;
        MoveCopyOutcome? outcome;
        try
        {
            outcome = await MoveCopy(this, path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cannot move or copy '{Path.GetFileName(path)}': {ex.Message}",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally { _moveCopyBusy = false; }

        if (_closed || outcome == null) return;
        bool done = outcome.Done.Any(d => string.Equals(d.From, path, StringComparison.OrdinalIgnoreCase));
        if (!done) return;   // canceled, skipped or failed (the Explorer already said what went wrong)

        string folderName = Path.GetFileName(outcome.Folder.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(folderName)) folderName = outcome.Folder;

        if (!outcome.IsMove)
        {
            ShowCurrent($"Copied to {folderName}");   // the same picture stays
            return;
        }

        // Moved: the picture is not in this folder any more.
        AnyDeleted = true;   // the Explorer reads its folder again when the viewer closes
        int at = _files.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return;
        _files.RemoveAt(at);
        if (_files.Count == 0)
        {
            Close();
            return;
        }
        _index = Math.Min(at, _files.Count - 1);
        ShowCurrent($"Moved to {folderName}");
    }
}
