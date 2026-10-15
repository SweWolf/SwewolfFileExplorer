using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

public partial class AboutWindow : Window
{
    private readonly Version _currentVersion;

    public AboutWindow()
    {
        InitializeComponent();

        // An .ico holds several sizes and WPF shows the first (16 px) by default: take the biggest one.
        var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/Resourses/fluent_file_explorer.ico"),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        IconImage.Source = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();

        Title = $"About {AppInfo.Name}";
        AppNameText.Text = AppInfo.Name;

        // <Version> in the .csproj; the SDK also derives the file version from it.
        _currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        VersionText.Text = $"Version {_currentVersion.Major}.{_currentVersion.Minor}.{_currentVersion.Build}";

        Loaded += AboutWindow_Loaded;
    }

    private async void AboutWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var result = await GitHubUpdateChecker.CheckAsync("SweWolf", "SwewolfFileExplorer", _currentVersion);
        if (result == null || !IsLoaded) return; // network error or window already closed

        if (result.IsUpdateAvailable)
        {
            UpdateStatusText.Text = $"↑ Version {result.LatestVersion} available";
            UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 210, 80));
        }
        else
        {
            UpdateStatusText.Text = "✓ This is the latest version";
            UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(120, 210, 120));
        }
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
