using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwewolfFileExplorer.Helpers;
using SwewolfFileExplorer.Views;

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

    private const string CleanUpHeader = "Clean Up Tracking Data";
    private const string CleanUpTitle  = "Clean Up Tracking Data";

    /// <summary>Header, enabled state and tooltip of "Clean Up Tracking Data" for the drive <paramref name="root"/> (a CD or DVD has nothing to clean).</summary>
    private static void PrepareCleanUpItem(MenuItem item, string root)
    {
        item.Header    = CleanUpHeader;
        item.IsEnabled = LibraryStore.GetDriveDataKind(root) != LibraryStore.DriveDataKind.NotPossible;
        item.ToolTip   = "Removes the read marks, ratings, folder options and other tracking data of this drive that belong to files and folders that no longer exist. A backup copy is saved first.";
    }

    private void TreeCtxCleanUp_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null && DriveRootOf(_treeMenuNode.FullPath) is { } root) CleanUpDriveData(root);
    }

    private async void CleanUpDriveData(string root)
    {
        if (_driveDataBusy) return;
        if (LibraryStore.GetDriveDataKind(root) == LibraryStore.DriveDataKind.NotPossible) return;
        string drive = root.TrimEnd('\\', '/');

        var confirm = MessageBox.Show(this,
            $"Confirm cleaning up the tracking data of drive {drive}.\n\n" +
            $"This program remembers read marks, ratings, folder options, sort orders and other tracking data for the files and folders on {drive}. " +
            "If a file or folder was deleted, renamed or moved outside this program, what is remembered about it is still there.\n\n" +
            $"Clean Up looks at everything that is remembered for drive {drive} and removes the records of files and folders that no longer exist. " +
            "Nothing is removed for files that are still there, and the files and folders themselves are never touched. " +
            "The ratings and read marks of a file that was moved or renamed outside this program are lost.\n\n" +
            "A backup copy of the tracking data is saved first.\n\n" +
            "Do you want to continue?",
            CleanUpTitle, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK);
        if (confirm != MessageBoxResult.OK) return;

        _driveDataBusy = true;
        try
        {
            // What was just marked, rated or moved is written first.
            await Task.Run(() => LibraryService.WaitForQueued(3000));

            var plan = WaitWindow.Run(this, CleanUpTitle, $"Checking the tracking data of drive {drive}...",
                "Looking for records of files and folders that no longer exist.",
                ct => Task.Run(() => LibraryStore.ScanForCleanup(root, ct), ct));

            if (plan == null)
            {
                FlashTitle($"{BaseTitle} - Clean up canceled, nothing was changed");
                return;
            }
            if (plan.Problem != null)
            {
                MessageBox.Show(this, plan.Problem, CleanUpTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (plan.DeadCount == 0)
            {
                MessageBox.Show(this, plan.Total == 0
                        ? $"Nothing to clean up: no tracking data is remembered for drive {drive}."
                        : $"Nothing to clean up: all {plan.Total:N0} records of drive {drive} belong to files and folders that still exist.",
                    CleanUpTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // A safeguard: when most of the records look dead, the drive may be missing something (a folder that did not come up), so ask.
            if (plan.MostAreDead)
            {
                var sure = MessageBox.Show(this,
                    $"{plan.DeadCount:N0} of the {plan.Total:N0} records of drive {drive} belong to files and folders that no longer exist. That is more than half of them.\n\n" +
                    "If the drive is not complete (a folder or a disk that did not come up), cleaning up now would remove records you want to keep. A backup copy is saved first, but check that the drive looks as it should.\n\n" +
                    "Do you want to remove them anyway?",
                    CleanUpTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (sure != MessageBoxResult.Yes) return;
            }

            Mouse.OverrideCursor = Cursors.Wait;
            string? backup = null;
            string? error;
            try { error = await Task.Run(() => LibraryStore.ApplyCleanup(plan, out backup)); }
            finally { Mouse.OverrideCursor = null; }

            if (error != null)
            {
                MessageBox.Show(this, $"The tracking data of drive {drive} could not be cleaned up: {error}\n\nNothing was removed.",
                    CleanUpTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(this,
                $"Removed {plan.DeadCount:N0} {(plan.DeadCount == 1 ? "record" : "records")} of files and folders that no longer exist from the tracking data of drive {drive} " +
                $"({plan.Total:N0} records were checked).\n\nA backup copy was saved before the clean up:\n{backup}",
                CleanUpTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            VM.RefreshCommand.Execute(null);   // read marks and the rest are read again
        }
        finally
        {
            _driveDataBusy = false;
        }
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
              $"The read marks, ratings, sort orders, column choices and other tracking data of the files on this drive are kept on this computer now, in your user profile, in this folder:\n{MainDataFolder}\n\n" +
              $"If you turn this on, new data is kept in a hidden folder named {folder} in the root of the drive instead, so it follows the drive to other computers that run this program.\n\n" +
              "Do you want to move the data that is kept for this drive on this computer now?\n\n" +
              "Yes: move it to the drive, and keep new data there.\n" +
              "No: keep new data on the drive, but leave the existing data on this computer (it will not be used for this drive any more).\n" +
              "Cancel: change nothing."
            : $"Confirm keeping the tracking data of drive {drive} on this computer again.\n\n" +
              $"The data kept on the drive can be moved back to this computer, into your user profile, in this folder:\n{MainDataFolder}\n\nIt is stored there under the drive letter, so it only matches this drive while it is called {drive}. " +
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
