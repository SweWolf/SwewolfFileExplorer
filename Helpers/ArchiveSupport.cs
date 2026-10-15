using System.IO;
using System.IO.Compression;

namespace MyFileExplorer.Helpers;

/// <summary>A folder or file inside an archive, as the list and the tree show it.</summary>
public sealed record VirtualEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime Modified)
{
    /// <summary>A packed file inside the archive that can be opened as a folder again (a zip inside a zip).</summary>
    public bool IsArchive => !IsDirectory && ArchiveSupport.IsArchiveName(Name);
}

/// <summary>An archive cannot be read: damaged, protected with a password, or an entry that is not there.</summary>
public sealed class ArchiveException : Exception
{
    public ArchiveException(string message) : base(message) { }
}

/// <summary>
/// Packed files (.zip and .cbz) as folders. An archive is read in place: nothing is unpacked to browse it.
/// A path into an archive is the path of the archive file followed by the path inside it, for example
/// <c>D:\Photos\Trip.zip\Day 1\Beach.jpg</c>; a zip inside a zip works the same way
/// (<c>D:\Photos\All.zip\Trip.zip\Day 1</c>). Everything that is a path in the app can be one of these.
/// </summary>
public static class ArchiveSupport
{
    private static readonly string[] Extensions = { ".zip", ".cbz" };

    /// <summary>
    /// How the names in a zip are read when the zip does not say they are UTF-8. Older tools (and Windows' own
    /// "Send to compressed folder") write them in the DOS code page of the computer, where "Å" is the byte 0x8F;
    /// reading that as UTF-8 gives "�". A zip that does say UTF-8 is read as UTF-8 whatever is set here.
    /// </summary>
    private static readonly System.Text.Encoding NameEncoding = CreateNameEncoding();

    private static System.Text.Encoding CreateNameEncoding()
    {
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return System.Text.Encoding.UTF8;
        }
    }
    private static readonly char[] Separators = { '\\', '/' };

    public static bool IsArchiveName(string name) =>
        Extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    // ── Where a path points ──────────────────────────────────────────────────

    /// <summary>
    /// The archive a path is in: the file on disk, then any zip inside it that has to be opened on the way
    /// (the names of those entries), and the folder or file inside the last archive ("" is its top).
    /// </summary>
    private sealed record Location(string[] Chain, string Inner);

    /// <summary>
    /// Null if the path does not lead into an archive (or the archive cannot be read: see <paramref name="error"/>).
    /// The path of a zip inside a zip is both the file and the top of the archive in it: by default it is the top
    /// (to browse it); with <paramref name="asEntry"/> it is the file (to read or copy it).
    /// </summary>
    private static Location? Locate(string path, out string? error, bool asEntry = false)
    {
        error = null;
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            int position = 0;
            while (position < path.Length)
            {
                int next = path.IndexOfAny(Separators, position);
                int end  = next < 0 ? path.Length : next;

                // Only a name that looks like a packed file is worth asking the disk about.
                if (end > position && IsArchiveName(path[position..end]))
                {
                    string file = path[..end];
                    if (File.Exists(file))
                    {
                        string rest = end < path.Length ? path[(end + 1)..] : "";
                        return LocateInside(new List<string> { file }, Clean(rest), asEntry);
                    }
                }

                if (next < 0) break;
                position = next + 1;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return null;
    }

    private static Location LocateInside(List<string> chain, string inner, bool asEntry)
    {
        // A zip inside the zip: the path goes on into it.
        while (inner.Length > 0)
        {
            var index = GetIndex(chain.ToArray());
            string? nested = null;
            int remainderStart = 0;

            int position = 0;
            while (position < inner.Length)
            {
                int next = inner.IndexOf('/', position);
                int end  = next < 0 ? inner.Length : next;
                if (IsArchiveName(inner[position..end]) && index.Files.TryGetValue(inner[..end], out var file)
                    && (!asEntry || end < inner.Length))   // as a file entry, a zip at the very end is the file, not an archive to go into
                {
                    nested = file.FullName;   // spelled as in the archive
                    remainderStart = Math.Min(end + 1, inner.Length);
                    break;
                }
                if (next < 0) break;
                position = next + 1;
            }

            if (nested == null) break;
            chain.Add(nested);
            inner = inner[remainderStart..];
        }

        return new Location(chain.ToArray(), inner);
    }

    private static string Clean(string path) => path.Replace('\\', '/').Trim('/');

    /// <summary>True if the path is an archive file, a folder in one, or a file in one.</summary>
    public static bool IsInside(string path) => Locate(path, out _) != null;

    /// <summary>True if the path is a folder the app can show: the top of an archive, or a folder inside one.</summary>
    public static bool FolderExists(string path, out string? error)
    {
        var location = Locate(path, out error);
        if (location == null) return false;

        try
        {
            return GetIndex(location.Chain).Folders.ContainsKey(location.Inner);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    private sealed record RawEntry(string Name, string FullName, bool IsDirectory, long Length, DateTime Modified);

    /// <summary>What is in a folder of an archive (folders, files, and zips inside the zip as files).</summary>
    public static List<VirtualEntry> List(string folderPath)
    {
        var location = Locate(folderPath, out var error)
            ?? throw new ArchiveException(error ?? $"'{folderPath}' is not in an archive.");

        var index = GetIndex(location.Chain);
        if (!index.Folders.TryGetValue(location.Inner, out var children))
            throw new ArchiveException($"The folder '{folderPath}' is not in the archive.");

        string parent = folderPath.TrimEnd('\\', '/');
        return children
            .Select(c => new VirtualEntry(c.Name, parent + "\\" + c.Name, c.IsDirectory, c.IsDirectory ? -1 : c.Length, c.Modified))
            .ToList();
    }

    /// <summary>True if the folder has a folder or a packed file in it (the tree shows an expand arrow for it).</summary>
    public static bool HasSubfolders(string folderPath)
    {
        try
        {
            return List(folderPath).Any(e => e.IsDirectory || e.IsArchive);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The entry at a path inside an archive: its size and date. Null if there is none (or the path is the top of an archive).</summary>
    public static VirtualEntry? GetEntry(string path)
    {
        try
        {
            var location = Locate(path, out _, asEntry: true);
            if (location == null || location.Inner.Length == 0) return null;

            var index = GetIndex(location.Chain);
            string name = Path.GetFileName(path.TrimEnd('\\', '/'));
            if (index.Files.TryGetValue(location.Inner, out var file))
                return new VirtualEntry(name, path, false, file.Length, file.Modified);

            string parent = location.Inner.Contains('/') ? location.Inner[..location.Inner.LastIndexOf('/')] : "";
            var folder = index.Folders.GetValueOrDefault(parent)?.FirstOrDefault(c => c.IsDirectory && c.FullName.Equals(location.Inner, StringComparison.OrdinalIgnoreCase));
            return folder == null ? null : new VirtualEntry(name, path, true, -1, folder.Modified);
        }
        catch
        {
            return null;
        }
    }

    // ── The index of an archive ──────────────────────────────────────────────

    private sealed class ArchiveIndex
    {
        // Case-insensitive, as paths are in Windows: the address bar may be typed in any case.
        public Dictionary<string, List<RawEntry>> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RawEntry> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (string Stamp, ArchiveIndex Index)> Cache = new();

    private static ArchiveIndex GetIndex(string[] chain)
    {
        var info = new FileInfo(chain[0]);
        string key   = string.Join("|", chain);
        string stamp = $"{info.Length}/{info.LastWriteTimeUtc.Ticks}";

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached) && cached.Stamp == stamp)
                return cached.Index;
        }

        var index = BuildIndex(chain);

        lock (CacheLock)
        {
            if (Cache.Count > 16) Cache.Clear();   // a few archives are enough; this is only so a list does not read the file again and again
            Cache[key] = (stamp, index);
        }
        return index;
    }

    private static ArchiveIndex BuildIndex(string[] chain)
    {
        var index = new ArchiveIndex();
        index.Folders[""] = new List<RawEntry>();

        using var opened = Open(chain);
        foreach (var entry in opened.Zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            bool isFolder = name.EndsWith('/');
            name = name.Trim('/');
            if (name.Length == 0) continue;

            var modified = entry.LastWriteTime.LocalDateTime;
            var segments = name.Split('/');

            // Zips often have no entry for a folder, only for the files in it: make the folders from the names.
            string parent = "";
            int folderCount = isFolder ? segments.Length : segments.Length - 1;
            for (int i = 0; i < folderCount; i++)
            {
                string path = parent.Length == 0 ? segments[i] : parent + "/" + segments[i];
                if (!index.Folders.ContainsKey(path))
                {
                    index.Folders[path] = new List<RawEntry>();
                    index.Folders[parent].Add(new RawEntry(segments[i], path, true, 0, modified));
                }
                parent = path;
            }

            if (!isFolder)
            {
                var file = new RawEntry(segments[^1], entry.FullName.Replace('\\', '/'), false, entry.Length, modified);
                if (index.Files.TryAdd(name, file))
                    index.Folders[parent].Add(file);
            }
        }

        return index;
    }

    /// <summary>Forget what was read of the archive that holds this path, so the next look reads the file again.</summary>
    public static void Invalidate(string path)
    {
        var location = Locate(path, out _);
        if (location == null) return;

        lock (CacheLock)
        {
            string first = location.Chain[0];
            foreach (var key in Cache.Keys.Where(k => k.StartsWith(first, StringComparison.OrdinalIgnoreCase)).ToList())
                Cache.Remove(key);
        }
    }

    // ── Opening ──────────────────────────────────────────────────────────────

    private sealed class OpenedArchive : IDisposable
    {
        private readonly List<IDisposable> _owners = new();
        public ZipArchive Zip { get; set; } = null!;
        public void Own(IDisposable item) => _owners.Add(item);
        public void Dispose()
        {
            for (int i = _owners.Count - 1; i >= 0; i--) _owners[i].Dispose();
        }
    }

    /// <summary>Opens the last archive of the chain; a zip inside a zip is first read into memory.</summary>
    private static OpenedArchive Open(string[] chain)
    {
        var opened = new OpenedArchive();
        try
        {
            var file = new FileStream(chain[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            opened.Own(file);
            var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false, NameEncoding);
            opened.Own(zip);

            for (int i = 1; i < chain.Length; i++)
            {
                var entry = zip.GetEntry(chain[i]) ?? throw new ArchiveException($"'{chain[i]}' is not in the archive.");
                CheckReadable(entry);

                var memory = new MemoryStream();
                using (var packed = entry.Open())
                    packed.CopyTo(memory);
                memory.Position = 0;

                opened.Own(memory);
                zip = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false, NameEncoding);
                opened.Own(zip);
            }

            opened.Zip = zip;
            return opened;
        }
        catch (InvalidDataException)
        {
            opened.Dispose();
            throw new ArchiveException($"'{Path.GetFileName(chain[^1])}' is not a valid zip file, or it is damaged.");
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    private static void CheckReadable(ZipArchiveEntry entry)
    {
        if (entry.IsEncrypted)
            throw new ArchiveException($"'{entry.Name}' is protected with a password. Archives with a password are not supported yet.");
    }

    /// <summary>Calls <paramref name="read"/> with the entry of a file inside an archive.</summary>
    private static T WithEntry<T>(string path, Func<ZipArchiveEntry, T> read)
    {
        var location = Locate(path, out var error, asEntry: true)
            ?? throw new ArchiveException(error ?? $"'{path}' is not in an archive.");

        var index = GetIndex(location.Chain);
        if (!index.Files.TryGetValue(location.Inner, out var file))
            throw new ArchiveException($"'{Path.GetFileName(path)}' is not in the archive.");

        using var opened = Open(location.Chain);
        var entry = opened.Zip.GetEntry(file.FullName) ?? throw new ArchiveException($"'{Path.GetFileName(path)}' is not in the archive.");
        CheckReadable(entry);
        return read(entry);
    }

    /// <summary>The content of a file inside an archive, in memory. For pictures: do not use it for a big video.</summary>
    public static Stream OpenRead(string path) =>
        WithEntry(path, entry =>
        {
            var memory = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue / 2));
            using (var packed = entry.Open())
                packed.CopyTo(memory);
            memory.Position = 0;
            return memory;
        });

    /// <summary>A file on disk, or a file inside an archive: for the code that only wants to read it.</summary>
    public static Stream OpenAnyFile(string path) =>
        File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
            : OpenRead(path);

    // ── Taking files out ─────────────────────────────────────────────────────

    /// <summary>Writes a file or a whole folder of an archive to <paramref name="destination"/> (which must not exist yet).</summary>
    public static void ExtractTo(string path, string destination)
    {
        var entry = GetEntry(path);
        bool isFolder = entry == null || entry.IsDirectory;

        if (!isFolder)
        {
            WithEntry(path, e =>
            {
                using var packed = e.Open();
                using var target = File.Create(destination);
                packed.CopyTo(target);
                return 0;
            });
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var child in List(path))
            ExtractTo(child.FullPath, Path.Combine(destination, SafeFileName(child.Name)));
    }

    /// <summary>A name that is only a name: nothing in an archive may write outside the folder it is unpacked into.</summary>
    private static string SafeFileName(string name)
    {
        foreach (var bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');
        return name.Trim().Length == 0 || name is "." or ".." ? "_" : name;
    }

    // ── Temporary files, for opening a file of an archive in another program ─

    private static readonly Dictionary<string, string> Extracted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folder for this run of the app. Removed when it ends (<see cref="CleanUpTemp"/>).</summary>
    private static string TempRoot => Path.Combine(Path.GetTempPath(), "MyFileExplorer");
    private static string TempFolder => Path.Combine(TempRoot, Environment.ProcessId.ToString());

    /// <summary>Unpacks a file of an archive to a temporary file, and returns where it is. The same file is unpacked once.</summary>
    public static string ExtractToTemp(string path)
    {
        lock (Extracted)
        {
            if (Extracted.TryGetValue(path, out var known) && File.Exists(known))
                return known;

            // A folder of its own for each file keeps its real name and cannot clash with another file of the same name.
            string folder = Path.Combine(TempFolder, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            string target = Path.Combine(folder, SafeFileName(Path.GetFileName(path.TrimEnd('\\', '/'))));
            ExtractTo(path, target);
            Extracted[path] = target;
            return target;
        }
    }

    public static void CleanUpTemp()
    {
        try
        {
            if (Directory.Exists(TempFolder)) Directory.Delete(TempFolder, recursive: true);
        }
        catch
        {
            // A program still has a file open: the next start cleans up what is left.
        }
    }

    /// <summary>At the start: removes the temporary files of runs that ended without cleaning up (a crash).</summary>
    public static void CleanUpStaleTemp()
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;

            foreach (var folder in Directory.GetDirectories(TempRoot))
            {
                if (!int.TryParse(Path.GetFileName(folder), out int pid) || pid == Environment.ProcessId) continue;

                bool running;
                try { running = System.Diagnostics.Process.GetProcessById(pid).ProcessName.Equals("MyFileExplorer", StringComparison.OrdinalIgnoreCase); }
                catch { running = false; }

                if (!running)
                {
                    try { Directory.Delete(folder, recursive: true); } catch { /* in use */ }
                }
            }
        }
        catch
        {
            // not important
        }
    }

    // ── Dates, for sorting the folders of the tree ───────────────────────────

    /// <summary>The date of a folder or archive: from the disk, or from the archive for a folder inside one.</summary>
    public static DateTime GetDate(string path, bool created)
    {
        if (File.Exists(path) || Directory.Exists(path))
            return created ? Directory.GetCreationTime(path) : Directory.GetLastWriteTime(path);

        return GetEntry(path)?.Modified ?? DateTime.MinValue;   // an archive has one date per entry: the creation date is the modified date
    }
}
