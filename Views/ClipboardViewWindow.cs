using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

/// <summary>"View Clipboard Contents": shows what is on the Windows clipboard right now: text, a picture, a list of files.
/// Anything else is only named. Read only; nothing is changed on the clipboard. Built in code.</summary>
public sealed class ClipboardViewWindow : Window
{
    public ClipboardViewWindow(Window owner)
    {
        Owner = owner;
        Title = $"{AppInfo.Name} - Clipboard Contents";
        Width = 560; Height = 420; MinWidth = 320; MinHeight = 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };

        var stack = new StackPanel { Margin = new Thickness(12) };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };

        var close = new Button { Content = "Close", Width = 115, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        close.Click += (_, _) => Close();
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(scroll);
        Grid.SetRow(close, 1);
        grid.Children.Add(close);
        Content = grid;

        Fill(stack);
    }

    private static void Heading(Panel p, string text)
        => p.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, p.Children.Count == 0 ? 0 : 14, 0, 4) });

    private static void Fill(Panel p)
    {
        bool any = false;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                if (files.Count > 0)
                {
                    any = true;
                    Heading(p, files.Count == 1 ? "1 file or folder" : $"{files.Count} files and folders");
                    p.Children.Add(new TextBox { Text = string.Join("\r\n", files), IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
                                                 HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 220 });
                }
            }
            else if (RemoteFiles.HasVirtualFiles())
            {
                any = true;
                Heading(p, "Files from another place");
                p.Children.Add(new TextBlock { Text = "These files (for example from a Remote Desktop session) are only copied when you paste.", TextWrapping = TextWrapping.Wrap });
            }

            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null)
                {
                    any = true;
                    Heading(p, $"Picture, {img.PixelWidth} x {img.PixelHeight}");
                    p.Children.Add(new Image { Source = img, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxHeight = 300, HorizontalAlignment = HorizontalAlignment.Left });
                }
            }

            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (text.Length > 0)
                {
                    any = true;
                    const int Limit = 200_000;
                    Heading(p, $"Text, {text.Length:N0} characters");
                    p.Children.Add(new TextBox { Text = text.Length > Limit ? text[..Limit] + "\r\n... (only the first 200,000 characters are shown)" : text,
                                                 IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                                                 VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 260 });
                }
            }

            if (!any)
            {
                var names = Clipboard.GetDataObject()?.GetFormats(false) ?? Array.Empty<string>();
                if (names.Length == 0)
                    p.Children.Add(new TextBlock { Text = "The clipboard is empty.", TextWrapping = TextWrapping.Wrap });
                else
                {
                    Heading(p, "Contents that cannot be shown here");
                    p.Children.Add(new TextBlock { Text = string.Join(", ", names), TextWrapping = TextWrapping.Wrap });
                }
            }
        }
        catch (Exception ex)
        {
            // The clipboard can be locked for a moment by another program.
            p.Children.Add(new TextBlock { Text = "The clipboard could not be read: " + ex.Message, TextWrapping = TextWrapping.Wrap });
        }
    }
}
