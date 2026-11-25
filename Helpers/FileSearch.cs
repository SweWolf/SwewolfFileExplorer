using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SwewolfFileExplorer.Helpers;

// FileSearch: finds files and folders by name (and dates) below one or more folders, with no index: a plain walk of the folders on
// several threads, results handed over in batches as they are found, Cancel at any time. Written 2026-11-17 for the search window
// (plan: D:\Dev\dev-notes\FileExplorer-search-plan.md). Only HiddenItems (the same Hidden rules as the list) is an explorer type.
//
// History
//   2026-11-17  First version: name words / wildcards, files / folders, hidden rules, created / modified date ranges, batches, cap.
//   2026-11-17  Progress about every 150 ms even with no results, with the folder that has been read longest (a slow folder shows);
//               onFolder callback with the time of every folder (for the search log).
//   2026-11-17  Size limits (MinBytes / MaxBytes, files only: a size search never returns folders).
//   2026-11-17  Approximate name matching (Simplify; digits kept; *.ext accepts jpg / jpeg style spellings).
//   2026-11-17  StopAfter (the user's limit; SearchSummary.StoppedAtLimit tells that it was that and not the built-in cap).
//   2026-11-17  ExcludedFolders (full paths or plain names; counted in SearchSummary.ExcludedSkipped).
//   2026-11-17  A term with * or ? must fit the whole name (anchored): *.ico no longer finds "MainWindow.IconView.cs".

public enum SearchKinds { FilesAndFolders, FilesOnly, FoldersOnly }

public enum SearchHidden { VisibleOnly, VisibleAndHidden, HiddenOnly }

/// <summary>What to look for. Dates are whole days: "to" includes that whole day. A null date is open on that side.</summary>
public sealed class SearchCriteria
{
    /// <summary>The folders to search (drive roots too). Never empty when a search runs.</summary>
    public List<string> Roots { get; set; } = new();

    /// <summary>Words that must all be in the name (any case). "quoted words" count as one; * and ? are wildcards. Empty: every name matches.</summary>
    public string Text { get; set; } = "";

    public bool Subfolders { get; set; } = true;

    /// <summary>Approximate name matching: capitals, accents, punctuation and doubled letters do not matter (see <see cref="FileSearch.Simplify"/>).</summary>
    public bool Approximate { get; set; }
    public SearchKinds Kinds { get; set; } = SearchKinds.FilesAndFolders;
    public SearchHidden Hidden { get; set; } = SearchHidden.VisibleOnly;

    public DateTime? ModifiedFrom { get; set; }
    public DateTime? ModifiedTo { get; set; }
    public DateTime? CreatedFrom { get; set; }
    public DateTime? CreatedTo { get; set; }

    /// <summary>Size limits of a FILE in bytes, both ends included; null = no limit on that side. A folder has no size: with a limit set no folder is a result.</summary>
    /// <summary>
    /// Folders that are never entered (and never a result): a full path (that folder and everything below it) or a plain name (any folder with
    /// that name, anywhere: node_modules, .git). Never applied to the folders in <see cref="Roots"/> themselves. See <see cref="FileSearch.ParseExcluded"/>.
    /// </summary>
    public List<string> ExcludedFolders { get; set; } = new();

    /// <summary>Stop when this many items are found (1 to <see cref="FileSearch.MaxResults"/>); null = no limit of the user's own (only the built-in one).</summary>
    public int? StopAfter { get; set; }

    public long? MinBytes { get; set; }
    public long? MaxBytes { get; set; }
    public bool HasSizeLimit => MinBytes != null || MaxBytes != null;

    public SearchCriteria Clone()
    {
        var c = (SearchCriteria)MemberwiseClone();
        c.Roots = new List<string>(Roots);
        c.ExcludedFolders = new List<string>(ExcludedFolders);
        return c;
    }

    /// <summary>A short text for the tab and the search bar: what was searched for.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Text.Trim().Length > 0) parts.Add(Text.Trim());
        if (ModifiedFrom != null || ModifiedTo != null) parts.Add("modified " + Range(ModifiedFrom, ModifiedTo));
        if (CreatedFrom != null || CreatedTo != null) parts.Add("created " + Range(CreatedFrom, CreatedTo));
        if (HasSizeLimit)
            parts.Add("size " + (MinBytes != null && MaxBytes != null ? $"{FormatBytes(MinBytes.Value)} - {FormatBytes(MaxBytes.Value)}"
                               : MinBytes != null ? $"at least {FormatBytes(MinBytes.Value)}" : $"at most {FormatBytes(MaxBytes!.Value)}"));
        return parts.Count > 0 ? string.Join(", ", parts) : "all items";
    }

    /// <summary>"200 MB", "1.5 GB", "0 KB": the biggest unit (KB, MB, GB) that keeps the number at 1 or more, up to 2 decimals.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.##} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.##} MB";
        return $"{bytes / 1024.0:0.##} KB";
    }

    private static string Range(DateTime? from, DateTime? to) =>
        from != null && to != null ? $"{from:d} - {to:d}" : from != null ? $"from {from:d}" : $"until {to:d}";
}

public sealed record SearchSummary(long Found, long FoldersSearched, long FoldersSkipped, bool Capped, bool Canceled, long ExcludedSkipped = 0, bool StoppedAtLimit = false);

public static class FileSearch
{
    /// <summary>At most this many results: the list is a list of view models, and a search that finds more than this needs narrowing.</summary>
    public const int MaxResults = 50_000;

    /// <summary>One name test for a search text (the name, and whether it is a folder); true for an empty text.</summary>
    /// <param name="approximate">
    /// Approximate matching: the words and the name (without its extension, for a file) are both reduced by <see cref="Simplify"/> (no capitals,
    /// accents or punctuation, doubled letters made one) before "is contained in" is asked. A word with * or ? is still exact, except that a
    /// plain <c>*.ext</c> also accepts the other spellings of the extension (jpg / jpeg, htm / html ...: <see cref="SameExtension"/>).
    /// </param>
    public static Func<string, bool, bool> NameMatcher(string text, bool approximate = false)
    {
        // Groups of terms: every term of a group must fit (AND); a capital OR on its own starts the next group, and one group is enough (as in
        // Windows Explorer). A quoted OR is an ordinary word. A group with no usable term is dropped; no group at all matches everything.
        var groups = new List<List<Func<string, bool, bool>>> { new() };
        foreach (var (term, quoted) in SplitTerms(text))
        {
            if (!quoted && term == "OR") { groups.Add(new()); continue; }
            var one = BuildTerm(term, quoted, approximate);
            if (one != null) groups[^1].Add(one);
        }
        groups.RemoveAll(g => g.Count == 0);
        if (groups.Count == 0) return (_, _) => true;
        return (name, isDir) =>
        {
            foreach (var g in groups)
            {
                bool all = true;
                foreach (var t in g) if (!t(name, isDir)) { all = false; break; }
                if (all) return true;
            }
            return false;
        };
    }

    /// <summary>One search word as a test, or null when there is nothing to look for in it (only punctuation, an empty <c>ext:</c>).</summary>
    private static Func<string, bool, bool>? BuildTerm(string term, bool quoted, bool approximate)
    {
        // ext:jpg (also ext:.jpg): files with that extension; a folder has none. Approximate matching accepts the other spellings (jpeg).
        if (!quoted && term.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
        {
            var wanted = term[4..].TrimStart('.');
            if (wanted.Length == 0) return null;
            return (name, isDir) => !isDir && (approximate ? SameExtension(Path.GetExtension(name), wanted)
                                                           : string.Equals(Path.GetExtension(name).TrimStart('.'), wanted, StringComparison.OrdinalIgnoreCase));
        }

        {
            if (term.IndexOfAny(new[] { '*', '?' }) >= 0)
            {
                // With a wildcard the term must fit the WHOLE name, as in Windows: *.ico ends in .ico (it must not find "X.IconView.cs"),
                // alpha* starts with alpha, *alpha* has it anywhere. Words without a wildcard are still "contained in the name".
                var regex = new Regex("^" + Regex.Escape(term).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                                      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

                // Approximate: *.jpg is "an extension", and its other spellings count (a folder has none: it is tested as before).
                if (approximate && term.Length > 2 && term.StartsWith("*.") && term.Skip(2).All(char.IsLetterOrDigit))
                {
                    var wanted = term[2..];
                    return (name, isDir) => isDir ? regex.IsMatch(name) : SameExtension(Path.GetExtension(name), wanted);
                }
                return (name, _) => regex.IsMatch(name);
            }
            if (approximate)
            {
                var t = Simplify(term);
                if (t.Length == 0) return null;   // only punctuation: nothing to look for
                return (name, isDir) => Simplify(isDir ? name : Path.GetFileNameWithoutExtension(name)).Contains(t, StringComparison.Ordinal);
            }
            return (name, _) => name.Contains(term, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The form a name is compared in by approximate matching: lower case; accents and rings gone (å ä -> a, ö -> o, é -> e; ø, æ, ß and a few
    /// more letters that do not split are mapped by hand); everything that is not a letter or a digit (space, dot, underscore, dash ...) removed;
    /// a letter that comes twice or more in a row (after the removal) is written once. Digits stay as they are. Letters of other alphabets
    /// are only lower-cased (not handled further).
    /// </summary>
    public static string Simplify(string name)
    {
        var sb = new StringBuilder(name.Length);
        char last = '\0';   // the last letter written ("" after a digit): only letters are made single
        void Put(char c)
        {
            if (char.IsDigit(c)) { sb.Append(c); last = '\0'; return; }
            if (!char.IsLetter(c)) return;
            c = char.ToLowerInvariant(c);
            if (c == last) return;
            sb.Append(c);
            last = c;
        }

        bool ascii = true;
        foreach (var ch in name) if (ch > 127) { ascii = false; break; }
        var source = ascii ? name : name.Normalize(NormalizationForm.FormD);   // å = a + a ring: the ring is a combining mark
        foreach (var ch in source)
        {
            if (ch < 128) { Put(ch); continue; }
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            switch (ch)
            {
                case 'ø': case 'Ø': Put('o'); break;
                case 'æ': case 'Æ': Put('a'); Put('e'); break;
                case 'œ': case 'Œ': Put('o'); Put('e'); break;
                case 'ß': Put('s'); break;   // ß = ss, and doubled letters count once anyway: one s
                case 'đ': case 'Đ': case 'ð': case 'Ð': Put('d'); break;
                case 'ł': case 'Ł': Put('l'); break;
                case 'þ': case 'Þ': Put('t'); Put('h'); break;
                default: Put(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The spellings of one file type that approximate matching treats as the same extension (without the dot, any case).</summary>
    private static readonly string[][] ExtensionFamilies =
    {
        new[] { "jpg", "jpeg", "jpe", "jfif" },
        new[] { "tif", "tiff" },
        new[] { "htm", "html" },
        new[] { "mpg", "mpeg", "mpe" },
        new[] { "mid", "midi" },
        new[] { "yml", "yaml" },
        new[] { "aif", "aiff", "aifc" },
    };

    /// <summary>The extension of a file (with or without its dot) is <paramref name="wanted"/>, or another spelling of the same type (jpg and jpeg).</summary>
    public static bool SameExtension(string actual, string wanted)
    {
        actual = actual.TrimStart('.');
        if (string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var family in ExtensionFamilies)
            if (family.Contains(actual, StringComparer.OrdinalIgnoreCase) && family.Contains(wanted, StringComparer.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// The text of the "folders to exclude" box (one per line; semicolons also separate): quotes and spaces at the ends removed, a closing
    /// backslash removed, empty lines and doubles dropped. An entry with a backslash, slash or colon is a full path, anything else a folder name.
    /// </summary>
    public static List<string> ParseExcluded(string? text)
    {
        var result = new List<string>();
        foreach (var raw in (text ?? "").Split('\n', '\r', ';'))
        {
            var s = raw.Trim().Trim('"').Trim();
            if (s.Length > 3) s = s.TrimEnd('\\', '/');   // "D:\" keeps its backslash: a drive root
            if (s.Length == 0) continue;
            if (!result.Contains(s, StringComparer.OrdinalIgnoreCase)) result.Add(s);
        }
        return result;
    }

    private static bool IsPathEntry(string s) => s.IndexOfAny(new[] { '\\', '/', ':' }) >= 0;

    /// <summary>The words of a search text; a word that was in quotes says so (so a quoted OR or "ext:jpg" is plain text).</summary>
    private static List<(string Text, bool Quoted)> SplitTerms(string text)
    {
        var terms = new List<(string, bool)>();
        var sb = new StringBuilder();
        bool quoted = false, wasQuoted = false;
        foreach (char ch in text ?? "")
        {
            if (ch == '"') { quoted = !quoted; wasQuoted = true; if (!quoted) Flush(); continue; }
            if (char.IsWhiteSpace(ch) && !quoted) { Flush(); continue; }
            sb.Append(ch);
        }
        Flush();
        return terms;

        void Flush() { if (sb.Length > 0) terms.Add((sb.ToString(), wasQuoted)); sb.Clear(); wasQuoted = false; }
    }

    /// <summary>
    /// Runs the search. <paramref name="onBatch"/> gets the found items (a few hundred at a time, from several threads one at a
    /// time) and the number of folders looked at so far. Returns when everything is searched, the cap is reached or <paramref name="ct"/> is canceled.
    /// </summary>
    public static Task<SearchSummary> RunAsync(SearchCriteria criteria, Action<List<FileSystemInfo>, long, string?> onBatch, CancellationToken ct,
                                               Action<string, long, int>? onFolder = null) =>
        Task.Run(() => Run(criteria, onBatch, ct, onFolder), ct);

    // onBatch(items, folders searched so far, the folder that has been read the longest right now (null when none)): called about every
    // 150 ms, also with no items, so a slow folder shows. onFolder(path, milliseconds, entries) is called when a folder is done (the debug log).
    private static SearchSummary Run(SearchCriteria c, Action<List<FileSystemInfo>, long, string?> onBatch, CancellationToken ct,
                                     Action<string, long, int>? onFolder)
    {
        var matches = NameMatcher(c.Text, c.Approximate);
        bool wantFiles = c.Kinds != SearchKinds.FoldersOnly;
        bool wantDirs  = c.Kinds != SearchKinds.FilesOnly;

        var queue = new ConcurrentQueue<string>();
        foreach (var root in c.Roots) queue.Enqueue(root);
        long outstanding = c.Roots.Count;   // folders queued or being read
        // The user's own limit (Stop After Finding N Items) or the built-in one, whichever is smaller.
        int limit = Math.Clamp(c.StopAfter ?? MaxResults, 1, MaxResults);
        bool userLimit = c.StopAfter != null && c.StopAfter < MaxResults;
        long searched = 0, skipped = 0, found = 0, excludedSkipped = 0;
        var excludedNames = new HashSet<string>(c.ExcludedFolders.Where(x => !IsPathEntry(x)), StringComparer.OrdinalIgnoreCase);
        var excludedPaths = new HashSet<string>(c.ExcludedFolders.Where(IsPathEntry).Select(x => x.Replace('/', '\\')), StringComparer.OrdinalIgnoreCase);
        bool capped = false;

        var batch = new List<FileSystemInfo>();
        var gate = new object();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastFlush = 0;

        int threads = Math.Clamp(Environment.ProcessorCount, 2, 6);
        var inflightPath = new string?[threads];   // what each worker is reading now, and since when
        var inflightSince = new long[threads];

        string? OldestInFlight()
        {
            string? best = null; long since = long.MaxValue;
            for (int i = 0; i < threads; i++)
            {
                var p = Volatile.Read(ref inflightPath[i]);
                if (p != null && Interlocked.Read(ref inflightSince[i]) < since) { best = p; since = inflightSince[i]; }
            }
            return best;
        }

        void Flush(bool force)
        {
            lock (gate)
            {
                if (!force && batch.Count < 200 && clock.ElapsedMilliseconds - lastFlush < 150) return;
                var send = batch; batch = new List<FileSystemInfo>();
                lastFlush = clock.ElapsedMilliseconds;
                onBatch(send, Interlocked.Read(ref searched), force ? null : OldestInFlight());
            }
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,                 // the default skips Hidden and System: the hidden rules are ours
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        void Worker(int me)
        {
            while (!ct.IsCancellationRequested && !Volatile.Read(ref capped))
            {
                if (!queue.TryDequeue(out var dir))
                {
                    if (Interlocked.Read(ref outstanding) == 0) return;
                    Thread.Sleep(2);   // other workers are still reading folders that may hold more
                    continue;
                }
                long t0 = clock.ElapsedMilliseconds;
                Interlocked.Exchange(ref inflightSince[me], t0);
                Volatile.Write(ref inflightPath[me], dir);
                int entries = 0;
                try { entries = ReadFolder(dir); }
                catch { Interlocked.Increment(ref skipped); entries = -1; }   // -1: the folder could not be read (the search log says which)
                finally
                {
                    Volatile.Write(ref inflightPath[me], null);
                    Interlocked.Decrement(ref outstanding); Interlocked.Increment(ref searched);
                }
                try { onFolder?.Invoke(dir, clock.ElapsedMilliseconds - t0, entries); } catch { }
                Flush(false);
            }
        }

        int ReadFolder(string dir)
        {
            int entries = 0;
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
            {
                entries++;
                if (ct.IsCancellationRequested) return entries;

                bool isDir = (e.Attributes & FileAttributes.Directory) != 0;
                bool hidden = isDir ? HiddenItems.IsHiddenByAttributes(e.Attributes) : HiddenItems.IsHiddenFile(e.Attributes, e.Name);

                // An excluded folder is neither entered nor a result (the folders searched in are never tested: only what lies below them).
                if (isDir && (excludedNames.Contains(e.Name) || excludedPaths.Contains(e.FullName.TrimEnd('\\'))))
                {
                    Interlocked.Increment(ref excludedSkipped);
                    continue;
                }

                // A hidden folder is not entered when hidden things are not wanted: what is in it is not seen in a folder window either.
                if (isDir && c.Subfolders && !(hidden && c.Hidden == SearchHidden.VisibleOnly) && !IsLink(e))
                {
                    Interlocked.Increment(ref outstanding);
                    queue.Enqueue(e.FullName);
                }

                if (isDir ? !wantDirs || c.HasSizeLimit : !wantFiles) continue;   // a folder has no size: never a result when a size is asked for
                if (!isDir && c.HasSizeLimit && !SizeMatches(c, e)) continue;
                if (c.Hidden == SearchHidden.VisibleOnly && hidden) continue;
                if (c.Hidden == SearchHidden.HiddenOnly && !hidden) continue;
                if (!matches(e.Name, isDir)) continue;
                if (!DatesMatch(c, e)) continue;

                lock (gate)
                {
                    if (found >= limit) { Volatile.Write(ref capped, true); return entries; }
                    found++;
                    batch.Add(e);
                }
            }
            return entries;
        }

        // Progress also while every worker is stuck in a slow folder: then nobody calls Flush, so a timer does.
        using var ticker = new Timer(_ => Flush(false), null, 300, 300);
        var tasks = Enumerable.Range(0, threads).Select(i => Task.Factory.StartNew(() => Worker(i), TaskCreationOptions.LongRunning)).ToArray();
        Task.WaitAll(tasks);
        ticker.Dispose();
        Flush(true);
        return new SearchSummary(found, searched, skipped, capped, ct.IsCancellationRequested, Interlocked.Read(ref excludedSkipped), capped && userLimit);
    }

    /// <summary>A symbolic link or junction (not followed: it could lead back up and never end). A cloud folder's reparse point is no link.</summary>
    private static bool IsLink(FileSystemInfo e)
    {
        if ((e.Attributes & FileAttributes.ReparsePoint) == 0) return false;
        try { return e.LinkTarget != null; }
        catch { return true; }
    }

    private static bool SizeMatches(SearchCriteria c, FileSystemInfo e)
    {
        long len;
        try { len = ((FileInfo)e).Length; } catch { return false; }
        return (c.MinBytes == null || len >= c.MinBytes) && (c.MaxBytes == null || len <= c.MaxBytes);
    }

    private static bool DatesMatch(SearchCriteria c, FileSystemInfo e)
    {
        if (c.ModifiedFrom != null || c.ModifiedTo != null)
        {
            var t = e.LastWriteTime;
            if (c.ModifiedFrom != null && t < c.ModifiedFrom.Value.Date) return false;
            if (c.ModifiedTo != null && t >= c.ModifiedTo.Value.Date.AddDays(1)) return false;
        }
        if (c.CreatedFrom != null || c.CreatedTo != null)
        {
            var t = e.CreationTime;
            if (c.CreatedFrom != null && t < c.CreatedFrom.Value.Date) return false;
            if (c.CreatedTo != null && t >= c.CreatedTo.Value.Date.AddDays(1)) return false;
        }
        return true;
    }
}
