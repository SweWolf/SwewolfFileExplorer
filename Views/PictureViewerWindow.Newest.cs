using System.IO;
using System.Windows;
using System.Windows.Threading;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

// "Show Newest Picture Automatically" (right-click menu, a check mark; saved as AppSettings.ViewerShowNewest). While it is on, the folder of the
// picture on show is watched, and when another program makes a picture there (an image generator, a scanner, a phone sync ...) the viewer waits
// until the file can be read and shows it. Turning it on shows the newest picture of the folder at once.
public partial class PictureViewerWindow
{
    private FileSystemWatcher? _newestWatcher;
    private string? _newestFolder;                  // the folder the watcher is on
    private bool _newestOn;
    private bool _newestBusy;                       // a newest picture is being looked for / waited for
    private bool _newestDirty;                      // something happened meanwhile: look again when done
    private string? _newestKey;                     // path + time of the newest picture last shown, so the same one is not loaded again
    private DispatcherTimer? _newestTimer;          // events come in bursts (a file being written): act when it has been quiet

    private void StartNewestFromSetting()
    {
        if (!AppSettingsStore.Current.ViewerShowNewest) return;
        NewestMenuItem.IsChecked = true;
        SetNewest(true, save: false);
    }

    private void MenuNewest_Click(object sender, RoutedEventArgs e) => SetNewest(NewestMenuItem.IsChecked, save: true);

    private void SetNewest(bool on, bool save)
    {
        _newestOn = on;
        if (save)
        {
            AppSettingsStore.Current.ViewerShowNewest = on;
            AppSettingsStore.Save(AppSettingsStore.Current);
        }

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
            ShowOverlay("Cannot watch this folder");   // a playlist, or a picture inside a compressed file
            return;
        }
        ShowOverlay("Newest picture: on");
        QueueNewest(immediately: true);
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

    // Raised on a thread-pool thread.
    private void OnNewestEvent(object sender, FileSystemEventArgs e)
    {
        if (_closed || !ThumbnailLoader.IsImage(e.FullPath)) return;
        Dispatcher.BeginInvoke(() => QueueNewest(immediately: false));
    }

    private void QueueNewest(bool immediately)
    {
        if (_closed || !_newestOn) return;
        if (_newestBusy) { _newestDirty = true; return; }

        _newestTimer ??= CreateNewestTimer();
        _newestTimer.Interval = TimeSpan.FromMilliseconds(immediately ? 1 : 500);
        _newestTimer.Stop();
        _newestTimer.Start();
    }

    private DispatcherTimer CreateNewestTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) => { timer.Stop(); ShowNewest(); };
        return timer;
    }

    private async void ShowNewest()
    {
        string? folder = _newestFolder;
        if (_closed || !_newestOn || folder == null || _newestBusy) return;
        if (_askingAtEnd) { _newestDirty = true; return; }   // the end-of-list question is open: look again after it
        _newestBusy = true;
        try
        {
            var found = await Task.Run(() => FindNewest(folder));
            if (found == null || _closed || !_newestOn) return;
            var (path, key) = found.Value;

            if (key == _newestKey) return;
            bool first = _newestKey == null;
            if (first && string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                _newestKey = key;   // the picture on show already is the newest
                return;
            }

            // The other program may still be writing the file.
            if (!await Task.Run(() => WaitUntilReadable(path)) || _closed || !_newestOn) return;

            _newestKey = key;
            TakeIntoList(folder, path);
            ShowCurrent(first ? "Newest picture" : "New picture");
        }
        catch { /* a folder that went away: nothing to show */ }
        finally
        {
            _newestBusy = false;
            if (_newestDirty) { _newestDirty = false; QueueNewest(immediately: false); }
        }
    }

    /// <summary>The picture of the folder that was made (or written) last, and a key made of its name and time. Runs on a background thread.</summary>
    private static (string Path, string Key)? FindNewest(string folder)
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
        return best == null ? null : (best.FullName, best.FullName + "|" + bestTime.Ticks);
    }

    /// <summary>True when the file can be opened for reading and has stopped growing (up to about 10 seconds). Runs on a background thread.</summary>
    private static bool WaitUntilReadable(string path)
    {
        long last = -1;
        for (int i = 0; i < 40; i++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 0 && stream.Length == last) return true;
                last = stream.Length;
            }
            catch (IOException) { last = -1; }
            catch (UnauthorizedAccessException) { last = -1; }
            Thread.Sleep(250);
        }
        return false;
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
