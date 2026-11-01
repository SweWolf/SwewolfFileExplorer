using System.Windows.Controls;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The right-click menu of a web address (the Host URL of the info panel, and of the picture viewer): Open in Default Browser, Open in each
/// installed browser (with its icon), Copy. Built each time the menu is asked for, as the installed browsers are looked up then.
/// </summary>
internal static class WebUrlMenu
{
    /// <param name="open">Opens the address in the browser (null = the default one).</param>
    /// <param name="copy">Puts the text on the clipboard.</param>
    /// <param name="openHint">The tooltip of the first item: what a plain click does.</param>
    /// <param name="copyGesture">The key shown next to Copy, or null.</param>
    public static ContextMenu Build(string url, Action<string, WebBrowsers.Browser?> open, Action<string> copy, string openHint, string? copyGesture)
    {
        bool isWeb = WebBrowsers.IsWebAddress(url);

        var menu = new ContextMenu();
        var openDefault = new MenuItem { Header = "Open in Default Browser", IsEnabled = isWeb, ToolTip = openHint };
        openDefault.Click += (_, _) => open(url, null);
        menu.Items.Add(openDefault);

        if (isWeb)
        {
            var browsers = WebBrowsers.Installed();
            if (browsers.Count > 0) menu.Items.Add(new Separator());
            foreach (var browser in browsers)
            {
                var item = new MenuItem { Header = "Open in " + browser.Name.Replace("_", "__") };
                if (IconHelper.GetIcon(browser.ExePath) is { } icon)
                    item.Icon = new Image { Source = icon, Width = 16, Height = 16 };
                var chosen = browser;
                item.Click += (_, _) => open(url, chosen);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
        }

        var copyItem = new MenuItem { Header = "Copy", ToolTip = "Copies the whole address" };
        if (copyGesture != null) copyItem.InputGestureText = copyGesture;
        copyItem.Click += (_, _) => copy(url);
        menu.Items.Add(copyItem);
        return menu;
    }
}
