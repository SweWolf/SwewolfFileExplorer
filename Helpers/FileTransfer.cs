using System.IO;
using System.Runtime.InteropServices;

namespace SwewolfFileExplorer.Helpers;

/// <summary>What the progress window shows. The transfer (on a background thread) writes it, the window reads it.</summary>
public sealed class TransferProgress
{
    private long _bytesDone, _bytesTotal;
    private int _fileIndex, _fileTotal;

    public bool Preparing { get; set; } = true;   // counting what there is to do
    public string CurrentName { get; set; } = "";
    public int FileIndex { get => Volatile.Read(ref _fileIndex); set => Volatile.Write(ref _fileIndex, value); }
    public int FileTotal { get => Volatile.Read(ref _fileTotal); set => Volatile.Write(ref _fileTotal, value); }
    public long BytesDone { get => Interlocked.Read(ref _bytesDone); set => Interlocked.Exchange(ref _bytesDone, value); }
    public long BytesTotal { get => Interlocked.Read(ref _bytesTotal); set => Interlocked.Exchange(ref _bytesTotal, value); }
}

/// <summary>What was done: the items that were copied or moved (from, to), the ones that failed (name, reason), and whether the user canceled.</summary>
public sealed record TransferResult(List<(string From, string To)> Done, List<(string Name, string Reason)> Failed, bool Canceled, int Total);

/// <summary>
/// Copies or moves files and folders on a background thread, with progress and a way to cancel (Paste, Cut and Paste).
///
/// The unit is what the user selected (a file or a folder): <b>an item is either done or not done</b>. On Cancel the items that
/// are finished stay finished, the one being copied is removed again (the half-written file or folder; the original is never
/// touched until its copy is complete) and the ones not started are left alone. Files are copied with Windows' own
/// <c>CopyFileEx</c>: it is fast, keeps dates, attributes and security, and reports progress and can be stopped.
/// A move inside one drive is a rename (instant); between drives it is a copy and then a delete of the original.
/// </summary>
public static class FileTransfer
{
    public static Task<TransferResult> RunAsync(IReadOnlyList<string> sources, string destinationFolder, bool move,
                                                TransferProgress progress, CancellationToken cancel) =>
        Task.Run(() => Run(sources, destinationFolder, move, progress, cancel));

    private static TransferResult Run(IReadOnlyList<string> sources, string destinationFolder, bool move,
                                      TransferProgress progress, CancellationToken cancel)
    {
        var done   = new List<(string, string)>();
        var failed = new List<(string, string)>();

        // First count what there is to do, so the window can say "file 3 of 12" and show a bar.
        int files = 0;
        long bytes = 0;
        foreach (var src in sources)
        {
            if (move && SameDrive(src, destinationFolder)) { files++; continue; }   // a rename: one step, no bytes
            try { Count(src, ref files, ref bytes, cancel); }
            catch (OperationCanceledException) { progress.Preparing = false; return new TransferResult(done, failed, true, sources.Count); }
            catch { files++; }   // unreadable: it is tried (and its error reported) when its turn comes
        }
        progress.FileTotal  = Math.Max(files, 1);
        progress.BytesTotal = bytes;
        progress.Preparing  = false;

        long bytesBefore = 0;
        foreach (var src in sources)
        {
            if (cancel.IsCancellationRequested) return new TransferResult(done, failed, true, sources.Count);

            var name = Path.GetFileName(src.TrimEnd('\\', '/'));
            var dest = GetUniqueDestPath(destinationFolder, name);
            try
            {
                if (move && SameDrive(src, destinationFolder))
                {
                    progress.CurrentName = name;
                    progress.FileIndex++;
                    if (Directory.Exists(src)) Directory.Move(src, dest); else File.Move(src, dest);
                }
                else
                {
                    if (Directory.Exists(src)) CopyDirectory(src, dest, progress, cancel, ref bytesBefore);
                    else                       CopyOne(src, dest, progress, cancel, ref bytesBefore);

                    if (move)   // the copy is complete: now the original goes
                    {
                        try { if (Directory.Exists(src)) Directory.Delete(src, recursive: true); else File.Delete(src); }
                        catch (Exception ex)
                        {
                            failed.Add((name, $"it was copied, but the original could not be deleted: {ex.Message}"));
                            continue;
                        }
                    }
                }
                done.Add((src, dest));
            }
            catch (OperationCanceledException)
            {
                RemovePartial(dest);
                return new TransferResult(done, failed, true, sources.Count);
            }
            catch (Exception ex)
            {
                RemovePartial(dest);
                failed.Add((name, ex.Message));
            }
        }

        return new TransferResult(done, failed, false, sources.Count);
    }

    // ── Counting ──────────────────────────────────────────────────────────────

    private static void Count(string path, ref int files, ref long bytes, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (Directory.Exists(path))
        {
            var dir = new DirectoryInfo(path);
            foreach (var f in dir.EnumerateFiles()) { files++; bytes += f.Length; }
            foreach (var d in dir.EnumerateDirectories())
                if ((d.Attributes & FileAttributes.ReparsePoint) == 0) Count(d.FullName, ref files, ref bytes, cancel);
        }
        else
        {
            files++;
            bytes += new FileInfo(path).Length;
        }
    }

    // ── Copying ───────────────────────────────────────────────────────────────

    private static void CopyDirectory(string src, string dest, TransferProgress progress, CancellationToken cancel, ref long bytesBefore)
    {
        cancel.ThrowIfCancellationRequested();
        Directory.CreateDirectory(dest);
        var dir = new DirectoryInfo(src);
        foreach (var f in dir.EnumerateFiles())
            CopyOne(f.FullName, Path.Combine(dest, f.Name), progress, cancel, ref bytesBefore);
        foreach (var d in dir.EnumerateDirectories())
        {
            // A junction or symbolic link is not followed (it could lead back into the folder being copied).
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            CopyDirectory(d.FullName, Path.Combine(dest, d.Name), progress, cancel, ref bytesBefore);
        }
    }

    private static void CopyOne(string src, string dest, TransferProgress progress, CancellationToken cancel, ref long bytesBefore)
    {
        cancel.ThrowIfCancellationRequested();
        progress.CurrentName = Path.GetFileName(src);
        progress.FileIndex++;

        long before = bytesBefore;
        int cancelFlag = 0;
        CopyProgressRoutine callback = (total, transferred, _, _, _, _, _, _, _) =>
        {
            progress.BytesDone = before + transferred;
            return cancel.IsCancellationRequested ? PROGRESS_CANCEL : PROGRESS_CONTINUE;
        };

        // COPY_FILE_FAIL_IF_EXISTS (1): never overwrite. COPY_FILE_ALLOW_DECRYPTED_DESTINATION (8): an encrypted file may go to a drive without encryption.
        if (!CopyFileExW(src, dest, callback, IntPtr.Zero, ref cancelFlag, 1 | 8))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ERROR_REQUEST_ABORTED || cancel.IsCancellationRequested) throw new OperationCanceledException();
            throw new IOException(new System.ComponentModel.Win32Exception(error).Message);
        }

        GC.KeepAlive(callback);
        bytesBefore += new FileInfo(src).Length;
        progress.BytesDone = bytesBefore;
    }

    /// <summary>Removes what an unfinished or failed copy left at <paramref name="dest"/> (a name made new for this copy, so nothing of the user's).</summary>
    private static void RemovePartial(string dest)
    {
        try
        {
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            else if (File.Exists(dest)) File.Delete(dest);
        }
        catch { /* nothing more can be done: the next paste gets another name */ }
    }

    // ── Names and drives ──────────────────────────────────────────────────────

    public static bool SameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>The path of <paramref name="name"/> in <paramref name="destDir"/>, or "name (1)", "name (2)" ... when that is taken.</summary>
    public static string GetUniqueDestPath(string destDir, string name)
    {
        var path = Path.Combine(destDir, name);
        if (!File.Exists(path) && !Directory.Exists(path)) return path;

        var ext            = Path.GetExtension(name);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(name);
        int i              = 1;

        do
        {
            path = Path.Combine(destDir, $"{nameWithoutExt} ({i++}){ext}");
        } while (File.Exists(path) || Directory.Exists(path));

        return path;
    }

    // ── Windows ───────────────────────────────────────────────────────────────

    private const uint PROGRESS_CONTINUE = 0;
    private const uint PROGRESS_CANCEL   = 1;
    private const int  ERROR_REQUEST_ABORTED = 1235;

    private delegate uint CopyProgressRoutine(long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred,
                                              uint streamNumber, uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CopyFileExW(string existingFileName, string newFileName, CopyProgressRoutine? progressRoutine,
                                           IntPtr data, ref int cancel, uint copyFlags);
}
