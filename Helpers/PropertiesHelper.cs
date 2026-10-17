using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Shows the standard Windows Properties window, the same one as Explorer's right-click
/// Properties. Must be called on an STA thread (the WPF UI thread).
/// </summary>
public static class PropertiesHelper
{
    /// <summary>
    /// Properties of one file or folder, or the combined properties of several items.
    /// <paramref name="changed"/> is called (on the UI thread) with the items that were changed from the window, as soon as
    /// that is seen (see <see cref="PropertiesWatcher"/>): the list can then refresh them.
    /// </summary>
    public static void Show(IReadOnlyCollection<string> paths, Action<IReadOnlyList<string>>? changed = null)
    {
        if (paths.Count == 0) return;

        var watcher = changed == null ? null : new PropertiesWatcher(paths, changed);   // looks at the items before they can change

        if (paths.Count == 1)
            ShowSingle(paths.First());
        else
            SHMultiFileProperties(SendToHelper.CreateDataObject(paths), 0);

        watcher?.Start();
    }

    private static void ShowSingle(string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = (uint)Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask  = SEE_MASK_INVOKEIDLIST,
            lpVerb = "properties",
            lpFile = System.IO.Path.GetFullPath(path),
            nShow  = SW_SHOW
        };

        if (!ShellExecuteEx(ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const int  SW_SHOW = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public uint   cbSize;
        public uint   fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int    nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint   dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    [DllImport("shell32.dll")]
    private static extern int SHMultiFileProperties(
        [MarshalAs(UnmanagedType.Interface)] IDataObject pdtobj, uint dwFlags);
}
