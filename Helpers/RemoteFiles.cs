using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Files on the clipboard that are not files on a disk: "virtual files", as a Remote Desktop session (mstsc), Outlook attachments, a
/// zip window or a phone put there. The clipboard holds only the names and sizes (<c>FileGroupDescriptorW</c>); the bytes are
/// fetched one file at a time (<c>FileContents</c>, an <c>IStream</c>) when they are pasted, so a big file really travels at that moment.
///
/// <b>Safety:</b> this code only ever <i>reads</i> from the clipboard and only ever <i>creates</i> things in the destination folder
/// (and, when the user chose Replace, puts the old file in the Recycle Bin: see <see cref="ConflictPolicy"/> and <see cref="FileReplace"/>;
/// every file is written complete under a temporary name first). A name that is taken is asked about, as for any paste.
/// The names come from another computer, so every part of a path is checked: no <c>..</c>, no drive letter or root, no characters
/// Windows does not allow, no device names (CON, NUL ...); an item with a bad name is skipped and reported. A Cut on the other side
/// is treated as a Copy: nothing is ever deleted over there from here.
/// Cancel removes what the unfinished item made (its half file, and for a folder the files and folders made so far; finished items stay).
/// </summary>
public static class RemoteFiles
{
    private const string GroupFormat    = "FileGroupDescriptorW";
    private const string ContentsFormat = "FileContents";

    /// <summary>True if the clipboard holds virtual files (names only; asking does not fetch any data).</summary>
    public static bool HasVirtualFiles()
    {
        try { return Clipboard.ContainsData(GroupFormat); }
        catch (COMException) { return false; }
    }

    private sealed record Entry(string RawName, bool IsDirectory, long Size, DateTime? Written);

    /// <summary>
    /// Copies the virtual files on the clipboard into <paramref name="destination"/>. Runs on a thread of its own (the clipboard
    /// needs an STA thread, and the other side may take a while to answer).
    /// </summary>
    public static Task<TransferResult> RunAsync(string destination, TransferProgress progress, CancellationToken token, ConflictPolicy? policy = null)
    {
        var tcs = new TaskCompletionSource<TransferResult>();
        var thread = new Thread(() =>
        {
            OleInitialize(IntPtr.Zero);
            try { tcs.SetResult(Run(destination, progress, token, policy)); }
            catch (Exception ex)
            {
                // The clipboard could not be read at all (the other side is gone, the clipboard changed ...): nothing was made.
                tcs.SetResult(new TransferResult(new(), new() { ("Clipboard", ex.Message) }, false, 0));
            }
            finally { OleUninitialize(); }
        }) { IsBackground = true, Name = "RemoteFilesPaste" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static TransferResult Run(string destination, TransferProgress progress, CancellationToken token, ConflictPolicy? policy)
    {
        var done   = new List<(string From, string To)>();
        var failed = new List<(string Name, string Reason)>();

        if (OleGetClipboard(out var data) != 0 || data == null)
            throw new InvalidOperationException("The clipboard is in use by another program. Try again.");

        uint clipboardAtStart = GetClipboardSequenceNumber();
        var entries = ReadDescriptors(data);
        progress.FileTotal  = entries.Count(e => !e.IsDirectory);
        progress.BytesTotal = entries.Where(e => !e.IsDirectory && e.Size > 0).Sum(e => e.Size);
        progress.Preparing  = false;
        if (policy != null)
        {
            policy.IsMove = false;   // a Cut over there is only a copy here
            policy.MultipleFiles = progress.FileTotal > 1;
        }

        // One unit per top-level name (a file, or a folder with everything in it): it is either done or not.
        var topFinal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // top-level name from the other side -> its path here
        var units    = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
        int fileIndex = 0;
        int skipped = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (token.IsCancellationRequested) break;

            var segments = SplitSafe(entry.RawName, out var problem);
            if (segments == null)
            {
                failed.Add((entry.RawName.Length > 0 ? entry.RawName : "(no name)", problem!));
                continue;
            }

            var top = segments[0];
            if (!topFinal.TryGetValue(top, out var topPath))
            {
                // A folder meets a folder of the same name: merged (its files are asked about one by one). A single file's name is
                // settled when it is copied. A name taken by something of the other kind gets a new name.
                var candidate = Path.Combine(destination, top);
                bool folderUnit = segments.Count > 1 || entry.IsDirectory;
                topPath = folderUnit
                    ? (File.Exists(candidate) ? FileTransfer.GetUniqueDestPath(destination, top) : candidate)
                    : candidate;
                topFinal[top] = topPath;
                units[top] = new Unit(topPath);
            }
            var unit = units[top];
            var target = segments.Count == 1 ? topPath : Path.Combine(new[] { topPath }.Concat(segments.Skip(1)).ToArray());

            try
            {
                if (entry.IsDirectory)
                {
                    if (!Directory.Exists(target))
                    {
                        Directory.CreateDirectory(target);
                        unit.Created.Add((target, true));
                    }
                    continue;
                }

                // A folder's files need the folder, even if the other side did not list it.
                var parent = Path.GetDirectoryName(target)!;
                if (!Directory.Exists(parent))
                {
                    MakeDirectories(parent, destination, unit);
                }

                progress.FileIndex   = ++fileIndex;
                progress.CurrentName = segments[^1];

                var placed = CopyOne(data, i, target, entry, unit, progress, token, policy);
                if (placed == null) skipped++;
                else if (segments.Count == 1) unit.TopPath = placed;   // a single file may have got another name
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                unit.Failed = true;
                if (GetClipboardSequenceNumber() != clipboardAtStart)
                {
                    // Something else was copied: the other side takes the files back, as in Explorer. The rest cannot be fetched.
                    failed.Add((segments[^1], "the clipboard changed while copying. Files from the other computer can only be fetched while they are on the clipboard"));
                    failed.Add(("The remaining files", "not copied"));
                    break;
                }
                failed.Add((segments[^1], ex is IOException or UnauthorizedAccessException ? ex.Message : "could not be read from the clipboard: " + ex.Message));
            }
        }

        bool canceled = token.IsCancellationRequested;
        if (canceled)
        {
            // The unit that was being copied is not finished: take away what it made. The ones before it stay.
            foreach (var unit in units.Values.Where(u => u.Open)) unit.Undo();
        }

        foreach (var (name, unit) in units)
        {
            if (unit.Failed || (canceled && unit.Open)) continue;
            done.Add(("remote:" + name, unit.TopPath));
        }

        return new TransferResult(done, failed, canceled, units.Count, skipped);
    }

    private sealed class Unit(string topPath)
    {
        public string TopPath { get; set; } = topPath;
        public List<(string Path, bool IsDirectory)> Created { get; } = new();   // new files and folders only: never a replaced file, never a folder that was there
        public bool Failed;
        public bool Open;   // a file of it was being written when the paste was canceled

        /// <summary>Removes what this paste made for the item: its files, then its (now empty) folders, deepest first. Nothing else.</summary>
        public void Undo()
        {
            foreach (var (path, isDir) in Created.AsEnumerable().Reverse())
            {
                try
                {
                    if (!isDir) File.Delete(path);
                    else if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
                }
                catch { /* leave it: better a leftover than touching something else */ }
            }
        }
    }

    private static void MakeDirectories(string folder, string destination, Unit unit)
    {
        // Make each missing level below the destination, remembering that we made it.
        var missing = new Stack<string>();
        for (var d = folder; d != null && !Directory.Exists(d) && !IsSame(d, destination); d = Path.GetDirectoryName(d))
            missing.Push(d);
        foreach (var d in missing)
        {
            Directory.CreateDirectory(d);
            unit.Created.Add((d, true));
        }
    }

    private static bool IsSame(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // ── One file ──────────────────────────────────────────────────────────────

    /// <summary>Copies one file; returns where it went, or null if the user skipped it.</summary>
    private static string? CopyOne(ComTypes.IDataObject data, int index, string target, Entry entry, Unit unit, TransferProgress progress,
                                   CancellationToken token, ConflictPolicy? policy)
    {
        // A name that is taken: ask before anything is fetched from the other side.
        var (choice, final, replace) = FileReplace.Decide(policy, target, entry.Size, entry.Written, "File from the clipboard", false);
        if (choice == ConflictChoice.Cancel) throw new OperationCanceledException();
        if (choice == ConflictChoice.Skip)
        {
            if (entry.Size > 0) progress.BytesDone += entry.Size;
            return null;
        }
        if (replace) FileReplace.CheckCanReplace(final);

        var format = new ComTypes.FORMATETC
        {
            cfFormat = unchecked((short)RegisterClipboardFormat(ContentsFormat)),
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex   = index,
            ptd      = IntPtr.Zero,
            tymed    = ComTypes.TYMED.TYMED_ISTREAM | ComTypes.TYMED.TYMED_HGLOBAL,
        };

        data.GetData(ref format, out var medium);
        var temp = FileReplace.TempNameFor(final);   // written complete under this name, in the destination folder, then renamed
        FileStream? output = null;
        bool keepTemp = false;
        unit.Open = true;
        try
        {
            output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long written = 0;

            if (medium.tymed == ComTypes.TYMED.TYMED_ISTREAM)
            {
                var stream = (ComTypes.IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                var chunk   = new byte[256 * 1024];
                var readPtr = Marshal.AllocCoTaskMem(sizeof(int));
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        stream.Read(chunk, chunk.Length, readPtr);
                        int read = Marshal.ReadInt32(readPtr);
                        if (read <= 0) break;
                        output.Write(chunk, 0, read);
                        written += read;
                        progress.BytesDone += read;
                    }
                }
                finally { Marshal.FreeCoTaskMem(readPtr); }
            }
            else if (medium.tymed == ComTypes.TYMED.TYMED_HGLOBAL)
            {
                long size = (long)GlobalSize(medium.unionmember);
                var pointer = GlobalLock(medium.unionmember);
                if (pointer == IntPtr.Zero) throw new IOException("no data");
                try
                {
                    var bytes = new byte[size];
                    Marshal.Copy(pointer, bytes, 0, (int)size);
                    output.Write(bytes, 0, bytes.Length);
                    written = size;
                    progress.BytesDone += size;
                }
                finally { GlobalUnlock(medium.unionmember); }
            }
            else throw new IOException("the other side gave no data");

            output.Dispose();
            output = null;

            // The other side told how big the file was. More arrived: it grew while it was copied (a log); that is fine, the copy is a
            // snapshot. Less arrived: the file got shorter, or the connection broke.
            if (entry.Size >= 0 && written < entry.Size) throw new IOException("the file got shorter while it was being copied, or the connection was interrupted");
            if (entry.Written is { } time) File.SetLastWriteTime(temp, time);

            if (replace) FileReplace.Remove(final);
            final = FileReplace.MoveIntoPlace(temp, final, replace);
            if (!replace) unit.Created.Add((final, false));
            unit.Open = false;
            return final;
        }
        catch (FileReplace.KeepTempException) { keepTemp = true; throw; }
        finally
        {
            ReleaseStgMedium(ref medium);
            output?.Dispose();
            if (!keepTemp)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* a leftover .swewolf-part file, ours */ }
            }
        }
    }

    // ── Names ─────────────────────────────────────────────────────────────────

    private static readonly Regex DeviceName = new(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Splits a path from the other computer into safe parts, or returns null and says why not. Nothing that could leave the
    /// destination folder gets through: ".." , a root or drive ("C:\x", "\\server\x", "/x"), ":" (also streams), characters
    /// Windows does not allow, device names, trailing dots and spaces.
    /// </summary>
    internal static List<string>? SplitSafe(string raw, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(raw)) { problem = "it has no name"; return null; }
        if (raw.StartsWith('\\') || raw.StartsWith('/')) { problem = "its name starts with a slash"; return null; }

        var parts = raw.Split('\\', '/');
        var list  = new List<string>();
        var bad   = Path.GetInvalidFileNameChars();
        foreach (var p in parts)
        {
            if (p.Length == 0) continue;   // a doubled or trailing slash
            if (p == "." || p == "..") { problem = "its name contains \"" + p + "\""; return null; }
            if (p.IndexOfAny(bad) >= 0) { problem = "its name has a character Windows does not allow"; return null; }
            if (p.EndsWith('.') || p.EndsWith(' ')) { problem = "a name ends with a dot or a space"; return null; }
            if (DeviceName.IsMatch(p)) { problem = "\"" + p + "\" is a reserved Windows name"; return null; }
            list.Add(p);
        }
        if (list.Count == 0) { problem = "it has no name"; return null; }
        return list;
    }

    // ── The list of names ─────────────────────────────────────────────────────

    private const int DescriptorSize = 592;   // FILEDESCRIPTORW
    private const uint FD_ATTRIBUTES = 0x4, FD_WRITESTIME = 0x20, FD_FILESIZE = 0x40;

    private static List<Entry> ReadDescriptors(ComTypes.IDataObject data)
    {
        var format = new ComTypes.FORMATETC
        {
            cfFormat = unchecked((short)RegisterClipboardFormat(GroupFormat)),
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex   = -1,
            ptd      = IntPtr.Zero,
            tymed    = ComTypes.TYMED.TYMED_HGLOBAL,
        };
        data.GetData(ref format, out var medium);
        try
        {
            var size = (long)GlobalSize(medium.unionmember);
            var p = GlobalLock(medium.unionmember);
            if (p == IntPtr.Zero || size < 4) return new();
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(p, bytes, 0, (int)size);
                int count = BitConverter.ToInt32(bytes, 0);
                var list = new List<Entry>();
                for (int i = 0; i < count; i++)
                {
                    int o = 4 + i * DescriptorSize;
                    if (o + DescriptorSize > bytes.Length) break;

                    uint flags = BitConverter.ToUInt32(bytes, o);
                    uint attrs = BitConverter.ToUInt32(bytes, o + 36);
                    bool isDir = (flags & FD_ATTRIBUTES) != 0 && (attrs & 0x10) != 0;
                    long fileSize = (flags & FD_FILESIZE) != 0
                        ? ((long)BitConverter.ToUInt32(bytes, o + 64) << 32) | BitConverter.ToUInt32(bytes, o + 68)
                        : -1;
                    DateTime? written = null;
                    if ((flags & FD_WRITESTIME) != 0)
                    {
                        long ft = BitConverter.ToInt64(bytes, o + 52);
                        if (ft > 0) try { written = DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { }
                    }
                    var name = Encoding.Unicode.GetString(bytes, o + 72, 520);
                    int nul = name.IndexOf('\0');
                    if (nul >= 0) name = name[..nul];
                    list.Add(new Entry(name, isDir, fileSize, written));
                }
                return list;
            }
            finally { GlobalUnlock(medium.unionmember); }
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int OleGetClipboard(out ComTypes.IDataObject data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);
}
