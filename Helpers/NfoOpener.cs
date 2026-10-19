using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Opens a <c>.nfo</c> file the way the user means it. Windows has registered <c>.nfo</c> for itself (System Information,
/// msinfo32.exe), but nearly every .nfo file in the world is a text file (the ASCII art of a release) or an XML file (the data of a
/// movie for Kodi or Jellyfin), which msinfo32 cannot open. So: a file that really is a System Information file is opened with
/// the program Windows has for it, and everything else in the user's text editor (<see cref="FindTextEditor"/>).
///
/// What a System Information file looks like (made with <c>msinfo32 /nfo</c>, 2026-10-18): UTF-16 XML, <c>&lt;?xml ...?&gt;</c> and then the
/// root element <c>&lt;MsInfo&gt;</c> (5 MB). The old binary format has no text at all (NUL bytes without a UTF-16 byte order mark).
/// </summary>
public static class NfoOpener
{
    public enum Kind { Text, SystemInformation }

    /// <summary>Is the .nfo file a System Information file (open it with Windows' program) or text / XML (open it in the text editor)?</summary>
    public static Kind Classify(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[8192];
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) return Kind.Text;

            var head = buffer.AsSpan(0, read);
            bool utf16 = read >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF));

            // No NUL bytes in a text file, except in UTF-16: so it is a binary file (the old System Information format).
            if (!utf16 && head.IndexOf((byte)0) >= 0) return Kind.SystemInformation;

            string text = utf16
                ? (head[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(buffer, 2, read - 2)
                : Encoding.UTF8.GetString(buffer, 0, read);

            // The first element after the XML declaration: <MsInfo> is System Information.
            int open = 0;
            while ((open = text.IndexOf('<', open)) >= 0)
            {
                if (open + 1 < text.Length && text[open + 1] is '?' or '!')
                {
                    int close = text.IndexOf('>', open);
                    if (close < 0) break;
                    open = close;
                    continue;
                }
                return text.AsSpan(open + 1).StartsWith("MsInfo", StringComparison.OrdinalIgnoreCase) ? Kind.SystemInformation : Kind.Text;
            }
            return Kind.Text;
        }
        catch
        {
            return Kind.Text;   // cannot be read: the editor will say so
        }
    }

    /// <summary>
    /// Opens the .nfo file in the text editor if it is a text or XML file. Returns false when the caller should open it the
    /// normal way (a System Information file, or no editor could be started).
    /// </summary>
    public static bool TryOpenInTextEditor(string path)
    {
        if (!path.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase)) return false;
        if (Classify(path) == Kind.SystemInformation) return false;

        foreach (var editor in new[] { FindTextEditor(), Notepad() })
        {
            if (editor == null) continue;
            try
            {
                Process.Start(new ProcessStartInfo(editor) { ArgumentList = { path }, UseShellExecute = false });
                return true;
            }
            catch
            {
                // this one could not be started: the next
            }
        }
        return false;
    }

    private static string? Notepad()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        return File.Exists(notepad) ? notepad : null;
    }

    /// <summary>
    /// The user's text editor: the path in settings.json (<see cref="AppSettings.TextEditorPath"/>) if it is set and there; else
    /// Visual Studio Code, looked up in the registry (App Paths, then the uninstall entries of the user and the system installer)
    /// and in its usual folders; else null (Notepad is the last resort).
    /// </summary>
    public static string? FindTextEditor()
    {
        var chosen = AppSettingsStore.Current.TextEditorPath?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(chosen) && File.Exists(chosen)) return chosen;

        return FindVsCode();
    }

    public static string? FindVsCode()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

                // "Code.exe" in App Paths: the way Windows finds a program by name.
                using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Code.exe"))
                    if (key?.GetValue(null) as string is { } exe && File.Exists(exe = exe.Trim('"'))) return exe;

                // The uninstall entries of Visual Studio Code: the user installer's and the system installer's.
                foreach (var id in new[] { "{771FD6B0-FA20-440A-A002-3B3BAC16DC50}_is1", "{EA457B21-F73E-494C-ACAB-524FDE069978}_is1" })
                {
                    using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{id}");
                    if (key?.GetValue("InstallLocation") as string is { Length: > 0 } folder)
                    {
                        var exe = Path.Combine(folder.Trim('"'), "Code.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
            }
            catch
            {
                // no access to this part of the registry: the next
            }
        }

        foreach (var exe in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
        })
            if (File.Exists(exe)) return exe;

        return null;
    }
}
