using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace SwewolfFileExplorer.Helpers;

/// <summary>Starts the sibling app File Info Viewer with one file ("File Info Viewer" in the right-click menus).</summary>
public static class FileInfoViewerLauncher
{
    private const string AppName = "FileInfoViewer";
    private const string DisplayName = "File Info Viewer";
    private const string DownloadPage = "https://github.com/SweWolf/FileInfoViewer/releases";

    private static ImageSource? _icon;

    /// <summary>
    /// The EXE, looked for in the order of D:\Dev\CLAUDE.md ("Finding a sibling app"): next to this EXE, then
    /// ..\FileInfoViewer\FileInfoViewer.exe. Null when it is not there (the menu item is then not shown). Asked each time a menu
    /// opens: two File.Exists calls, and a program installed later shows up without a restart.
    /// </summary>
    public static string? FindExe()
    {
        var here = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(here, AppName + ".exe"),
            Path.GetFullPath(Path.Combine(here, "..", AppName, AppName + ".exe")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>The program's own icon (kept after the first lookup).</summary>
    public static ImageSource? GetIcon(string exe) => _icon ??= IconHelper.GetIcon(exe);

    /// <summary>Starts File Info Viewer with the file as its argument.</summary>
    public static void Open(string file)
    {
        var exe = FindExe();
        if (exe == null)
        {
            var answer = MessageBox.Show(
                $"{DisplayName} was not found.\n\n" +
                $"It is a separate program by SweWolf Software. You can download it from the releases page:\n{DownloadPage}\n\n" +
                $"After downloading, put it either in the same folder as {AppInfo.Name}, or in its own folder named {AppName} next to the {AppInfo.Name} folder.\n\n" +
                "Do you want to open the download page now?",
                AppInfo.Name, MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo { FileName = DownloadPage, UseShellExecute = true }); }
                catch { }
            }
            return;
        }

        try
        {
            var info = new ProcessStartInfo { FileName = exe, UseShellExecute = false };
            info.ArgumentList.Add(file);
            Process.Start(info);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{DisplayName} could not be started.\n\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
