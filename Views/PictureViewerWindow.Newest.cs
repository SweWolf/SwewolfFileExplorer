using System.IO;
using System.Windows;
using System.Windows.Threading;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

// "Show Newest Picture Automatically" (right-click menu, a check mark; never saved: the viewer always starts with it off). While it is on, the
// folder of the picture on show is watched, and when another program makes a picture there (an image generator, a scanner, a phone sync ...)
// the viewer waits until the file can be read and shows it. Turning it on shows the newest picture of the folder at once.
public partial class PictureViewerWindow
{
    private FileSystemWatcher? _newestWatcher;
    private string? _newestFolder;                  // the folder the watcher is on
    private bool _newestOn;

    /// <summary>True while "Show Newest Picture Automatically" is on. Then the viewer is a preview, not a reading: the Explorer does not
    /// mark the pictures shown as read and does not remember them as the last opened file.</summary>
    public bool NewestOn => _newestOn;
    private bool _newestBusy;                       // a newest picture is being waited for
    private string? _newestCandidate;               // the last picture the watcher reported (set on a thread-pool thread)
    private string? _newestKey;                     // name, time and size of the picture last shown, so the same one is not loaded again
    private DispatcherTimer? _newestTimer;          // events come in bursts (a file being written): act when it has been quiet for a moment
    private DispatcherTimer? _newestPoll;           // a look at the folder now and then, in case the watcher missed something (a cloud or network drive)

    private void MenuNewest_Click(object sender, RoutedEventArgs e) => SetNewest(NewestMenuItem.IsChecked);

    private void SetNewest(bool on)
    {
        _newestOn = on;

        if (!on)
        {
            StopNewestWatch();
            ShowOverlay("Newest picture: off");
            return;
        }

        _newestKey = null;
        WatchFolderOfCurrent();
        if (_newestWatcher == null)
        {
            NewestMenuItem.IsChecked = false;
            _newestOn = false;
            ShowOverlay("Cannot watch this folder");   // a playlist, or a picture inside a compressed file
            return;
        }
        ShowOverlay("Newest picture: on");
        _newestPoll ??= CreateNewestPoll();
        _newestPoll.Start();
        QueueNewest(delayMs: 1);
    }

    /// <summary>Puts the watcher on the folder of the picture on show (it changes when the viewer goes on to another folder).</summary>
    private void WatchFolderOfCurrent()
    {
        if (!_newestOn || _closed) return;

        string? folder = IsPlaylist || _files.Count == 0 ? null : Path.GetDirectoryName(CurrentPath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            DisposeWatcher();
            return;
        }
        if (_newestWatcher != null && string.Equals(folder, _newestFolder, StringComparison.OrdinalIgnoreCase)) return;

        DisposeWatcher();
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += OnNewestEvent;
            watcher.Changed += OnNewestEvent;
            watcher.Renamed += OnNewestEvent;
            watcher.EnableRaisingEvents = true;
            _newestWatcher = watcher;
            _newestFolder  = folder;
        }
        catch { /* a place that cannot be watched: the viewer works as before */ }
    }

    // Raised on a thread-pool thread. The picture named here is the one to show: no need to search the folder.
    private void OnNewestEvent(object sender, FileSystemEventArgs e)
    {
        if (_closed || !ThumbnailLoader.IsImage(e.FullPath)) return;
        _newestCandidate = e.FullPath;
        Dispatcher.BeginInvoke(() => QueueNewest(delayMs: 100));
    }

    private void QueueNewest(int delayMs)
    {
        if (_closed || !_newestOn || _newestBusy) return;   // a busy round picks up the latest candidate by itself

        _newestTimer ??= CreateNewestTimer();
        _newestTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _newestTimer.Stop();
        _newestTimer.Start();
    }

    private DispatcherTimer CreateNewestTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) => { timer.Stop(); ShowNewest(); };
        return timer;
    }

    private DispatcherTimer CreateNewestPoll()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { if (!_newestBusy && !_askingAtEnd) QueueNewest(delayMs: 1); };
        return timer;
    }

    private async void ShowNewest()
    {
        string? folder = _newestFolder;
        if (_closed || !_newestOn || folder == null || _newestBusy || _askingAtEnd) return;
        _newestBusy = true;
        try
        {
            while (!_closed && _newestOn)
            {
                // What the watcher said, else (turning it on, the look now and then) the newest picture of the folder.
                string? path = Interlocked.Exchange(ref _newestCandidate, null);
                bool fromWatcher = path != null;
                path ??= await Task.Run(() => FindNewest(folder));
                if (path == null) return;

                string? key = KeyOf(path);
                if (key == null || key == _newestKey) return;

                if (!fromWatcher && _newestKey == null && string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
                {
                    _newestKey = key;   // the picture on show already is the newest
                    return;
                }

                // The other program may still be writing the file.
                string? ready = await Task.Run(() => WaitUntilReadable(path));
                if (_closed || !_newestOn) return;
                if (ready == null) { _newestKey = key; return; }   // never became readable: not tried again until it changes
                if (_newestCandidate != null && !string.Equals(_newestCandidate, path, StringComparison.OrdinalIgnoreCase))
                    continue;   // a newer picture came meanwhile: that one, not this

                bool first = _newestKey == null;
                _newestKey = ready;
                TakeIntoList(folder, path);
                ShowCurrent(first ? "Newest picture" : null);
                if (_newestCandidate == null) return;   // else something else came while loading: go round again
            }
        }
        catch { /* a folder that went away: nothing to show */ }
        finally { _newestBusy = false; }
    }

    private static string? KeyOf(string path)
    {
        try { var f = new FileInfo(path); return f.Exists ? path + "|" + f.LastWriteTimeUtc.Ticks + "|" + f.Length : null; }
        catch { return null; }
    }

    /// <summary>The picture of the folder that was made (or written) last. Runs on a background thread.</summary>
    private static string? FindNewest(string folder)
    {
        FileInfo? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            if (!ThumbnailLoader.IsImage(file.FullName) || HiddenItems.IsHiddenFile(file.Attributes, file.Name)) continue;
            // Made last; a file that another program wrote over (it keeps its creation date) counts by its write time.
            var time = file.CreationTimeUtc > file.LastWriteTimeUtc ? file.CreationTimeUtc : file.LastWriteTimeUtc;
            if (best == null || time > bestTime) { best = file; bestTime = time; }
        }
        return best?.FullName;
    }

    /// <summary>
    /// Waits until the file can be opened for reading and has the same size twice in a row (checked every 100 ms, up to about 10 seconds).
    /// Returns its key then, or null if it never got there. Runs on a background thread.
    /// </summary>
    private static string? WaitUntilReadable(string path)
    {
        long last = -1;
        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 0 && stream.Length == last) return KeyOf(path);
                last = stream.Length;
            }
            catch (IOException) { last = -1; }
            catch (UnauthorizedAccessException) { last = -1; }
            Thread.Sleep(100);
        }
        return null;
    }

    /// <summary>Makes the list the folder as it is now (in the Explorer's order), with the new picture in it, and moves to that picture.</summary>
    private void TakeIntoList(string folder, string path)
    {
        List<string>? fresh = null;
        try { fresh = RefreshPictures?.Invoke(folder, path); } catch { }

        if (fresh != null && fresh.Count > 0)
        {
            _files.Clear();
            _files.AddRange(fresh);
        }
        int index = _files.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) { _files.Add(path); index = _files.Count - 1; }
        _index = index;
    }

    private void StopNewestWatch()
    {
        _newestOn = false;
        _newestTimer?.Stop();
        _newestPoll?.Stop();
        DisposeWatcher();
    }

    private void DisposeWatcher()
    {
        if (_newestWatcher == null) return;
        _newestWatcher.EnableRaisingEvents = false;
        _newestWatcher.Dispose();
        _newestWatcher = null;
        _newestFolder  = null;
    }
}
