using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Puts items that were just sent to the Recycle Bin back where they came from (the Undo of a delete).
///
/// Windows does not say which bin item a deleted file became, so the bin is looked at afterwards (through the shell, the way
/// Explorer's own Recycle Bin window does): an item matches when its original folder + name are the path that was deleted and it
/// was deleted at or after the moment the delete started. <b>Nothing is guessed</b>: if there is not exactly one matching item,
/// or the old path is taken again, or its folder is gone, that item is left where it is and reported. Tested with a throwaway app
/// (2026-11-13): files, a folder with a file in it, a name with å ä ö, a file with no extension, and an older bin item with the
/// same path (skipped).
///
/// <b>How an item is put back (speed, measured 2026-11-13):</b> the shell's own "undelete" verb takes about 230 ms per item (400
/// files: 94 s; firing all the calls without waiting did not help, 200 files took 42 s). So the item is moved out of the bin
/// directly, which is what the verb does underneath: the bin keeps each deleted item as <c>$R...</c> (the item itself) and
/// <c>$I...</c> (a small record with the original path) in <c>X:\$Recycle.Bin\&lt;SID&gt;</c>; the <c>$R</c> item is renamed to the
/// old path and the <c>$I</c> record deleted (200 files: 0.27 s, and the bin lists none of them afterwards). <b>Only when everything
/// checks out:</b> the item's path is really a <c>$R</c> item directly inside a <c>$Recycle.Bin</c> folder, and its <c>$I</c> record
/// names exactly the path being restored (a second, independent identification). Otherwise, or if the move fails, that item goes
/// through the shell's verb as before (slow but the shell does the bookkeeping).
///
/// Found by the first test: the bin's "date deleted" is in UTC (kind Unspecified) and has whole seconds; "undelete" onto a path that
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

            // The fast way: move the item out of the bin directly (all the checks passed).
            string? binItem = null;
            try { binItem = (string?)candidates[0].Path; } catch { /* the shell gave no path: use the verb */ }
            if (TryRestoreDirectly(binItem, key)) { restored.Add(original); continue; }

            // The slow way, through the shell.
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

    /// <summary>
    /// Moves the bin's <c>$R</c> item to <paramref name="target"/> and deletes its <c>$I</c> record, but only when the item really is
    /// a <c>$R</c> item of a Recycle Bin and its <c>$I</c> record names exactly <paramref name="target"/>. False (nothing changed)
    /// when anything is not as expected or the move fails: the caller then uses the shell's verb.
    /// </summary>
    private static bool TryRestoreDirectly(string? binItem, string target)
    {
        try
        {
            if (string.IsNullOrEmpty(binItem)) return false;
            var name   = Path.GetFileName(binItem);
            var sidDir = Path.GetDirectoryName(binItem);
            var binDir = sidDir == null ? null : Path.GetDirectoryName(sidDir);
            if (!name.StartsWith("$R", StringComparison.Ordinal) || name.Length < 3 || sidDir == null || binDir == null) return false;
            if (!string.Equals(Path.GetFileName(binDir), "$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) return false;

            // The record that goes with it must say that this item came from the path we are restoring.
            var record = Path.Combine(sidDir, "$I" + name[2..]);
            if (!File.Exists(record)) return false;
            var stored = OriginalPathIn(File.ReadAllBytes(record));
            if (stored == null || !string.Equals(stored.TrimEnd('\\', '/'), target, StringComparison.OrdinalIgnoreCase)) return false;

            bool isDir = Directory.Exists(binItem);
            if (!isDir && !File.Exists(binItem)) return false;
            if (File.Exists(target) || Directory.Exists(target)) return false;   // checked by the caller too

            if (isDir) Directory.Move(binItem, target);
            else       File.Move(binItem, target);

            try { File.Delete(record); }
            catch (Exception ex) { LogFiles.AppendError($"Undo delete: \"{target}\" is back, but the Recycle Bin record {record} could not be deleted: {ex.Message}"); }
            return true;
        }
        catch (Exception ex)
        {
            LogFiles.AppendError($"Undo delete: direct restore of \"{target}\" failed, using Windows' own restore: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The original path stored in a bin <c>$I</c> record (version 1: Windows 7 to 10, version 2: Windows 10 and 11), or null if it is not one.</summary>
    internal static string? OriginalPathIn(byte[] record)
    {
        if (record.Length < 28) return null;
        long version = BitConverter.ToInt64(record, 0);
        if (version == 2)
        {
            int chars = BitConverter.ToInt32(record, 24);   // length of the path in characters, with the ending 0
            if (chars <= 0 || 28 + (long)chars * 2 > record.Length) return null;
            return System.Text.Encoding.Unicode.GetString(record, 28, chars * 2).TrimEnd('\0');
        }
        if (version == 1)
            return System.Text.Encoding.Unicode.GetString(record, 24, record.Length - 24).TrimEnd('\0');
        return null;
    }
}
