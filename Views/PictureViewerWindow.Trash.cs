using System.IO;
using System.Windows;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

// "Move to Trash" (T, or the right-click menu) and Ctrl+Z. A picture that is not wanted is moved into a hidden subfolder called Trash next to it
// (an ordinary move on the same drive: instant, and what the library remembers about the file follows it), and the viewer goes on to the next
// picture. Nothing is deleted. Ctrl+Z takes the pictures back, the last one first, as long as this window is open. Not for pictures inside a
// compressed file or in a playlist.
//
// The files that belong to the picture go with it: the same name with .xmp .aae .dop .pp3 .json .txt (photo.xmp), the full name with .xmp .json
// .txt (photo.jpg.xmp), and the other half of a RAW + JPEG pair (photo.nef for photo.jpg, and the other way round). They are looked for by
// trying those names, never by listing the folder, and the moving is done on a background thread (a folder on a slow drive).
public partial class PictureViewerWindow
{
    private const string TrashFolderName = "Trash";

    private static readonly string[] SideExtensionsByStem     = { ".xmp", ".aae", ".dop", ".pp3", ".json", ".txt" };
    private static readonly string[] SideExtensionsByFullName = { ".xmp", ".json", ".txt" };
    private static readonly string[] RawExtensions  = { ".nef", ".nrw", ".dng", ".cr2", ".cr3", ".arw", ".orf", ".rw2", ".raf", ".pef", ".srw" };
    private static readonly string[] JpegExtensions = { ".jpg", ".jpeg" };

    /// <summary>One move to the Trash folder: everything that was moved (the picture first), and which pictures left the list, where.</summary>
    private sealed record TrashedGroup(List<(string From, string To)> Moved, List<(string Path, int Index)> RemovedFromList);

    private readonly Stack<TrashedGroup> _trashed = new();
    private bool _trashBusy;

    private void MenuTrash_Click(object sender, RoutedEventArgs e) => MoveToTrash();
    private void MenuUndoTrash_Click(object sender, RoutedEventArgs e) => UndoTrash();

    private async void MoveToTrash()
    {
        if (_files.Count == 0 || _closed || _trashBusy) return;
        string path = CurrentPath;

        if (IsPlaylist || !File.Exists(path))
        {
            ShowOverlay("The Trash feature does not work for\npictures in a compressed file or a playlist");
            return;
        }

        string folder = Path.GetDirectoryName(path) ?? "";
        if (string.Equals(Path.GetFileName(folder), TrashFolderName, StringComparison.OrdinalIgnoreCase))
        {
            ShowOverlay("This picture is in a Trash folder already");
            return;
        }

        // The files that belong to the picture (photo.xmp, the RAW of a JPEG ...): do they go too?
        bool includeSides = false;
        var sides = FindSideFiles(path, folder);
        if (sides.Count > 0)
        {
            int answer = AskAboutSideFiles("Move to Trash Folder", $"You are moving \"{Path.GetFileName(path)}\" to the Trash folder.", "to the Trash folder", "Move", sides);
            if (answer == 2) return;
            includeSides = answer == 0;
        }

        _trashBusy = true;
        List<(string From, string To)> moved;
        try
        {
            moved = await Task.Run(() => TrashFiles(path, folder, includeSides));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cannot move '{Path.GetFileName(path)}' to the Trash folder: {ex.Message}",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally { _trashBusy = false; }

        AnyDeleted = true;   // the Explorer reads its folder again when the viewer closes
        LibraryService.Move(moved);
        FileUndo.Set(new PairsUndo("Move", moved));   // the Explorer's Undo can take it back too (same folder, so the same drive); the viewer's own Ctrl+Z does the same

        // Every moved picture leaves the list (the other half of a RAW + JPEG pair is a picture too).
        var gone = new HashSet<string>(moved.Select(m => m.From), StringComparer.OrdinalIgnoreCase);
        var removed = new List<(string Path, int Index)>();
        for (int i = 0; i < _files.Count; i++)
            if (gone.Contains(_files[i])) removed.Add((_files[i], i));
        int mainIndex = removed.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)).Index;
        int before = _files.Take(mainIndex).Count(f => !gone.Contains(f));   // where the next picture will be
        _files.RemoveAll(f => gone.Contains(f));
        _trashed.Push(new TrashedGroup(moved, removed));
        if (_closed) return;

        if (_files.Count == 0)
        {
            Close();
            return;
        }
        _index = Math.Min(before, _files.Count - 1);

        int others = moved.Count - 1;
        ShowCurrent("Moved to Trash folder" + (others > 0 ? $" (and {others} related file{(others == 1 ? "" : "s")})" : "") + "\nCtrl+Z takes it back");
    }

    /// <summary>Moves the picture and its side files into the Trash folder (made, and hidden, if needed). The picture must move, or this throws.</summary>
    private static List<(string From, string To)> TrashFiles(string path, string folder, bool includeSides)
    {
        string trash = Path.Combine(folder, TrashFolderName);
        if (!Directory.Exists(trash))
        {
            var made = Directory.CreateDirectory(trash);
            made.Attributes |= FileAttributes.Hidden;
        }

        var moved = new List<(string From, string To)>();
        string target = FreeName(trash, Path.GetFileName(path));
        File.Move(path, target);
        moved.Add((path, target));

        // The side files keep the name the picture got in the Trash folder (it may have a number).
        string newStem = Path.GetFileNameWithoutExtension(target), newFull = Path.GetFileName(target);
        var wanted = includeSides ? SideCandidates(path, folder, newStem, newFull) : new List<(string From, string NewName)>();

        foreach (var (from, newName) in wanted)
        {
            try
            {
                if (!File.Exists(from) || moved.Any(m => string.Equals(m.From, from, StringComparison.OrdinalIgnoreCase))) continue;
                string to = FreeName(trash, newName);
                File.Move(from, to);
                moved.Add((from, to));
            }
            catch { /* a side file that cannot move (in use, no access) stays where it is */ }
        }
        return moved;
    }

    /// <summary>The names that belong to a picture (side files by the same name, the other half of a RAW + JPEG pair), with the name each gets when the picture is renamed to <paramref name="newStem"/> / <paramref name="newFull"/>. Not checked for existence.</summary>
    private static List<(string From, string NewName)> SideCandidates(string path, string folder, string newStem, string newFull)
    {
        string stem = Path.GetFileNameWithoutExtension(path), full = Path.GetFileName(path), ext = Path.GetExtension(path);

        var wanted = new List<(string From, string NewName)>();
        foreach (var e in SideExtensionsByStem)     wanted.Add((Path.Combine(folder, stem + e), newStem + e));
        foreach (var e in SideExtensionsByFullName) wanted.Add((Path.Combine(folder, full + e), newFull + e));
        string[] partners = Array.Exists(RawExtensions, r => r.Equals(ext, StringComparison.OrdinalIgnoreCase)) ? JpegExtensions
                          : Array.Exists(JpegExtensions, j => j.Equals(ext, StringComparison.OrdinalIgnoreCase)) ? RawExtensions
                          : Array.Empty<string>();
        foreach (var e in partners) wanted.Add((Path.Combine(folder, stem + e), newStem + e));
        return wanted;
    }

    /// <summary>The files that exist beside <paramref name="path"/> and belong to it (found by trying the names, never by listing the folder).</summary>
    private static List<string> FindSideFiles(string path, string folder)
    {
        var found = new List<string>();
        foreach (var (from, _) in SideCandidates(path, folder, "", ""))
            if (!string.Equals(from, path, StringComparison.OrdinalIgnoreCase) && File.Exists(from)
                && !found.Contains(from, StringComparer.OrdinalIgnoreCase))
                found.Add(from);
        return found;
    }

    /// <summary>
    /// "Do you also want to move these files?" (as for video and music files in the Explorer). 0 = Yes (them too), 1 = No (only the picture),
    /// 2 = Cancel. <paramref name="where"/> is "to the Trash folder" or "to the folder"; <paramref name="verb"/> is Move or Copy.
    /// </summary>
    private int AskAboutSideFiles(string title, string head, string where, string verb, IReadOnlyList<string> sides)
    {
        var list = RelatedFiles.Describe(sides.Select(Path.GetFileName).ToList()!, Array.Empty<string>());
        string lower = verb.ToLowerInvariant();
        string message = $"{head}\n\nDo you also want to {lower} these files {where}?\n\n{list}\n\n" +
                         $"Yes: {verb} them too.\nNo: {verb} only the picture.\nCancel: Change nothing.";

        var dialog = new ChoiceDialog(message, new[] { "Yes", "No", "Cancel" }, 0, 2) { Title = title, FontSize = 16, MinWidth = 760, Owner = this };
        dialog.ShowDialog();
        return dialog.Result;
    }

    private async void UndoTrash()
    {
        if (_closed || _trashBusy) return;
        if (_trashed.Count == 0)
        {
            ShowOverlay("Nothing to take back from Trash");
            return;
        }

        var group = _trashed.Peek();
        var (mainFrom, mainTo) = group.Moved[0];
        if (!File.Exists(mainTo))
        {
            _trashed.Pop();
            ShowOverlay("The picture is not in the Trash folder any more");
            return;
        }
        if (File.Exists(mainFrom) || Directory.Exists(mainFrom))
        {
            ShowOverlay("Cannot take it back: the name is used again");   // stays on the stack, so it can be tried again
            return;
        }

        _trashBusy = true;
        List<(string From, string To)> back;
        try
        {
            back = await Task.Run(() => RestoreFiles(group));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cannot take '{Path.GetFileName(mainFrom)}' back from the Trash folder: {ex.Message}",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally { _trashBusy = false; }

        _trashed.Pop();
        AnyDeleted = true;
        LibraryService.Move(back.Select(b => (From: b.To, To: b.From)));   // (the library follows them home)
        FileUndo.Clear();   // taken back here: the Explorer's Undo must not offer the same step again
        if (_closed) return;

        // The pictures come back to the places they had in the list.
        foreach (var (p, index) in group.RemovedFromList)
            if (File.Exists(p) && !_files.Contains(p, StringComparer.OrdinalIgnoreCase))
                _files.Insert(Math.Clamp(index, 0, _files.Count), p);
        _index = Math.Max(0, _files.FindIndex(f => string.Equals(f, mainFrom, StringComparison.OrdinalIgnoreCase)));

        int missed = group.Moved.Count - back.Count;
        ShowCurrent("Taken back from Trash" + (missed > 0 ? $"\n{missed} related file{(missed == 1 ? "" : "s")} could not be taken back" : ""));
    }

    /// <summary>Moves what is in the group back. Returns the pairs (original, in Trash) that were moved; the picture must move, or this throws.</summary>
    private static List<(string From, string To)> RestoreFiles(TrashedGroup group)
    {
        var done = new List<(string From, string To)>();
        for (int i = 0; i < group.Moved.Count; i++)
        {
            var (from, to) = group.Moved[i];
            try
            {
                if (!File.Exists(to) || File.Exists(from) || Directory.Exists(from)) { if (i == 0) throw new IOException("The name is used again."); continue; }
                File.Move(to, from);
                done.Add((from, to));
            }
            catch when (i > 0) { /* a side file that cannot move stays in the Trash folder */ }
        }
        return done;
    }

    /// <summary>A name that is free in the folder: the file's own, else "name (2).ext", "name (3).ext" ...</summary>
    private static string FreeName(string folder, string fileName)
    {
        string full = Path.Combine(folder, fileName);
        if (!File.Exists(full) && !Directory.Exists(full)) return full;

        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        for (int n = 2; ; n++)
        {
            full = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!File.Exists(full) && !Directory.Exists(full)) return full;
        }
    }
}
