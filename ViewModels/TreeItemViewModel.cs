using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.ViewModels;

public class TreeItemViewModel : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _childrenLoaded;

    public string Name { get; }
    public string FullPath { get; }
    public ImageSource? Icon { get; }
    public ObservableCollection<TreeItemViewModel> Children { get; } = new();

    /// <summary>A packed file (a zip): shown as a folder, after the real folders. What is in it is read from the file.</summary>
    public bool IsArchive { get; }

    /// <summary>A hidden or system folder, in the tree only because the user asked for the hidden folders: shown faded.</summary>
    public bool IsHidden { get; }

    public override string ToString() => Name;   // what accessibility tools read for the node

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            OnPropertyChanged();
            if (value && !_childrenLoaded && !string.IsNullOrEmpty(FullPath))
                LoadChildren();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    /// <summary>Tells how the subfolders of a folder are sorted. Handed down from the nodes to their children.</summary>
    public delegate (SortColumn Column, bool Ascending) TreeSortLookup(string folderPath);

    private readonly TreeSortLookup? _sortOf;

    /// <summary>Tells if the hidden subfolders of a folder are shown. Handed down from the nodes to their children.</summary>
    private readonly Func<string, bool>? _showHiddenOf;

    private const FileAttributes HiddenAttributes = FileAttributes.Hidden | FileAttributes.System;

    /// <summary>The hidden and system subfolders of this node are in the tree (the packed files too).</summary>
    private bool ShowHidden => !ChildrenInArchive && (_showHiddenOf?.Invoke(FullPath) ?? false);

    // True if this node is a folder inside an archive. Its children are inside the archive too.
    private readonly bool _inArchive;

    /// <summary>The children of this node are in an archive: it is a packed file, or a folder in one.</summary>
    private bool ChildrenInArchive => IsArchive || _inArchive;

    // isVirtual = true for synthetic nodes (Quick Access, This PC) that manage their own children
    public TreeItemViewModel(string name, string fullPath, ImageSource? icon, bool isVirtual = false,
                             TreeSortLookup? sortOf = null, bool isArchive = false, bool inArchive = false,
                             Func<string, bool>? showHiddenOf = null, bool isHidden = false)
    {
        Name = name;
        FullPath = fullPath;
        Icon = icon;
        _childrenLoaded = isVirtual;
        _sortOf = sortOf;
        IsArchive = isArchive;
        _inArchive = inArchive;
        _showHiddenOf = showHiddenOf;
        IsHidden = isHidden;
    }

    /// <summary>Adds a dummy child so the expand arrow is visible before loading.</summary>
    public void InitializeLazyLoad()
    {
        if (string.IsNullOrEmpty(FullPath)) return;

        // A packed file always gets the arrow: finding out if it has folders in it means opening it,
        // and a folder can have hundreds of them. Opening it shows what is in it.
        if (IsArchive || HasSubDirectories())
            Children.Add(new TreeItemViewModel("", "", null));
    }

    private bool HasSubDirectories()
    {
        try
        {
            if (_inArchive) return ArchiveSupport.HasSubfolders(FullPath);

            return VisibleFolders().Any() || VisibleArchiveFiles().Any();
        }
        catch { return false; }
    }

    private static bool IsHiddenPath(string path)
    {
        try { return (File.GetAttributes(path) & HiddenAttributes) != 0; }
        catch { return false; }
    }

    /// <summary>The subfolders to list: not the hidden and system ones, unless they are asked for.</summary>
    private IEnumerable<string> VisibleFolders() =>
        Directory.EnumerateDirectories(FullPath).Where(d => ShowHidden || !IsHiddenPath(d));

    private IEnumerable<string> VisibleArchiveFiles() =>
        EnumerateArchiveFiles(FullPath).Where(f => ShowHidden || !IsHiddenPath(f));

    /// <summary>How many hidden and system subfolders (and packed files) the real folder <paramref name="folder"/> has.</summary>
    public static int CountHiddenIn(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return 0;
            return Directory.EnumerateDirectories(folder).Count(IsHiddenPath)
                 + EnumerateArchiveFiles(folder).Count(IsHiddenPath);
        }
        catch { return 0; }
    }

    /// <summary>True if <paramref name="path"/> is a real folder with the Hidden or System attribute.</summary>
    public static bool IsHiddenFolder(string path) => Directory.Exists(path) && IsHiddenPath(path);

    /// <summary>The packed files of a folder. The patterns are applied by the file system, which is much faster than looking at every file.</summary>
    private static IEnumerable<string> EnumerateArchiveFiles(string folder) =>
        ArchiveSupport.FilePatterns.SelectMany(pattern => Directory.EnumerateFiles(folder, pattern))
            .Where(f => ArchiveSupport.IsArchiveName(f));   // "*.zip" also finds ".zipx"

    /// <summary>Reads the subfolders again (a folder was created, renamed or deleted).</summary>
    public void Refresh()
    {
        if (string.IsNullOrEmpty(FullPath)) return;

        if (ChildrenInArchive) ArchiveSupport.Invalidate(FullPath);

        if (_childrenLoaded)
        {
            LoadChildren();
        }
        else
        {
            Children.Clear();
            InitializeLazyLoad();   // the expand arrow depends on whether there are subfolders
        }
    }

    public void LoadChildren()
    {
        _childrenLoaded = true;
        Children.Clear();
        if (string.IsNullOrEmpty(FullPath)) return;

        try
        {
            if (ChildrenInArchive) LoadArchiveChildren();
            else                   LoadFolderChildren();
        }
        catch { }
    }

    private void LoadFolderChildren()
    {
        foreach (var child in BuildFolderChildren())
            Children.Add(child);
    }

    /// <summary>
    /// The nodes of the subfolders, in order: all the folders first, then the packed files, which open like folders.
    /// A node that is in <paramref name="reuse"/> is used again, so it keeps what is expanded below it.
    /// </summary>
    private List<TreeItemViewModel> BuildFolderChildren(Dictionary<string, TreeItemViewModel>? reuse = null)
    {
        var nodes = new List<TreeItemViewModel>();

        foreach (var dir in SortFolders(VisibleFolders().ToList()))
        {
            try
            {
                if (reuse != null && reuse.TryGetValue(dir, out var old)) { nodes.Add(old); continue; }

                var item = new TreeItemViewModel(
                    Path.GetFileName(dir) ?? dir,
                    dir,
                    IconHelper.GetIcon(dir),
                    sortOf: _sortOf,
                    showHiddenOf: _showHiddenOf,
                    isHidden: IsHiddenPath(dir));
                item.InitializeLazyLoad();
                nodes.Add(item);
            }
            catch { /* skip inaccessible */ }
        }

        foreach (var file in SortFolders(VisibleArchiveFiles().ToList()))
        {
            try
            {
                if (reuse != null && reuse.TryGetValue(file, out var old)) { nodes.Add(old); continue; }

                var item = new TreeItemViewModel(
                    Path.GetFileName(file),
                    file,
                    IconHelper.GetIcon(file),
                    sortOf: _sortOf,
                    isArchive: true,
                    showHiddenOf: _showHiddenOf,
                    isHidden: IsHiddenPath(file));
                item.InitializeLazyLoad();
                nodes.Add(item);
            }
            catch { /* skip inaccessible */ }
        }

        return nodes;
    }

    /// <summary>
    /// Reads the subfolders again (the hidden ones were shown or hidden, a folder was created, renamed or deleted):
    /// adds and removes nodes in place, so the nodes that stay keep their place, what is expanded below
    /// them, and the selection. A packed file is read again, which starts over.
    /// </summary>
    public void SyncChildren()
    {
        if (string.IsNullOrEmpty(FullPath)) return;

        if (ChildrenInArchive)
        {
            Refresh();
            return;
        }

        if (!_childrenLoaded)
        {
            Children.Clear();
            InitializeLazyLoad();   // the expand arrow depends on whether there is a folder to show
            return;
        }

        try
        {
            var existing = Children.Where(c => !string.IsNullOrEmpty(c.FullPath)).ToDictionary(c => c.FullPath);
            var wanted   = BuildFolderChildren(existing);

            foreach (var gone in Children.Where(c => !wanted.Contains(c)).ToList())
            {
                // The tree selects the parent when the selected node is removed, and the window would take
                // that for a click and navigate there: so nothing selected is removed.
                Deselect(gone);
                Children.Remove(gone);
            }

            for (int i = 0; i < wanted.Count; i++)
            {
                if (i < Children.Count && ReferenceEquals(Children[i], wanted[i])) continue;

                int at = Children.IndexOf(wanted[i]);
                if (at >= 0) Children.Move(at, i);
                else         Children.Insert(i, wanted[i]);
            }
        }
        catch { }
    }

    private void LoadArchiveChildren()
    {
        var entries = ArchiveSupport.List(FullPath);

        foreach (var folder in SortEntries(entries.Where(e => e.IsDirectory)))
        {
            var item = new TreeItemViewModel(folder.Name, folder.FullPath, IconHelper.GetIconForName(folder.Name, true),
                sortOf: _sortOf, inArchive: true);
            item.InitializeLazyLoad();
            Children.Add(item);
        }

        // A zip inside the zip.
        foreach (var archive in SortEntries(entries.Where(e => e.IsArchive)))
        {
            var item = new TreeItemViewModel(archive.Name, archive.FullPath, IconHelper.GetIconForName(archive.Name, false),
                sortOf: _sortOf, isArchive: true, inArchive: true);
            item.InitializeLazyLoad();
            Children.Add(item);
        }
    }

    /// <summary>Puts the folders in the order chosen for this folder (Name, ascending if none was chosen).</summary>
    private IEnumerable<string> SortFolders(IEnumerable<string> folders) =>
        SortBy(folders, Path.GetFileName,
            f => ChildrenInArchive ? ArchiveSupport.GetDate(f, created: true)  : Directory.GetCreationTime(f),
            f => ChildrenInArchive ? ArchiveSupport.GetDate(f, created: false) : Directory.GetLastWriteTime(f));

    private IEnumerable<VirtualEntry> SortEntries(IEnumerable<VirtualEntry> entries) =>
        SortBy(entries, e => e.Name, e => e.Modified, e => e.Modified);   // an archive keeps one date per entry

    private IEnumerable<T> SortBy<T>(IEnumerable<T> items, Func<T, string?> nameOf,
        Func<T, DateTime> createdOf, Func<T, DateTime> modifiedOf) where T : notnull
    {
        var (column, ascending) = _sortOf?.Invoke(FullPath) ?? (SortColumn.Name, true);
        if (column == SortColumn.None) return items;   // no sort: the order they were read in (inside an archive: as stored)

        var byName = Comparer<string?>.Create(FileItemComparer.NaturalCompare);

        if (column == SortColumn.Name)
        {
            return ascending
                ? items.OrderBy(nameOf, byName)
                : items.OrderByDescending(nameOf, byName);
        }

        // Read each date once; the comparer would ask for it again and again.
        var dateOf = column == SortColumn.DateCreated ? createdOf : modifiedOf;
        var dates = items.ToDictionary(i => i, dateOf);

        // Ties are always broken by name, ascending, as in the file list.
        return (ascending ? dates.Keys.OrderBy(i => dates[i]) : dates.Keys.OrderByDescending(i => dates[i]))
            .ThenBy(nameOf, byName);
    }

    /// <summary>
    /// Puts the loaded subfolders in the order now chosen for this folder, by moving the nodes,
    /// so the expanded nodes below them stay expanded. The packed files stay after the folders.
    /// </summary>
    public void Resort()
    {
        if (!_childrenLoaded || string.IsNullOrEmpty(FullPath)) return;

        var nodes    = Children.Where(c => !string.IsNullOrEmpty(c.FullPath)).ToList();
        var folders  = nodes.Where(c => !c.IsArchive).ToDictionary(c => c.FullPath);
        var archives = nodes.Where(c => c.IsArchive).ToDictionary(c => c.FullPath);

        var sorted = SortFolders(folders.Keys).Select(p => folders[p])
            .Concat(SortFolders(archives.Keys).Select(p => archives[p]))
            .ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            if (!ReferenceEquals(Children[i], sorted[i]))
                Children.Move(Children.IndexOf(sorted[i]), i);
        }
    }

    private static void Deselect(TreeItemViewModel node)
    {
        node.IsSelected = false;
        foreach (var child in node.Children) Deselect(child);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
