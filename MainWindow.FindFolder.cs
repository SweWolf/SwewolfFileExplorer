using System.IO;
using System.Windows;
using SwewolfFileExplorer.Helpers;
using SwewolfFileExplorer.Views;

namespace SwewolfFileExplorer;

/// <summary>
/// Find Folder (2026-11-27): the magnifier above the tree and "Find Folder" in the tree's right-click menu open the <see cref="FindFolderWindow"/>,
/// a small window beside this one that reads the folders below the folder shown (or the folder under the mouse) and filters them as the user
/// types. One window at a time: asking again brings it forward and points it at the new folder. Double-click / Enter there comes back here
/// through <see cref="GoToFoundFolder"/>.
/// </summary>
public partial class MainWindow
{
    private FindFolderWindow? _findFolder;

    private void FindFolderButton_Click(object sender, RoutedEventArgs e) => ShowFindFolder(null);

    private void TreeCtxFindFolder_Click(object sender, RoutedEventArgs e) => ShowFindFolder(_treeMenuNode?.FullPath);

    /// <summary>Opens the window; <paramref name="scope"/> is the folder to look in (null or not a real folder: the folder shown).</summary>
    private void ShowFindFolder(string? scope)
    {
        if (string.IsNullOrEmpty(scope) || !Directory.Exists(scope))
            scope = VM.HasFolder && !VM.InArchive && Directory.Exists(VM.CurrentPath) ? VM.CurrentPath : "";

        if (_findFolder != null)
        {
            _findFolder.SetScope(scope);
            if (_findFolder.WindowState == WindowState.Minimized) _findFolder.WindowState = WindowState.Normal;
            _findFolder.Activate();
            return;
        }

        var window = new FindFolderWindow(this, scope, GoToFoundFolder);
        window.Closed += (_, _) => { if (ReferenceEquals(_findFolder, window)) _findFolder = null; };
        _findFolder = window;
        window.Show();
    }

    /// <summary>The active tab goes to the folder (the tree selects and expands it), or the folder opens in a new tab; either way the folder can become the root of the tree.</summary>
    private void GoToFoundFolder(string folder, FindFolderTarget target)
    {
        if (target is FindFolderTarget.NewTab or FindFolderTarget.NewTabTreeRoot) VM.OpenNewTab(folder, activate: true);
        if (target is FindFolderTarget.TreeRoot or FindFolderTarget.NewTabTreeRoot) SetTreeRoot(folder);   // the tab shown is the new one in the second case
        if (target is FindFolderTarget.ThisTab or FindFolderTarget.TreeRoot) VM.Navigate(folder);
    }
}
