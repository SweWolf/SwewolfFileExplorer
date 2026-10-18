using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace SwewolfFileExplorer.Helpers;

/// <summary>What happened when a video or music file was handed to SweWolf Media Player.</summary>
public enum LaunchResult
{
    /// <summary>The player was started: it marks the file read after 60 seconds of playing, so the explorer does not.</summary>
    Started,
    /// <summary>No player: the caller opens the file with the program Windows has for it (and marks it read, as for any file).</summary>
    UseDefaultProgram,
    /// <summary>The user did not want to open it after all.</summary>
    Canceled,
}

/// <summary>Which files go to SweWolf Media Player (video and music), and how it is started and found.</summary>
public static class MediaPlayerLauncher
{
    private const string AppName = "SwewolfMediaPlayer";
    private const string DisplayName = "SweWolf Media Player";
    private const string DownloadPage = "https://github.com/SweWolf/SwewolfMediaPlayer/releases";

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".mpg", ".mpeg", ".mpe", ".ts", ".m2ts", ".mts",
        ".3gp", ".vob", ".ogv", ".divx", ".asf", ".rm", ".rmvb",
    };

    // music (the ones the player lists, plus SID tunes)
    private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wma", ".wav", ".aif", ".aiff", ".sid",
    };

    /// <summary>A video or music file: it is played in SweWolf Media Player.</summary>
    public static bool IsMedia(string name) => IsVideo(name) || MusicExtensions.Contains(Path.GetExtension(name));

    /// <summary>A video file.</summary>
    public static bool IsVideo(string name) => VideoExtensions.Contains(Path.GetExtension(name));

    /// <summary>
    /// The player's EXE, looked for in the order of D:\Dev\CLAUDE.md ("Finding a sibling app"): next to this EXE, then
    /// ..\SwewolfMediaPlayer\SwewolfMediaPlayer.exe, then the path the user entered once (saved in settings.json).
    /// </summary>
    public static string? FindPlayer()
    {
        var here = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(here, AppName + ".exe"),
            Path.GetFullPath(Path.Combine(here, "..", AppName, AppName + ".exe")),
            AppSettingsStore.Current.MediaPlayerPath,
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
    }

    /// <summary>
    /// Starts the player with the file. <paramref name="libraryPath"/> is what the file is called in the library when it differs
    /// from the file played (a file unpacked from an archive to a temporary folder).
    /// </summary>
    public static LaunchResult Open(string playPath, string? libraryPath)
    {
        var exe = FindPlayer();
        if (exe == null)
        {
            exe = AskForPlayer(out var otherwise);
            if (exe == null) return otherwise;
        }

        try
        {
            var info = new ProcessStartInfo { FileName = exe, UseShellExecute = false };
            info.ArgumentList.Add(playPath);
            if (libraryPath != null)
            {
                info.ArgumentList.Add("--library-path");
                info.ArgumentList.Add(libraryPath);
            }
            Process.Start(info);
            return LaunchResult.Started;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{DisplayName} could not be started.\n\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return LaunchResult.UseDefaultProgram;
        }
    }

    /// <summary>The player was not found: let the user point to it, go to the download page, or use another program this time.</summary>
    private static string? AskForPlayer(out LaunchResult otherwise)
    {
        otherwise = LaunchResult.Canceled;

        var answer = MessageBox.Show(
            $"{DisplayName} was not found, so this file cannot be played in it.\n\n" +
            "Yes: choose SwewolfMediaPlayer.exe\n" +
            "No: open the download page on GitHub\n" +
            "Cancel: open the file with the program Windows has for it",
            AppInfo.Name, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Cancel)
        {
            otherwise = LaunchResult.UseDefaultProgram;
            return null;
        }

        if (answer == MessageBoxResult.No)
        {
            try { Process.Start(new ProcessStartInfo { FileName = DownloadPage, UseShellExecute = true }); }
            catch { /* no browser: nothing to do */ }
            return null;
        }

        var dialog = new OpenFileDialog
        {
            Title = $"Find {DisplayName}",
            Filter = $"{DisplayName} ({AppName}.exe)|{AppName}.exe|Programs (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        // Remembered for the next time, but only as the last place to look: a copy next to this EXE still comes first.
        var settings = AppSettingsStore.Current;
        settings.MediaPlayerPath = dialog.FileName;
        AppSettingsStore.Save(settings);
        return dialog.FileName;
    }
}
