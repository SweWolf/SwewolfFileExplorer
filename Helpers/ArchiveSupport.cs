using System.IO;
using System.IO.Compression;

namespace SwewolfFileExplorer.Helpers;

/// <summary>A folder or file inside an archive, as the list and the tree show it.</summary>
public sealed record VirtualEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime Modified)
{
    /// <summary>A packed file inside the archive that can be opened as a folder again (a zip inside a zip).</summary>
    public bool IsArchive => !IsDirectory && ArchiveSupport.IsArchiveName(Name);
}

/// <summary>An archive cannot be read: damaged, protected with a password, 7-Zip missing, or an entry that is not there.</summary>
public sealed class ArchiveException : Exception
{
    public ArchiveException(string message) : base(message) { }
}

/// <summary>An entry as an archive stores it. <see cref="StoredName"/> is what the format's reader needs to find it again.</summary>
internal sealed record RawEntry(string Name, string FullName, bool IsDirectory, long Length, DateTime Modified,
                                bool Encrypted = false, string? StoredName = null);

/// <summary>
/// Packed files as folders. An archive is read in place: nothing is unpacked to browse it.
/// A path into an archive is the path of the archive file followed by the path inside it, for example
/// <c>D:\Photos\Trip.zip\Day 1\Beach.jpg</c>; an archive inside an archive works the same way
/// (<c>D:\Photos\All.zip\Trip.zip\Day 1</c>). Everything that is a path in the app can be one of these.
/// Zip files (.zip, .cbz) are read with .NET; RAR, 7z, CAB, GZ and TAR files (.rar, .cbr, .7z, .cab, .gz, .tar, .tgz) with 7-Zip, if it is installed.
/// </summary>
public static class ArchiveSupport
{
    private static readonly string[] ZipExtensions = { ".zip", ".cbz" };
    private static readonly string[] SevenZipExtensions = { ".rar", ".cbr", ".7z", ".cab", ".gz", ".tar", ".tgz" };
    private static readonly char[] Separators = { '\\', '/' };

    /// <summary>The file name patterns of the archives, for a file system search (it applies them much faster than a loop does).</summary>
    public static readonly string[] FilePatterns = { "*.zip", "*.cbz", "*.rar", "*.cbr", "*.7z", "*.cab", "*.gz", "*.tar", "*.tgz" };

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

    private static bool HasExtension(string name, string[] extensions) =>
        extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    public static bool IsArchiveName(string name) => HasExtension(name, ZipExtensions) || HasExtension(name, SevenZipExtensions);

    // ── Where a path points ──────────────────────────────────────────────────

    /// <summary>
    /// The archive a path is in: the file on disk, then any archive inside it that has to be opened on the way
    /// (the names of those entries), and the folder or file inside the last archive ("" is its top).
    /// </summary>
    private sealed record Location(string[] Chain, string Inner);

    /// <summary>
    /// Null if the path does not lead into an archive (or the archive cannot be read: see <paramref name="error"/>).
    /// The path of an archive inside an archive is both the file and the top of the archive in it: by default it is the top
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
        // An archive inside the archive: the path goes on into it.
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
                    && (!asEntry || end < inner.Length))   // as a file entry, an archive at the very end is the file, not an archive to go into
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
    public static bool IsInside(string path) => WebFolder.IsWebPath(path) || Locate(path, out _) != null;   // a web folder is shown like an archive (Helpers/WebFolder.cs)

    /// <summary>
    /// The archive file on the disk that a path is, or is inside (for an archive inside an archive: the outer one, the file that really
    /// lies on the disk); null if the path does not lead into an archive. Cheaper than <see cref="Locate"/>: nothing is opened or read,
    /// the disk is only asked whether the packed file exists. Used for what belongs to the whole download (its Mark of the Web).
    /// </summary>
    public static string? DiskArchiveOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            int position = 0;
            while (position < path.Length)
            {
                int next = path.IndexOfAny(Separators, position);
                int end  = next < 0 ? path.Length : next;

                if (end > position && IsArchiveName(path[position..end]) && File.Exists(path[..end]))
                    return path[..end];

                if (next < 0) break;
                position = next + 1;
            }
        }
        catch { /* not a usable path */ }

        return null;
    }

    /// <summary>
    /// True when a folder inside an archive is listed in the order its files are stored, until the user sorts it: **zip and cbz only**
    /// (TeraTool browsed only those in place, and a zip is usually packed in page order). A RAR, cbr or 7z is listed by name: 7-Zip gives
    /// them in the order they were added, which for a comic repacked by someone is often not the reading order (a RAR found by the user
    /// stores pages 000 to 002 after 003 to 039 and ComicInfo.xml). The packed file that holds the folder decides: the last one in the path.
    /// </summary>
    public static bool ShowsStoredOrder(string path)
    {
        if (!IsInside(path)) return false;
        var segments = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = segments.Length - 1; i >= 0; i--)
            if (IsArchiveName(segments[i])) return HasExtension(segments[i], ZipExtensions);
        return false;
    }

    /// <summary>True if the path is a folder the app can show: the top of an archive, or a folder inside one.</summary>
    public static bool FolderExists(string path, out string? error)
    {
        if (WebFolder.IsWebPath(path)) return WebFolder.TryList(path, out error);

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

    /// <summary>What is in a folder of an archive (folders, files, and archives inside the archive as files).</summary>
    public static List<VirtualEntry> List(string folderPath)
    {
        if (WebFolder.IsWebPath(folderPath)) return WebFolder.List(folderPath);

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
        if (WebFolder.IsWebPath(folderPath)) return true;   // asking the server for every folder of the tree would be slow: the arrow goes when the folder turns out to be empty

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
        if (WebFolder.IsWebPath(path)) return WebFolder.GetEntry(path);

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

    private static string Stamp(string file)
    {
        var info = new FileInfo(file);
        return $"{info.Length}/{info.LastWriteTimeUtc.Ticks}";
    }

    private static ArchiveIndex GetIndex(string[] chain)
    {
        string key   = string.Join("|", chain);
        string stamp = Stamp(chain[0]);

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

        using var reader = OpenReader(chain);
        foreach (var entry in reader.ReadEntries())
        {
            string name = entry.FullName.Replace('\\', '/').Trim('/');
            if (name.Length == 0) continue;

            var segments = name.Split('/');

            // Archives often have no entry for a folder, only for the files in it: make the folders from the names.
            string parent = "";
            int folderCount = entry.IsDirectory ? segments.Length : segments.Length - 1;
            for (int i = 0; i < folderCount; i++)
            {
                string path = parent.Length == 0 ? segments[i] : parent + "/" + segments[i];
                if (!index.Folders.ContainsKey(path))
                {
                    index.Folders[path] = new List<RawEntry>();
                    index.Folders[parent].Add(new RawEntry(segments[i], path, true, 0, entry.Modified));
                }
                parent = path;
            }

            if (!entry.IsDirectory)
            {
                var file = new RawEntry(segments[^1], name, false, entry.Length, entry.Modified, entry.Encrypted, entry.StoredName);
                if (index.Files.TryAdd(name, file))
                    index.Folders[parent].Add(file);
            }
        }

        return index;
    }

    /// <summary>Forget what was read of the archive that holds this path, so the next look reads the file again.</summary>
    public static void Invalidate(string path)
    {
        if (WebFolder.IsWebPath(path)) { WebFolder.Invalidate(path); return; }

        var location = Locate(path, out _);
        if (location == null) return;

        lock (CacheLock)
        {
            string first = location.Chain[0];
            foreach (var key in Cache.Keys.Where(k => k.StartsWith(first, StringComparison.OrdinalIgnoreCase)).ToList())
                Cache.Remove(key);
        }
    }

    // ── Readers: one for each way of reading an archive ──────────────────────

    /// <summary>Reads one archive: lists its entries, and writes the content of one entry to a stream.</summary>
    private abstract class ArchiveReader : IDisposable
    {
        public abstract IEnumerable<RawEntry> ReadEntries();
        public abstract void CopyTo(RawEntry entry, Stream destination);
        public virtual void Dispose() { }
    }

    /// <summary>A zip, read with .NET.</summary>
    private sealed class ZipReader : ArchiveReader
    {
        private readonly Stream _stream;
        private readonly ZipArchive _zip;

        public ZipReader(Stream stream, string displayName)
        {
            _stream = stream;
            try
            {
                _zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, NameEncoding);
            }
            catch (InvalidDataException)
            {
                stream.Dispose();
                throw new ArchiveException($"'{displayName}' is not a valid zip file, or it is damaged.");
            }
        }

        public override IEnumerable<RawEntry> ReadEntries()
        {
            foreach (var entry in _zip.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                yield return new RawEntry("", name, name.EndsWith('/'), entry.Length,
                    entry.LastWriteTime.LocalDateTime, entry.IsEncrypted, entry.FullName);
            }
        }

        public override void CopyTo(RawEntry entry, Stream destination)
        {
            var zipEntry = _zip.GetEntry(entry.StoredName ?? entry.FullName)
                ?? throw new ArchiveException($"'{Path.GetFileName(entry.FullName)}' is not in the archive.");

            using var packed = zipEntry.Open();
            packed.CopyTo(destination);
        }

        public override void Dispose()
        {
            _zip.Dispose();
            _stream.Dispose();
        }
    }

    /// <summary>A RAR or 7z file, read with 7-Zip.</summary>
    private sealed class SevenZipReader : ArchiveReader
    {
        private readonly string _file;
        public SevenZipReader(string file) => _file = file;

        public override IEnumerable<RawEntry> ReadEntries() => SevenZipRunner.List(_file);

        public override void CopyTo(RawEntry entry, Stream destination)
        {
            long copied = SevenZipRunner.Extract(_file, entry.StoredName ?? entry.FullName, destination);
            if (copied == 0 && entry.Length > 0)
                throw new ArchiveException($"'{Path.GetFileName(entry.FullName)}' could not be read from the archive.");
        }
    }

    /// <summary>Opens the last archive of the chain; an archive inside an archive is first read into memory (a zip) or to a temporary file.</summary>
    private static ArchiveReader OpenReader(string[] chain)
    {
        ArchiveReader reader = CreateFileReader(chain[0]);
        try
        {
            for (int i = 1; i < chain.Length; i++)
            {
                var parentChain = chain[..i];
                if (!GetIndex(parentChain).Files.TryGetValue(chain[i], out var entry))
                    throw new ArchiveException($"'{chain[i]}' is not in the archive.");
                CheckReadable(entry);

                ArchiveReader inner;
                if (HasExtension(chain[i], ZipExtensions))
                {
                    var memory = new MemoryStream();
                    reader.CopyTo(entry, memory);
                    memory.Position = 0;
                    inner = new ZipReader(memory, Path.GetFileName(chain[i]));
                }
                else
                {
                    inner = new SevenZipReader(NestedTempFile(chain[..(i + 1)], reader, entry));
                }

                reader.Dispose();   // the outer archive is not needed any more
                reader = inner;
            }
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private static ArchiveReader CreateFileReader(string file)
    {
        if (!HasExtension(file, ZipExtensions))
            return new SevenZipReader(file);

        return new ZipReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                             Path.GetFileName(file));
    }

    private static void CheckReadable(RawEntry entry)
    {
        if (entry.Encrypted)
            throw new ArchiveException($"'{Path.GetFileName(entry.FullName)}' is protected with a password. Archives with a password are not supported yet.");
    }

    // 7-Zip needs a file on the disk: an archive inside another one is unpacked once to the temporary folder.
    private static readonly Dictionary<string, string> NestedTemp = new(StringComparer.OrdinalIgnoreCase);

    private static string NestedTempFile(string[] chain, ArchiveReader outer, RawEntry entry)
    {
        string key = string.Join("|", chain) + "|" + Stamp(chain[0]);
        lock (NestedTemp)
        {
            if (NestedTemp.TryGetValue(key, out var known) && File.Exists(known)) return known;

            string folder = Path.Combine(TempFolder, "nested", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            string target = Path.Combine(folder, SafeFileName(Path.GetFileName(entry.FullName)));
            using (var file = File.Create(target))
                outer.CopyTo(entry, file);

            NestedTemp[key] = target;
            return target;
        }
    }

    /// <summary>Calls <paramref name="read"/> with the reader of the archive that holds a file, and the entry of that file.</summary>
    private static T WithEntry<T>(string path, Func<ArchiveReader, RawEntry, T> read)
    {
        var location = Locate(path, out var error, asEntry: true)
            ?? throw new ArchiveException(error ?? $"'{path}' is not in an archive.");

        var index = GetIndex(location.Chain);
        if (!index.Files.TryGetValue(location.Inner, out var file))
            throw new ArchiveException($"'{Path.GetFileName(path)}' is not in the archive.");

        CheckReadable(file);
        using var reader = OpenReader(location.Chain);
        return read(reader, file);
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    /// <summary>The content of a file inside an archive, in memory. For pictures: do not use it for a big video.</summary>
    public static Stream OpenRead(string path) =>
        WebFolder.IsWebPath(path) ? WebFolder.OpenRead(path) :
        WithEntry(path, (reader, entry) =>
        {
            var memory = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue / 2));
            reader.CopyTo(entry, memory);
            memory.Position = 0;
            return (Stream)memory;
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
        if (WebFolder.IsWebPath(path)) { WebFolder.ExtractTo(path, destination); return; }

        var entry = GetEntry(path);
        bool isFolder = entry == null || entry.IsDirectory;

        if (!isFolder)
        {
            try
            {
                WithEntry(path, (reader, raw) =>
                {
                    using var target = File.Create(destination);
                    reader.CopyTo(raw, target);
                    return 0;
                });
            }
            catch
            {
                try { File.Delete(destination); } catch { /* a half-written file is left */ }
                throw;
            }
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
    private static string TempRoot => Path.Combine(Path.GetTempPath(), "SwewolfFileExplorer");
    private static string TempFolder => Path.Combine(TempRoot, Environment.ProcessId.ToString());

    /// <summary>Unpacks a file or folder of an archive to a temporary place, and returns where it is. The same one is unpacked once.</summary>
    public static string ExtractToTemp(string path)
    {
        lock (Extracted)
        {
            if (Extracted.TryGetValue(path, out var known) && (File.Exists(known) || Directory.Exists(known)))
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

    /// <summary>Writes bytes to a file in the temporary folder (removed with the rest when the app ends) and returns its path.</summary>
    public static string SaveToTemp(string fileName, byte[] data)
    {
        string folder = Path.Combine(TempFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        string target = Path.Combine(folder, SafeFileName(fileName));
        File.WriteAllBytes(target, data);
        return target;
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
                try { running = System.Diagnostics.Process.GetProcessById(pid).ProcessName.Equals("SwewolfFileExplorer", StringComparison.OrdinalIgnoreCase); }
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
        if (WebFolder.IsWebPath(path)) return WebFolder.GetEntry(path)?.Modified ?? DateTime.MinValue;

        if (File.Exists(path) || Directory.Exists(path))
            return created ? Directory.GetCreationTime(path) : Directory.GetLastWriteTime(path);

        return GetEntry(path)?.Modified ?? DateTime.MinValue;   // an archive has one date per entry: the creation date is the modified date
    }
}
