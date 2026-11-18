using System.IO;
using System.Text;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// search.log (Settings > Troubleshooting > Log Searches, off by default): what the last search did, folder by folder, to find out why a
/// search is slow. The file is made new for every search (it can be several MB for a big drive): what was asked, one line per folder
/// ("milliseconds&lt;TAB&gt;entries&lt;TAB&gt;path", in the order the folders were finished), then the 25 slowest folders and the totals.
/// Find it with Help > Open Log Folder. Never throws: a log must not stop a search.
/// </summary>
internal sealed class SearchLog : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();
    private readonly List<(string Path, long Ms, int Entries)> _all = new();

    internal static string File_ => Path.Combine(LogFiles.LogFolder, "search.log");

    private SearchLog(StreamWriter? writer) => _writer = writer;

    /// <summary>Starts a log for this search, or returns null when the setting is off (or the file cannot be made).</summary>
    public static SearchLog? Start(SearchCriteria c)
    {
        if (!AppSettingsStore.Current.LogSearches) return null;
        try
        {
            Directory.CreateDirectory(LogFiles.LogFolder);
            var w = new StreamWriter(File_, append: false, new UTF8Encoding(true)) { NewLine = "\r\n" };
            w.WriteLine($"Search started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            w.WriteLine($"Look in: {string.Join("; ", c.Roots)}");
            w.WriteLine($"Name: {c.Text}   Subfolders: {c.Subfolders}   Kinds: {c.Kinds}   Hidden: {c.Hidden}");
            if (c.ExcludedFolders.Count > 0) w.WriteLine($"Excluded folders: {string.Join(" | ", c.ExcludedFolders)}");
            w.WriteLine($"Modified:{c.ModifiedFrom:d} - {c.ModifiedTo:d}   Created: {c.CreatedFrom:d} - {c.CreatedTo:d}");
            w.WriteLine("milliseconds\tentries\tfolder");
            return new SearchLog(w);
        }
        catch { return null; }
    }

    public void Folder(string path, long ms, int entries)
    {
        try
        {
            lock (_gate)
            {
                _all.Add((path, ms, entries));
                _writer?.WriteLine($"{ms}\t{(entries < 0 ? "UNREADABLE" : entries.ToString())}\t{path}");
            }
        }
        catch { }
    }

    public void Finish(SearchSummary s, TimeSpan took)
    {
        try
        {
            lock (_gate)
            {
                if (_writer == null) return;
                _writer.WriteLine();
                var bad = _all.Where(f => f.Entries < 0).ToList();
                if (bad.Count > 0)
                {
                    _writer.WriteLine("Folders that could not be read:");
                    foreach (var f in bad) _writer.WriteLine(f.Path);
                    _writer.WriteLine();
                }
                _writer.WriteLine("The 25 slowest folders:");
                foreach (var f in _all.OrderByDescending(f => f.Ms).Take(25))
                    _writer.WriteLine($"{f.Ms}\t{f.Entries}\t{f.Path}");
                _writer.WriteLine();
                _writer.WriteLine($"{(s.Canceled ? "Canceled" : s.StoppedAtLimit ? "Stopped at the user's limit" : s.Capped ? "Stopped at the built-in limit" : "Finished")}: {s.Found:N0} found, {s.FoldersSearched:N0} folders, " +
                                  $"{s.FoldersSkipped:N0} could not be read, {s.ExcludedSkipped:N0} excluded folders skipped, {took.TotalSeconds:0.0} s in all, {_all.Sum(f => f.Ms) / 1000.0:0.0} s added up over all threads.");
            }
        }
        catch { }
    }

    public void Dispose()
    {
        try { lock (_gate) _writer?.Dispose(); } catch { }
    }
}
