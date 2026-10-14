using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using MyFileExplorer.Helpers;

namespace MyFileExplorer.ViewModels;

public class FileItemViewModel : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public DateTime DateModified { get; set; }
    public string TypeName { get; set; } = "";
    public long Size { get; set; }   // -1 for directories

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); }
    }

    public void LoadIconAsync()
    {
        Task.Run(() =>
        {
            var icon = IconHelper.GetIcon(FullPath);
            Application.Current?.Dispatcher.InvokeAsync(() => Icon = icon);
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
