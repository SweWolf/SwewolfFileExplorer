using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Applies the color scheme ("Light", "Dark" or "System"): the built-in Fluent theme of WPF for the controls,
/// and the few brushes of our own (<c>PaneBrush</c>, <c>LineBrush</c>, ...) that the windows use with DynamicResource.
/// </summary>
public static class ThemeService
{
    // The brushes the windows ask for; see MainWindow.xaml.
    private static readonly Dictionary<string, (string Light, string Dark, string Cyber)> Palette = new()
    {
        ["PaneBrush"]        = ("#F3F3F3", "#2B2B2B", "#0B0B0B"),   // tab strip, tree header, info panel
        ["LineBrush"]        = ("#DDDDDD", "#454545", "#555555"),   // thin separator lines
        ["TabHoverBrush"]    = ("#E6E6E6", "#3A3A3A", "#1C1C1C"),
        ["TabSelectedBrush"] = ("#FFFFFF", "#1F1F1F", "#14313B"),   // Cyber: a dark teal, as black was too close to the strip's own color
        ["SplitterBrush"]    = ("#EEEEEE", "#333333", "#1C1C1C"),
        ["CloseHoverBrush"]   = ("#C42B1C", "#C42B1C", "#C42B1C"),   // a close button under the mouse: red in every style, as in Windows
        ["ClosePressedBrush"] = ("#A52313", "#A52313", "#A52313"),
        ["OnAccentBrush"]    =("#FFFFFF", "#000000", "#000000"),   // text on a pressed-in toggle button (the accent is dark in Light, light in Dark)
    };

    // Cyber: the Dark theme with black backgrounds and light blue text (as the colors of the old TeraTool).
    private static readonly Color CyberText = Color.FromRgb(0xAD, 0xD8, 0xE6);

    private static string _style = "System";

    // The theme brushes that Cyber replaced, to be removed again when another style is chosen.
    private static readonly List<string> CyberKeys = new();

    /// <summary>True while the dark colors are in use (Dark, Cyber, or System with a dark Windows).</summary>
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
        bool cyber = style == "Cyber Cyan";
        IsDark = cyber || style == "Dark" || (style == "System" && WindowsUsesDarkTheme());

        var app = Application.Current;
        if (app == null) return;

        // Fluent for all: the classic theme has no dark colors, and switching back to it at runtime leaves wrong colors.
        app.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;

        foreach (var key in CyberKeys) app.Resources.Remove(key);
        CyberKeys.Clear();
        if (cyber) ApplyCyberColors(app);

        foreach (var (key, colors) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(cyber ? colors.Cyber : IsDark ? colors.Dark : colors.Light);
            app.Resources[key] = new SolidColorBrush(color);
        }
    }

    /// <summary>
    /// Puts our own brushes over the ones of the Fluent dark theme (the controls look them up by name, dynamically):
    /// the near-white text becomes light blue, and the window backgrounds become black.
    /// </summary>
    private static void ApplyCyberColors(Application app)
    {
        foreach (var dictionary in app.Resources.MergedDictionaries.ToList())
        {
            foreach (System.Collections.DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key || entry.Value is not SolidColorBrush brush) continue;

                var color = brush.Color;
                Color? replacement = null;

                bool isText = (key.Contains("Foreground") || key.StartsWith("TextFillColor"))
                              && !key.Contains("Dark") && !key.Contains("Inverse") && !key.StartsWith("Accent");
                bool isWindowBackground = key is "ApplicationBackgroundBrush" or "WindowBackground"
                                          || (key.StartsWith("SolidBackgroundFillColor") && !key.Contains("Alt"));

                if (isText && color.R == 255 && color.G == 255 && color.B == 255)
                    replacement = Color.FromArgb(color.A, CyberText.R, CyberText.G, CyberText.B);   // keeps the transparency
                else if (isWindowBackground)
                    replacement = Colors.Black;

                if (replacement == null) continue;

                var replaced = new SolidColorBrush(replacement.Value);
                replaced.Freeze();
                app.Resources[key] = replaced;
                CyberKeys.Add(key);
            }
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
