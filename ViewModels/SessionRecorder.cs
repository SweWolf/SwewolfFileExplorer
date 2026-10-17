using System.IO;
using System.Windows.Threading;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.ViewModels;

/// <summary>
/// Remembers the open tabs of a session all the time, so a power failure or a crash loses (almost) nothing: the file is
/// written soon after a tab is opened or closed, another folder is shown or another tab becomes the active one, and
/// the selected file is written only after it has stayed selected for a few seconds (holding the arrow key in a folder
/// of thousands of files must not write thousands of times).
///
/// The tabs are read on the UI thread (cheap); writing the file happens on a background thread, one at a time, the safe
/// way (<see cref="WorkspaceStore.Write"/>). Never saves a session with no tabs (the last tab closing ends the program:
/// the session stays as it was just before).
/// </summary>
public sealed class SessionRecorder
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _structureTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _selectionTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly object _writeLock = new();

    /// <summary>The session file being kept up to date; null when no session is on.</summary>
    public string? FilePath { get; private set; }

    public bool IsActive => FilePath != null;

    /// <summary>The name of the session (the file name without .json).</summary>
    public string? Name => FilePath == null ? null : Path.GetFileNameWithoutExtension(FilePath);

    /// <summary>Raised when a session was started or stopped (the window shows its name in the title).</summary>
    public event Action? Changed;

    public SessionRecorder(MainViewModel vm)
    {
        _vm = vm;
        _structureTimer.Tick += (_, _) => Save();
        _selectionTimer.Tick += (_, _) => Save();
    }

    /// <summary>Starts keeping <paramref name="file"/> up to date, and writes the tabs as they are now.</summary>
    public void Start(string file)
    {
        FilePath = file;
        Save(background: false);
        Changed?.Invoke();
    }

    /// <summary>Stops saving. The file stays as it is.</summary>
    public void Stop()
    {
        _structureTimer.Stop();
        _selectionTimer.Stop();
        FilePath = null;
        Changed?.Invoke();
    }

    /// <summary>Tabs, folders, roots or the active tab changed: save in half a second (changes in a burst are saved once).</summary>
    public void TouchStructure()
    {
        if (!IsActive) return;
        _structureTimer.Stop();
        _structureTimer.Start();
    }

    /// <summary>A file was selected: save when it has stayed selected for a few seconds.</summary>
    public void TouchSelection()
    {
        if (!IsActive) return;
        _selectionTimer.Stop();
        _selectionTimer.Start();
    }

    /// <summary>Writes what is pending now and waits for it (the window is closing).</summary>
    public void Flush()
    {
        if (IsActive) Save(background: false);
    }

    private void Save(bool background = true)
    {
        _structureTimer.Stop();
        _selectionTimer.Stop();

        var file = FilePath;
        if (file == null) return;

        var data = _vm.CaptureState();
        if (data.Tabs.Count == 0) return;   // never "no tabs"

        // One write at a time; an older state never overwrites a newer one (background tasks may run out of order).
        int number = ++_saveNumber;
        void Write()
        {
            lock (_writeLock)
            {
                if (number < _writtenNumber) return;
                _writtenNumber = number;
                WorkspaceStore.Write(file, StoreKind.Session, data);
            }
        }

        if (background) Task.Run(Write);
        else            Write();
    }

    private int _saveNumber;
    private int _writtenNumber;
}
