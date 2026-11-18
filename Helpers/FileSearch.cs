using System.Collections.Concurrent;
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

public sealed record SearchSummary(long Found, long FoldersSearched, long FoldersSkipped, bool Capped, bool Canceled, long ExcludedSkipped = 0);

public static class FileSearch
{
    /// <summary>At most this many results: the list is a list of view models, and a search that finds more than this needs narrowing.</summary>
    public const int MaxResults = 50_000;

    /// <summary>One name test for a search text; true for an empty text.</summary>
    public static Func<string, bool> NameMatcher(string text)
    {
        var terms = new List<Func<string, bool>>();
        foreach (var term in SplitTerms(text))
        {
            if (term.IndexOfAny(new[] { '*', '?' }) >= 0)
            {
                // With a wildcard the term must fit the WHOLE name, as in Windows: *.ico ends in .ico (it must not find "X.IconView.cs"),
                // alpha* starts with alpha, *alpha* has it anywhere. Words without a wildcard are still "contained in the name".
                var regex = new Regex("^" + Regex.Escape(term).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                                      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                terms.Add(name => regex.IsMatch(name));
            }
            else
            {
                var t = term;
                terms.Add(name => name.Contains(t, StringComparison.OrdinalIgnoreCase));
            }
        }
        return name => { foreach (var t in terms) if (!t(name)) return false; return true; };
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

    private static List<string> SplitTerms(string text)
    {
        var terms = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (char ch in text ?? "")
        {
            if (ch == '"') { quoted = !quoted; if (!quoted) Flush(); continue; }
            if (char.IsWhiteSpace(ch) && !quoted) { Flush(); continue; }
            sb.Append(ch);
        }
        Flush();
        return terms;

        void Flush() { if (sb.Length > 0) terms.Add(sb.ToString()); sb.Clear(); }
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
        var matches = NameMatcher(c.Text);
        bool wantFiles = c.Kinds != SearchKinds.FoldersOnly;
        bool wantDirs  = c.Kinds != SearchKinds.FilesOnly;

        var queue = new ConcurrentQueue<string>();
        foreach (var root in c.Roots) queue.Enqueue(root);
        long outstanding = c.Roots.Count;   // folders queued or being read
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
                if (!matches(e.Name)) continue;
                if (!DatesMatch(c, e)) continue;

                lock (gate)
                {
                    if (found >= MaxResults) { Volatile.Write(ref capped, true); return entries; }
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
        return new SearchSummary(found, searched, skipped, capped, ct.IsCancellationRequested, Interlocked.Read(ref excludedSkipped));
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
