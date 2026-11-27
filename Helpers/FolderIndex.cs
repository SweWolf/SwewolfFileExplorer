using System.Collections.Concurrent;
using System.IO;

namespace SwewolfFileExplorer.Helpers;

// FolderIndex: the folders under one or more folders, read once in the background and kept in memory (nothing is saved), for "Find Folder"
// (the little window of the tree that filters the folders as the user types). Real folders only: no packed files, no playlists.
// History:
//   2026-11-27  First version.

/// <summary>One folder of the index. <see cref="Location"/> is the folder it lies in. <see cref="Hidden"/>: it is hidden, or lies inside a hidden folder.</summary>
public sealed record FolderEntry(string Name, string FullPath, string Location, DateTime Created, DateTime Modified, bool Hidden);

/// <summary>What the user typed in the Find Folder window. Dates are whole days: "to" includes that day. A null date is open on that side.</summary>
public sealed record FolderQuery(string Text, bool Approximate, SearchHidden Hidden,
                                 DateTime? ModifiedFrom, DateTime? ModifiedTo, DateTime? CreatedFrom, DateTime? CreatedTo,
                                 int SortColumn, bool Ascending);

public sealed record FolderQueryResult(List<FolderEntry> Rows, int Total);

public sealed class FolderIndex
{
    /// <summary>The index stops growing here (a very big disk): memory stays small, and the window says so.</summary>
    public const int MaxEntries = 1_000_000;

    private readonly object _gate = new();
    private readonly List<FolderEntry> _entries = new();
    private readonly CancellationTokenSource _cts = new();
    private long _lastChanged;
    private long _skipped;
    private bool _capped;

    /// <summary>New folders were added (about every 150 ms while it reads). Raised on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Everything is read (or it was stopped). Raised on a background thread.</summary>
    public event Action? Finished;

    public bool Running { get; private set; }
    public bool Capped { get { lock (_gate) return _capped; } }

    /// <summary>Folders that could not be read (no access ...).</summary>
    public long Skipped => Interlocked.Read(ref _skipped);

    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>A copy of what has been read so far (a few milliseconds for a few hundred thousand folders).</summary>
    public FolderEntry[] Snapshot() { lock (_gate) return _entries.ToArray(); }

    public void Stop() => _cts.Cancel();

    /// <summary>
    /// Starts reading the folders below <paramref name="roots"/> (the roots themselves are not in the index). <paramref name="excluded"/> is a list as
    /// <see cref="FileSearch.ParseExcluded"/> makes it: a full path (that folder and everything below it) or a plain name (any folder with that name).
    /// </summary>
    public void Start(IEnumerable<string> roots, IEnumerable<string> excluded)
    {
        Running = true;
        var ct = _cts.Token;
        var rootList = roots.ToList();
        var excludedList = excluded.ToList();
        Task.Run(() =>
        {
            try { Walk(rootList, excludedList, ct); }
            catch { /* a failure ends the reading; what was found stays */ }
            finally { Running = false; Changed?.Invoke(); Finished?.Invoke(); }
        });
    }

    private static bool IsPathEntry(string s) => s.IndexOfAny(new[] { '\\', '/', ':' }) >= 0;

    private static bool IsLink(FileSystemInfo e)
    {
        if ((e.Attributes & FileAttributes.ReparsePoint) == 0) return false;
        try { return e.LinkTarget != null; }
        catch { return true; }
    }

    private void Walk(List<string> roots, List<string> excluded, CancellationToken ct)
    {
        var excludedNames = new HashSet<string>(excluded.Where(x => !IsPathEntry(x)), StringComparer.OrdinalIgnoreCase);
        var excludedPaths = new HashSet<string>(excluded.Where(IsPathEntry).Select(x => x.Replace('/', '\\').TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);

        var queue = new ConcurrentQueue<(string Dir, bool Hidden)>();
        foreach (var r in roots) queue.Enqueue((r, false));
        long outstanding = roots.Count;
        bool capped = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,   // the default skips Hidden and System: the hidden choice is the user's
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        void Worker()
        {
            var found = new List<FolderEntry>();
            while (!ct.IsCancellationRequested && !Volatile.Read(ref capped))
            {
                if (!queue.TryDequeue(out var item))
                {
                    if (Interlocked.Read(ref outstanding) == 0) return;
                    Thread.Sleep(2);   // other workers are still reading folders that may hold more
                    continue;
                }

                found.Clear();
                try
                {
                    foreach (var d in new DirectoryInfo(item.Dir).EnumerateDirectories("*", options))
                    {
                        if (ct.IsCancellationRequested) break;
                        if (excludedNames.Contains(d.Name) || excludedPaths.Contains(d.FullName.TrimEnd('\\'))) continue;

                        bool hidden = item.Hidden || HiddenItems.IsHiddenByAttributes(d.Attributes);
                        found.Add(new FolderEntry(d.Name, d.FullName, item.Dir, d.CreationTime, d.LastWriteTime, hidden));
                        if (!IsLink(d))
                        {
                            Interlocked.Increment(ref outstanding);
                            queue.Enqueue((d.FullName, hidden));
                        }
                    }
                }
                catch { Interlocked.Increment(ref _skipped); }
                finally { Interlocked.Decrement(ref outstanding); }

                if (found.Count > 0)
                {
                    lock (_gate)
                    {
                        _entries.AddRange(found);
                        if (_entries.Count >= MaxEntries) { _capped = true; Volatile.Write(ref capped, true); }
                    }
                }

                long now = clock.ElapsedMilliseconds;
                long last = Interlocked.Read(ref _lastChanged);
                if (now - last >= 150 && Interlocked.CompareExchange(ref _lastChanged, now, last) == last) Changed?.Invoke();
            }
        }

        int threads = Math.Clamp(Environment.ProcessorCount, 2, 4);
        Task.WaitAll(Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(Worker, TaskCreationOptions.LongRunning)).ToArray());
    }

    /// <summary>
    /// The folders of <paramref name="all"/> that fit the query, sorted, and how many fit in all; at most <paramref name="cap"/> rows are returned.
    /// The name test is the one of the Search window (<see cref="FileSearch.NameMatcher"/>: words, "phrases", * and ?, OR).
    /// </summary>
    public static FolderQueryResult Query(FolderEntry[] all, FolderQuery q, int cap, CancellationToken ct)
    {
        var matches = FileSearch.NameMatcher(q.Text, q.Approximate, approximateWildcards: true);   // lisato* = starts with, also when approximate
        var found = new List<FolderEntry>();
        foreach (var e in all)
        {
            if (ct.IsCancellationRequested) break;
            if (q.Hidden == SearchHidden.VisibleOnly && e.Hidden) continue;
            if (q.Hidden == SearchHidden.HiddenOnly && !e.Hidden) continue;
            if (!DateFits(e.Modified, q.ModifiedFrom, q.ModifiedTo) || !DateFits(e.Created, q.CreatedFrom, q.CreatedTo)) continue;
            if (!matches(e.Name, true)) continue;
            found.Add(e);
        }

        Comparison<FolderEntry> compare = q.SortColumn switch
        {
            1 => (a, b) => string.Compare(a.Location, b.Location, StringComparison.CurrentCultureIgnoreCase),
            2 => (a, b) => a.Created.CompareTo(b.Created),
            3 => (a, b) => a.Modified.CompareTo(b.Modified),
            _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
        };
        // Equal values keep a steady order: the name, then the place.
        int Then(FolderEntry a, FolderEntry b)
        {
            int r = compare(a, b);
            if (r == 0) r = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            if (r == 0) r = string.Compare(a.Location, b.Location, StringComparison.CurrentCultureIgnoreCase);
            return q.Ascending ? r : -r;
        }
        found.Sort(Then);

        int total = found.Count;
        if (total > cap) found.RemoveRange(cap, total - cap);
        return new FolderQueryResult(found, total);
    }

    private static bool DateFits(DateTime t, DateTime? from, DateTime? to)
    {
        if (from != null && t < from.Value.Date) return false;
        if (to != null && t >= to.Value.Date.AddDays(1)) return false;
        return true;
    }
}
