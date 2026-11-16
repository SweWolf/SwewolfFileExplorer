using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Paste, Cut + Paste and drops into a folder that needs administrator rights (Program Files ...): the normal transfer says "access
/// denied" for those items (<see cref="TransferResult.AccessDenied"/>); after the user agrees, this runs the same transfer in a second
/// copy of the program started with <c>runas</c> (Windows shows its own permission question, as Windows Explorer does with its
/// "Continue" button). The helper has no window: it reads a job file, runs <see cref="FileTransfer"/>, and writes its progress and its
/// result to files in a temporary folder, which the first copy reads (so the usual progress window and Cancel work).
///
/// Rules of the helper: it never overwrites (a taken name is Keep Both; the user's earlier answer is not carried over), and the
/// Undo of such a step is not offered (the undo would need the administrator too). The job file is read at once and the
/// temporary folder is removed when the transfer is over.
/// </summary>
public static class ElevatedTransfer
{
    public const string Argument = "--elevated-transfer";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public sealed class Job
    {
        public List<string> Sources { get; set; } = new();
        public string Destination { get; set; } = "";
        public bool Move { get; set; }
        /// <summary>"transfer" (copy / move into Destination) or "delete" (Sources are deleted; Permanent = not to the Recycle Bin).</summary>
        public string Kind { get; set; } = "transfer";
        public bool Permanent { get; set; }
        public string ProgressFile { get; set; } = "";
        public string ResultFile { get; set; } = "";
        public string CancelFile { get; set; } = "";
    }

    public sealed class ProgressDto
    {
        public string Name { get; set; } = "";
        public int Index { get; set; }
        public int Total { get; set; }
        public long Done { get; set; }
        public long Bytes { get; set; }
        public bool Preparing { get; set; }
    }

    public sealed class Pair { public string From { get; set; } = ""; public string To { get; set; } = ""; }
    public sealed class Fail { public string Name { get; set; } = ""; public string Reason { get; set; } = ""; }
    public sealed class Created { public string Path { get; set; } = ""; public bool IsDirectory { get; set; } }

    public sealed class ResultDto
    {
        public List<Pair> Done { get; set; } = new();
        public List<Fail> Failed { get; set; } = new();
        public bool Canceled { get; set; }
        public int Total { get; set; }
        public int Skipped { get; set; }
        public List<Pair> Renamed { get; set; } = new();
        public List<string> Identical { get; set; } = new();
        public List<Created> CreatedItems { get; set; } = new();
        public string? Error { get; set; }
    }

    // ── The helper (second copy of the program, elevated) ───────────────────────────────────────

    /// <summary>True when the command line asks for the helper (the first argument).</summary>
    public static bool IsHelperStart(string[] args) => args.Length >= 2 && args[0] == Argument;

    /// <summary>Runs the job named by <paramref name="args"/>[1] and writes the result. Called from <c>App.OnStartup</c>; no window is made.</summary>
    public static void RunHelper(string[] args)
    {
        // Sign of life first, then everything that goes wrong is written down where the first copy looks: a helper that dies silently
        // would leave the first copy waiting for nothing.
        var dir = Path.GetDirectoryName(args[1]) ?? "";
        void Note(string text) { try { File.AppendAllText(Path.Combine(dir, "helper.log"), $"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}"); } catch { } }
        Note($"started as {Environment.UserName}, elevated={IsElevated()}");

        Job job;
        try { job = JsonSerializer.Deserialize<Job>(File.ReadAllText(args[1]), Json)!; }
        catch (Exception ex)
        {
            Note($"the job file could not be read: {ex.GetType().Name}: {ex.Message}");
            try { File.WriteAllText(Path.Combine(dir, "result.json"), JsonSerializer.Serialize(new ResultDto { Error = "the administrator helper could not read its job (" + ex.Message + ")" }, Json)); } catch { }
            return;
        }

        var result = new ResultDto();
        try
        {
            var progress = new TransferProgress();
            using var cts = new CancellationTokenSource();
            using var timer = new Timer(_ =>
            {
                try
                {
                    if (File.Exists(job.CancelFile)) cts.Cancel();
                    var dto = new ProgressDto
                    {
                        Name = progress.CurrentName, Index = progress.FileIndex, Total = progress.FileTotal,
                        Done = progress.BytesDone, Bytes = progress.BytesTotal, Preparing = progress.Preparing,
                    };
                    File.WriteAllText(job.ProgressFile, JsonSerializer.Serialize(dto, Json));
                }
                catch { /* the other side may be reading it: the next tick writes again */ }
            }, null, 0, 250);

            TransferResult r;
            if (job.Kind == "delete")
            {
                var items = job.Sources.Select(s => new FileDelete.Item(s, Path.GetFileName(s.TrimEnd('\\', '/')), Directory.Exists(s))).ToList();
                r = FileDelete.RunAsync(items, job.Permanent, progress, cts.Token).GetAwaiter().GetResult();
            }
            else if (job.Kind == "rename")
            {
                // Sources[0] is renamed to Destination (a full path). Never replaces: Move fails when the name is taken.
                var from = job.Sources[0];
                try
                {
                    if (Directory.Exists(from)) Directory.Move(from, job.Destination); else File.Move(from, job.Destination);
                    r = new TransferResult(new() { (from, job.Destination) }, new(), false, 1);
                }
                catch (Exception ex)
                {
                    Note($"rename failed: {ex.GetType().Name}: {ex.Message}");
                    r = new TransferResult(new(), new() { (Path.GetFileName(from), ex.Message) }, false, 1);
                }
            }
            else
            {
                var policy = new ConflictPolicy(_ => (ConflictChoice.KeepBoth, true));   // never overwrite
                r = FileTransfer.RunAsync(job.Sources, job.Destination, job.Move, progress, cts.Token, policy).GetAwaiter().GetResult();
            }

            result.Done = r.Done.Select(d => new Pair { From = d.From, To = d.To }).ToList();
            result.Failed = r.Failed.Select(f => new Fail { Name = f.Name, Reason = f.Reason }).ToList();
            result.Canceled = r.Canceled;
            result.Total = r.Total;
            result.Skipped = r.Skipped;
            result.Renamed = r.RenamedOld.Select(d => new Pair { From = d.From, To = d.To }).ToList();
            result.Identical = r.IdenticalSkipped;
            result.CreatedItems = (r.CreatedItems ?? new()).Select(c => new Created { Path = c.Path, IsDirectory = c.IsDirectory }).ToList();
        }
        catch (Exception ex) { result.Error = ex.Message; Note($"the transfer failed: {ex}"); }
        Note($"done: {result.Done.Count} done, {result.Failed.Count} failed, error={result.Error}");

        // Written under another name and moved into place, so the other side never reads half of it.
        try
        {
            var temp = job.ResultFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(result, Json));
            File.Move(temp, job.ResultFile, true);
        }
        catch { /* nobody to tell */ }
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ── The first copy ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the transfer of <paramref name="sources"/> in an elevated helper. Windows asks for permission first; if the user says no,
    /// the result is "canceled" with nothing done (and <paramref name="declined"/> is set). Progress and Cancel are passed on.
    /// </summary>
    public static async Task<(TransferResult Result, bool Declined)> RunAsync(IReadOnlyList<string> sources, string destination, bool move,
                                                                              TransferProgress progress, CancellationToken cancel,
                                                                              string kind = "transfer", bool permanent = false)
    {
        // The window that has the keyboard now: Windows' permission prompt and the helper take the foreground, and it is not given back
        // by itself (found by the user: another program came to the front after every elevated step).
        var foreground = GetForegroundWindow();
        try { return await RunCoreAsync(sources, destination, move, progress, cancel, kind, permanent); }
        finally { RestoreForeground(foreground); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    /// <summary>Brings <paramref name="window"/> (one of ours) back to the front once the dialogs of the step are closed.</summary>
    private static void RestoreForeground(IntPtr window)
    {
        try
        {
            GetWindowThreadProcessId(window, out var pid);
            if (window == IntPtr.Zero || pid != (uint)Environment.ProcessId) return;   // the front window was not ours: leave it
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (!IsWindow(window)) return;
                // Windows only lets the program that has the foreground change it; a tap on Alt makes this program count as having had input.
                keybd_event(0x12, 0, 0, UIntPtr.Zero);
                keybd_event(0x12, 0, 2, UIntPtr.Zero);
                SetForegroundWindow(window);
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch { }
    }

    private static async Task<(TransferResult Result, bool Declined)> RunCoreAsync(IReadOnlyList<string> sources, string destination, bool move,
                                                                                   TransferProgress progress, CancellationToken cancel,
                                                                                   string kind, bool permanent)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SwewolfFileExplorer-elevated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var job = new Job
        {
            Sources = sources.ToList(), Destination = destination, Move = move, Kind = kind, Permanent = permanent,
            ProgressFile = Path.Combine(dir, "progress.json"),
            ResultFile = Path.Combine(dir, "result.json"),
            CancelFile = Path.Combine(dir, "cancel"),
        };
        var jobFile = Path.Combine(dir, "job.json");
        await File.WriteAllTextAsync(jobFile, JsonSerializer.Serialize(job, Json));

        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The program's own path is not known.");
            Process? process;
            try
            {
                // On another thread: Windows' permission question blocks the caller until it is answered, and the window must not freeze meanwhile.
                process = await Task.Run(() => Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = $"{Argument} \"{jobFile}\"",
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                }));
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // the user said no to Windows' question
            {
                return (new TransferResult(new(), new(), true, sources.Count), true);
            }
            catch (Exception ex)   // the helper could not be started at all (blocked by policy, a missing file ...): say why, never crash
            {
                try { LogFiles.AppendError($"The administrator helper could not be started ({exe}): {ex.GetType().Name}: {ex.Message}"); } catch { }
                var failed = sources.Select(s => (Path.GetFileName(s.TrimEnd('\\', '/')), $"the administrator helper could not be started: {ex.Message}")).ToList();
                return (new TransferResult(new(), failed, false, sources.Count), false);
            }

            // Wait for the result. Never forever: the helper must show a sign of life (its own log file appears at once) within 30 seconds,
            // and after Cancel it gets 15 seconds to answer. A process that has ended without a result also ends the wait.
            var started = DateTime.UtcNow;
            DateTime? cancelSentAt = null;
            string helperLog = Path.Combine(dir, "helper.log");
            string? gaveUp = null;
            while (!File.Exists(job.ResultFile))
            {
                if (cancel.IsCancellationRequested && cancelSentAt == null)
                {
                    cancelSentAt = DateTime.UtcNow;
                    try { File.WriteAllText(job.CancelFile, ""); } catch { }
                }
                ReadProgress(job.ProgressFile, progress);

                bool exited = false;
                try { exited = process?.HasExited ?? false; } catch { /* no access to ask an elevated process: the time limits below decide */ }
                if (exited)
                {
                    await Task.Delay(500);   // a moment for the result file to appear
                    if (!File.Exists(job.ResultFile)) gaveUp = "the administrator helper ended without a result";
                    break;
                }

                if (!File.Exists(helperLog) && (DateTime.UtcNow - started).TotalSeconds > 30)
                { gaveUp = "the administrator helper did not start working (Windows or security software may have blocked it)"; break; }
                if (cancelSentAt is { } at && (DateTime.UtcNow - at).TotalSeconds > 15)
                { gaveUp = "the administrator helper did not answer after Cancel"; break; }
                await Task.Delay(150);
            }

            ResultDto? dto = null;
            if (gaveUp == null)
                try { dto = JsonSerializer.Deserialize<ResultDto>(await File.ReadAllTextAsync(job.ResultFile), Json); } catch { }
            try { process?.Dispose(); } catch { }

            string helperText = "";
            try { if (File.Exists(helperLog)) helperText = File.ReadAllText(helperLog).Trim(); } catch { }

            if (gaveUp != null || dto == null || dto.Error != null)
            {
                var why = gaveUp ?? dto?.Error ?? "the helper ended without a result";
                try { LogFiles.AppendError($"The administrator helper failed: {why}. Helper log: {(helperText.Length > 0 ? helperText.Replace(Environment.NewLine, " | ") : "(none)")}"); } catch { }
                var failed = sources.Select(s => (Path.GetFileName(s.TrimEnd('\\', '/')), why)).ToList();
                return (new TransferResult(new(), failed, false, sources.Count), false);
            }

            var result = new TransferResult(
                dto.Done.Select(d => (d.From, d.To)).ToList(),
                dto.Failed.Select(f => (f.Name, f.Reason)).ToList(),
                dto.Canceled, dto.Total, dto.Skipped,
                dto.Renamed.Select(d => (d.From, d.To)).ToList(),
                dto.Identical,
                dto.CreatedItems.Select(c => (c.Path, c.IsDirectory)).ToList());
            return (result, false);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* left in %TEMP% */ }
        }
    }

    private static void ReadProgress(string file, TransferProgress progress)
    {
        try
        {
            if (!File.Exists(file)) return;
            var p = JsonSerializer.Deserialize<ProgressDto>(File.ReadAllText(file), Json);
            if (p == null) return;
            progress.CurrentName = p.Name;
            progress.FileIndex = p.Index;
            progress.FileTotal = Math.Max(p.Total, 1);
            progress.BytesTotal = p.Bytes;
            progress.BytesDone = p.Done;
            progress.Preparing = p.Preparing;
        }
        catch { /* half written: the next read gets it */ }
    }
}
