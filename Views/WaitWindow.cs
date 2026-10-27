using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// "Please wait" window for something that goes out on the network and may take a while: a message, a moving bar and Cancel.
/// <see cref="Run"/> starts the work when the window is up, closes it when the work is done, and gives the result back
/// (default if it was canceled or failed). Built in code.
/// </summary>
public sealed class WaitWindow : Window
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly Button _cancelButton;
    private readonly TextBlock _heading;
    private bool _done;

    private WaitWindow(string title, string heading, string detail)
    {
        Title                 = title;
        Width                 = 460;
        SizeToContent         = SizeToContent.Height;
        ResizeMode            = ResizeMode.NoResize;
        ShowInTaskbar         = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding     = true;
        Cursor                = Cursors.AppStarting;   // the arrow with a small hourglass

        _heading = new TextBlock { Text = heading, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        var detailText = new TextBlock
        {
            Text = detail, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (System.Windows.Media.Brush)FindResource(SystemColors.GrayTextBrushKey),
        };
        var bar = new ProgressBar { IsIndeterminate = true, Height = 14, Margin = new Thickness(0, 14, 0, 0) };

        _cancelButton = new Button
        {
            Content = "Cancel", Width = 90, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right,
            IsCancel = true, BorderThickness = new Thickness(2),
            BorderBrush = (System.Windows.Media.Brush)FindResource(SystemColors.AccentColorBrushKey),
        };
        _cancelButton.Click += (_, _) => RequestCancel();

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(_heading);
        panel.Children.Add(detailText);
        panel.Children.Add(bar);
        panel.Children.Add(_cancelButton);
        Content = panel;

        Closing += (_, e) =>
        {
            if (_done) return;
            e.Cancel = true;   // the work must stop first; Run closes the window when it has
            RequestCancel();
        };
    }

    private void RequestCancel()
    {
        if (_cancel.IsCancellationRequested) return;
        _cancelButton.IsEnabled = false;
        _heading.Text = "Canceling...";
        _cancel.Cancel();
    }

    /// <summary>Shows the window over <paramref name="owner"/>, runs <paramref name="work"/> and returns what it gave (default if it failed or was canceled).</summary>
    public static T? Run<T>(Window? owner, string title, string heading, string detail, Func<CancellationToken, Task<T>> work)
    {
        var window = new WaitWindow(title, heading, detail);
        if (owner is { IsVisible: true }) window.Owner = owner;

        T? result = default;
        window.Loaded += async (_, _) =>
        {
            try { result = await work(window._cancel.Token); }
            catch { /* canceled or failed: no result */ }
            finally
            {
                window._done = true;
                window.Close();
            }
        };
        window.ShowDialog();
        return result;
    }
}
