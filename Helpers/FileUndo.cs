using System.IO;
using Microsoft.VisualBasic.FileIO;
using SwewolfFileExplorer.ViewModels;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// What an Undo did: the items that are back where they were (the window selects them), what could not be undone (name, reason),
/// what is left to undo (the entry stays for another try), and the folders whose tree nodes must be read again.
/// </summary>
public sealed record UndoReport(List<string> Restored, List<(string Name, string Reason)> Problems, UndoEntry? Remaining, List<string> TouchedFolders);

/// <summary>One thing the user did to files, and how to take it back. Only ever one of these exists (<see cref="FileUndo"/>).</summary>
public abstract class UndoEntry
{
    /// <summary>The text of the Undo button, e.g. "Undo Delete (3 items)".</summary>
    public abstract string Text { get; }

    /// <summary>The tooltip: what will be put back, and the shortcut.</summary>
    public abstract string Tip { get; }

    /// <summary>True when the work shows a progress window of its own (the window then does not set the wait cursor).</summary>
    public virtual bool ShowsOwnProgress => false;

    /// <summary>
    /// Takes the step back. <b>Everything is checked first</b>: an item that is gone, changed, or whose place is taken is not touched
    /// and is reported; nothing is ever overwritten or deleted on a guess.
    /// </summary>
    public abstract Task<UndoReport> UndoAsync();

    protected static string Label(string verb, IReadOnlyList<string> names, string itemWord = "items")
    {
        if (names.Count == 1)
        {
            var n = names[0];
            if (n.Length > 28) n = n[..25] + "...";
            return $"Undo {verb}: {n}";
        }
        return $"Undo {verb} ({names.Count} {itemWord})";
    }

    protected static string TipOf(string what, IReadOnlyList<string> names)
    {
        var lines = names.Take(10).Select(n => "  " + n).ToList();
        if (names.Count > 10) lines.Add($"  ... and {names.Count - 10} more");
        return what + "\n" + string.Join("\n", lines) + "\n(Ctrl+Z)";
    }

    protected static string? ParentOf(string path) => Path.GetDirectoryName(path.TrimEnd('\\', '/'));
}

/// <summary>
/// The one step that can be undone: the last thing done to files in the program (delete, rename, move, copy, new folder / file).
/// It is a single slot for the whole program, not one per tab or folder, and it is kept in memory only: every new file operation
/// replaces it, and an operation that cannot be undone empties it, so the button never describes an older step than the last one.
/// </summary>
public static class FileUndo
{
    public static UndoEntry? Current { get; private set; }

    /// <summary>True while an undo is running (a second one waits for the user to ask again).</summary>
    public static bool Busy { get; set; }

    public static event Action? Changed;

    public static void Set(UndoEntry? entry)
    {
        Current = entry;
        Changed?.Invoke();
    }

    public static void Clear() => Set(null);
}

/// <summary>A delete to the Recycle Bin: the items are looked up in the bin and put back (<see cref="RecycleBinRestore"/>).</summary>
public sealed class DeleteUndo(List<string> paths, DateTime startedUtc) : UndoEntry
{
    private readonly List<string> _paths = paths;
    private IReadOnlyList<string> Names => _paths.Select(p => Path.GetFileName(p.TrimEnd('\\', '/'))).ToList();

    public override string Text => Label("Delete", Names);
    public override string Tip  => TipOf("Put back from the Recycle Bin:", Names);

    public override async Task<UndoReport> UndoAsync()
    {
        var result = await RecycleBinRestore.RestoreAsync(_paths, startedUtc);
        var problems = result.Failed.Select(f => (Path.GetFileName(f.Path.TrimEnd('\\', '/')), f.Reason)).ToList();
        // Not kept for another try: what the bin could not give back will not be there later either.
        return new UndoReport(result.Restored, problems, null, result.Restored.Select(p => ParentOf(p) ?? "").Where(p => p.Length > 0).Distinct().ToList());
    }
}

/// <summary>
/// A rename or a move on one drive (the pairs are old path, new path; a rename's related files, such as subtitles, are in the same list):
/// each item is moved back. What is remembered about the items (read marks, ratings ...) goes back too.
/// </summary>
public sealed class PairsUndo(string verb, List<(string From, string To)> pairs) : UndoEntry
{
    private readonly string _verb = verb;
    private readonly List<(string From, string To)> _pairs = pairs;
    private IReadOnlyList<string> Names => _pairs.Select(p => Path.GetFileName(p.From.TrimEnd('\\', '/'))).ToList();

    public override string Text => Label(_verb, Names);
    public override string Tip  => TipOf(_verb == "Move" ? "Move back to where they were:" : "Give the old names back:", Names);

    public override async Task<UndoReport> UndoAsync()
    {
        var problems = new List<(string Name, string Reason)>();
        var todo     = new List<(string Now, string Original)>();

        // Last done, first undone. Everything is checked before anything is touched.
        foreach (var (original, now) in Enumerable.Reverse(_pairs))
        {
            var name = Path.GetFileName(now.TrimEnd('\\', '/'));
            bool caseOnly = string.Equals(original, now, StringComparison.OrdinalIgnoreCase);
            var parent = ParentOf(original);
            if (!File.Exists(now) && !Directory.Exists(now))
                problems.Add((name, "it is gone"));
            else if (!caseOnly && (File.Exists(original) || Directory.Exists(original)))
                problems.Add((name, "something with the old name exists now"));
            else if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                problems.Add((name, "the old folder is gone"));
            else
                todo.Add((now, original));
        }

        var done = new List<(string Now, string Original)>();
        await Task.Run(() =>
        {
            foreach (var (now, original) in todo)
            {
                try
                {
                    if (Directory.Exists(now)) Directory.Move(now, original);
                    else                       File.Move(now, original);
                    done.Add((now, original));
                }
                catch (Exception ex)
                {
                    problems.Add((Path.GetFileName(now.TrimEnd('\\', '/')), ex is IOException io && io.Message.Contains("being used", StringComparison.OrdinalIgnoreCase)
                        ? "it is in use" : ex.Message.TrimEnd('.')));
                }
            }
        });

        LibraryService.Move(done);   // what is remembered goes back to the old names and places too

        // What could not be undone stays, so it can be tried again.
        var doneNow = new HashSet<string>(done.Select(d => d.Now), StringComparer.OrdinalIgnoreCase);
        var left    = _pairs.Where(p => !doneNow.Contains(p.To)).ToList();
        var touched = done.SelectMany(d => new[] { ParentOf(d.Now), ParentOf(d.Original) }).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!)
                          .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new UndoReport(done.Select(d => d.Original).ToList(), problems, left.Count > 0 ? new PairsUndo(_verb, left) : null, touched);
    }
}

/// <summary>
/// A copy: the files and folders the copy made are sent to the Recycle Bin again, but only while they are still as the copy left
/// them (a file that was edited since is kept) and a folder only while it holds nothing but what the copy put there.
/// </summary>
public sealed class CopyUndo(List<(string Path, long Length, DateTime WriteUtc)> files, List<string> folders, int topLevel, string? firstName) : UndoEntry
{
    private readonly List<(string Path, long Length, DateTime WriteUtc)> _files = files;
    private readonly List<string> _folders = folders;
    private readonly int _topLevel = topLevel;
    private readonly string? _firstName = firstName;

    public override string Text => _topLevel == 1 && _firstName != null ? Label("Copy", new[] { _firstName }) : $"Undo Copy ({_topLevel} items)";
    public override string Tip  => $"Remove what the copy made ({_files.Count} {(_files.Count == 1 ? "file" : "files")}, to the Recycle Bin), as long as it is unchanged.\n(Ctrl+Z)";
    public override bool ShowsOwnProgress => true;

    public override async Task<UndoReport> UndoAsync()
    {
        var problems = new List<(string Name, string Reason)>();
        var remove   = new List<FileDelete.Item>();

        await Task.Run(() =>
        {
            foreach (var (path, length, writeUtc) in _files)
            {
                var name = Path.GetFileName(path);
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) continue;   // already gone: nothing to remove
                    if (info.Length != length || info.LastWriteTimeUtc != writeUtc) { problems.Add((name, "it was changed after it was copied, so it is kept")); continue; }
                    remove.Add(new FileDelete.Item(path, name, false));
                }
                catch (Exception ex) { problems.Add((name, ex.Message.TrimEnd('.'))); }
            }
        });

        var removedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (remove.Count > 0)
        {
            var outcome = await FolderTabViewModel.RunWithProgressAsync((progress, token) => FileDelete.RunAsync(remove, false, progress, token),
                "Undo Copy", $"{remove.Count} files", ParentOf(remove[0].Path) ?? "", heading: "Removing what the copy made",
                icon: IconHelper.GetRecycleBinIcon(full: true));
            foreach (var d in outcome.Done) removedFiles.Add(d.From);
            foreach (var f in outcome.Failed) problems.Add((f.Name, f.Reason));
        }

        // The folders the copy made, deepest first, and only when they are empty now.
        await Task.Run(() =>
        {
            foreach (var dir in _folders.OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    if (Directory.EnumerateFileSystemEntries(dir).Any()) { problems.Add((Path.GetFileName(dir), "it holds other files now, so it is kept")); continue; }
                    Directory.Delete(dir, false);
                }
                catch (Exception ex) { problems.Add((Path.GetFileName(dir), ex.Message.TrimEnd('.'))); }
            }
        });

        var touched = _files.Select(f => ParentOf(f.Path)).Concat(_folders.Select(ParentOf)).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!)
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new UndoReport(new(), problems, null, touched);
    }
}

/// <summary>A new folder or a new file (New Folder, New Text Document, New File from Clipboard): removed again while it is still empty / unchanged.</summary>
public sealed class CreatedUndo(string path, bool isDirectory, long length, DateTime writeUtc) : UndoEntry
{
    private readonly string _path = path;
    private readonly bool _isDirectory = isDirectory;
    private readonly long _length = length;
    private readonly DateTime _writeUtc = writeUtc;

    private string Name => Path.GetFileName(_path.TrimEnd('\\', '/'));

    public override string Text => _isDirectory ? "Undo New Folder" : "Undo New File";
    public override string Tip  => $"Remove \"{Name}\" again" + (_isDirectory ? ", as long as it is empty." : ", as long as it is unchanged (it goes to the Recycle Bin).") + "\n(Ctrl+Z)";

    public static CreatedUndo? ForNewFolder(string path) => Directory.Exists(path) ? new CreatedUndo(path, true, 0, default) : null;

    public static CreatedUndo? ForNewFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new CreatedUndo(path, false, info.Length, info.LastWriteTimeUtc) : null;
        }
        catch { return null; }
    }

    public override Task<UndoReport> UndoAsync()
    {
        var problems = new List<(string Name, string Reason)>();
        try
        {
            if (_isDirectory)
            {
                if (!Directory.Exists(_path)) problems.Add((Name, "it is gone"));
                else if (Directory.EnumerateFileSystemEntries(_path).Any()) problems.Add((Name, "it is not empty any more, so it is kept"));
                else Directory.Delete(_path, false);
            }
            else
            {
                var info = new FileInfo(_path);
                if (!info.Exists) problems.Add((Name, "it is gone"));
                else if (info.Length != _length || info.LastWriteTimeUtc != _writeUtc) problems.Add((Name, "it was changed after it was made, so it is kept"));
                else FileSystem.DeleteFile(_path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
        }
        catch (Exception ex) { problems.Add((Name, ex.Message.TrimEnd('.'))); }

        var parent = ParentOf(_path);
        return Task.FromResult(new UndoReport(new(), problems, null, string.IsNullOrEmpty(parent) ? new() : new() { parent }));
    }
}
