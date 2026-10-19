using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Microsoft.Win32;

namespace SwewolfFileExplorer.Helpers;

public enum TerminalKind { WindowsTerminal, PowerShell, CommandPrompt }

/// <summary>
/// Opens a terminal in a folder: Windows Terminal, Windows PowerShell or the Command Prompt, also as the administrator (Windows
/// asks the user first). Which ones are installed is looked at once per run (Windows Terminal comes with Windows 11 but is not on
/// every Windows 10); a program that is not there is not offered. The icon is the program's own, read from its file.
/// </summary>
public static class TerminalLauncher
{
    private static readonly Dictionary<TerminalKind, string?> Exes = new();
    private static readonly Dictionary<TerminalKind, ImageSource?> Icons = new();

    /// <summary>The program that opens this kind of terminal, or null if it is not on this computer.</summary>
    public static string? FindExe(TerminalKind kind)
    {
        lock (Exes)
        {
            if (!Exes.TryGetValue(kind, out var exe))
                Exes[kind] = exe = Look(kind);
            return exe;
        }
    }

    public static bool IsInstalled(TerminalKind kind) => FindExe(kind) != null;

    private static string? Look(TerminalKind kind)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        switch (kind)
        {
            case TerminalKind.PowerShell:
                return Existing(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"));

            case TerminalKind.CommandPrompt:
                return Existing(Path.Combine(system, "cmd.exe"));

            default:
                // Windows Terminal is started through its "app execution alias" (a small link in WindowsApps, also on the PATH).
                var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                         "Microsoft", "WindowsApps", "wt.exe");
                if (File.Exists(alias)) return alias;

                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    try { var p = Path.Combine(dir.Trim(), "wt.exe"); if (File.Exists(p)) return p; }
                    catch { /* a bad entry on the PATH */ }
                }
                return null;
        }
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    // ── Icons ─────────────────────────────────────────────────────────────────

    /// <summary>The icon of the program (null if it has no icon that can be read).</summary>
    public static ImageSource? GetIcon(TerminalKind kind)
    {
        lock (Icons)
        {
            if (Icons.TryGetValue(kind, out var icon)) return icon;

            var exe = FindExe(kind);
            string? source = exe;
            // The alias of Windows Terminal has the plain "window" icon: the real one is in the program's own file.
            if (kind == TerminalKind.WindowsTerminal && exe != null) source = FindTerminalProgram() ?? exe;

            return Icons[kind] = source == null ? null : IconHelper.GetIcon(source);
        }
    }

    /// <summary>
    /// WindowsTerminal.exe inside the app's package. Windows does not let a program list the WindowsApps folder, but it records
    /// where each package is in the registry (the current user's package repository).
    /// </summary>
    private static string? FindTerminalProgram()
    {
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (packages == null) return null;

            foreach (var name in packages.GetSubKeyNames().Where(n => n.StartsWith("Microsoft.WindowsTerminal_", StringComparison.OrdinalIgnoreCase))
                                                          .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase))
            {
                using var key = packages.OpenSubKey(name);
                if (key?.GetValue("PackageRootFolder") is string root)
                {
                    var exe = Path.Combine(root, "WindowsTerminal.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        catch { /* no icon of its own: the alias's icon is used */ }

        return null;
    }

    // ── Starting ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the terminal in <paramref name="folder"/> (a folder on the disk), as the administrator if asked (Windows shows its
    /// question first; when the user says no, a <see cref="System.ComponentModel.Win32Exception"/> with <see cref="UserDeclined"/>
    /// is thrown). Throws if it cannot be started.
    /// </summary>
    public static void Open(TerminalKind kind, string folder, bool admin = false)
    {
        var exe = FindExe(kind) ?? throw new FileNotFoundException("That program is not installed.");

        if (kind == TerminalKind.WindowsTerminal && !admin)
        {
            // wt -d <folder>: the arguments are quoted by .NET, so a folder with spaces, or the root of a drive, is safe.
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            info.ArgumentList.Add("-d");
            info.ArgumentList.Add(folder);
            Process.Start(info)?.Dispose();
            return;
        }

        // PowerShell and the Command Prompt: started in the folder, in a console window of their own (Windows opens it in Windows
        // Terminal if that is the default). As the administrator ("runas") a program may start in System32 whatever the working
        // folder is, so the folder is also given as an argument; ShellExecute takes the arguments as one text, hence the quoting.
        var start = new ProcessStartInfo(exe) { WorkingDirectory = folder, UseShellExecute = true };
        if (admin) start.Verb = "runas";

        var path = folder.Length > 3 ? folder.TrimEnd('\\') : folder;   // not "D:" for a drive's root
        switch (kind)
        {
            case TerminalKind.WindowsTerminal:   // only as the administrator (the other way is above)
                start.Arguments = path.Contains(' ') ? $"-d \"{path}\"" : $"-d {path}";   // a drive's root has no space, so it is never quoted (a closing \ would escape the quote)
                break;
            case TerminalKind.PowerShell when admin:
                start.Arguments = $"-NoExit -Command Set-Location -LiteralPath '{folder.Replace("'", "''")}'";
                break;
            case TerminalKind.CommandPrompt when admin:
                start.Arguments = $"/k cd /d \"{path}\"";
                break;
        }

        Process.Start(start)?.Dispose();
    }

    /// <summary>The error number of "the user said no in the UAC question".</summary>
    public const int UserDeclined = 1223;
}
