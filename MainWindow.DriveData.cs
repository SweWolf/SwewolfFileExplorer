using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer;

/// <summary>
/// "Store Tracking Data (Read Marks, Folder Options, Ratings...) Locally on This Drive": the item in the right-click menu of a drive
/// (tree and This PC grid) that keeps the library data of the drive in a hidden folder in its root, so it follows the drive to other
/// computers. Turning it on or off asks first and can move the existing data (LibraryStore.MoveDriveData); turning it off keeps the
/// folder on the drive as a backup (renamed _BAK). Removable drives always keep their data on the drive: the item is checked and grey.
/// </summary>
public partial class MainWindow
{
    private const string DriveDataHeader = "Store Tracking Data (Read Marks, Folder Options, Ratings...) Locally on This Drive";

    private bool _driveDataBusy;

    /// <summary>Where the main library file is (LibraryStore.MainFolder), written the way it can be typed into the address bar.</summary>
    private const string MainDataFolder = @"%AppData%\SweWolfSoftware\Library";

    /// <summary>The root of a drive for a menu path, or null if the path is not a drive's root.</summary>
    private static string? DriveRootOf(string path)
    {
        var root = System.IO.Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && string.Equals(root.TrimEnd('\\', '/'), path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            ? root : null;
    }

    /// <summary>Check mark, enabled state and tooltip of the item for the drive <paramref name="root"/>.</summary>
    private static void PrepareDriveDataItem(MenuItem item, string root)
    {
        var kind = LibraryStore.GetDriveDataKind(root);
        item.Header      = DriveDataHeader;
        item.IsCheckable = false;   // the check mark is drawn by hand: the click asks first, and may be canceled
        item.Icon        = kind is LibraryStore.DriveDataKind.OnDriveByChoice or LibraryStore.DriveDataKind.OnDriveAlways
            ? new TextBlock { Text = "✓", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center }
            : null;
        item.IsEnabled   = kind is LibraryStore.DriveDataKind.Main or LibraryStore.DriveDataKind.OnDriveByChoice;
        item.ToolTip     = kind switch
        {
            LibraryStore.DriveDataKind.OnDriveAlways =>
                $"USB drives, memory cards and other removable drives always keep their tracking data on the drive itself, in the hidden folder {LibraryStore.UsbFolderName} in its root.",
            LibraryStore.DriveDataKind.NotPossible =>
                "A CD or DVD cannot be written to, so its tracking data is kept on this computer.",
            LibraryStore.DriveDataKind.OnDriveByChoice =>
                $"The read marks, ratings, folder sort orders and column choices of this drive's files are kept on the drive, in the hidden folder {LibraryStore.UsbFolderName} in its root, so they follow the drive to other computers. Click to keep them on this computer again.",
            _ =>
                $"Keeps the read marks, ratings, folder sort orders, column choices and other tracking data of this drive's files in a hidden folder ({LibraryStore.UsbFolderName}) in the root of the drive, instead of on this computer, so they follow the drive to other computers that run this program. You are asked first, and can move the data that is kept on this computer now.",
        };
    }

    private void TreeCtxDriveData_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null && DriveRootOf(_treeMenuNode.FullPath) is { } root) ToggleDriveData(root);
    }

    private async void ToggleDriveData(string root)
    {
        if (_driveDataBusy) return;
        var kind = LibraryStore.GetDriveDataKind(root);
        if (kind is not (LibraryStore.DriveDataKind.Main or LibraryStore.DriveDataKind.OnDriveByChoice)) return;

        string drive = root.TrimEnd('\\', '/');
        bool turnOn  = kind == LibraryStore.DriveDataKind.Main;
        string folder = LibraryStore.UsbFolderName;

        string text = turnOn
            ? $"Confirm storing the tracking data of drive {drive} on the drive itself.\n\n" +
              $"The read marks, ratings, sort orders, column choices and other tracking data of the files on this drive are kept on this computer now, in your user profile (the folder {MainDataFolder}). " +
              $"If you turn this on, new data is kept in a hidden folder named {folder} in the root of the drive instead, so it follows the drive to other computers that run this program.\n\n" +
              "Do you want to move the data that is kept for this drive on this computer now?\n\n" +
              "Yes: move it to the drive, and keep new data there.\n" +
              "No: keep new data on the drive, but leave the existing data on this computer (it will not be used for this drive any more).\n" +
              "Cancel: change nothing."
            : $"Confirm keeping the tracking data of drive {drive} on this computer again.\n\n" +
              $"The data kept on the drive can be moved back to this computer, into your user profile (the folder {MainDataFolder}). It is stored there under the drive letter, so it only matches this drive while it is called {drive}. " +
              $"The folder on the drive is not deleted: it is renamed {folder}_BAK as a backup.\n\n" +
              "Do you want to move the data back to this computer now?\n\n" +
              "Yes: move it back, and keep new data on this computer.\n" +
              "No: keep new data on this computer, but leave the existing data in the backup folder on the drive.\n" +
              "Cancel: change nothing.";

        var answer = MessageBox.Show(this, text, "Tracking Data Location", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Cancel) return;
        bool move = answer == MessageBoxResult.Yes;

        _driveDataBusy = true;
        Mouse.OverrideCursor = Cursors.Wait;
        string? error;
        try
        {
            error = await Task.Run(() =>
            {
                LibraryService.WaitForQueued(3000);   // what was just marked, rated or moved is written first
                if (turnOn)
                    return move ? LibraryStore.MoveDriveData(root, toDrive: true)
                                : (LibraryStore.MarkDrivePortable(root) ? null : "The drive cannot be written to.");

                var failed = move ? LibraryStore.MoveDriveData(root, toDrive: false) : null;
                return failed ?? LibraryStore.UnmarkDrivePortable(root);
            });
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _driveDataBusy = false;
        }

        if (error != null)
        {
            MessageBox.Show(this, $"The tracking data of drive {drive} could not be {(turnOn ? "moved to the drive" : "moved back")}: {error}\n\nNothing was changed.",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        FlashTitle(turnOn ? $"{BaseTitle} - Tracking data of {drive} is now stored on the drive"
                          : $"{BaseTitle} - Tracking data of {drive} is now stored on this computer");
        VM.RefreshCommand.Execute(null);   // read marks and the rest are read again from where they are kept now
    }
}
