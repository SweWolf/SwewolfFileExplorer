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

    public SearchCriteria Clone()
    {
        var c = (SearchCriteria)MemberwiseClone();
        c.Roots = new List<string>(Roots);
        return c;
    }

    /// <summary>A short text for the tab and the search bar: what was searched for.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Text.Trim().Length > 0) parts.Add(Text.Trim());
        if (ModifiedFrom != null || ModifiedTo != null) parts.Add("modified " + Range(ModifiedFrom, ModifiedTo));
        if (CreatedFrom != null || CreatedTo != null) parts.Add("created " + Range(CreatedFrom, CreatedTo));
        return parts.Count > 0 ? string.Join(", ", parts) : "all items";
    }

    private static string Range(DateTime? from, DateTime? to) =>
        from != null && to != null ? $"{from:d} - {to:d}" : from != null ? $"from {from:d}" : $"until {to:d}";
}

public sealed record SearchSummary(long Found, long FoldersSearched, long FoldersSkipped, bool Capped, bool Canceled);

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
                var regex = new Regex(Regex.Escape(term).Replace("\\*", ".*").Replace("\\?", "."),
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
    public static Task<SearchSummary> RunAsync(SearchCriteria criteria, Action<List<FileSystemInfo>, long> onBatch, CancellationToken ct) =>
        Task.Run(() => Run(criteria, onBatch, ct), ct);

    private static SearchSummary Run(SearchCriteria c, Action<List<FileSystemInfo>, long> onBatch, CancellationToken ct)
    {
        var matches = NameMatcher(c.Text);
        bool wantFiles = c.Kinds != SearchKinds.FoldersOnly;
        bool wantDirs  = c.Kinds != SearchKinds.FilesOnly;

        var queue = new ConcurrentQueue<string>();
        foreach (var root in c.Roots) queue.Enqueue(root);
        long outstanding = c.Roots.Count;   // folders queued or being read
        long searched = 0, skipped = 0, found = 0;
        bool capped = false;

        var batch = new List<FileSystemInfo>();
        var gate = new object();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastFlush = 0;

        void Flush(bool force)
        {
            lock (gate)
            {
                if (batch.Count == 0 && !force) return;
                if (!force && batch.Count < 200 && clock.ElapsedMilliseconds - lastFlush < 150) return;
                var send = batch; batch = new List<FileSystemInfo>();
                lastFlush = clock.ElapsedMilliseconds;
                onBatch(send, Interlocked.Read(ref searched));
            }
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,                 // the default skips Hidden and System: the hidden rules are ours
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        void Worker()
        {
            while (!ct.IsCancellationRequested && !Volatile.Read(ref capped))
            {
                if (!queue.TryDequeue(out var dir))
                {
                    if (Interlocked.Read(ref outstanding) == 0) return;
                    Thread.Sleep(2);   // other workers are still reading folders that may hold more
                    continue;
                }
                try { ReadFolder(dir); }
                catch { Interlocked.Increment(ref skipped); }
                finally { Interlocked.Decrement(ref outstanding); Interlocked.Increment(ref searched); }
                Flush(false);
            }
        }

        void ReadFolder(string dir)
        {
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
            {
                if (ct.IsCancellationRequested) return;

                bool isDir = (e.Attributes & FileAttributes.Directory) != 0;
                bool hidden = isDir ? HiddenItems.IsHiddenByAttributes(e.Attributes) : HiddenItems.IsHiddenFile(e.Attributes, e.Name);

                // A hidden folder is not entered when hidden things are not wanted: what is in it is not seen in a folder window either.
                if (isDir && c.Subfolders && !(hidden && c.Hidden == SearchHidden.VisibleOnly) && !IsLink(e))
                {
                    Interlocked.Increment(ref outstanding);
                    queue.Enqueue(e.FullName);
                }

                if (isDir ? !wantDirs : !wantFiles) continue;
                if (c.Hidden == SearchHidden.VisibleOnly && hidden) continue;
                if (c.Hidden == SearchHidden.HiddenOnly && !hidden) continue;
                if (!matches(e.Name)) continue;
                if (!DatesMatch(c, e)) continue;

                lock (gate)
                {
                    if (found >= MaxResults) { Volatile.Write(ref capped, true); return; }
                    found++;
                    batch.Add(e);
                }
            }
        }

        int threads = Math.Clamp(Environment.ProcessorCount, 2, 6);
        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(Worker, TaskCreationOptions.LongRunning)).ToArray();
        Task.WaitAll(tasks);
        Flush(true);
        return new SearchSummary(found, searched, skipped, capped, ct.IsCancellationRequested);
    }

    /// <summary>A symbolic link or junction (not followed: it could lead back up and never end). A cloud folder's reparse point is no link.</summary>
    private static bool IsLink(FileSystemInfo e)
    {
        if ((e.Attributes & FileAttributes.ReparsePoint) == 0) return false;
        try { return e.LinkTarget != null; }
        catch { return true; }
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
