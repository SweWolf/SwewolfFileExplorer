using System.IO;
using System.Runtime.InteropServices;

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
