using System.IO;
using System.Windows;
using System.Windows.Input;
using SwewolfFileExplorer.ViewModels;
using SwewolfFileExplorer.Views;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The question before a delete, in one place for the list, the tree and the picture viewer (the user's rules, 2026-10-22):
/// <list type="bullet">
/// <item>Items that cannot reach the Recycle Bin, and Shift+Delete (permanent): always asked, never skipped.</item>
/// <item>Files: asked, with a check box "Don't ask again until the application is restarted". Once it is checked a file goes to the Recycle
/// Bin at once, with a quick flash message instead.</item>
/// <item>Folders: no check box. Asked only if the folder or a subfolder holds at least one file that is not hidden by Windows (Hidden or
/// System attribute: thumbnail files and the like do not count). An empty folder goes at once, with the flash message.</item>
/// </list>
/// A mix: the question is asked if any item needs it; the check box is only there when only files need it.
/// </summary>
public static class DeleteConfirm
{
    private const string CheckBoxText = "Don't ask again until the application is restarted";

    /// <summary>Set by the check box; lasts until the program ends (not saved).</summary>
    private static bool _skipFileQuestion;

    /// <summary>True if the folder or any subfolder holds a file that Windows does not hide. Stops at the first one.</summary>
    public static bool HasVisibleFile(string folder)
    {
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            try
            {
                foreach (var e in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos())
                {
                    if (e is DirectoryInfo d)
                    {
                        if ((d.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(d.FullName);   // junctions are not followed
                    }
                    else if (!HiddenItems.IsHiddenByAttributes(e.Attributes)) return true;
                }
            }
            catch { return true; }   // cannot look (no access ...): better to ask than to delete without a word
        }
        return false;
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    /// <summary>
    /// Asks what has to be asked. Returns true when the delete should go on (the answer was Yes, or nothing needed asking: then the flash
    /// message was shown). Shows the hourglass while folders are looked at.
    /// </summary>
    public static async Task<bool> AskAsync(Window? owner, IReadOnlyList<(string Path, string Name, bool IsDirectory)> items, bool permanent)
    {
        var names = items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count} items";
        var nl2   = Environment.NewLine;
        var bin   = IconHelper.GetRecycleBinIcon(full: true);

        if (permanent)
            return MessageBox.Show(owner,
                $"Are you sure you want to permanently delete {names}?{nl2}This cannot be undone.",
                "Confirm Permanent Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, bin) == MessageBoxResult.Yes;

        // What would not go to the Recycle Bin (a folder: only the drive is looked at, its size is not known).
        var noBin = items
            .Select(i => FileReplace.WhyNotRecycled(i.Path, i.IsDirectory ? 0 : SafeLength(i.Path)))
            .Where(r => r != null).Select(r => r!).ToList();
        if (noBin.Count > 0)
            return MessageBox.Show(owner,
                $"{(items.Count == 1 ? "This item" : $"{noBin.Count} of these {items.Count} items")} cannot be moved to the Recycle Bin ({noBin[0]}).{nl2}"
                + $"{(items.Count == 1 ? "It" : "They")} would be deleted permanently. This cannot be undone.{nl2}{nl2}Delete {names} anyway?",
                "Confirm Permanent Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, bin) == MessageBoxResult.Yes;

        // A bookmark (a shortcut in the Bookmarks folder, or a group) is only a bookmark, and it goes to the Recycle Bin: no question.
        if (items.All(i => FolderTabViewModel.IsUnderBookmarks(i.Path) && !FolderTabViewModel.IsBookmarksRoot(i.Path)))
        {
            await FlashMessage.Show(owner, $"Moving {names} to the Recycle Bin...");
            return true;
        }

        // Which folders hold a visible file (the hourglass shows the program is working; the search stops at the first file).
        var folders = items.Where(i => i.IsDirectory).Select(i => i.Path).ToList();
        var folderNeedsAsking = false;
        if (folders.Count > 0)
        {
            var old = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.Wait;
            try { folderNeedsAsking = await Task.Run(() => folders.Any(HasVisibleFile)); }
            finally { Mouse.OverrideCursor = old; }
        }

        var hasFiles = items.Any(i => !i.IsDirectory);
        var mustAsk  = folderNeedsAsking || (hasFiles && !_skipFileQuestion);
        if (!mustAsk)
        {
            await FlashMessage.Show(owner, $"Moving {names} to the Recycle Bin...");
            return true;
        }

        var question = $"Are you sure you want to move {names} to the Recycle Bin?";
        if (folderNeedsAsking)   // a folder with files in it: always asked, no check box
            return MessageBox.Show(owner, question, "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.None, bin) == MessageBoxResult.Yes;

        var result = MessageBox.ShowWithCheckBox(owner, question, "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question,
                                                 MessageBoxResult.None, bin, CheckBoxText, out var dontAsk);
        if (result != MessageBoxResult.Yes) return false;
        if (dontAsk) _skipFileQuestion = true;
        return true;
    }
}
