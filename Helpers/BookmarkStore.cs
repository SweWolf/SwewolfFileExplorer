using System.IO;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>One thing in the Bookmarks folder: a bookmark (a shortcut), or a group (a sub-folder with bookmarks in it).</summary>
public sealed class BookmarkEntry
{
    /// <summary>What it is called: the name of the shortcut without ".lnk", or of the group's folder.</summary>
    public string Name { get; init; } = "";

    /// <summary>The shortcut file, or the group's folder.</summary>
    public string Path { get; init; } = "";

    /// <summary>Where the shortcut points (a folder or a file); null for a group and for a shortcut that points to nothing usable.</summary>
    public string? Target { get; init; }

    public bool IsGroup { get; init; }

    /// <summary>The target is there (only found out when asked for: a disk that is not connected makes this false). Always true for a group.</summary>
    public bool Available { get; init; } = true;

    /// <summary>The target is a file, not a folder.</summary>
    public bool IsFile { get; init; }

    /// <summary>The point in a video or music file where this bookmark starts (milliseconds); null for a bookmark of the whole file or folder.</summary>
    public long? StartMs { get; init; }

    public List<BookmarkEntry> Children { get; } = new();
}

/// <summary>
/// The bookmarks: a folder of Windows shortcuts (<c>.lnk</c> files), <c>%AppData%\SweWolfSoftware\SwewolfFileExplorer\Bookmarks</c>.
/// A bookmark is a shortcut to a folder (or a file); a sub-folder is a group. Because they are ordinary shortcuts they can also be seen,
/// renamed, moved and deleted in Windows Explorer, copied to another computer, and so on. The order is by name, groups first. A path
/// inside a compressed file cannot be a shortcut's target, so it cannot be bookmarked.
/// </summary>
public static class BookmarkStore
{
    /// <summary>Another folder instead of the real one: for tests only.</summary>
    public static string? FolderOverride { get; set; }

    public static string Folder => FolderOverride ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SweWolfSoftware", AppInfo.FolderName, "Bookmarks");

    // ── Reading ───────────────────────────────────────────────────────────────

    // Where a shortcut points, kept until the shortcut file changes (asking Windows for it costs a moment per file).
    private static readonly Dictionary<string, (DateTime Written, string? Target, long? StartMs)> TargetCache = new(StringComparer.OrdinalIgnoreCase);

    // ── The start point of a bookmark ─────────────────────────────────────────
    // A shortcut has no field for a time, so it is written in the shortcut's Comment (what Windows shows in its Properties) in plain
    // words, "Start time: 0:12:34.500" (hours:minutes:seconds.thousandths), so nobody wonders what it is. SweWolf Media Player makes
    // such shortcuts too (the same text: change both together).

    /// <summary>The text of the Comment for a start point of <paramref name="ms"/> milliseconds.</summary>
    public static string FormatStartTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(ms, 0));
        return $"Start time: {(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    /// <summary>The start point named in a Comment (milliseconds), or null if it does not begin with "Start time:".</summary>
    public static long? ParseStartTime(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(comment,
            @"^\s*Start\s*time\s*:?\s*(\d+):(\d{1,2}):(\d{1,2})(?:[.,](\d{1,3}))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        long ms = (long.Parse(m.Groups[1].Value) * 3600 + long.Parse(m.Groups[2].Value) * 60 + long.Parse(m.Groups[3].Value)) * 1000;
        if (m.Groups[4].Success) ms += long.Parse(m.Groups[4].Value.PadRight(3, '0'));
        return ms;
    }

    /// <summary>
    /// Everything in the Bookmarks folder as a tree of entries. Touches the disk (and, with <paramref name="checkAvailable"/>, the
    /// targets: a disk that is not connected can make that slow): call it on a background thread.
    /// </summary>
    public static List<BookmarkEntry> Read(bool checkAvailable = true)
    {
        var root = new List<BookmarkEntry>();
        if (!Directory.Exists(Folder)) return root;

        dynamic? shell = null;
        try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); } catch { /* no targets then */ }
        ReadFolder(Folder, root, shell, checkAvailable);
        return root;
    }

    private static void ReadFolder(string folder, List<BookmarkEntry> into, dynamic? shell, bool checkAvailable)
    {
        try
        {
            foreach (var dir in new DirectoryInfo(folder).EnumerateDirectories().Where(d => (d.Attributes & FileAttributes.Hidden) == 0)
                                                          .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var group = new BookmarkEntry { Name = dir.Name, Path = dir.FullName, IsGroup = true };
                ReadFolder(dir.FullName, group.Children, shell, checkAvailable);
                into.Add(group);
            }

            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.lnk").Where(f => (f.Attributes & FileAttributes.Hidden) == 0)
                                                          .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var (target, startMs) = TargetOf(file, (object?)shell);
                bool exists = false, isFile = false;
                if (target != null && checkAvailable)
                {
                    try
                    {
                        if (Directory.Exists(target)) exists = true;
                        else if (File.Exists(target)) { exists = true; isFile = true; }
                    }
                    catch { /* not there */ }
                }

                into.Add(new BookmarkEntry
                {
                    Name      = System.IO.Path.GetFileNameWithoutExtension(file.Name),
                    Path      = file.FullName,
                    Target    = target,
                    Available = !checkAvailable || exists,
                    IsFile    = isFile,
                    StartMs   = startMs,
                });
            }
        }
        catch { /* a folder that cannot be read: what could be read is kept */ }
    }

    /// <summary>The bookmark of this shortcut file (a .lnk inside the Bookmarks folder), or null if it is not one.</summary>
    public static string? TargetOfBookmarkFile(string path) => TargetOfBookmarkFile(path, out _);

    /// <summary>The same, and the start point of the bookmark (null if it has none).</summary>
    public static string? TargetOfBookmarkFile(string path, out long? startMs)
    {
        startMs = null;
        try
        {
            if (!string.Equals(System.IO.Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)) return null;
            var full = System.IO.Path.GetFullPath(path);
            var root = System.IO.Path.GetFullPath(Folder).TrimEnd('\\') + "\\";
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var (target, start) = TargetOf(new FileInfo(full), (object?)shell);
            startMs = start;
            return target;
        }
        catch { return null; }
    }

    private static (string? Target, long? StartMs) TargetOf(FileInfo file, object? shellObject)
    {
        dynamic? shell = shellObject;   // (a dynamic argument would make the whole call dynamic, which cannot be deconstructed)
        lock (TargetCache)
            if (TargetCache.TryGetValue(file.FullName, out var cached) && cached.Written == file.LastWriteTimeUtc) return (cached.Target, cached.StartMs);

        string? target = null;
        long? startMs = null;
        try
        {
            if (shell != null)
            {
                dynamic link = shell.CreateShortcut(file.FullName);
                string t = link.TargetPath;
                target = string.IsNullOrWhiteSpace(t) ? null : t;
                startMs = ParseStartTime((string?)link.Description);
            }
        }
        catch { /* a damaged shortcut: no target */ }

        lock (TargetCache) TargetCache[file.FullName] = (file.LastWriteTimeUtc, target, startMs);
        return (target, startMs);
    }

    /// <summary>
    /// A short text that changes when anything in the Bookmarks folder is added, removed, renamed or changed (also from Windows
    /// Explorer): the tree compares it to see if it must read the folder again.
    /// </summary>
    public static string Snapshot()
    {
        try
        {
            if (!Directory.Exists(Folder)) return "";
            return string.Join("|", Directory.EnumerateFileSystemEntries(Folder, "*", System.IO.SearchOption.AllDirectories)
                                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                             .Select(p => p + "@" + File.GetLastWriteTimeUtc(p).Ticks));
        }
        catch { return ""; }
    }

    // ── Adding, renaming, removing ────────────────────────────────────────────

    /// <summary>
    /// Bookmarks a folder or a file. Returns the entry, or null with a message when it cannot be done. If the same target is
    /// bookmarked already nothing is added: <paramref name="already"/> is true and the existing entry is returned.
    /// </summary>
    public static BookmarkEntry? Add(string target, out bool already, out string error, string? inFolder = null)
    {
        already = false;
        error = "";
        try
        {
            target = System.IO.Path.GetFullPath(target);
            if (target.Length > 3) target = target.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

            bool isFile = File.Exists(target);
            if (!isFile && !Directory.Exists(target))
            {
                error = "That is not a folder or a file on the disk. (A path inside a compressed file cannot be bookmarked.)";
                return null;
            }

            // Added with Ctrl+D (no folder given): the same target is never bookmarked twice anywhere. Pasted into a group: only that
            // group is looked at, so one folder or file can be in several groups.
            var existing = inFolder == null ? Find(Read(checkAvailable: false), target)
                                            : FindInFolder(Read(checkAvailable: false), target, inFolder);
            if (existing != null) { already = true; return existing; }

            // Named after the folder (or file); a drive after its letter.
            var name = System.IO.Path.GetFileName(target);
            if (name.Length == 0) name = "Drive " + target[0];
            name = CleanName(name);
            if (name.Length == 0) name = "Bookmark";

            var where = inFolder ?? Folder;   // a group (a sub-folder of the Bookmarks folder) or the Bookmarks folder itself
            Directory.CreateDirectory(where);
            var lnk = UniquePath(where, name, ".lnk");

            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link  = shell.CreateShortcut(lnk);
            link.TargetPath       = target;
            link.WorkingDirectory = isFile ? System.IO.Path.GetDirectoryName(target) ?? "" : target;
            link.Save();

            if (!File.Exists(lnk)) { error = "The shortcut could not be saved."; return null; }
            return new BookmarkEntry { Name = System.IO.Path.GetFileNameWithoutExtension(lnk), Path = lnk, Target = target, IsFile = isFile };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // ── Web addresses: Internet shortcuts (.url) ─────────────────────────────
    // A .lnk cannot point to a web address, so a bookmark of one is an Internet shortcut: a small text file Windows opens in the
    // default browser. They are not in the tree (it only shows folders); the list shows them, opens them and renames them like any file.
    // Their icon (the page's favicon) is kept outside the Bookmarks folder, in %AppData%\...\Favicons, one .ico per site. It belongs with the
    // bookmarks (a .url names it with a full path), so it is in Roaming like them; until 2026-11-10 it was in %LocalAppData%
    // (see MigrateFavicons).

    /// <summary>Another folder for the favicons instead of the real one: for tests only.</summary>
    public static string? FaviconFolderOverride { get; set; }

    /// <summary>Where the favicons are kept.</summary>
    public static string FaviconFolder => FaviconFolderOverride ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SweWolfSoftware", AppInfo.FolderName, "Favicons");

    /// <summary>Where the favicons were kept before 2026-11-10.</summary>
    private static string OldFaviconFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SweWolfSoftware", AppInfo.FolderName, "Favicons");

    /// <summary>
    /// Moves the favicons from %LocalAppData% to %AppData% (once, at the start of the program; nothing happens when there is nothing to move)
    /// and changes the <c>IconFile=</c> line of the Internet shortcuts that named the old place, so their icons stay. Nothing is lost:
    /// a file is only moved when the new place has no file of that name (an identical one is removed from the old place), a shortcut is
    /// only changed when the icon it will name is there, and a failure on one file goes on with the next. The old folder is removed
    /// when it is empty. <paramref name="oldFolder"/> is for tests.
    /// </summary>
    public static void MigrateFavicons(string? oldFolder = null)
    {
        try
        {
            oldFolder ??= OldFaviconFolder;
            var newFolder = FaviconFolder;
            if (!Directory.Exists(oldFolder) || SameFolder(oldFolder, newFolder)) return;

            Directory.CreateDirectory(newFolder);
            foreach (var file in Directory.EnumerateFiles(oldFolder))
            {
                try
                {
                    var target = System.IO.Path.Combine(newFolder, System.IO.Path.GetFileName(file));
                    if (!File.Exists(target)) File.Move(file, target);
                    else if (File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(target))) File.Delete(file);
                    // a different file of that name already in the new place: both stay
                }
                catch { /* the next one */ }
            }

            RewriteIconPaths(oldFolder, newFolder);
            try { Directory.Delete(oldFolder); } catch { /* not empty: what could not be moved stays */ }
        }
        catch
        {
            // Not being able to move the icons is not worth stopping the program: the shortcuts still name the old place and work.
        }
    }

    /// <summary>In every Internet shortcut of the Bookmarks folder (and its groups): <c>IconFile=</c> in the old folder becomes the new folder, if the icon is there.</summary>
    private static void RewriteIconPaths(string oldFolder, string newFolder)
    {
        if (!Directory.Exists(Folder)) return;

        // A .url is written in the ANSI code page (see AddUrl): read and written back the same way, so nothing else in it changes.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var ansi = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        var oldPrefix = oldFolder.TrimEnd('\\') + "\\";
        var line = new System.Text.RegularExpressions.Regex(@"^IconFile=([^\r\n]*)", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var file in Directory.EnumerateFiles(Folder, "*.url", System.IO.SearchOption.AllDirectories))
        {
            try
            {
                var text = ansi.GetString(File.ReadAllBytes(file));
                bool changed = false;
                var result = line.Replace(text, m =>
                {
                    var value = m.Groups[1].Value.Trim();
                    if (!value.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)) return m.Value;
                    var candidate = System.IO.Path.Combine(newFolder, value[oldPrefix.Length..]);
                    if (!File.Exists(candidate)) return m.Value;   // the icon did not move: leave the shortcut as it is
                    changed = true;
                    return "IconFile=" + candidate;
                });
                if (!changed) continue;

                var temp = file + ".part" + Environment.ProcessId;
                try
                {
                    File.WriteAllBytes(temp, ansi.GetBytes(result));
                    File.Move(temp, file, overwrite: true);
                }
                catch
                {
                    try { File.Delete(temp); } catch { }
                    throw;
                }
            }
            catch { /* the next shortcut */ }
        }
    }

    /// <summary>
    /// The web address as an address, or null if the text is not one: <c>http</c> / <c>https</c> only; "www.example.com" gets https.
    /// Spaces, other schemes (file:, javascript:) and plain file names are refused.
    /// </summary>
    public static string? AsWebAddress(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > 2000 || text.Any(char.IsWhiteSpace)) return null;
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(uri.Host) || !uri.Host.Contains('.') && !uri.IsLoopback) return null;
        return uri.AbsoluteUri;
    }

    /// <summary>The suggested name for a bookmark of a page: its title, else the site (and the last part of the address).</summary>
    public static string SuggestUrlName(string url, string? title)
    {
        var name = CleanName(title ?? "");
        if (name.Length > 0) return name.Length > 100 ? name[..100].TrimEnd() : name;

        try
        {
            var uri  = new Uri(url);
            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            var last = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault() ?? "";
            last = CleanName(System.IO.Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(last)).Replace('-', ' ').Replace('_', ' '));
            name = CleanName(last.Length > 0 ? $"{host} - {last}" : host);
        }
        catch { }
        return name.Length > 0 ? name : "Web Bookmark";
    }

    /// <summary>
    /// The bookmark (.url) that already holds this web address: in <paramref name="inFolder"/> only (a group or the Bookmarks folder
    /// itself, not the groups below it), or anywhere in the Bookmarks folder when no folder is given. Null if none.
    /// </summary>
    public static string? FindUrlBookmark(string url, string? inFolder = null)
    {
        try
        {
            var where = inFolder ?? Folder;
            if (!Directory.Exists(where)) return null;
            foreach (var file in Directory.EnumerateFiles(where, "*.url", inFolder == null ? System.IO.SearchOption.AllDirectories : System.IO.SearchOption.TopDirectoryOnly))
            {
                var existing = ReadUrlOf(file);
                if (existing != null && string.Equals(existing.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return file;
            }
        }
        catch { }
        return null;
    }

    /// <summary>The address in an Internet shortcut (its <c>URL=</c> line); null if it has none.</summary>
    public static string? ReadUrlOf(string urlFile)
    {
        try
        {
            foreach (var line in File.ReadLines(urlFile))
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return line[4..].Trim();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Makes the Internet shortcut <paramref name="name"/>.url in <paramref name="inFolder"/> (a group or the Bookmarks folder) and,
    /// with <paramref name="icon"/> (the bytes of an .ico), keeps the page's icon for it. Never replaces a file: returns null with a
    /// message when the name is taken or the file cannot be written.
    /// </summary>
    public static string? AddUrl(string url, string name, byte[]? icon, string inFolder, out string error)
    {
        error = "";
        try
        {
            name = CleanName(name);
            if (name.Length == 0) { error = "Enter a name."; return null; }

            Directory.CreateDirectory(inFolder);
            var path = System.IO.Path.Combine(inFolder, name + ".url");
            if (File.Exists(path) || Directory.Exists(path)) { error = $"There is a bookmark named \"{name}\" already."; return null; }

            var text = new StringBuilder();
            text.Append("[InternetShortcut]\r\n");
            text.Append("URL=").Append(url).Append("\r\n");

            if (icon != null)
            {
                try
                {
                    Directory.CreateDirectory(FaviconFolder);
                    var host = new Uri(url).Host.ToLowerInvariant();
                    var iconPath = System.IO.Path.Combine(FaviconFolder, CleanName(host) + ".ico");
                    File.WriteAllBytes(iconPath, icon);
                    text.Append("IconIndex=0\r\n");
                    text.Append("IconFile=").Append(iconPath).Append("\r\n");
                }
                catch { /* no icon then: the shortcut still works */ }
            }

            // Windows reads an Internet shortcut in the ANSI code page (.NET's own default is UTF-8): the address is plain ASCII,
            // but the icon path may hold å ä ö.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var bytes = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetBytes(text.ToString());

            // Written whole under another name and moved into place, so that nobody ever sees the finished name as an empty file (the
            // shell remembers the icon it found the first time, and an empty .url has the plain one). Move never replaces a file.
            var temp = path + ".part" + Environment.ProcessId;
            try
            {
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
            return path;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The bookmark of this target that lies directly in <paramref name="folder"/> (a group or the Bookmarks folder); null if none.</summary>
    private static BookmarkEntry? FindInFolder(List<BookmarkEntry> entries, string target, string folder)
    {
        foreach (var e in entries)
        {
            if (e.IsGroup)
            {
                var inside = FindInFolder(e.Children, target, folder);
                if (inside != null) return inside;
            }
            else if (e.StartMs == null && e.Target != null && SameFolder(System.IO.Path.GetDirectoryName(e.Path) ?? "", folder)
                     && SameFolder(e.Target, target))
                return e;
        }
        return null;
    }

    private static BookmarkEntry? Find(List<BookmarkEntry> entries, string target)
    {
        foreach (var e in entries)
        {
            if (e.IsGroup)
            {
                var inside = Find(e.Children, target);
                if (inside != null) return inside;
            }
            else if (e.StartMs == null && e.Target != null && string.Equals(e.Target.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }

    /// <summary>Gives a bookmark (or a group) another name: its shortcut file (or folder) is renamed. False with a message if it cannot.</summary>
    public static bool Rename(BookmarkEntry entry, string newName, out string error)
    {
        error = "";
        try
        {
            newName = CleanName(newName);
            if (newName.Length == 0) { error = "Enter a name."; return false; }

            var dir = System.IO.Path.GetDirectoryName(entry.Path)!;
            var path = System.IO.Path.Combine(dir, entry.IsGroup ? newName : newName + ".lnk");
            if (string.Equals(path, entry.Path, StringComparison.Ordinal)) return true;

            bool onlyCase = string.Equals(path, entry.Path, StringComparison.OrdinalIgnoreCase);
            if (!onlyCase && (File.Exists(path) || Directory.Exists(path))) { error = $"There is a bookmark named \"{newName}\" already."; return false; }

            if (entry.IsGroup) Directory.Move(entry.Path, path); else File.Move(entry.Path, path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Removes a bookmark (or a group with what is in it): to the Recycle Bin, so it can be brought back.</summary>
    public static bool Remove(BookmarkEntry entry, out string error)
    {
        error = "";
        try
        {
            if (entry.IsGroup) FileSystem.DeleteDirectory(entry.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else               FileSystem.DeleteFile(entry.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ── Names ─────────────────────────────────────────────────────────────────

    /// <summary>Leaves out what a file name cannot hold, doubled spaces, and the dots and spaces at the ends.</summary>
    public static string CleanName(string name)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => Array.IndexOf(bad, c) < 0).ToArray());
        return System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim().Trim('.').Trim();
    }

    /// <summary>A path in <paramref name="folder"/> for a new file that does not exist yet: the name, else the name with (2), (3) ...</summary>
    public static string UniquePath(string folder, string name, string extension)
    {
        var path = System.IO.Path.Combine(folder, name + extension);
        for (int i = 2; File.Exists(path) || Directory.Exists(path); i++)
            path = System.IO.Path.Combine(folder, $"{name} ({i}){extension}");
        return path;
    }
}
