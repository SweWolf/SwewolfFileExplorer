using System.Diagnostics;
using System.IO;
using System.Windows;

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
    /// ..\SwewolfMediaPlayer\SwewolfMediaPlayer.exe. Not found: null, and the caller uses the program Windows has for the file.
    /// </summary>
    public static string? FindPlayer()
    {
        var here = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(here, AppName + ".exe"),
            Path.GetFullPath(Path.Combine(here, "..", AppName, AppName + ".exe")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Starts the player with the file. <paramref name="libraryPath"/> is what the file is called in the library when it differs
    /// from the file played (a file unpacked from an archive to a temporary folder).
    /// </summary>
    public static LaunchResult Open(string playPath, string? libraryPath, long startMs = 0, bool noResume = false)
    {
        var exe = FindPlayer();
        if (exe == null) return LaunchResult.UseDefaultProgram;   // the file's own program (the caller opens it)

        try
        {
            var info = new ProcessStartInfo { FileName = exe, UseShellExecute = false };
            info.ArgumentList.Add(playPath);
            if (libraryPath != null)
            {
                info.ArgumentList.Add("--library-path");
                info.ArgumentList.Add(libraryPath);
            }
            if (startMs > 0)   // Demo Play: the file starts at this time
            {
                info.ArgumentList.Add("--start-at");
                info.ArgumentList.Add(startMs.ToString());
            }
            if (noResume) info.ArgumentList.Add("--no-resume");   // Demo / Intro Play: the saved resume point is not used or asked about
            Process.Start(info);
            return LaunchResult.Started;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{DisplayName} could not be started.\n\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return LaunchResult.UseDefaultProgram;
        }
    }
}
