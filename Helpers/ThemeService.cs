using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MyFileExplorer.Helpers;

/// <summary>
/// Applies the color scheme ("Light", "Dark" or "System"): the built-in Fluent theme of WPF for the controls,
/// and the few brushes of our own (<c>PaneBrush</c>, <c>LineBrush</c>, ...) that the windows use with DynamicResource.
/// </summary>
public static class ThemeService
{
    // The brushes the windows ask for; see MainWindow.xaml.
    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["PaneBrush"]        = ("#F3F3F3", "#2B2B2B"),   // tab strip, tree header
        ["LineBrush"]        = ("#DDDDDD", "#454545"),   // thin separator lines
        ["TabHoverBrush"]    = ("#E6E6E6", "#3A3A3A"),
        ["TabSelectedBrush"] = ("#FFFFFF", "#1F1F1F"),
        ["SplitterBrush"]    = ("#EEEEEE", "#333333"),
    };

    private static string _style = "System";

    /// <summary>True while the dark colors are in use.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Starts following Windows when the style is "System".</summary>
    public static void Initialize()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _style == "System")
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(_style));
        };
    }

    public static void Apply(string style)
    {
        _style = style;
        IsDark = style == "Dark" || (style == "System" && WindowsUsesDarkTheme());

        var app = Application.Current;
        if (app == null) return;

        // Fluent for both: the classic theme has no dark colors, and switching back to it at runtime leaves wrong colors.
        app.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;

        foreach (var (key, colors) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(IsDark ? colors.Dark : colors.Light);
            app.Resources[key] = new SolidColorBrush(color);
        }
    }

    /// <summary>The "Choose your default app mode" setting of Windows.</summary>
    private static bool WindowsUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is 0;
        }
        catch
        {
            return false;
        }
    }
}
