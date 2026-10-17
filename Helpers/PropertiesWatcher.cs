using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Windows does not tell whether the user clicked OK (or Apply) in the Properties window: the window opens on its own and
/// <c>ShellExecuteEx</c> returns at once. So this looks at the result: once a second it compares the items with how they were
/// when the window was opened (date modified, size, attributes, still there), and reports what changed, so the list can
/// refresh those rows (an edited track number, a new name, the Hidden attribute ...). Cancel changes nothing, so nothing is reported.
///
/// It stops when the Properties window has closed. The window of one file belongs to this program and is found by its class
/// (#32770, not by its title, which depends on the language of Windows); if it is not found (the window of several files)
/// it keeps looking for a few minutes. Used on the UI thread.
/// </summary>
public sealed class PropertiesWatcher
{
    private readonly record struct Snapshot(bool Exists, bool IsDirectory, DateTime Written, long Length, FileAttributes Attributes);

    private readonly Dictionary<string, Snapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<IntPtr> _windowsBefore;
    private readonly Action<IReadOnlyList<string>> _changed;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DateTime _started = DateTime.UtcNow;

    private IntPtr _dialog;
    private int _closedTicks;

    private static readonly TimeSpan FindDialogFor = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan GiveUpAfter   = TimeSpan.FromMinutes(5);

    /// <summary>Call before the Properties window is opened.</summary>
    public PropertiesWatcher(IEnumerable<string> paths, Action<IReadOnlyList<string>> changed)
    {
        _changed = changed;
        foreach (var path in paths) _snapshots[path] = Take(path);
        _windowsBefore = OwnDialogs().ToHashSet();
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Call after the Properties window was opened.</summary>
    public void Start() => _timer.Start();

    private void Tick()
    {
        var changed = new List<string>();
        foreach (var path in _snapshots.Keys.ToList())
        {
            var now = Take(path);
            if (now == _snapshots[path]) continue;
            _snapshots[path] = now;
            changed.Add(path);
        }
        if (changed.Count > 0) _changed(changed);

        var elapsed = DateTime.UtcNow - _started;
        if (_dialog == IntPtr.Zero)
        {
            _dialog = OwnDialogs().FirstOrDefault(h => !_windowsBefore.Contains(h));
            if (_dialog == IntPtr.Zero && elapsed > GiveUpAfter) _timer.Stop();   // never found: do not watch for ever
        }
        else if (!IsWindow(_dialog))
        {
            // The values are written about when the window closes: look once more before stopping.
            if (++_closedTicks >= 2) _timer.Stop();
        }
    }

    private static Snapshot Take(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var d = new DirectoryInfo(path);
                return new Snapshot(true, true, d.LastWriteTimeUtc, 0, d.Attributes);
            }

            var f = new FileInfo(path);
            return f.Exists ? new Snapshot(true, false, f.LastWriteTimeUtc, f.Length, f.Attributes) : default;
        }
        catch
        {
            return default;
        }
    }

    // ── The dialogs of this program (class #32770) ───────────────────────────

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private static List<IntPtr> OwnDialogs()
    {
        var found = new List<IntPtr>();
        uint me = (uint)Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != me) return true;

            var name = new StringBuilder(32);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() == "#32770") found.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        return found;
    }
}
