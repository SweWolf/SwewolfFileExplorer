using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

// "Show Host URL" (right-click menu; remembered per Explorer tab while the program runs, never stored): the address the picture was downloaded from (what the Explorer shows as Host URL in its info
// panel: the file's Mark of the Web, or what the library has kept), in the upper left corner, the address only. A left-click opens it in the
// default browser; a right-click lists the installed browsers and Copy. Nothing is shown for a picture with no known address.
public partial class PictureViewerWindow
{
    /// <summary>Set by the Explorer: the host URL of a picture (null when there is none). Called off the UI thread.</summary>
    public Func<string, string?>? GetHostUrl { get; set; }

    /// <summary>Set by the Explorer: whether the item starts checked (what this tab had last), and told when the user changes it. Never stored in a file.</summary>
    public bool ShowHostUrlAtStart { get; set; }
    public Action<bool>? HostUrlChanged { get; set; }

    private bool _hostUrlInit;
    private string? _hostUrl;
    private int _hostUrlVersion;

    private void MenuHostUrl_Click(object sender, RoutedEventArgs e)
    {
        HostUrlChanged?.Invoke(HostUrlMenuItem.IsChecked);
        UpdateHostUrl();
    }

    /// <summary>Shows or hides the address of the current picture (read in the background; a late answer for another picture is dropped).</summary>
    private async void UpdateHostUrl()
    {
        if (!_hostUrlInit)
        {
            _hostUrlInit = true;
            HostUrlMenuItem.IsChecked = ShowHostUrlAtStart;
        }

        int version = ++_hostUrlVersion;
        _hostUrl = null;
        HostUrlBorder.Visibility = Visibility.Collapsed;
        if (!HostUrlMenuItem.IsChecked || GetHostUrl == null || _files.Count == 0 || _closed) return;

        string path = CurrentPath;
        var get = GetHostUrl;
        string? url;
        try { url = await Task.Run(() => get(path)); }
        catch { return; }
        if (version != _hostUrlVersion || _closed || string.IsNullOrWhiteSpace(url)) return;

        _hostUrl = url.Trim();
        HostUrlText.Text = _hostUrl;
        HostUrlBorder.MaxWidth = Math.Max(200, ActualWidth - 24);
        HostUrlBorder.ToolTip = WebBrowsers.IsWebAddress(_hostUrl) ? "Click to open the address in the default browser. Right-click for the browsers." : _hostUrl;
        HostUrlBorder.Visibility = Visibility.Visible;
    }

    // The press must not reach the picture (a click there goes to the next or previous picture).
    private void HostUrl_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void HostUrl_MouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_hostUrl != null) OpenHostUrl(_hostUrl, null);
    }

    private void HostUrl_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_hostUrl == null) { e.Handled = true; return; }
        HostUrlBorder.ContextMenu = WebUrlMenu.Build(_hostUrl, OpenHostUrl, text => { try { Clipboard.SetText(text); } catch { } },
            "A click does the same", null);
    }

    private void OpenHostUrl(string url, WebBrowsers.Browser? browser)
    {
        if (!WebBrowsers.IsWebAddress(url)) return;   // only real web addresses are opened
        try
        {
            if (browser == null) WebBrowsers.OpenInDefault(url); else WebBrowsers.Open(browser, url);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The address could not be opened: " + ex.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
