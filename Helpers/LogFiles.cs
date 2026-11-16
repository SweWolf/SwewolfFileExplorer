using System.IO;
using System.Text;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Log files under %APPDATA%\SweWolfSoftware\&lt;app&gt;\Logs\ (Help > Open Log File, Help > Open Log Folder):
///   errors.log   one line per thing that could not be done: "yyyy-MM-dd HH:mm:ss&lt;TAB&gt;what, and why". Errors only,
///                no log of what worked (the user's decision, 2026-10-18).
///   internet.log one line per call to a web service, also the ones that worked (2026-11-15), so the errors stay few and readable.
///
/// Taken from MP3toMP4's LogFiles.cs (same folder layout, LogFolder, and the way a file is cut down when it gets too big),
/// without its conversion parts. When copying to another app: change the namespace and AppFolderName.
/// When changing this file, add a line to the history below, so the copies in the other apps can be compared with it.
///
/// History:
///   2026-10-18  Created in the explorer, from MP3toMP4's LogFiles.cs (2026-09-27): LogFolder, TruncateToNewestHalf;
///               new: ErrorsLogFile, AppendError (errors.log over 0.5 MB is cut down to its newest 0.25 MB).
///   2026-11-15  New in the explorer: InternetLogFile, AppendInternet (internet.log: every web call, also the ones that worked;
///               first used by the TMDB calls).
///   2026-11-15  AppendInternet only writes when the setting LogInternetCalls is on (off by default).
///   2026-11-16  New in the explorer: AppendWarning (a "Warning:" line in errors.log), only written when the setting LogWarnings is on (off by default).
/// </summary>
internal static class LogFiles
{
    private static string AppFolderName => AppInfo.FolderName;   // "SwewolfFileExplorer"

    internal static string LogFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SweWolfSoftware", AppFolderName, "Logs");

    internal static string ErrorsLogFile => Path.Combine(LogFolder, "errors.log");

    /// <summary>internet.log: one line per call to a web service (what was asked, the answer, the time). Never holds a key or a password.</summary>
    internal static string InternetLogFile => Path.Combine(LogFolder, "internet.log");

    private const long ErrorsMaxBytes = 512 * 1024;

    /// <summary>
    /// Appends one line to internet.log ("yyyy-MM-dd HH:mm:ss&lt;TAB&gt;method&lt;TAB&gt;site&lt;TAB&gt;what&lt;TAB&gt;answer&lt;TAB&gt;time"). Unlike errors.log it
    /// also lists the calls that worked; the caller must leave secrets out of the text. Cut down like errors.log. Never throws.
    /// </summary>
    internal static void AppendInternet(string message)
    {
        if (!AppSettingsStore.Current.LogInternetCalls) return;   // Settings > Troubleshooting: off by default
        try
        {
            lock (WriteLock)
            {
                Directory.CreateDirectory(LogFolder);
                var file = InternetLogFile;
                var line = message.Replace("\r", "").Replace("\n", " ");
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{line}\r\n", Encoding.UTF8);
                TruncateToNewestHalf(file, ErrorsMaxBytes);
            }
        }
        catch { }
    }
    private static readonly object WriteLock = new();

    /// <summary>
    /// Appends a "Warning:" line to errors.log: something that needed more than the first try but is not a failure by itself
    /// (e.g. a folder that needs administrator permission; the program then asks for it). Never throws.
    /// </summary>
    internal static void AppendWarning(string message)
    {
        if (!AppSettingsStore.Current.LogWarnings) return;   // Settings > Troubleshooting > Log Warnings: off by default
        AppendError("Warning: " + message);
    }

    /// <summary>
    /// Appends one line to errors.log. Logging must never stop what the user is doing, so errors here are ignored.
    /// </summary>
    internal static void AppendError(string message)
    {
        try
        {
            lock (WriteLock)
            {
                Directory.CreateDirectory(LogFolder);
                var file = ErrorsLogFile;
                var line = message.Replace("\r", "").Replace("\n", " ");
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{line}\r\n", Encoding.UTF8);
                TruncateToNewestHalf(file, ErrorsMaxBytes);
            }
        }
        catch { }
    }

    /// <summary>
    /// When the file is larger than maxBytes, keeps only the newest whole lines that fit in half of it,
    /// so the file isn't rewritten on every call. Writes a temporary file first and then replaces the
    /// log, so a failure never leaves a half-written log behind.
    /// </summary>
    private static void TruncateToNewestHalf(string file, long maxBytes)
    {
        if (new FileInfo(file).Length <= maxBytes) return;

        string[] lines = File.ReadAllLines(file, Encoding.UTF8);
        long budget = maxBytes / 2, size = 0;
        int first = lines.Length;
        while (first > 0)
        {
            size += Encoding.UTF8.GetByteCount(lines[first - 1]) + 2;   // + CRLF
            if (size > budget) break;
            first--;
        }

        string temp = file + ".tmp";
        File.WriteAllLines(temp, lines[first..], Encoding.UTF8);
        File.Move(temp, file, overwrite: true);
    }
}
