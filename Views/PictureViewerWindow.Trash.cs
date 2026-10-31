using System.IO;
using System.Windows;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

// "Move to Trash" (T, or the right-click menu) and Ctrl+Z. A picture that is not wanted is moved into a hidden subfolder called Trash next to it
// (an ordinary move on the same drive: instant, and what the library remembers about the file follows it), and the viewer goes on to the next
// picture. Nothing is deleted. Ctrl+Z takes the pictures back, the last one first, as long as this window is open. Not for pictures inside a
// compressed file or in a playlist.
public partial class PictureViewerWindow
{
    private const string TrashFolderName = "Trash";

    private readonly record struct TrashedPicture(string Original, string InTrash, int Index);
    private readonly Stack<TrashedPicture> _trashed = new();

    private void MenuTrash_Click(object sender, RoutedEventArgs e) => MoveToTrash();
    private void MenuUndoTrash_Click(object sender, RoutedEventArgs e) => UndoTrash();

    private void MoveToTrash()
    {
        if (_files.Count == 0 || _closed) return;
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

        string target;
        try
        {
            string trash = Path.Combine(folder, TrashFolderName);
            if (!Directory.Exists(trash))
            {
                var made = Directory.CreateDirectory(trash);
                made.Attributes |= FileAttributes.Hidden;
            }
            target = FreeName(trash, Path.GetFileName(path));
            File.Move(path, target);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cannot move '{Path.GetFileName(path)}' to the Trash folder: {ex.Message}",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        AnyDeleted = true;   // the Explorer reads its folder again when the viewer closes
        LibraryService.Move(new[] { (path, target) });
        _trashed.Push(new TrashedPicture(path, target, _index));

        _files.RemoveAt(_index);
        if (_files.Count == 0)
        {
            Close();
            return;
        }
        if (_index >= _files.Count) _index = _files.Count - 1;
        ShowCurrent("Moved to Trash folder\nCtrl+Z takes it back");
    }

    private void UndoTrash()
    {
        if (_closed) return;
        if (_trashed.Count == 0)
        {
            ShowOverlay("Nothing to take back from Trash");
            return;
        }

        var item = _trashed.Peek();
        if (!File.Exists(item.InTrash))
        {
            _trashed.Pop();
            ShowOverlay("The picture is not in the Trash folder any more");
            return;
        }
        if (File.Exists(item.Original) || Directory.Exists(item.Original))
        {
            ShowOverlay("Cannot take it back: the name is used again");   // stays on the stack, so it can be tried again
            return;
        }

        try
        {
            File.Move(item.InTrash, item.Original);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cannot take '{Path.GetFileName(item.Original)}' back from the Trash folder: {ex.Message}",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _trashed.Pop();
        AnyDeleted = true;
        LibraryService.Move(new[] { (item.InTrash, item.Original) });

        int index = Math.Clamp(item.Index, 0, _files.Count);
        _files.Insert(index, item.Original);
        _index = index;
        ShowCurrent("Taken back from Trash");
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
