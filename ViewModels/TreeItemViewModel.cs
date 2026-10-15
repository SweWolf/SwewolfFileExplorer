using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using MyFileExplorer.Helpers;

namespace MyFileExplorer.ViewModels;

public class TreeItemViewModel : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _childrenLoaded;

    public string Name { get; }
    public string FullPath { get; }
    public ImageSource? Icon { get; }
    public ObservableCollection<TreeItemViewModel> Children { get; } = new();

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

    // isVirtual = true for synthetic nodes (Quick Access, This PC) that manage their own children
    public TreeItemViewModel(string name, string fullPath, ImageSource? icon, bool isVirtual = false,
                             TreeSortLookup? sortOf = null)
    {
        Name = name;
        FullPath = fullPath;
        Icon = icon;
        _childrenLoaded = isVirtual;
        _sortOf = sortOf;
    }

    /// <summary>Adds a dummy child so the expand arrow is visible before loading.</summary>
    public void InitializeLazyLoad()
    {
        if (!string.IsNullOrEmpty(FullPath) && HasSubDirectories())
            Children.Add(new TreeItemViewModel("", "", null));
    }

    private bool HasSubDirectories()
    {
        try { return Directory.EnumerateDirectories(FullPath).Any(); }
        catch { return false; }
    }

    /// <summary>Reads the subfolders again (a folder was created, renamed or deleted).</summary>
    public void Refresh()
    {
        if (string.IsNullOrEmpty(FullPath)) return;

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
            foreach (var dir in SortFolders(Directory.GetDirectories(FullPath)))
            {
                try
                {
                    var item = new TreeItemViewModel(
                        Path.GetFileName(dir) ?? dir,
                        dir,
                        IconHelper.GetIcon(dir),
                        sortOf: _sortOf);
                    item.InitializeLazyLoad();
                    Children.Add(item);
                }
                catch { /* skip inaccessible */ }
            }
        }
        catch { }
    }

    /// <summary>Puts the folders in the order chosen for this folder (Name, ascending if none was chosen).</summary>
    private IEnumerable<string> SortFolders(IEnumerable<string> folders)
    {
        var (column, ascending) = _sortOf?.Invoke(FullPath) ?? (SortColumn.Name, true);
        var byName = Comparer<string>.Create(FileItemComparer.NaturalCompare);

        if (column == SortColumn.Name)
        {
            return ascending
                ? folders.OrderBy(f => Path.GetFileName(f), byName)
                : folders.OrderByDescending(f => Path.GetFileName(f), byName);
        }

        // Read each date once; the comparer would ask for it again and again.
        var dates = folders.ToDictionary(f => f, f => column == SortColumn.DateCreated
            ? Directory.GetCreationTime(f)
            : Directory.GetLastWriteTime(f));

        // Ties are always broken by name, ascending, as in the file list.
        return (ascending ? dates.Keys.OrderBy(f => dates[f]) : dates.Keys.OrderByDescending(f => dates[f]))
            .ThenBy(f => Path.GetFileName(f), byName);
    }

    /// <summary>
    /// Puts the loaded subfolders in the order now chosen for this folder, by moving the nodes,
    /// so the expanded nodes below them stay expanded.
    /// </summary>
    public void Resort()
    {
        if (!_childrenLoaded || string.IsNullOrEmpty(FullPath)) return;

        var byPath = Children.Where(c => !string.IsNullOrEmpty(c.FullPath)).ToDictionary(c => c.FullPath);
        var sorted = SortFolders(byPath.Keys).Select(p => byPath[p]).ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            if (!ReferenceEquals(Children[i], sorted[i]))
                Children.Move(Children.IndexOf(sorted[i]), i);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
