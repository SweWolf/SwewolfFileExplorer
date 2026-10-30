using System.Globalization;
using System.Windows.Data;

namespace SwewolfFileExplorer.Converters;

/// <summary>A date and time as Windows is set to show them (Region settings: short date + short time).
/// Not a fixed pattern: XAML's StringFormat uses US English unless told otherwise.</summary>
public class DateTimeConverter : IValueConverter
{
    public static string Format(DateTime value) => value.ToString("g", CultureInfo.CurrentCulture);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DateTime d && d != default ? Format(d) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
