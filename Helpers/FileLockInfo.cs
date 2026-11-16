using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Which programs have a file open (Windows' Restart Manager): used when a rename, move or delete fails with "being used by another
/// process", so the message and the log say who it is instead of leaving the user to guess.
/// </summary>
public static class FileLockInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, System.Text.StringBuilder key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] fileNames, uint apps, IntPtr applications, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? info, out uint reasons);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// True when another program has the file open in a way that stops it from being deleted or renamed (a sharing violation when we ask
    /// for DELETE access; nothing is changed). For a folder: any of its files (the first 5000). Does not depend on the language of Windows,
    /// unlike the text of the exception.
    /// </summary>
    public static bool IsLocked(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
                return System.IO.Directory.EnumerateFiles(path, "*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Take(5000).Any(IsFileLocked);
            return IsFileLocked(path);
        }
        catch { return false; }
    }

    private static bool IsFileLocked(string file)
    {
        const uint DELETE = 0x10000, ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
        var h = CreateFileW(file, DELETE, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (h != new IntPtr(-1)) { CloseHandle(h); return false; }
        return Marshal.GetLastWin32Error() == 32;   // ERROR_SHARING_VIOLATION
    }

    /// <summary>
    /// <see cref="WhoUses"/> with a time limit: an empty list when the answer takes longer than <paramref name="limit"/> (the caller
    /// goes on without names; the lookup is left to finish by itself on a pool thread).
    /// </summary>
    public static async Task<List<string>> WhoUsesAsync(string path, TimeSpan limit)
    {
        var task = Task.Run(() => WhoUses(path));
        return await Task.WhenAny(task, Task.Delay(limit)) == task ? await task : new List<string>();
    }

    /// <summary>The programs that have the file open, as "Name (process id)"; empty when none is found or it cannot be asked.
    /// For a folder: the programs that have one of the files inside it open (the first 200 files; the Restart Manager only knows files).</summary>
    public static List<string> WhoUses(string path)
    {
        var result = new List<string>();
        uint session = 0;
        try
        {
            if (RmStartSession(out session, 0, new System.Text.StringBuilder(33)) != 0) return result;

            string[] files = new[] { path };
            if (System.IO.Directory.Exists(path))
            {
                files = System.IO.Directory.EnumerateFiles(path, "*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Take(200).ToArray();
                if (files.Length == 0) return result;
            }
            if (RmRegisterResources(session, (uint)files.Length, files, 0, IntPtr.Zero, 0, null) != 0) return result;

            uint needed = 0, count = 0, reasons = 0;
            int code = RmGetList(session, out needed, ref count, null, out reasons);   // 234 (more data): needed says how many
            if (code != 234 && code != 0) return result;
            if (needed == 0) return result;

            var info = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, info, out reasons) != 0) return result;

            int self = Environment.ProcessId;
            for (int i = 0; i < count; i++)
            {
                int pid = info[i].Process.ProcessId;
                string name = info[i].AppName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    try { name = Process.GetProcessById(pid).ProcessName; } catch { name = "a program"; }
                }
                var text = pid == self ? $"{name} (this program, {pid})" : $"{name} ({pid})";
                if (!result.Contains(text)) result.Add(text);
            }
        }
        catch (Exception)
        {
            // the Restart Manager is not there or refused: no names
        }
        finally
        {
            if (session != 0) { try { RmEndSession(session); } catch { } }
        }

        return result;
    }
}
