using System.Globalization;
using System.Windows.Data;

namespace SweWolfFileExplorer.Converters;

public class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long size)
        {
            if (size < 0) return "";   // directories
            if (size < 1024) return $"{size} B";
            if (size < 1024 * 1024) return $"{size / 1024.0:F1} KB";
            if (size < 1024L * 1024 * 1024) return $"{size / 1024.0 / 1024.0:F1} MB";
            return $"{size / 1024.0 / 1024.0 / 1024.0:F1} GB";
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
