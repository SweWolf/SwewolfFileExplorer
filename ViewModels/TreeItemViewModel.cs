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

    // isVirtual = true for synthetic nodes (Quick Access, This PC) that manage their own children
    public TreeItemViewModel(string name, string fullPath, ImageSource? icon, bool isVirtual = false)
    {
        Name = name;
        FullPath = fullPath;
        Icon = icon;
        _childrenLoaded = isVirtual;
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
            foreach (var dir in Directory.GetDirectories(FullPath).OrderBy(Path.GetFileName))
            {
                try
                {
                    var item = new TreeItemViewModel(
                        Path.GetFileName(dir) ?? dir,
                        dir,
                        IconHelper.GetIcon(dir));
                    item.InitializeLazyLoad();
                    Children.Add(item);
                }
                catch { /* skip inaccessible */ }
            }
        }
        catch { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
