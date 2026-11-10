using System.Collections.Concurrent;
using System.Windows.Media;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// One long-lived STA thread that looks up the icons of Internet shortcuts (.url). The shell's icon handler for them is
/// apartment-threaded: from a thread-pool (MTA) thread it gives the plain page icon instead of the default browser's.
/// </summary>
internal static class UrlIconThread
{
    private static readonly BlockingCollection<Action> Queue = new();
    private static Thread? _thread;
    private static readonly object Gate = new();

    public static ImageSource? Run(Func<ImageSource?> work)
    {
        lock (Gate)
        {
            if (_thread == null)
            {
                _thread = new Thread(() => { foreach (var job in Queue.GetConsumingEnumerable()) job(); })
                { IsBackground = true, Name = "UrlIcons" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }

        ImageSource? result = null;
        using var done = new ManualResetEventSlim();
        Queue.Add(() =>
        {
            try { result = work(); } catch { }
            finally { done.Set(); }
        });
        done.Wait();
        return result;
    }
}
