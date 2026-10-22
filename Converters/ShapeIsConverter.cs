using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SwewolfFileExplorer.Converters;

/// <summary>Visible when the text equals the converter parameter (which drawn icon of a tree node to show).</summary>
public class ShapeIsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
