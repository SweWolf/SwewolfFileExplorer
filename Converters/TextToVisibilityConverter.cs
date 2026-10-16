using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SwewolfFileExplorer.Converters;

/// <summary>Visible when the text is not empty.</summary>
public class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
