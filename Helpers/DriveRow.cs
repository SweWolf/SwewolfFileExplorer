using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// One line of the drives list that is shown when This PC is selected (name, type, file system, total size, free space).
/// A separate class from the file rows: the grid has other columns. Changed in place when the numbers change, so the selection stays.
/// </summary>
public sealed class DriveRow : INotifyPropertyChanged
{
    public string Root { get; }
    public ImageSource? Icon { get; }

    private string _name = "", _typeName = "", _fileSystem = "", _totalText = "", _freeText = "";

    public string Name       { get => _name;       private set => Set(ref _name, value); }
    public string TypeName   { get => _typeName;   private set => Set(ref _typeName, value); }
    public string FileSystem { get => _fileSystem; private set => Set(ref _fileSystem, value); }
    public string TotalText  { get => _totalText;  private set => Set(ref _totalText, value); }
    public string FreeText   { get => _freeText;   private set => Set(ref _freeText, value); }

    public DriveRow(string root, ImageSource? icon)
    {
        Root = root;
        Icon = icon;
    }

    public void Update(string name, string typeName, string fileSystem, long total, long free)
    {
        Name       = name;
        TypeName   = typeName;
        FileSystem = fileSystem;
        TotalText  = FormatSize(total);
        FreeText   = FormatSize(free);
    }

    /// <summary>The way Explorer writes a drive size: three significant digits, in the unit that fits ("930 GB", "7.27 TB").</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "";
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        // Explorer cuts the digits off instead of rounding (7.2799 TB is "7.27 TB", 111.9 GB is "111 GB").
        double Cut(double v, double scale) => Math.Floor(v * scale) / scale;
        string number = unit == 0 ? value.ToString("0")
                      : value < 10  ? Cut(value, 100).ToString("0.00")
                      : value < 100 ? Cut(value, 10).ToString("0.0")
                      :               Cut(value, 1).ToString("0");
        return $"{number} {units[unit]}";
    }

    /// <summary>The "Type" column: the words Explorer uses for a drive.</summary>
    public static string TypeNameOf(System.IO.DriveType type) => type switch
    {
        System.IO.DriveType.Fixed     => "Local Disk",
        System.IO.DriveType.Removable => "Removable Disk",
        System.IO.DriveType.Network   => "Network Drive",
        System.IO.DriveType.CDRom     => "CD Drive",
        System.IO.DriveType.Ram       => "RAM Disk",
        _                             => "Drive"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
