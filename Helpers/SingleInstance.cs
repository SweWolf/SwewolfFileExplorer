// SingleInstance.cs: what happens when the program is started while it is already running.
//
// The first instance owns a named mutex and runs a named pipe server. A later start (when Settings > General > "When the Program Is
// Started Again" says so) asks that instance, over the pipe, to take over what the new start was asked to do (a folder, a file ...) and
// then ends. If the running instance cannot take it now, or does not answer, the new start simply goes on as a normal new window.
//
// The names hold the Windows user and a hash of the EXE's full path: only the same EXE in the same place counts, so a scratch build or a
// copy in another folder never hands over to the user's own copy. SWEWOLF_INSTANCE_NAME gives a test a name of its own.
//
// Protocol: one JSON line each way (camelCase).
//   -> {"cmd":"probe"}                                   <- {"status":"ready","session":"name"|null} or {"status":"busy"}
//      (the new start always probes first; if a session is on in the running window the user is asked before "open", whatever the setting)
//   -> {"cmd":"open","file":..,"folder":..,"image":..,"select":..}   <- {"status":"accepted","pid":n} or {"status":"busy"}
//   -> {"cmd":"go"}   (after the new start called AllowSetForegroundWindow: only then the window comes to the front and does the work)
// Paths are made absolute by the new start (its working folder is not the running instance's).

using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SwewolfFileExplorer.Helpers;

public static class SingleInstance
{
    public const int ModeNewWindow = 0, ModeUseRunning = 1, ModeAsk = 2;

    public enum HandOverResult { NotHandedOver, HandedOver, Canceled }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static Mutex? _mutex;

    /// <summary>The name of the mutex and the pipe: the user, and the EXE's place (or the test name).</summary>
    public static string Name
    {
        get
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            var custom = Environment.GetEnvironmentVariable("SWEWOLF_INSTANCE_NAME");
            if (!string.IsNullOrEmpty(custom)) return $"SweWolfFileExplorer.{sid}.{custom}";

            var exe = (Environment.ProcessPath ?? "").ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exe)), 0, 6);
            return $"SweWolfFileExplorer.{sid}.{hash}";
        }
    }

    private const int MaxSlots = 4;
    private static string? _pipeName;   // the pipe this instance answers on (null: not an owner)

    private static string SlotName(int slot) => slot == 1 ? Name : $"{Name}.{slot}";

    private static bool TryClaim(int slot)
    {
        try
        {
            var mutex = new Mutex(true, "Local\\" + SlotName(slot), out bool createdNew);
            if (!createdNew) { mutex.Dispose(); return false; }
            _mutex = mutex;
            _pipeName = SlotName(slot);
            return true;
        }
        catch { return false; }   // e.g. owned by an elevated instance
    }

    /// <summary>True if this is the first instance (it now owns the name for as long as the program runs).</summary>
    public static bool TryBecomeOwner() => TryClaim(1);

    /// <summary>
    /// A start that found the first window stuck (crashed and hanging, or busy) and opens a new window anyway claims the next free place, so
    /// that the start after this one finds this window instead of the stuck one (found by the user 2026-11-19: a crashed instance made
    /// every later start a new window that no other start could find).
    /// </summary>
    public static void TryBecomeSuccessor()
    {
        if (_mutex != null) return;
        for (int slot = 2; slot <= MaxSlots; slot++) if (TryClaim(slot)) return;
    }

    /// <summary>True when the last <see cref="TryHandOver"/> found no window that could take the request (all stuck, busy or unreachable).</summary>
    public static bool NoReadyWindow { get; private set; }

    // ── The new start's side ─────────────────────────────────────────────

    /// <summary>
    /// Asks the running instance to do what this start was asked to do. <paramref name="askUser"/> is called (when not null) once the running
    /// instance is known to be ready: true = use the running window, false = start a new one, null = cancel. Anything that goes wrong
    /// gives NotHandedOver: the caller then starts as usual.
    /// </summary>
    public static HandOverResult TryHandOver(StartupTarget target, Func<string?, bool?> ask)
    {
        NoReadyWindow = false;
        bool declined = false;
        for (int slot = 1; slot <= MaxSlots; slot++)
        {
            var result = TryHandOverTo(SlotName(slot), slot == 1 ? 1500 : 300, target, ask, out var state);
            if (state == SlotState.Done) return result;
            // NoServer / NotReady: stuck, busy or nobody there (a pipe that is not there looks like a time-out); the next window may be fine
            if (state == SlotState.Declined) { declined = true; break; }
        }
        NoReadyWindow = !declined;
        return HandOverResult.NotHandedOver;
    }

    private enum SlotState { Done, NoServer, NotReady, Declined }

    private static HandOverResult TryHandOverTo(string pipe, int connectMs, StartupTarget target, Func<string?, bool?> ask, out SlotState state)
    {
        state = SlotState.NoServer;
        try
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            client.Connect(connectMs);   // the owner may still be starting its server

            state = SlotState.NotReady;
            var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };   // not disposed: a hung server must not block us here
            using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);

            // First "are you ready, and is a session on?" (nothing is changed by it); the user is asked after that when needed.
            var probe = Exchange(writer, reader, new { cmd = "probe" }, 3000);
            if (Status(probe) != "ready") { FailureReason = probe == null ? "no answer to the question \"are you ready\" within 3 seconds (the running window may have crashed)" : "the running window said it is busy (see its own log line)"; return HandOverResult.NotHandedOver; }

            string? session = null;
            if (probe!.RootElement.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.String) session = s.GetString();

            var answer = ask(session);
            if (answer == null) { state = SlotState.Done; return HandOverResult.Canceled; }
            if (answer == false) { state = SlotState.Declined; return HandOverResult.NotHandedOver; }

            var reply = Exchange(writer, reader, new { cmd = "open", file = target.TabSetFile, folder = target.Folder, image = target.Image, select = target.SelectFile }, 3000);
            if (Status(reply) != "accepted")
            {
                FailureReason = reply == null ? "no answer from the running window within 3 seconds" : "the running window said it is busy (see its own log line)";
                return HandOverResult.NotHandedOver;
            }

            try
            {
                if (reply!.RootElement.TryGetProperty("pid", out var pid)) AllowSetForegroundWindow(pid.GetInt32());
            }
            catch { }

            Send(writer, "{\"cmd\":\"go\"}", 1000);
            state = SlotState.Done;
            return HandOverResult.HandedOver;
        }
        catch (Exception ex)   // no server, no answer, access denied: a normal new window
        {
            FailureReason = $"{ex.GetType().Name}: {ex.Message}";
            return HandOverResult.NotHandedOver;
        }
    }

    /// <summary>Why the last <see cref="TryHandOver"/> ended as NotHandedOver (null if it did not); App writes it to the log.</summary>
    public static string? FailureReason { get; private set; }

    private static JsonDocument? Exchange(StreamWriter writer, StreamReader reader, object message, int timeoutMs)
    {
        // Written with a time limit: a window that has crashed and hangs never reads, and a plain WriteLine would then block this start for ever
        // (found 2026-11-19 with a suspended copy: the new start never got its window).
        if (!Send(writer, JsonSerializer.Serialize(message, Json), timeoutMs)) return null;
        var read = reader.ReadLineAsync();
        if (!read.Wait(timeoutMs) || read.Result == null) return null;
        try { return JsonDocument.Parse(read.Result); } catch { return null; }
    }

    private static bool Send(StreamWriter writer, string line, int timeoutMs)
    {
        try { return writer.WriteLineAsync(line).Wait(timeoutMs); }
        catch { return false; }
    }

    private static string? Status(JsonDocument? doc) =>
        doc != null && doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;

    // ── The running instance's side ──────────────────────────────────────

    /// <summary>
    /// Starts answering other starts. <paramref name="handle"/> is called on the UI thread, after the window was brought to the front, with
    /// what the other start asked for.
    /// </summary>
    public static void StartServer(Window main, Func<string?> sessionName, Action<StartupTarget> handle)
    {
        if (_pipeName == null) return;   // not the owner: nobody sends us anything
        var dispatcher = main.Dispatcher;
        var thread = new Thread(() => ServerLoop(_pipeName, CancellationToken.None, main, dispatcher, sessionName, handle)) { IsBackground = true, Name = "Single instance pipe" };
        thread.Start();
    }

    // ── One pipe per session that is on (found by a start that names that session file) ──

    private static string? _sessionPipeFile;
    private static CancellationTokenSource? _sessionServer;

    /// <summary>The pipe of a session: the user, the EXE's place and the session file's full path (any case). Every window with a session on
    /// answers on its own, so a start that names a session file finds exactly the window that has it, however many windows are open.</summary>
    private static string SessionPipeName(string sessionFile)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sessionFile).ToLowerInvariant())), 0, 6);
        return $"{Name}.session.{hash}";
    }

    /// <summary>Called when a session starts (its file) or stops (null) in this window: starts or stops the pipe that answers for it.</summary>
    public static void SetSession(Window main, string? sessionFile, Func<string?> sessionName, Action<StartupTarget> handle)
    {
        if (string.Equals(_sessionPipeFile, sessionFile, StringComparison.OrdinalIgnoreCase)) return;

        _sessionServer?.Cancel();
        _sessionServer = null;
        _sessionPipeFile = sessionFile;
        if (sessionFile == null) return;

        var cts = new CancellationTokenSource();
        _sessionServer = cts;
        var pipe = SessionPipeName(sessionFile);
        var dispatcher = main.Dispatcher;
        new Thread(() => ServerLoop(pipe, cts.Token, main, dispatcher, sessionName, handle)) { IsBackground = true, Name = "Session pipe" }.Start();
    }

    public enum SessionHandOver { NotRunning, HandedOver, Busy }

    /// <summary>
    /// A start that names a session file: if a window of this program has that very session on, it is brought to the front (no question)
    /// and this start ends. NotRunning = no window has it (the caller opens it in a new window); Busy = a window has it but cannot come
    /// forward now (a dialog is open): the caller must not open the same session a second time.
    /// </summary>
    public static SessionHandOver TryHandOverToSession(string sessionFile)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", SessionPipeName(sessionFile), PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            try { client.Connect(400); }
            catch (Exception) { return SessionHandOver.NotRunning; }   // nobody has this session on

            var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };   // not disposed: a hung server must not block us here
            using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);

            if (Status(Exchange(writer, reader, new { cmd = "probe" }, 3000)) != "ready") return SessionHandOver.Busy;

            // Nothing to open: the window only comes to the front.
            var reply = Exchange(writer, reader, new { cmd = "open" }, 3000);
            if (Status(reply) != "accepted") return SessionHandOver.Busy;

            try { if (reply!.RootElement.TryGetProperty("pid", out var pid)) AllowSetForegroundWindow(pid.GetInt32()); } catch { }
            Send(writer, "{\"cmd\":\"go\"}", 1000);
            return SessionHandOver.HandedOver;
        }
        catch (Exception ex)
        {
            FailureReason = $"{ex.GetType().Name}: {ex.Message}";
            return SessionHandOver.Busy;   // it answered but something went wrong: do not open the session twice
        }
    }

    private static void ServerLoop(string pipeName, CancellationToken cancel, Window main, Dispatcher dispatcher, Func<string?> sessionName, Action<StartupTarget> handle)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                                                             PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                server.WaitForConnectionAsync(cancel).GetAwaiter().GetResult();
                Serve(server, main, dispatcher, sessionName, handle);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                try { LogFiles.AppendError($"Single instance pipe: {ex.Message}"); } catch { }
                Thread.Sleep(1000);   // do not spin if something is wrong with the pipe
            }
        }
    }

    private static void Serve(NamedPipeServerStream server, Window main, Dispatcher dispatcher, Func<string?> sessionName, Action<StartupTarget> handle)
    {
        using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

        var first = ReadLine(reader, 3000);
        if (first == null) return;
        var doc = JsonDocument.Parse(first);
        var cmd = doc.RootElement.TryGetProperty("cmd", out var c) ? c.GetString() : null;

        bool ready = IsReady(main, dispatcher, sessionName, out var reason, out var session);

        if (cmd == "probe")
        {
            if (!ready) LogBusy("probe", reason);
            // "session": the name of the session that is on in this window, if any: the new start asks the user before it goes into it.
            writer.WriteLine(JsonSerializer.Serialize(new { status = ready ? "ready" : "busy", session }, Json));
            // The new start may now ask the user and then send "open" on the same connection.
            first = ReadLine(reader, 60000);
            if (first == null) return;
            doc = JsonDocument.Parse(first);
            cmd = doc.RootElement.TryGetProperty("cmd", out c) ? c.GetString() : null;
            ready = IsReady(main, dispatcher, sessionName, out reason, out _);   // things may have changed while the user was asked
        }

        if (cmd != "open") return;

        if (!ready)
        {
            LogBusy("open", reason);
            writer.WriteLine(JsonSerializer.Serialize(new { status = "busy" }, Json));
            return;
        }

        string? Prop(string name) => doc.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var target = new StartupTarget(Prop("file"), Prop("folder"), Prop("image"), Prop("select"));

        writer.WriteLine(JsonSerializer.Serialize(new { status = "accepted", pid = Environment.ProcessId }, Json));

        // The new start now allows us to come to the front, and says "go" (or just closes the connection).
        ReadLine(reader, 2000);
        dispatcher.BeginInvoke(new Action(() => handle(target)));
    }

    private static string? ReadLine(StreamReader reader, int timeoutMs)
    {
        try
        {
            var read = reader.ReadLineAsync();
            return read.Wait(timeoutMs) ? read.Result : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Can the window take a request now? Asked of the UI thread itself: a frozen window never answers, and after a second that counts as "no".
    /// Not ready: the window is not loaded or not visible yet (start-up, or only the picture viewer is on screen) or a modal dialog is open
    /// (WPF's ShowDialog disables every window of the thread, so the main window's Win32 "enabled" flag says it).
    /// </summary>
    private static bool IsReady(Window main, Dispatcher dispatcher, Func<string?> sessionName, out string reason, out string? session)
    {
        session = null;
        try
        {
            var op = dispatcher.InvokeAsync(() =>
            {
                if (!main.IsLoaded) return ("the window is not loaded yet", (string?)null);
                if (!main.IsVisible) return ("the window is not shown (only the picture viewer is open)", null);
                var hwnd = new WindowInteropHelper(main).Handle;
                if (hwnd == IntPtr.Zero) return ("the window has no handle", null);
                return (IsWindowEnabled(hwnd) ? "" : "a dialog is open", sessionName());
            }, DispatcherPriority.Send);   // ahead of everything queued: a window with lots of work queued (icons, tags, thumbnails) still answers
            if (!op.Task.Wait(2000)) { reason = "the window did not answer within 2 seconds (it was busy)"; return false; }
            (reason, session) = op.Task.Result;
            return reason.Length == 0;
        }
        catch (Exception ex) { reason = ex.Message; return false; }
    }

    /// <summary>The running instance says in the log why it could not take a request (Help > Open Log File), so "it opened a new window" can be explained.</summary>
    private static void LogBusy(string command, string reason)
    {
        try { LogFiles.AppendError($"Started again: this window answered \"busy\" to \"{command}\": {reason}."); } catch { }
    }

    /// <summary>Restores the window if it is minimized and brings it to the front.</summary>
    public static void BringToFront(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero && IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE: back to the state before minimizing */);
        window.Activate();
        // The allowed-to-come-forward right can be used up; this nudge makes the window show in front at least.
        if (!window.IsActive)
        {
            window.Topmost = true;
            window.Topmost = false;
        }
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
