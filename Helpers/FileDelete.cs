using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Deletes files and folders (to the Recycle Bin, or for good) on a background thread, with progress and a way to cancel.
///
/// The items go to Windows in <b>batches</b> (one <c>SHFileOperation</c> call per batch, as Explorer does it): one call per file was
/// so slow for hundreds of files that the window looked frozen for minutes (found by the user, ~400 screenshots, 4-6 minutes), and it ran
/// on the UI thread. Cancel is looked at between batches; the items already deleted stay deleted. What was really deleted is
/// found by looking at the disk afterwards, so the answer is the truth even if Windows only did part of a batch.
/// Result: <see cref="TransferResult.Done"/> holds (path, "") for each deleted item, Failed the ones that are still there.
/// </summary>
public static class FileDelete
{
    public sealed record Item(string Path, string Name, bool IsDirectory);

    // How many items go to Windows in one call. It adapts: a call has a cost of its own (one call per file was 2.5 times slower than
    // batches of 5 or more), but a big batch makes the counter jump. The size is chosen so that a call takes about a tenth of a
    // second: the counter moves ten times a second, one item at a time when Windows is slow, in steps of several when it is fast.
    private const double TargetSeconds = 0.1;
    private const int MaxBatch = 50;

    public static Task<TransferResult> RunAsync(IReadOnlyList<Item> items, bool permanent, TransferProgress progress, CancellationToken cancel)
    {
        var tcs = new TaskCompletionSource<TransferResult>();
        // SHFileOperation wants a single-threaded apartment, as the UI thread is.
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(Run(items, permanent, progress, cancel)); }
            catch (Exception ex) { tcs.SetResult(new TransferResult(new(), new() { ("Delete", ex.Message) }, false, items.Count)); }
        }) { IsBackground = true, Name = "FileDelete" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static TransferResult Run(IReadOnlyList<Item> items, bool permanent, TransferProgress progress, CancellationToken cancel)
    {
        var denied = new List<string>();
        var r = RunCore(items, permanent, progress, cancel, denied);
        return r with { AccessDenied = denied };
    }

    private static TransferResult RunCore(IReadOnlyList<Item> items, bool permanent, TransferProgress progress, CancellationToken cancel, List<string> denied)
    {
        var done   = new List<(string From, string To)>();
        var failed = new List<(string Name, string Reason)>();

        progress.FileTotal  = Math.Max(items.Count, 1);
        progress.ItemWord   = "Item";
        progress.Preparing  = false;

        // A path of 248 characters or more does not fit SHFileOperation: those go one by one the old way.
        var batchable = items.Where(i => i.Path.Length < 248).ToList();
        var singles   = items.Where(i => i.Path.Length >= 248).ToList();

        int handled = 0, batchSize = 1;
        int start = 0;
        while (start < batchable.Count)
        {
            if (cancel.IsCancellationRequested) return new TransferResult(done, failed, true, items.Count);

            var batch = batchable.Skip(start).Take(batchSize).ToList();
            progress.CurrentName = batch[0].Name;
            progress.FileIndex   = handled + 1;

            var timer = System.Diagnostics.Stopwatch.StartNew();
            int rc = ShFileOperation(batch.Select(b => b.Path), permanent);
            double perItem = timer.Elapsed.TotalSeconds / batch.Count;
            batchSize = (int)Math.Clamp(Math.Round(TargetSeconds / Math.Max(perItem, 0.0005)), 1, MaxBatch);

            foreach (var b in batch)
            {
                if (!File.Exists(b.Path) && !Directory.Exists(b.Path)) { done.Add((b.Path, "")); continue; }

                // Still there: was it "access denied" (a folder in Program Files ...)? Then the caller can offer the administrator.
                bool noAccess = rc is 5 or 0x78 or 0x79 or 0x7C || ProbeAccessDenied(b.Path);
                if (noAccess) denied.Add(b.Path);
                failed.Add((b.Name, noAccess ? "access denied (administrator permission is needed)"
                                             : "it could not be deleted (in use by another program, or no access)"));
                try
                {
                    var text = $"Delete of \"{b.Path}\" failed: SHFileOperation returned 0x{rc:X}";
                    if (noAccess) LogFiles.AppendWarning(text + " (administrator permission is needed)");   // the program offers to go on as the administrator
                    else LogFiles.AppendError(text);
                }
                catch { }
            }
            handled += batch.Count;
            start   += batch.Count;   // by what was taken, not by the next size
            progress.FileIndex = handled;
        }

        foreach (var item in singles)
        {
            if (cancel.IsCancellationRequested) return new TransferResult(done, failed, true, items.Count);
            progress.CurrentName = item.Name;
            progress.FileIndex   = ++handled;
            try
            {
                var recycle = permanent ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;
                if (item.IsDirectory) FileSystem.DeleteDirectory(item.Path, UIOption.OnlyErrorDialogs, recycle);
                else                  FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, recycle);
                if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) done.Add((item.Path, ""));
                else failed.Add((item.Name, "it could not be deleted"));
            }
            catch (Exception ex)
            {
                if (ex is UnauthorizedAccessException) denied.Add(item.Path);
                failed.Add((item.Name, ex.Message));
                try
                {
                    var text = $"Delete of \"{item.Path}\" failed: {ex.GetType().Name}: {ex.Message}";
                    if (ex is UnauthorizedAccessException) LogFiles.AppendWarning(text + " (administrator permission is needed)");
                    else LogFiles.AppendError(text);
                }
                catch { }
            }
        }

        return new TransferResult(done, failed, false, items.Count);
    }

    // ── Windows ───────────────────────────────────────────────────────────────

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

    /// <summary>
    /// Deletes the paths in one call, with no window of Windows' own (our window shows the progress) and no questions (we asked
    /// already, and we looked at what can reach the Recycle Bin). The return code is ignored: the caller looks at the disk.
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    /// <summary>True when the current user is not allowed to delete the item (asked with a DELETE access request; nothing is changed).</summary>
    private static bool ProbeAccessDenied(string path)
    {
        const uint DELETE = 0x10000, ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
        var h = CreateFileW(path, DELETE, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (h != new IntPtr(-1)) { CloseHandle(h); return false; }
        return Marshal.GetLastWin32Error() == 5;
    }

    private static int ShFileOperation(IEnumerable<string> paths, bool permanent)
    {
        var op = new SHFILEOPSTRUCT
        {
            hwnd   = IntPtr.Zero,
            wFunc  = FO_DELETE,
            pFrom  = string.Join("\0", paths) + "\0",   // the marshaler adds the last terminator: a list ends with two
            pTo    = null,
            fFlags = (ushort)(FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI | (permanent ? 0 : FOF_ALLOWUNDO)),
        };
        return SHFileOperationW(ref op);
    }
}
