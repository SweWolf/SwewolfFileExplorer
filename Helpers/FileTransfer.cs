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
    public string ItemWord { get; set; } = "File";   // "File 3 of 12" (a delete says "Item")
    public int FileIndex { get => Volatile.Read(ref _fileIndex); set => Volatile.Write(ref _fileIndex, value); }
    public int FileTotal { get => Volatile.Read(ref _fileTotal); set => Volatile.Write(ref _fileTotal, value); }
    public long BytesDone { get => Interlocked.Read(ref _bytesDone); set => Interlocked.Exchange(ref _bytesDone, value); }
    public long BytesTotal { get => Interlocked.Read(ref _bytesTotal); set => Interlocked.Exchange(ref _bytesTotal, value); }
}

/// <summary>What was done: the items that were copied or moved (from, to), the ones that failed (name, reason), whether the user canceled, and how many files were skipped at the user's choice.</summary>
public sealed record TransferResult(List<(string From, string To)> Done, List<(string Name, string Reason)> Failed, bool Canceled, int Total, int Skipped = 0,
                                    List<(string From, string To)>? Renamed = null, List<string>? Identical = null,
                                    List<(string Path, bool IsDirectory)>? CreatedItems = null)
{
    // CreatedItems: every new file and folder the finished items made (for the Undo of a copy; never a replaced file).

    /// <summary>Source files that were not copied because the file already in the destination has exactly the same contents (a copy only, never a move).</summary>
    public List<string> IdenticalSkipped => Identical ?? new();

    /// <summary>Existing files that were renamed to "name (old N)" so the pasted file could have their name (what the library remembers follows them).</summary>
    public List<(string From, string To)> RenamedOld => Renamed ?? new();

    /// <summary>Sources (full paths) that failed because Windows said "access denied": the paste offers to run them again as the administrator (<see cref="ElevatedTransfer"/>).</summary>
    public List<string> AccessDenied { get; init; } = new();
}

/// <summary>
/// Copies or moves files and folders on a background thread, with progress and a way to cancel (Paste, Cut and Paste).
///
/// The unit is what the user selected (a file or a folder): <b>an item is either done or not done</b>. On Cancel the items that
/// are finished stay finished, the one being copied is removed again (the half-written file; for a folder only what this
/// paste made in it) and the ones not started are left alone. Files are copied with Windows' own
/// <c>CopyFileEx</c>: it is fast, keeps dates, attributes and security, reports progress and can be stopped.
/// A move inside one drive is a rename (instant); between drives it is a copy and then a delete of the original.
///
/// <b>A name that is taken</b> (see <see cref="ConflictPolicy"/>, <see cref="FileReplace"/>): the user is asked Replace / Keep Both /
/// Skip / Cancel. Every file is first written under a temporary name in its destination folder and renamed when complete; a replaced
/// file goes to the Recycle Bin only when the new one is complete. A folder meeting a folder of the same name is merged (the files
/// inside are asked about one by one); a name taken by something of another kind, or by the source itself, is Keep Both.
/// </summary>
public static class FileTransfer
{
    public static Task<TransferResult> RunAsync(IReadOnlyList<string> sources, string destinationFolder, bool move,
                                                TransferProgress progress, CancellationToken cancel, ConflictPolicy? policy = null) =>
        Task.Run(() => Run(sources, destinationFolder, move, progress, cancel, policy));

    /// <summary>What one item (a file, or a folder with everything in it) made, and what it copied (for the library and for a move's delete).</summary>
    private sealed class ItemState
    {
        public readonly List<(string Path, bool IsDirectory)> Created = new();   // new files and folders made by this paste (never a replaced file)
        public readonly List<(string From, string To)> Files = new();            // every file copied or moved, source and destination
        public int Skipped;
        public bool Merged;
    }

    private sealed class Ctx(bool move, TransferProgress progress, CancellationToken cancel, ConflictPolicy? policy)
    {
        public readonly bool Move = move;
        public readonly TransferProgress Progress = progress;
        public readonly CancellationToken Cancel = cancel;
        public readonly ConflictPolicy? Policy = policy;
        public long BytesBefore;
        public int Skipped;
        public readonly List<string> Identical = new();   // copied-over files that were byte for byte the same: nothing was done
        public readonly List<(string From, string To)> Renamed = new();   // old files renamed to "(old N)", kept even if the item later fails
        public readonly List<(string Path, bool IsDirectory)> AllCreated = new();   // what the finished items made (see TransferResult.CreatedItems)
        public ItemState Item = new();
    }

    private static TransferResult Run(IReadOnlyList<string> sources, string destinationFolder, bool move,
                                      TransferProgress progress, CancellationToken cancel, ConflictPolicy? policy)
    {
        var done   = new List<(string, string)>();
        var failed = new List<(string, string)>();
        var denied = new List<string>();

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

        if (policy != null)
        {
            policy.IsMove = move;
            policy.MultipleFiles = sources.Count > 1 || sources.Any(Directory.Exists);   // one file: nothing to apply "to all"
        }

        var ctx = new Ctx(move, progress, cancel, policy);
        foreach (var src in sources)
        {
            if (cancel.IsCancellationRequested) return new TransferResult(done, failed, true, sources.Count, ctx.Skipped, ctx.Renamed, ctx.Identical, ctx.AllCreated);

            var name = Path.GetFileName(src.TrimEnd('\\', '/'));
            var dest = Path.Combine(destinationFolder, name);
            ctx.Item = new ItemState();

            // A move to another drive is a copy and then a delete of the original. If another program has the original open the delete
            // would fail after the copy was made: so it is not started at all (no stray copy); the caller can offer Try Again.
            if (move && !SameDrive(src, destinationFolder) && FileLockInfo.IsLocked(src))
            {
                failed.Add((name, "it is in use by another program, so it was not moved"));
                continue;
            }

            try
            {
                if (Directory.Exists(src))
                {
                    var (final, merged) = ChooseFolderDest(src, dest);
                    ctx.Item.Merged = merged;

                    if (move && !merged && SameDrive(src, destinationFolder))
                    {
                        progress.CurrentName = name;
                        progress.FileIndex++;
                        Directory.Move(src, final);
                        done.Add((src, final));
                        continue;
                    }

                    if (!merged) { Directory.CreateDirectory(final); ctx.Item.Created.Add((final, true)); }
                    CopyDirectory(src, final, ctx);

                    if (move) DeleteCopiedSources(src, ctx, failed);

                    // A folder that arrived whole is one entry for the library; a merged or partly skipped one is its files.
                    if (!merged && ctx.Item.Skipped == 0) done.Add((src, final));
                    else done.AddRange(ctx.Item.Files);
                    ctx.AllCreated.AddRange(ctx.Item.Created);
                }
                else
                {
                    var final = TransferFile(src, dest, ctx);
                    if (final == null) continue;   // skipped at the user's choice

                    if (move && !SameDrive(src, destinationFolder))   // the copy is complete: now the original goes
                    {
                        try { File.Delete(src); }
                        catch (Exception ex)
                        {
                            failed.Add((name, $"it was copied, but the original could not be deleted: {ex.Message}"));
                            continue;
                        }
                    }
                    done.Add((src, final));
                    ctx.AllCreated.AddRange(ctx.Item.Created);
                }
            }
            catch (OperationCanceledException)
            {
                Undo(ctx.Item);
                return new TransferResult(done, failed, true, sources.Count, ctx.Skipped, ctx.Renamed, ctx.Identical, ctx.AllCreated) { AccessDenied = denied };
            }
            catch (Exception ex)
            {
                Undo(ctx.Item);
                failed.Add((name, ex.Message));
                if (IsAccessDenied(ex)) denied.Add(src);
                try
                {
                    var text = $"Transfer of \"{src}\" to \"{destinationFolder}\" failed: {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}";
                    if (IsAccessDenied(ex)) LogFiles.AppendWarning(text + " (administrator permission is needed)");   // the program offers to go on as the administrator
                    else LogFiles.AppendError(text);
                }
                catch { }
            }
        }

        return new TransferResult(done, failed, false, sources.Count, ctx.Skipped, ctx.Renamed, ctx.Identical, ctx.AllCreated) { AccessDenied = denied };
    }

    /// <summary>Windows' "access denied" (error 5), however it reached us: an UnauthorizedAccessException or an IOException that carries the code.</summary>
    internal static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException || (ex.HResult & 0xFFFF) == 5 && ((uint)ex.HResult >> 16) == 0x8007;

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

    // ── Folders ───────────────────────────────────────────────────────────────

    /// <summary>The folder to copy into: the same-named folder that is there (merge), or the new name when the name is taken by a file or by the source itself.</summary>
    private static (string Path, bool Merged) ChooseFolderDest(string src, string dest)
    {
        if (!Directory.Exists(dest) && !File.Exists(dest)) return (dest, false);
        if (Directory.Exists(dest) && !SamePath(src, dest)) return (dest, true);
        return (GetUniqueDestPath(Path.GetDirectoryName(dest)!, Path.GetFileName(dest)), false);
    }

    private static void CopyDirectory(string src, string dest, Ctx ctx)
    {
        ctx.Cancel.ThrowIfCancellationRequested();
        var dir = new DirectoryInfo(src);
        foreach (var f in dir.EnumerateFiles())
            TransferFile(f.FullName, Path.Combine(dest, f.Name), ctx);

        foreach (var d in dir.EnumerateDirectories())
        {
            // A junction or symbolic link is not followed (it could lead back into the folder being copied).
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;

            var sub = Path.Combine(dest, d.Name);
            if (File.Exists(sub)) sub = GetUniqueDestPath(dest, d.Name);   // a file of that name: the folder gets another
            if (!Directory.Exists(sub))
            {
                Directory.CreateDirectory(sub);
                ctx.Item.Created.Add((sub, true));
            }
            CopyDirectory(d.FullName, sub, ctx);
        }
    }

    /// <summary>After a folder was moved by copying: deletes the files that were copied, then the folders left empty. Files that were skipped stay, and so do their folders.</summary>
    private static void DeleteCopiedSources(string srcRoot, Ctx ctx, List<(string, string)> failed)
    {
        foreach (var (from, _) in ctx.Item.Files)
        {
            try { File.Delete(from); }
            catch (Exception ex) { failed.Add((Path.GetFileName(from), $"it was copied, but the original could not be deleted: {ex.Message}")); }
        }
        RemoveEmptyFolders(srcRoot);
    }

    private static void RemoveEmptyFolders(string folder)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(folder))
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) RemoveEmptyFolders(d);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch { /* not empty, or in use: it stays */ }
    }

    // ── One file ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Copies (or, inside one drive, moves) one file to <paramref name="dest"/>, asking what to do if the name is taken.
    /// Returns where it went, or null if the user skipped it.
    /// </summary>
    private static string? TransferFile(string src, string dest, Ctx ctx)
    {
        ctx.Cancel.ThrowIfCancellationRequested();
        var info = new FileInfo(src);
        ctx.Progress.CurrentName = info.Name;
        ctx.Progress.FileIndex++;

        // A copy onto a file with exactly the same contents does nothing (checked byte for byte; a move is never checked).
        if (!ctx.Move && File.Exists(dest) && !SamePath(src, dest) && FilesAreIdentical(src, dest, ctx))
        {
            ctx.BytesBefore += info.Length;
            ctx.Progress.BytesDone = ctx.BytesBefore;
            ctx.Identical.Add(src);
            return null;
        }

        var (choice, final, replace) = FileReplace.Decide(ctx.Policy, dest, info.Length, info.LastWriteTime,
                                                          "Pasted file", SamePath(src, dest));
        if (choice == ConflictChoice.Cancel) throw new OperationCanceledException();
        if (choice == ConflictChoice.Skip)
        {
            ctx.BytesBefore += info.Length;
            ctx.Progress.BytesDone = ctx.BytesBefore;
            ctx.Skipped++;
            ctx.Item.Skipped++;
            return null;
        }

        bool renameOld = choice == ConflictChoice.KeepBothRenameOld;
        if (replace) FileReplace.CheckCanReplace(dest);   // before a big copy, not after it
        if (renameOld) FileReplace.CheckCanRename(dest);

        if (ctx.Move && SameDrive(src, final))
        {
            // A rename: instant, so no temporary file is needed.
            if (replace) FileReplace.Remove(dest);
            if (renameOld) ctx.Renamed.Add((final, FileReplace.PutInPlaceRenamingOld(src, final, newIsTemp: false)));
            else File.Move(src, final);
        }
        else
        {
            final = CopyViaTemp(src, final, replace, renameOld, ctx);
            if (!replace && !renameOld) ctx.Item.Created.Add((final, false));   // a replaced / renamed-old file stays: the new one is complete and in place
        }

        ctx.Item.Files.Add((src, final));
        return final;
    }

    /// <summary>
    /// True only if both files have the same size and every byte is the same. Different size is decided at once; otherwise both are read
    /// to the end (progress follows, Cancel works). A file that cannot be read counts as not identical (the normal question follows).
    /// </summary>
    public static bool FilesAreIdentical(string a, string b) => FilesAreIdentical(a, b, null);

    private static bool FilesAreIdentical(string a, string b, Ctx? ctx)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;

            const int Size = 1024 * 1024;
            var bufA = new byte[Size];
            var bufB = new byte[Size];
            using var sa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Size);
            using var sb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Size);
            long done = 0;
            while (true)
            {
                ctx?.Cancel.ThrowIfCancellationRequested();
                int na = sa.ReadAtLeast(bufA, Size, throwOnEndOfStream: false);
                int nb = sb.ReadAtLeast(bufB, Size, throwOnEndOfStream: false);
                if (na != nb) return false;
                if (na == 0) return done == fa.Length;   // a file that grew or shrank meanwhile is not the same
                if (!bufA.AsSpan(0, na).SequenceEqual(bufB.AsSpan(0, nb))) return false;
                done += na;
                if (ctx != null) ctx.Progress.BytesDone = ctx.BytesBefore + done;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static string CopyViaTemp(string src, string final, bool replace, bool renameOld, Ctx ctx)
    {
        var temp = FileReplace.TempNameFor(final);
        bool keepTemp = false;
        try
        {
            // A file that is being written to (a log) may grow while it is copied: that is fine, the copy is a snapshot from a moment
            // while it ran. Shorter than it was when the copy began is not.
            long expected = new FileInfo(src).Length;
            CopyOne(src, temp, ctx);
            if (new FileInfo(temp).Length < expected) throw new IOException("the copy is not complete");

            if (renameOld)
            {
                ctx.Renamed.Add((final, FileReplace.PutInPlaceRenamingOld(temp, final, newIsTemp: true)));
                return final;
            }
            if (replace) FileReplace.Remove(final);
            return FileReplace.MoveIntoPlace(temp, final, replace);
        }
        catch (FileReplace.KeepTempException) { keepTemp = true; throw; }
        finally
        {
            if (!keepTemp)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* a leftover .swewolf-part file, ours */ }
            }
        }
    }

    private static void CopyOne(string src, string dest, Ctx ctx)
    {
        long before = ctx.BytesBefore;
        int cancelFlag = 0;
        CopyProgressRoutine callback = (total, transferred, _, _, _, _, _, _, _) =>
        {
            ctx.Progress.BytesDone = before + transferred;
            return ctx.Cancel.IsCancellationRequested ? PROGRESS_CANCEL : PROGRESS_CONTINUE;
        };

        // COPY_FILE_FAIL_IF_EXISTS (1): never overwrite. COPY_FILE_ALLOW_DECRYPTED_DESTINATION (8): an encrypted file may go to a drive without encryption.
        if (!CopyFileExW(src, dest, callback, IntPtr.Zero, ref cancelFlag, 1 | 8))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ERROR_REQUEST_ABORTED || ctx.Cancel.IsCancellationRequested) throw new OperationCanceledException();
            throw new IOException(new System.ComponentModel.Win32Exception(error).Message, unchecked((int)(0x80070000u | (uint)error)));   // the code is kept: "access denied" offers the administrator
        }

        GC.KeepAlive(callback);
        ctx.BytesBefore += new FileInfo(src).Length;
        ctx.Progress.BytesDone = ctx.BytesBefore;
    }

    /// <summary>
    /// Removes what an unfinished or failed item made: the files, then the folders, deepest first, and only what this paste created.
    /// A replaced file is not in the list (the new file is complete and in place, the old one in the Recycle Bin).
    /// </summary>
    private static void Undo(ItemState item)
    {
        foreach (var (path, isDir) in Enumerable.Reverse(item.Created))
        {
            try
            {
                if (!isDir) File.Delete(path);
                else if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
            }
            catch { /* leave it: better a leftover than touching something else */ }
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

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
