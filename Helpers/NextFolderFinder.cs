using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Finds the next folder with pictures after a given folder, in the order of the navigation tree (as TeraTool did):
/// first into the subfolders of the folder, then its next sibling, then the next sibling of its parent, and so on.
/// Packed files (zip, RAR ...) come after the folders and are entered like folders, so from the last folder of a zip
/// it goes on to the next zip. It never leaves the drive. A folder only counts if it holds a visible picture
/// (hidden files, and files with an extension of the hidden list, do not count).
///
/// Runs on a background thread (looking into many folders and archives takes time): path based, it does not touch
/// the tree nodes. The sort order is the tree's, per folder, so what is "next" is what the user sees in the tree.
/// </summary>
public static class NextFolderFinder
{
    private sealed record Node(string Path, string Name, DateTime Created, DateTime Modified, bool IsArchive, bool InArchive);

    /// <summary>
    /// The path of the next folder (or packed file) with pictures after <paramref name="folder"/>, or null if there is none.
    /// </summary>
    /// <param name="sortOf">The tree's sort order of the subfolders of a folder (<c>SharedState.GetTreeSort</c>).</param>
    /// <param name="showHiddenOf">True if the tree shows the hidden subfolders of a folder (<c>SharedState.ShowsHiddenInTree</c>).</param>
    public static string? Find(string folder, Func<string, (SortColumn Column, bool Ascending)> sortOf,
                               Func<string, bool> showHiddenOf, CancellationToken cancel)
    {
        try
        {
            foreach (var node in Following(folder, sortOf, showHiddenOf, cancel))
            {
                cancel.ThrowIfCancellationRequested();
                if (HasPictures(node)) return node.Path;
            }
        }
        catch (OperationCanceledException) { }

        return null;
    }

    // ── The walk ─────────────────────────────────────────────────────────────

    /// <summary>Every folder after <paramref name="start"/> in tree order, one at a time (the walk stops when the caller does).</summary>
    private static IEnumerable<Node> Following(string start, Func<string, (SortColumn, bool)> sortOf,
                                               Func<string, bool> showHiddenOf, CancellationToken cancel)
    {
        // 1. Inside the folder itself.
        foreach (var node in Descendants(start, sortOf, showHiddenOf, cancel))
            yield return node;

        // 2. Then the later siblings (and what is in them), one level up at a time, up to the root of the drive.
        string current = start;
        while (true)
        {
            string? parent = ParentOf(current);
            if (parent == null) yield break;

            var siblings = ChildrenOf(parent, sortOf, showHiddenOf);
            int at = siblings.FindIndex(s => SamePath(s.Path, current));

            if (at >= 0)
            {
                for (int i = at + 1; i < siblings.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    yield return siblings[i];
                    foreach (var node in Descendants(siblings[i].Path, sortOf, showHiddenOf, cancel))
                        yield return node;
                }
            }

            current = parent;
        }
    }

    private static IEnumerable<Node> Descendants(string path, Func<string, (SortColumn, bool)> sortOf,
                                                 Func<string, bool> showHiddenOf, CancellationToken cancel)
    {
        foreach (var child in ChildrenOf(path, sortOf, showHiddenOf))
        {
            cancel.ThrowIfCancellationRequested();
            yield return child;
            foreach (var node in Descendants(child.Path, sortOf, showHiddenOf, cancel))
                yield return node;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder above: for D:\x.zip\Day 1 it is D:\x.zip, for D:\x.zip it is D:\. Null at the root of a drive.</summary>
    private static string? ParentOf(string path)
    {
        try { return Path.GetDirectoryName(path.TrimEnd('\\', '/')); }
        catch { return null; }
    }

    /// <summary>The subfolders of a folder as the tree lists them: the folders in the tree's order, then the packed files.</summary>
    private static List<Node> ChildrenOf(string folder, Func<string, (SortColumn, bool)> sortOf, Func<string, bool> showHiddenOf)
    {
        try
        {
            var nodes = ArchiveSupport.IsInside(folder)
                ? ChildrenInArchive(folder, sortOf)
                : ChildrenOnDisk(folder, sortOf, showHiddenOf);
            return nodes;
        }
        catch
        {
            return new List<Node>();   // not readable (access denied, damaged archive, 7-Zip missing ...): skip it
        }
    }

    private static List<Node> ChildrenOnDisk(string folder, Func<string, (SortColumn, bool)> sortOf, Func<string, bool> showHiddenOf)
    {
        bool showHidden = Safe(() => showHiddenOf(folder));
        var info = new DirectoryInfo(folder);

        // Not symbolic links and junctions (they can lead back up and make the walk go in circles). A cloud folder
        // (OneDrive, Google Drive) also has the reparse attribute but is no link: it has no target, so it is kept.
        var folders = info.EnumerateDirectories()
            .Where(d => d.LinkTarget == null &&(showHidden || !HiddenItems.IsHiddenByAttributes(d.Attributes)))
            .Select(d => new Node(d.FullName, d.Name, d.CreationTime, d.LastWriteTime, false, false));

        var archives = info.EnumerateFiles()
            .Where(f => ArchiveSupport.IsArchiveName(f.Name) && (showHidden || !HiddenItems.IsHiddenByAttributes(f.Attributes)))
            .Select(f => new Node(f.FullName, f.Name, f.CreationTime, f.LastWriteTime, true, false));

        var (column, ascending) = Safe(() => sortOf(folder), (SortColumn.Name, true));
        return Sorted(folders, column, ascending).Concat(Sorted(archives, column, ascending)).ToList();
    }

    private static List<Node> ChildrenInArchive(string folder, Func<string, (SortColumn, bool)> sortOf)
    {
        var entries = ArchiveSupport.List(folder);
        var (column, ascending) = Safe(() => sortOf(folder), (SortColumn.None, true));

        var folders  = entries.Where(e => e.IsDirectory)
            .Select(e => new Node(e.FullPath, e.Name, e.Modified, e.Modified, false, true));
        var archives = entries.Where(e => e.IsArchive)
            .Select(e => new Node(e.FullPath, e.Name, e.Modified, e.Modified, true, true));

        return Sorted(folders, column, ascending).Concat(Sorted(archives, column, ascending)).ToList();
    }

    /// <summary>The same order as the tree: by name (natural), or by date with ties broken by name; None keeps the order read.</summary>
    private static IEnumerable<Node> Sorted(IEnumerable<Node> nodes, SortColumn column, bool ascending)
    {
        if (column == SortColumn.None) return nodes;

        var byName = Comparer<string?>.Create(FileItemComparer.NaturalCompare);

        if (column == SortColumn.DateCreated || column == SortColumn.DateModified)
        {
            Func<Node, DateTime> dateOf = column == SortColumn.DateCreated ? n => n.Created : n => n.Modified;
            return (ascending ? nodes.OrderBy(dateOf) : nodes.OrderByDescending(dateOf)).ThenBy(n => n.Name, byName);
        }

        return ascending ? nodes.OrderBy(n => n.Name, byName) : nodes.OrderByDescending(n => n.Name, byName);
    }

    // The sort and hidden lookups belong to the UI thread; reading them here is a snapshot that may fail while they change.
    private static T Safe<T>(Func<T> get, T fallback = default!)
    {
        try { return get(); }
        catch { return fallback; }
    }

    // ── Is it a folder worth going to? ───────────────────────────────────────

    /// <summary>True if the folder (or packed file) holds at least one visible picture directly in it.</summary>
    private static bool HasPictures(Node node)
    {
        try
        {
            if (node.InArchive || node.IsArchive)
            {
                return ArchiveSupport.List(node.Path).Any(e =>
                    !e.IsDirectory && ThumbnailLoader.IsImage(e.Name) && !HiddenItems.IsHiddenInArchive(e.Name));
            }

            return new DirectoryInfo(node.Path).EnumerateFiles().Any(f =>
                ThumbnailLoader.IsImage(f.Name) && !HiddenItems.IsHiddenFile(f.Attributes, f.Name));
        }
        catch
        {
            return false;
        }
    }
}
