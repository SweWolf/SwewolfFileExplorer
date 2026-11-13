using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Puts items that were just sent to the Recycle Bin back where they came from (the Undo of a delete).
///
/// Windows does not say which bin item a deleted file became, so the bin is looked at afterwards (through the shell, the way
/// Explorer's own Recycle Bin window does): an item matches when its original folder + name are the path that was deleted and it
/// was deleted at or after the moment the delete started. <b>Nothing is guessed</b>: if there is not exactly one matching item,
/// or the old path is taken again, or its folder is gone, that item is left where it is and reported. The restore itself is the
/// shell's own "undelete" verb, which also takes care of the bin's bookkeeping. Tested with a throwaway app (2026-11-13): files,
/// a folder with a file in it, a name with å ä ö, a file with no extension, and an older bin item with the same path (skipped).
///
/// Found by that test: the bin's "date deleted" is in UTC (kind Unspecified) and has whole seconds; "undelete" onto a path that
/// exists shows a Windows question, so that case is checked here first and never reaches the shell. Reading the bin costs about 5 ms
/// per item in it (a bin with thousands of items takes seconds), so it runs on a thread of its own.
/// </summary>
public static class RecycleBinRestore
{
    public sealed record Result(List<string> Restored, List<(string Path, string Reason)> Failed);

    public static Task<Result> RestoreAsync(IReadOnlyList<string> paths, DateTime deletedFromUtc)
    {
        var tcs = new TaskCompletionSource<Result>();
        // The shell's automation objects want a single-threaded apartment.
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(Restore(paths, deletedFromUtc)); }
            catch (Exception ex)
            {
                LogFiles.AppendError($"Undo delete failed: {ex.GetType().Name}: {ex.Message}");
                tcs.SetResult(new Result(new(), paths.Select(p => (p, "the Recycle Bin could not be read: " + ex.Message)).ToList()));
            }
        }) { IsBackground = true, Name = "RecycleBinRestore" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static Result Restore(IReadOnlyList<string> paths, DateTime deletedFromUtc)
    {
        var restored = new List<string>();
        var failed   = new List<(string Path, string Reason)>();

        // The bin keeps whole seconds: an item deleted after the start is never stamped before the start's own second.
        var floor = new DateTime(deletedFromUtc.Ticks - deletedFromUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        var targets = paths.ToDictionary(p => p.TrimEnd('\\', '/'), p => p, StringComparer.OrdinalIgnoreCase);
        var parents = new HashSet<string>(targets.Keys.Select(p => Path.GetDirectoryName(p) ?? ""), StringComparer.OrdinalIgnoreCase);

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic bin   = shell.NameSpace(10);   // ssfBITBUCKET

        var found = new Dictionary<string, List<dynamic>>(StringComparer.OrdinalIgnoreCase);
        foreach (dynamic item in bin.Items())
        {
            string from = item.ExtendedProperty("System.Recycle.DeletedFrom") ?? "";
            if (!parents.Contains(from.TrimEnd('\\', '/'))) continue;

            string name = item.ExtendedProperty("System.FileName") ?? "";
            if (name.Length == 0) continue;
            var full = Path.Combine(from, name);
            if (!targets.ContainsKey(full)) continue;

            // Deleted before this delete started: an older item with the same path, not ours.
            object? stamp = item.ExtendedProperty("System.Recycle.DateDeleted");
            if (stamp is DateTime d && DateTime.SpecifyKind(d, DateTimeKind.Utc) < floor) continue;

            if (!found.TryGetValue(full, out var list)) found[full] = list = new List<dynamic>();
            list.Add(item);
        }

        foreach (var (key, original) in targets)
        {
            found.TryGetValue(key, out var candidates);
            if (candidates == null || candidates.Count == 0)
            { failed.Add((original, "it is not in the Recycle Bin (it may have been emptied, or the item was deleted for good)")); continue; }
            if (candidates.Count > 1)
            { failed.Add((original, "more than one item in the Recycle Bin matches, so it is left there")); continue; }
            if (File.Exists(key) || Directory.Exists(key))
            { failed.Add((original, "something with this name exists again")); continue; }
            var parent = Path.GetDirectoryName(key);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            { failed.Add((original, "the folder it was in is gone")); continue; }

            try { candidates[0].InvokeVerb("undelete"); }
            catch (Exception ex) { failed.Add((original, "Windows could not restore it: " + ex.Message)); continue; }

            // The shell does it in its own time: wait until the item is there (a big folder takes a moment).
            bool there = false;
            for (int i = 0; i < 200 && !there; i++)   // up to ten seconds
            {
                there = File.Exists(key) || Directory.Exists(key);
                if (!there) Thread.Sleep(50);
            }
            if (there) restored.Add(original);
            else failed.Add((original, "Windows did not put it back"));
        }

        return new Result(restored, failed);
    }
}
