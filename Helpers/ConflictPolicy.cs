using System.IO;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace SwewolfFileExplorer.Helpers;

public enum ConflictChoice { Replace, KeepBoth, Skip, Cancel }

/// <summary>What the "file exists" question shows: the file in the folder and the file being pasted (size and date; -1 / null if not known).</summary>
public sealed record ConflictInfo(string Name, string Folder, long ExistingSize, DateTime ExistingWritten,
                                  long SourceSize, DateTime? SourceWritten, string Where, bool ReplaceIsPermanent,
                                  bool IsMove = false, bool MultipleFiles = true, string? PermanentReason = null);

/// <summary>
/// Decides what happens when a pasted file meets a file of the same name. The question is asked through <c>ask</c> (the window,
/// on the UI thread); "Do this for all conflicts" is remembered here for the rest of the paste. Called from the copying thread.
/// Without a question (<c>ask</c> null) the safe answer is Keep Both.
/// </summary>
public sealed class ConflictPolicy
{
    private readonly Func<ConflictInfo, (ConflictChoice Choice, bool ForAll)>? _ask;
    private ConflictChoice? _forAll;
    private bool _forAllWasPermanent;
    private volatile bool _asking;

    public ConflictPolicy(Func<ConflictInfo, (ConflictChoice, bool)>? ask) => _ask = ask;

    /// <summary>Set by the paste before it starts: a move or a copy, and whether more than one file is involved (title of the question; "for all" is only offered then).</summary>
    public bool IsMove { get; set; }
    public bool MultipleFiles { get; set; } = true;

    /// <summary>True while the question is on screen (the progress window waits for it).</summary>
    public bool IsAsking => _asking;

    public ConflictChoice Resolve(ConflictInfo info)
    {
        // "Replace for all" does not carry over to a drive with no Recycle Bin unless the user saw that warning when choosing it.
        if (_forAll is { } remembered && !(remembered == ConflictChoice.Replace && info.ReplaceIsPermanent && !_forAllWasPermanent))
            return remembered;
        if (_ask == null) return ConflictChoice.KeepBoth;
        info = info with { IsMove = IsMove, MultipleFiles = MultipleFiles };

        _asking = true;
        try
        {
            var (choice, all) = _ask(info);
            if (all && choice != ConflictChoice.Cancel) { _forAll = choice; _forAllWasPermanent = info.ReplaceIsPermanent; }
            return choice;
        }
        finally { _asking = false; }
    }
}

/// <summary>
/// The safe way to put a file where another one is: the new file is first written complete under a temporary name in the
/// <b>same folder</b> (same drive, so the last step is an instant rename), the old file then goes to the Recycle Bin, and the
/// temporary file takes the real name. A copy that stops halfway therefore never leaves a half file under a real name, and the
/// old file is never touched until the new one is complete.
/// </summary>
public static class FileReplace
{
    /// <summary>A file that was written but could not be given its name: it stays under its temporary name (never deleted).</summary>
    public sealed class KeepTempException(string message) : IOException(message);

    /// <summary>True if a file on this path goes to a Recycle Bin when deleted (a fixed local drive; not a network share or removable drive).</summary>
    public static bool HasRecycleBin(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return false;
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch { return false; }
    }

    /// <summary>
    /// Null if a file of this size goes to the Recycle Bin when deleted; otherwise why not (fit to show). Not on a network share or
    /// removable drive, not where the user chose "Don't move files to the Recycle Bin", and <b>not when the file is bigger than the
    /// bin's size limit for the drive</b>: Windows then deletes it for good, and the call we use for the Recycle Bin does so without
    /// asking (found by a test: a 400 GB file over a 375 GB limit was gone in half a second, no dialog, nothing in the bin).
    /// The limit is read from the registry (<c>HKCU\...\Explorer\BitBucket\Volume\{volume}</c>: <c>MaxCapacity</c> in MB, <c>NukeOnDelete</c>).
    /// </summary>
    public static string? WhyNotRecycled(string path, long size)
    {
        if (!HasRecycleBin(path)) return "there is no Recycle Bin on this drive";
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path))!;
            var name = new StringBuilder(260);
            if (!GetVolumeNameForVolumeMountPointW(root, name, name.Capacity)) return null;   // unknown: the old behavior
            var guid = name.ToString().Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(s => s.StartsWith("Volume{"))?[6..];
            if (guid == null) return null;

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + guid);
            if (key == null) return null;   // the user never changed it: Windows' own default
            if (key.GetValue("NukeOnDelete") is int nuke && nuke != 0) return "Recycle Bin is set to delete files at once on this drive";
            if (key.GetValue("MaxCapacity") is int maxMb && maxMb > 0 && size > maxMb * 1024L * 1024)
                return "the file is bigger than the Recycle Bin's size limit for this drive (" + FormatMb(maxMb) + ")";
        }
        catch { /* cannot tell: the old behavior */ }
        return null;
    }

    private static string FormatMb(int mb) => mb >= 1024 ? $"{mb / 1024.0:F0} GB" : $"{mb} MB";

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, int bufferLength);

    /// <summary>Throws (with a reason fit to show) if the existing file cannot be replaced: read-only, or in use by another program.</summary>
    public static void CheckCanReplace(string existing)
    {
        if ((File.GetAttributes(existing) & FileAttributes.ReadOnly) != 0)
            throw new IOException("the existing file is read-only");
        try { using var _ = new FileStream(existing, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (IOException) { throw new IOException("the existing file is in use by another program"); }
    }

    /// <summary>The old file goes to the Recycle Bin (or, where there is none, is deleted: the user was told when asked).</summary>
    public static void Remove(string existing)
    {
        if (WhyNotRecycled(existing, new FileInfo(existing).Length) != null)
        {
            File.Delete(existing);   // the user was told when asked ("Replace Permanently")
            return;
        }
        try { FileSystem.DeleteFile(existing, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); }
        catch (OperationCanceledException) { throw new IOException("the existing file was not moved to the Recycle Bin"); }
        if (File.Exists(existing)) throw new IOException("the existing file could not be moved to the Recycle Bin");
    }

    /// <summary>A name for the file while it is being written: in the same folder, starts with a dot, ends in .swewolf-part.</summary>
    public static string TempNameFor(string finalPath)
    {
        var dir  = Path.GetDirectoryName(finalPath)!;
        var name = Path.GetFileName(finalPath);
        if (name.Length > 100) name = name[..100];
        return Path.Combine(dir, "." + name + "." + Guid.NewGuid().ToString("N")[..8] + ".swewolf-part");
    }

    /// <summary>
    /// Gives the finished temporary file its real name and returns the path it got. If the name was taken meanwhile (and nothing
    /// was replaced) it gets a new name ("x (1)"). If something was replaced and this fails, the file is kept under its temporary
    /// name and the message says so.
    /// </summary>
    public static string MoveIntoPlace(string temp, string finalPath, bool replaced)
    {
        try
        {
            File.Move(temp, finalPath);
            return finalPath;
        }
        catch (IOException ex)
        {
            if (!replaced && (File.Exists(finalPath) || Directory.Exists(finalPath)))
            {
                var other = FileTransfer.GetUniqueDestPath(Path.GetDirectoryName(finalPath)!, Path.GetFileName(finalPath));
                File.Move(temp, other);
                return other;
            }
            if (replaced)
                throw new KeepTempException($"the old file is in the Recycle Bin, but the new one could not be given its name and is saved as \"{temp}\" ({ex.Message})");
            throw;
        }
    }

    /// <summary>
    /// What to do about the file that is wanted at <paramref name="dest"/>. Returns the path to write to and whether an existing file is
    /// replaced; <c>Skip</c> and <c>Cancel</c> are returned as they are. A name taken by a folder, or the source itself, is Keep Both
    /// without a question (as Explorer's "Copy" in the same folder).
    /// </summary>
    public static (ConflictChoice Choice, string Final, bool Replace) Decide(ConflictPolicy? policy, string dest, long sourceSize,
        DateTime? sourceWritten, string where, bool sourceIsDest)
    {
        if (!File.Exists(dest) && !Directory.Exists(dest)) return (ConflictChoice.KeepBoth, dest, false);

        var folder = Path.GetDirectoryName(dest)!;
        var name   = Path.GetFileName(dest);
        if (sourceIsDest || Directory.Exists(dest))
            return (ConflictChoice.KeepBoth, FileTransfer.GetUniqueDestPath(folder, name), false);

        var existing = new FileInfo(dest);
        var whyNot = WhyNotRecycled(dest, existing.Length);
        var info = new ConflictInfo(name, folder, existing.Length, existing.LastWriteTime, sourceSize, sourceWritten, where, whyNot != null, PermanentReason: whyNot);
        var choice = (policy ?? new ConflictPolicy(null)).Resolve(info);

        return choice switch
        {
            ConflictChoice.Replace  => (choice, dest, true),
            ConflictChoice.KeepBoth => (choice, FileTransfer.GetUniqueDestPath(folder, name), false),
            _                       => (choice, dest, false),
        };
    }
}
