using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Microsoft.Win32;

namespace SwewolfFileExplorer.Helpers;

public enum TerminalKind { WindowsTerminal, PowerShell, CommandPrompt }

/// <summary>
/// Opens a terminal in a folder: Windows Terminal, Windows PowerShell or the Command Prompt. Which ones are installed is looked at
/// once per run (Windows Terminal comes with Windows 11 but is not on every Windows 10); a program that is not there is not
/// offered. The icon is the program's own, read from its file.
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

    /// <summary>The icon of the program (null if it has none that can be read).</summary>
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

    /// <summary>Opens the terminal in <paramref name="folder"/> (a folder on the disk). Throws if it cannot be started.</summary>
    public static void Open(TerminalKind kind, string folder)
    {
        var exe = FindExe(kind) ?? throw new FileNotFoundException("That program is not installed.");

        if (kind == TerminalKind.WindowsTerminal)
        {
            // wt -d <folder>: the arguments are quoted by .NET, so a folder with spaces, or the root of a drive, is safe.
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            info.ArgumentList.Add("-d");
            info.ArgumentList.Add(folder);
            Process.Start(info)?.Dispose();
        }
        else
        {
            // PowerShell and the Command Prompt: started in the folder, in a console window of their own (Windows opens it in
            // Windows Terminal if that is the default).
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = folder, UseShellExecute = true })?.Dispose();
        }
    }
}
