using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Where Windows keeps the special folders of the user. The folders can be moved (Properties > Location), so the place is
/// asked of Windows and never worked out from the profile folder: Downloads is not always in <c>C:\Users\name\Downloads</c>.
/// .NET's <see cref="Environment.SpecialFolder"/> has no Downloads, which is why this is here.
/// </summary>
public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid folderId, uint flags, IntPtr token, out IntPtr path);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr memory);

    // ── shell:sendto, shell:startup ... (what Explorer's address bar understands) ─────────────────────────────

    private static Dictionary<string, Guid>? _shellNames;

    /// <summary>
    /// The names Windows has for its special folders ("SendTo", "Startup", "Common Startup", "AppData", "Local AppData", "Fonts",
    /// "Downloads" ...), read from the registry where Windows lists every known folder with its name (the same names as after
    /// <c>shell:</c> in Explorer's address bar). Case does not matter.
    /// </summary>
    private static Dictionary<string, Guid> ShellNames()
    {
        if (_shellNames != null) return _shellNames;

        var names = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var descriptions = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FolderDescriptions");
            foreach (var id in descriptions?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var key = descriptions!.OpenSubKey(id);
                if (key?.GetValue("Name") is string name && Guid.TryParse(id, out var guid))
                    names.TryAdd(name, guid);
            }
        }
        catch { /* no names: shell: then says that the folder is unknown */ }

        return _shellNames = names;
    }

    /// <summary>
    /// Turns <c>shell:sendto</c> (also with a path after it: <c>shell:startup\Sub</c>) into the real path, as Explorer's address bar does.
    /// Returns false, with a message, when the name is not one of Windows' folders or the folder is not on the disk (the Recycle Bin,
    /// the Control Panel ... are not folders here). <paramref name="typed"/> must start with "shell:" (any case).
    /// </summary>
    public static bool TryResolveShellPath(string typed, out string path, out string error)
    {
        path = ""; error = "";
        var rest = typed["shell:".Length..].Trim();
        if (rest.Length == 0) { error = "Type a name after shell:, for example shell:sendto."; return false; }

        int slash = rest.IndexOfAny(new[] { '\\', '/' });
        var name = (slash < 0 ? rest : rest[..slash]).Trim();
        var sub  = slash < 0 ? "" : rest[(slash + 1)..].Trim('\\', '/', ' ');

        if (!ShellNames().TryGetValue(name, out var guid)) { error = $"Unknown shell folder: {name}"; return false; }

        try
        {
            if (SHGetKnownFolderPath(guid, 0, IntPtr.Zero, out var pointer) == 0 && pointer != IntPtr.Zero)
            {
                try { path = Marshal.PtrToStringUni(pointer) ?? ""; }
                finally { CoTaskMemFree(pointer); }
            }
        }
        catch { path = ""; }

        if (path.Length == 0) { error = $"shell:{name} is not a folder on the disk."; return false; }
        if (sub.Length > 0) path = Path.Combine(path, sub);
        return true;
    }

    /// <summary>The Downloads folder of the user, where Windows says it is (also when it was moved); null if it is not there.</summary>
    public static string? Downloads()
    {
        try
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var pointer) == 0 && pointer != IntPtr.Zero)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(pointer);
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
                }
                finally { CoTaskMemFree(pointer); }
            }
        }
        catch { /* fall through to the usual place */ }

        var usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(usual) ? usual : null;
    }
}
