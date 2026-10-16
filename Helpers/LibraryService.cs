using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace SwewolfFileExplorer.Helpers;

/// <summary>Unread: nothing in the folder was opened. Partly: something was. Read: everything (subfolders too) was. None: an empty folder.</summary>
public enum FolderState { None, Unread, PartlyRead, Read }

/// <summary>What the library knows about one folder: which of its files were opened, and the folder's own state.</summary>
public sealed record FolderInfo(HashSet<string> ReadNames, FolderState State);

/// <summary>
/// Remembers which files were opened ("read"), in SQLite files (see docs/library-db.md for the schema).
/// <list type="bullet">
/// <item>Fixed drives and network shares: one main file, %AppData%\SweWolfSoftware\SweWolf File Explorer\library.db, keyed by the full path.</item>
/// <item>Removable drives (USB sticks and USB hard disks): their own file, E:\.SweWolfFileExplorerData\library.db, keyed by the
/// path from the drive's root, so the state travels with the drive. It is created the first time a file is opened.</item>
/// <item>Media that cannot be written (CD/DVD, a write-protected stick): the main file, keyed by the volume serial number + the
/// path from the root, so the same disc is recognized in any drive.</item>
/// </list>
/// A file is read when it is opened in the app. A folder is Read when every visible file in it and every subfolder
/// (a packed file counts as a subfolder) is read: hidden files and folders are not counted. A folder that was never listed
/// is not Read, so a parent turns Read only after the whole tree has been seen (or after "Mark as Read" on it).
/// Only what must be remembered is stored: no row means unread.
/// </summary>
public static class LibraryService
{
    public const string UsbFolderName = ".SweWolfFileExplorerData";
    public const string DbFileName = "library.db";
    private const int SchemaVersion = 1;

    private static readonly object Gate = new();   // one thing at a time on the database files

    /// <summary>Only for tests: use this file instead of the user's main database.</summary>
    public static string? MainDbFileOverride { get; set; }

    private static string MainDbFile => MainDbFileOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SweWolfSoftware", AppInfo.Name, DbFileName);

    /// <summary>A folder's state changed (raised on a background thread): the window updates what it shows.</summary>
    public static event Action<string, FolderInfo>? FolderChanged;

    // ── Which file holds the data of a path ──────────────────────────────────

    /// <param name="Relative">The keys are paths from the root of the drive (USB file, or a volume key in the main file).</param>
    private sealed record Route(string DbFile, string Prefix, bool Relative, string Root);

    private static readonly Dictionary<string, uint> DriveTypes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> UsbRoots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SchemaDone = new(StringComparer.OrdinalIgnoreCase);

    private const uint DriveRemovable = 2;
    private const uint DriveCdRom = 5;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string rootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(string root, IntPtr volumeName, int volumeNameSize,
        out uint serialNumber, out uint maxComponentLength, out uint flags, IntPtr fileSystemName, int fileSystemNameSize);

    /// <summary>Only for tests: treat a drive as another kind (2 removable, 3 fixed, 5 CD).</summary>
    public static void SetDriveTypeForTest(string root, uint type) => DriveTypes[root] = type;

    private static uint DriveTypeOf(string root)
    {
        if (!DriveTypes.TryGetValue(root, out var type))
            DriveTypes[root] = type = GetDriveTypeW(root);
        return type;
    }

    private static Route? Resolve(string path, bool create)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return null;

        var type = DriveTypeOf(root);
        if (type == DriveCdRom) return VolumeRoute(root);

        if (type != DriveRemovable)
            return new Route(MainDbFile, "", false, root);

        var usbDir = Path.Combine(root, UsbFolderName);
        var usbDb = Path.Combine(usbDir, DbFileName);

        if (UsbRoots.Contains(root) || File.Exists(usbDb))
        {
            UsbRoots.Add(root);
            return new Route(usbDb, "", true, root);
        }

        if (create && TryCreateUsbFolder(usbDir))
        {
            UsbRoots.Add(root);
            return new Route(usbDb, "", true, root);
        }

        return VolumeRoute(root);   // nothing on the drive yet, or it cannot be written
    }

    private static Route VolumeRoute(string root)
    {
        if (!GetVolumeInformationW(root, IntPtr.Zero, 0, out var serial, out _, out _, IntPtr.Zero, 0))
            return new Route(MainDbFile, "", false, root);
        return new Route(MainDbFile, "vol:" + serial.ToString("X8"), true, root);
    }

    private static bool TryCreateUsbFolder(string usbDir)
    {
        try
        {
            var dir = Directory.CreateDirectory(usbDir);
            dir.Attributes |= FileAttributes.Hidden;   // Windows ignores the leading dot: the attribute does the hiding
            return true;
        }
        catch
        {
            return false;   // write-protected
        }
    }

    /// <summary>The text a path is stored under (see <see cref="Route"/>). The root of a drive is "" (relative) or "D:".</summary>
    private static string KeyOf(Route route, string path)
    {
        var p = path.TrimEnd('\\', '/');
        if (route.Relative)
        {
            var rootLength = route.Root.TrimEnd('\\', '/').Length;
            p = p.Length >= rootLength ? p.Substring(rootLength) : "";
        }
        return route.Prefix + p;
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

    // ── The database files ───────────────────────────────────────────────────

    private static SqliteConnection? Open(Route route, bool create)
    {
        if (!File.Exists(route.DbFile))
        {
            if (!create) return null;
            try { Directory.CreateDirectory(Path.GetDirectoryName(route.DbFile)!); }
            catch { return null; }
        }

        try
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = route.DbFile,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,           // the file is released as soon as the work is done (a stick must be removable)
                DefaultTimeout = 5,
            }.ToString());
            connection.Open();
            EnsureSchema(connection, route.DbFile);
            return connection;
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureSchema(SqliteConnection connection, string file)
    {
        if (!SchemaDone.Add(file)) return;

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $@"
                CREATE TABLE IF NOT EXISTS Folder (
                    Path      TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                    OwnUnread INTEGER,
                    Complete  INTEGER NOT NULL DEFAULT 0
                ) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS File (
                    Folder TEXT NOT NULL COLLATE NOCASE,
                    Name   TEXT NOT NULL COLLATE NOCASE,
                    ReadAt TEXT,
                    PRIMARY KEY (Folder, Name)
                ) WITHOUT ROWID;
                PRAGMA user_version = {SchemaVersion};";
            command.ExecuteNonQuery();
        }
        catch
        {
            SchemaDone.Remove(file);   // read-only: tried again next time
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
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
                    else if (!HiddenItems.IsHiddenExtension(entry.Name)) files.Add(entry.Name);
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
        lock (Gate) return SyncCore(folder, create);
    }

    private static FolderInfo SyncCore(string folder, bool create)
    {
        var empty = new FolderInfo(new HashSet<string>(StringComparer.OrdinalIgnoreCase), FolderState.None);

        var listing = Enumerate(folder);
        if (listing == null) return empty;

        var route = Resolve(folder, create);
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool started = false, complete;

        using var connection = route == null ? null : Open(route, create);
        if (route == null || connection == null)
        {
            complete = listing.Files.Count == 0 && listing.Children.Count == 0;
            return new FolderInfo(read, StateOf(listing, complete, started));
        }

        var key = KeyOf(route, folder);

        using (var command = Command(connection, "SELECT Name FROM File WHERE Folder = $f", ("$f", key)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) read.Add(reader.GetString(0));

        started = AnythingReadUnder(connection, key);

        int ownUnread = listing.Files.Count(name => !read.Contains(name));
        complete = ownUnread == 0 && listing.Children.All(child => IsComplete(connection, route, child));

        bool wasComplete = false, hadRow = false;
        long? oldUnread = null;
        using (var command = Command(connection, "SELECT OwnUnread, Complete FROM Folder WHERE Path = $p", ("$p", key)))
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
                using var command = Command(connection,
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

    private static bool IsComplete(SqliteConnection connection, Route route, string folder)
    {
        using var command = Command(connection, "SELECT Complete FROM Folder WHERE Path = $p", ("$p", KeyOf(route, folder)));
        return command.ExecuteScalar() is long value && value != 0;
    }

    /// <summary>A read file in this folder or in any folder below it (a key range: "key\" up to "key]", the character after the backslash).</summary>
    private static bool AnythingReadUnder(SqliteConnection connection, string key)
    {
        using var command = Command(connection,
            "SELECT 1 FROM File WHERE Folder = $k OR (Folder > $lo AND Folder < $hi) LIMIT 1",
            ("$k", key), ("$lo", key + "\\"), ("$hi", key + "]"));
        return command.ExecuteScalar() != null;
    }

    // ── Marking files read or unread (in the background) ─────────────────────

    private static readonly object QueueLock = new();
    private static List<(string Path, bool Read)> _pending = new();
    private static bool _workerRunning;

    /// <summary>
    /// Remembers that these files were opened (or not). Returns at once: the work is done in the background in bursts,
    /// so paging through 500 pictures costs a few writes, not 500.
    /// </summary>
    public static void SetRead(IEnumerable<string> paths, bool read)
    {
        lock (QueueLock)
        {
            foreach (var path in paths) _pending.Add((path, read));
            if (_pending.Count == 0 || _workerRunning) return;
            _workerRunning = true;
            Task.Run(Worker);
        }
    }

    private static void Worker()
    {
        while (true)
        {
            Thread.Sleep(100);   // gather a burst

            List<(string Path, bool Read)> batch;
            lock (QueueLock)
            {
                if (_pending.Count == 0)
                {
                    _workerRunning = false;
                    return;
                }
                batch = _pending;
                _pending = new();
            }

            try { Apply(batch); }
            catch { /* not remembering one file is not worth an error box */ }
        }
    }

    /// <summary>Waits (at most a few seconds) until everything queued is written: call when the app closes.</summary>
    public static void Flush(int timeoutMilliseconds = 3000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            lock (QueueLock) if (!_workerRunning) return;
            Thread.Sleep(20);
        }
    }

    private static void Apply(List<(string Path, bool Read)> batch)
    {
        // The last word wins for a file named twice; one transaction per folder.
        var byFolder = batch
            .Select(item => (Folder: Path.GetDirectoryName(item.Path), Name: Path.GetFileName(item.Path), item.Read))
            .Where(item => !string.IsNullOrEmpty(item.Folder) && !string.IsNullOrEmpty(item.Name))
            .GroupBy(item => item.Folder!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byFolder)
        {
            FolderInfo info;
            lock (Gate)
            {
                var route = Resolve(group.Key, create: true);
                if (route == null) continue;

                using (var connection = Open(route, create: true))
                {
                    if (connection == null) continue;
                    var key = KeyOf(route, group.Key);

                    try
                    {
                        using var transaction = connection.BeginTransaction();
                        foreach (var file in group)
                        {
                            using var command = file.Read
                                ? Command(connection, "INSERT INTO File (Folder, Name, ReadAt) VALUES ($f, $n, $t) ON CONFLICT DO NOTHING",
                                    ("$f", key), ("$n", file.Name), ("$t", DateTime.UtcNow.ToString("o")))
                                : Command(connection, "DELETE FROM File WHERE Folder = $f AND Name = $n",
                                    ("$f", key), ("$n", file.Name));
                            command.Transaction = transaction;
                            command.ExecuteNonQuery();
                        }
                        transaction.Commit();
                    }
                    catch { continue; }   // write-protected
                }

                info = SyncCore(group.Key, create: true);
            }
            FolderChanged?.Invoke(group.Key, info);
        }
    }

    // ── Mark a whole folder tree ─────────────────────────────────────────────

    /// <summary>
    /// "Mark as Read" / "Mark as Unread" on a folder: every visible file below it, packed files included
    /// (this reads their lists: it can take a while, so call it in the background).
    /// </summary>
    public static void MarkTree(string folder, bool read)
    {
        FolderInfo info;
        lock (Gate)
        {
            var route = Resolve(folder, create: true);
            if (route == null) return;

            using (var connection = Open(route, create: true))
            {
                if (connection == null) return;
                var key = KeyOf(route, folder);

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

                            var currentKey = KeyOf(route, current);
                            foreach (var name in listing.Files)
                            {
                                using var command = Command(connection,
                                    "INSERT INTO File (Folder, Name, ReadAt) VALUES ($f, $n, $t) ON CONFLICT DO NOTHING",
                                    ("$f", currentKey), ("$n", name), ("$t", stamp));
                                command.Transaction = transaction;
                                command.ExecuteNonQuery();
                            }

                            using (var command = Command(connection,
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
                            using var command = Command(connection,
                                $"DELETE FROM {table.Item1} WHERE {table.Item2} = $k OR ({table.Item2} > $lo AND {table.Item2} < $hi)",
                                ("$k", key), ("$lo", key + "\\"), ("$hi", key + "]"));
                            command.Transaction = transaction;
                            command.ExecuteNonQuery();
                        }
                    }

                    transaction.Commit();
                }
                catch { return; }   // write-protected
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
