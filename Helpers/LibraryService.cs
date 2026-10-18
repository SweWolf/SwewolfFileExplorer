using System.IO;
using Microsoft.Data.Sqlite;

namespace SwewolfFileExplorer.Helpers;

/// <summary>Unread: nothing in the folder was opened. Partly: something was. Read: everything (subfolders too) was. None: an empty folder.</summary>
public enum FolderState { None, Unread, PartlyRead, Read }

/// <summary>What the library knows about one folder: which of its files were opened, and the folder's own state.</summary>
public sealed record FolderInfo(HashSet<string> ReadNames, FolderState State);

/// <summary>
/// The explorer's side of the library: what a folder looks like now (visible files, subfolders, packed files) compared with
/// what <see cref="LibraryStore"/> remembers, and from that the state of the folder (Unread, Partly Read, Read).
/// A file is read when it is opened in the app (or watched for a while in the media player). A folder is Read when every
/// visible file in it and every subfolder (a packed file counts as a subfolder) is read: hidden files and folders are not
/// counted. A folder that was never listed is not Read, so a parent turns Read only after the whole tree has been seen
/// (or after "Mark as Read" on it). Only what must be remembered is stored: no row means unread.
/// Where the files are and what is in them: docs/library-db.md.
/// </summary>
public static class LibraryService
{
    static LibraryService()
    {
        // Read marks are written in the background by LibraryStore: the state of that folder is worked out afterwards.
        LibraryStore.FolderWritten += folder =>
        {
            FolderInfo info;
            lock (LibraryStore.Gate) info = SyncCore(folder, create: true);
            FolderChanged?.Invoke(folder, info);
        };
    }

    /// <summary>A folder's state changed (raised on a background thread): the window updates what it shows.</summary>
    public static event Action<string, FolderInfo>? FolderChanged;

    /// <summary>Only for tests: use this file instead of the user's main database.</summary>
    public static string? MainDbFileOverride
    {
        get => LibraryStore.MainDbFileOverride;
        set => LibraryStore.MainDbFileOverride = value;
    }

    public static void SetDriveTypeForTest(string root, uint type) => LibraryStore.SetDriveTypeForTest(root, type);
    public static bool IsPortableDrive(string root) => LibraryStore.IsPortableDrive(root);

    /// <summary>Remembers that these files were opened (or not), in the background. See <see cref="LibraryStore.SetRead"/>.</summary>
    public static void SetRead(IEnumerable<string> paths, bool read) => LibraryStore.SetRead(paths, read);

    /// <summary>Remembers, in the background, that this file was the last one opened in its folder (Jump to Last Opened File).</summary>
    public static void SetLastOpened(string filePath) => Queue(() => LibraryStore.SetLastOpened(filePath));

    /// <summary>Waits until everything queued is written: call when the app closes.</summary>
    public static void Flush(int timeoutMilliseconds = 3000)
    {
        WaitForQueued(timeoutMilliseconds);
        LibraryStore.Flush(timeoutMilliseconds);
    }

    // ── What the app changed itself: ratings, deleted, renamed and moved items ──
    // All of it is written in the background, one thing after the other (in the order it was asked for). Reading a folder
    // waits for it (WaitForQueued), so the list never shows what was there before.

    private static Task _queued = Task.CompletedTask;
    private static readonly object QueueLock = new();

    /// <summary>Runs <paramref name="work"/> in the background after everything queued before it.</summary>
    public static void Queue(Action work)
    {
        lock (QueueLock)
        {
            _queued = _queued.ContinueWith(_ =>
            {
                try { work(); } catch { /* not remembering something is not worth an error box */ }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Waits (at most a few seconds) for what was queued; call on a background thread or when closing.</summary>
    public static void WaitForQueued(int timeoutMilliseconds = 3000)
    {
        Task pending;
        lock (QueueLock) pending = _queued;
        try { pending.Wait(timeoutMilliseconds); } catch { }
    }

    /// <summary>Files and folders were deleted in the app: what is remembered about them (and below them) goes too.</summary>
    public static void Forget(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;
        Queue(() =>
        {
            LibraryStore.Flush();   // a read mark still waiting to be written must not bring a row back
            LibraryStore.Forget(list);
        });
    }

    /// <summary>Files and folders were renamed or moved in the app: what is remembered about them follows.</summary>
    public static void Move(IEnumerable<(string From, string To)> moves)
    {
        var list = moves.ToList();
        if (list.Count == 0) return;
        Queue(() =>
        {
            LibraryStore.Flush();
            LibraryStore.Move(list);
        });
    }

    /// <summary>The folder above, or null at the root of a drive. Works for paths inside a packed file too.</summary>
    private static string? ParentOf(string folder)
    {
        var root = Path.GetPathRoot(folder) ?? "";
        var trimmed = folder.TrimEnd('\\', '/');
        var rootTrimmed = root.TrimEnd('\\', '/');
        if (trimmed.Length <= rootTrimmed.Length) return null;

        int cut = trimmed.LastIndexOf('\\');
        if (cut < 0) return null;
        var parent = trimmed.Substring(0, cut);
        return parent.Length <= rootTrimmed.Length ? root : parent;
    }

    // ── What is in a folder ──────────────────────────────────────────────────

    /// <summary>The visible files of a folder (names) and its visible subfolders and packed files (full paths).</summary>
    private sealed record Listing(List<string> Files, List<string> Children);

    private static Listing? Enumerate(string folder)
    {
        try
        {
            var files = new List<string>();
            var children = new List<string>();

            if (ArchiveSupport.IsInside(folder))
            {
                foreach (var entry in ArchiveSupport.List(folder))
                {
                    if (entry.IsDirectory || entry.IsArchive) children.Add(entry.FullPath);
                    else if (!HiddenItems.IsHiddenInArchive(entry.Name)) files.Add(entry.Name);
                }
            }
            else
            {
                var directory = new DirectoryInfo(folder);
                foreach (var d in directory.EnumerateDirectories())
                    if (!HiddenItems.IsHiddenByAttributes(d.Attributes)) children.Add(d.FullName);

                foreach (var f in directory.EnumerateFiles())
                {
                    if (HiddenItems.IsHiddenFile(f.Attributes, f.Name)) continue;
                    if (ArchiveSupport.IsArchiveName(f.Name)) children.Add(f.FullName);
                    else files.Add(f.Name);
                }
            }

            return new Listing(files, children);
        }
        catch
        {
            return null;   // no access, or the folder is gone
        }
    }

    // ── Reading and updating the state of a folder ───────────────────────────

    /// <summary>
    /// Looks at the folder as it is now and at what is remembered, brings the folder's row up to date (and the
    /// rows above it, when the folder turned Read or stopped being Read) and says which files are read.
    /// <paramref name="create"/> = false never creates a database file (browsing alone leaves no trace).
    /// </summary>
    public static FolderInfo Sync(string folder, bool create)
    {
        lock (LibraryStore.Gate) return SyncCore(folder, create);
    }

    private static FolderInfo SyncCore(string folder, bool create)
    {
        try
        {
            return SyncInner(folder, create);
        }
        catch (Exception ex)
        {
            // A damaged file is replaced (here, with its connection closed): the next look finds a working one.
            if (!LibraryStore.RepairAfter(ex, folder)) throw;
            return new FolderInfo(new HashSet<string>(StringComparer.OrdinalIgnoreCase), FolderState.None);
        }
    }

    private static FolderInfo SyncInner(string folder, bool create)
    {
        var empty = new FolderInfo(new HashSet<string>(StringComparer.OrdinalIgnoreCase), FolderState.None);

        var listing = Enumerate(folder);
        if (listing == null) return empty;

        var route = LibraryStore.Resolve(folder, create);
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool started = false, complete;

        using var connection = route == null ? null : LibraryStore.Open(route, create);
        if (route == null || connection == null)
        {
            complete = listing.Files.Count == 0 && listing.Children.Count == 0;
            return new FolderInfo(read, StateOf(listing, complete, started));
        }

        var key = LibraryStore.KeyOf(route, folder);

        using (var command = LibraryStore.Command(connection, "SELECT Name FROM File WHERE Folder = $f", ("$f", key)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) read.Add(reader.GetString(0));

        started = AnythingReadUnder(connection, key);

        int ownUnread = listing.Files.Count(name => !read.Contains(name));
        complete = ownUnread == 0 && listing.Children.All(child => IsComplete(connection, route, child));

        bool wasComplete = false, hadRow = false;
        long? oldUnread = null;
        using (var command = LibraryStore.Command(connection, "SELECT OwnUnread, Complete FROM Folder WHERE Path = $p", ("$p", key)))
        using (var reader = command.ExecuteReader())
            if (reader.Read())
            {
                hadRow = true;
                oldUnread = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                wasComplete = reader.GetInt64(1) != 0;
            }

        // A folder nobody has touched leaves no row (a long browse would fill the file with nothing).
        bool worthAStoring = hadRow || complete || started;
        if (worthAStoring && (!hadRow || oldUnread != ownUnread || wasComplete != complete))
        {
            try
            {
                using var command = LibraryStore.Command(connection,
                    "INSERT INTO Folder (Path, OwnUnread, Complete) VALUES ($p, $u, $c) " +
                    "ON CONFLICT(Path) DO UPDATE SET OwnUnread = $u, Complete = $c",
                    ("$p", key), ("$u", ownUnread), ("$c", complete ? 1 : 0));
                command.ExecuteNonQuery();
            }
            catch { /* a write-protected drive: nothing is remembered */ }
        }

        // The parent counts this folder: it may have turned Read, or may not be Read any more.
        if (complete != wasComplete)
        {
            var parent = ParentOf(folder);
            if (parent != null) SyncCore(parent, create);
        }

        return new FolderInfo(read, StateOf(listing, complete, started));
    }

    private static FolderState StateOf(Listing listing, bool complete, bool started)
    {
        if (listing.Files.Count == 0 && listing.Children.Count == 0) return FolderState.None;
        if (complete) return FolderState.Read;
        return started ? FolderState.PartlyRead : FolderState.Unread;
    }

    private static bool IsComplete(SqliteConnection connection, LibraryRoute route, string folder)
    {
        using var command = LibraryStore.Command(connection, "SELECT Complete FROM Folder WHERE Path = $p", ("$p", LibraryStore.KeyOf(route, folder)));
        return command.ExecuteScalar() is long value && value != 0;
    }

    /// <summary>A read file in this folder or in any folder below it (a key range: "key\" up to "key]", the character after the backslash).</summary>
    private static bool AnythingReadUnder(SqliteConnection connection, string key)
    {
        using var command = LibraryStore.Command(connection,
            "SELECT 1 FROM File WHERE Folder = $k OR (Folder > $lo AND Folder < $hi) LIMIT 1",
            ("$k", key), ("$lo", key + "\\"), ("$hi", key + "]"));
        return command.ExecuteScalar() != null;
    }

    // ── Mark a whole folder tree ─────────────────────────────────────────────

    /// <summary>
    /// "Mark as Read" / "Mark as Unread" on a folder: every visible file below it, packed files included
    /// (this reads their lists: it can take a while, so call it in the background).
    /// </summary>
    public static void MarkTree(string folder, bool read)
    {
        FolderInfo info;
        lock (LibraryStore.Gate)
        {
            var route = LibraryStore.Resolve(folder, create: true);
            if (route == null) return;

            Exception? failure = null;
            using (var connection = LibraryStore.Open(route, create: true))
            {
                if (connection == null) return;
                var key = LibraryStore.KeyOf(route, folder);

                try
                {
                    using var transaction = connection.BeginTransaction();

                    if (read)
                    {
                        var stamp = DateTime.UtcNow.ToString("o");
                        var todo = new Stack<string>();
                        todo.Push(folder);
                        while (todo.Count > 0)
                        {
                            var current = todo.Pop();
                            var listing = Enumerate(current);
                            if (listing == null) continue;

                            var currentKey = LibraryStore.KeyOf(route, current);
                            foreach (var name in listing.Files)
                            {
                                using var command = LibraryStore.Command(connection,
                                    "INSERT INTO File (Folder, Name, ReadAt) VALUES ($f, $n, $t) ON CONFLICT DO NOTHING",
                                    ("$f", currentKey), ("$n", name), ("$t", stamp));
                                command.Transaction = transaction;
                                command.ExecuteNonQuery();
                            }

                            using (var command = LibraryStore.Command(connection,
                                "INSERT INTO Folder (Path, OwnUnread, Complete) VALUES ($p, 0, 1) " +
                                "ON CONFLICT(Path) DO UPDATE SET OwnUnread = 0, Complete = 1", ("$p", currentKey)))
                            {
                                command.Transaction = transaction;
                                command.ExecuteNonQuery();
                            }

                            foreach (var child in listing.Children) todo.Push(child);
                        }
                    }
                    else
                    {
                        foreach (var table in new[] { ("File", "Folder"), ("Folder", "Path") })
                        {
                            using var command = LibraryStore.Command(connection,
                                $"DELETE FROM {table.Item1} WHERE {table.Item2} = $k OR ({table.Item2} > $lo AND {table.Item2} < $hi)",
                                ("$k", key), ("$lo", key + "\\"), ("$hi", key + "]"));
                            command.Transaction = transaction;
                            command.ExecuteNonQuery();
                        }
                    }

                    transaction.Commit();
                }
                catch (Exception ex) { failure = ex; }   // write-protected, busy or damaged
            }

            if (failure != null)
            {
                LibraryStore.RepairAfter(failure, folder);
                return;
            }

            // The rows above the folder follow (a parent's state may change); SyncCore also recomputes this folder.
            // After a "read" the old row of the folder was already right, so tell the parent explicitly.
            info = SyncCore(folder, create: true);
            var parent = ParentOf(folder);
            if (parent != null) SyncCore(parent, create: true);
        }
        FolderChanged?.Invoke(folder, info);
    }
}
