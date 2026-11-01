using System.IO;
using System.Windows;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

// "Move/Copy to Folder..." in the right-click menu (key M): the same window and the same moving and copying as in the Explorer (progress window,
// questions about names that are taken, the library follows). If files belong to the picture (photo.xmp, the RAW of a JPEG ...) the user is asked
// whether they go too, as for video and music files. After a move the picture has left the folder, so the viewer goes on to the next one; after
// a copy it stays on the same picture. Not for pictures inside a compressed file or in a playlist.
public partial class PictureViewerWindow
{
    /// <summary>What a Move/Copy to Folder did: the window of the Explorer asks where to, does it, and says what was done (null: nothing).</summary>
    public sealed record MoveCopyOutcome(bool IsMove, string Folder, List<(string From, string To)> Done);

    /// <summary>
    /// Set by the Explorer: asks where to (the dialog is owned by the first window), then calls the second function with "is it a move" to learn which
    /// side files go along (null = the user canceled), moves or copies, and returns the outcome.
    /// </summary>
    public Func<Window, string, Func<bool, IReadOnlyList<string>?>, Task<MoveCopyOutcome?>>? MoveCopy { get; set; }

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

        string folder = Path.GetDirectoryName(path) ?? "";

        // Asked after the folder is chosen, when it is known whether it is a move or a copy.
        IReadOnlyList<string>? SidesFor(bool isMove)
        {
            var sides = FindSideFiles(path, folder);
            if (sides.Count == 0) return Array.Empty<string>();

            string verb = isMove ? "Move" : "Copy";
            int answer = AskAboutSideFiles(isMove ? "Move File" : "Copy File",
                $"You are {(isMove ? "moving" : "copying")} \"{Path.GetFileName(path)}\".", "to the same folder", verb, sides);
            return answer == 2 ? null : answer == 0 ? sides : Array.Empty<string>();
        }

        _moveCopyBusy = true;
        MoveCopyOutcome? outcome;
        try
        {
            outcome = await MoveCopy(this, path, SidesFor);
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
        int others = outcome.Done.Count - 1;
        string more = others > 0 ? $" (and {others} related file{(others == 1 ? "" : "s")})" : "";

        if (!outcome.IsMove)
        {
            ShowCurrent($"Copied to {folderName}{more}");   // the same picture stays
            return;
        }

        // Moved: the picture (and the other half of a RAW + JPEG pair, which is a picture too) is not in this folder any more.
        AnyDeleted = true;   // the Explorer reads its folder again when the viewer closes
        var gone = new HashSet<string>(outcome.Done.Select(d => d.From), StringComparer.OrdinalIgnoreCase);
        int at = _files.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return;
        int next = _files.Take(at).Count(f => !gone.Contains(f));   // where the next picture will be
        _files.RemoveAll(f => gone.Contains(f));
        if (_files.Count == 0)
        {
            Close();
            return;
        }
        _index = Math.Min(next, _files.Count - 1);
        ShowCurrent($"Moved to {folderName}{more}");
    }
}
