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
    private readonly TextBlock _detail;
    private readonly ProgressBar _bar;
    private readonly StackPanel _panel;
    private volatile bool _optionOn;
    private bool _done;

    private WaitWindow(string title, string heading, string detail, System.Windows.Media.ImageSource? icon)
    {
        Title                 = title;
        if (icon != null) Icon = icon;   // the icon in the title bar (default: the app's own)
        Width                 = 460;
        SizeToContent         = SizeToContent.Height;
        ResizeMode            = ResizeMode.NoResize;
        ShowInTaskbar         = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding     = true;
        Cursor                = Cursors.AppStarting;   // the arrow with a small hourglass

        _heading = new TextBlock { Text = heading, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        _detail = new TextBlock
        {
            Text = detail, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (System.Windows.Media.Brush)FindResource(SystemColors.GrayTextBrushKey),
        };
        _bar = new ProgressBar { IsIndeterminate = true, Height = 14, Margin = new Thickness(0, 14, 0, 0) };

        _cancelButton = new Button
        {
            Content = "Cancel", Width = 90, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right,
            IsCancel = true, BorderThickness = new Thickness(2),
            BorderBrush = (System.Windows.Media.Brush)FindResource(SystemColors.AccentColorBrushKey),
        };
        _cancelButton.Click += (_, _) => RequestCancel();

        _panel = new StackPanel { Margin = new Thickness(16) };
        _panel.Children.Add(_heading);
        _panel.Children.Add(_detail);
        _panel.Children.Add(_bar);
        _panel.Children.Add(_cancelButton);
        Content = _panel;

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

    /// <summary>
    /// Like <see cref="Run{T}"/>, but with a bar that fills from 0 to 100 %: the work reports how many of <paramref name="total"/> items it has done
    /// (the numbers are shown as "120 of 500 <paramref name="unit"/>"). With <paramref name="optionText"/> the window also has a check box,
    /// unchecked at first: once the user checks it it stays checked (it cannot be taken back, so the work only ever has to change its mind once),
    /// and the work asks <c>optionOn</c> (thread-safe) as often as it likes.
    /// </summary>
    public static T? RunWithProgress<T>(Window? owner, string title, string heading, int total, string unit,
                                        Func<CancellationToken, IProgress<int>, Func<bool>, Task<T>> work, string? optionText = null,
                                        System.Windows.Media.ImageSource? icon = null)
    {
        var window = new WaitWindow(title, heading, $"0 of {total} {unit}", icon);
        if (optionText != null)
        {
            var box = new CheckBox { Content = optionText, Margin = new Thickness(0, 14, 0, 0) };
            box.Checked += (_, _) => { window._optionOn = true; box.IsEnabled = false; };
            window._panel.Children.Insert(window._panel.Children.IndexOf(window._cancelButton), box);
        }
        window._bar.IsIndeterminate = false;
        window._bar.Minimum = 0;
        window._bar.Maximum = Math.Max(1, total);
        window._bar.Value = 0;

        var progress = new Progress<int>(done =>
        {
            if (window._done) return;
            window._bar.Value = done;
            window._detail.Text = $"{done} of {total} {unit}";
        });
        return window.Show(owner, ct => work(ct, progress, () => window._optionOn));
    }

    /// <summary>Shows the window over <paramref name="owner"/>, runs <paramref name="work"/> and returns what it gave (default if it failed or was canceled).</summary>
    public static T? Run<T>(Window? owner, string title, string heading, string detail, Func<CancellationToken, Task<T>> work,
                            System.Windows.Media.ImageSource? icon = null)
    {
        return new WaitWindow(title, heading, detail, icon).Show(owner, work);
    }

    private T? Show<T>(Window? owner, Func<CancellationToken, Task<T>> work)
    {
        if (owner is { IsVisible: true }) Owner = owner;

        T? result = default;
        Loaded += async (_, _) =>
        {
            try { result = await work(_cancel.Token); }
            catch { /* canceled or failed: no result */ }
            finally
            {
                _done = true;
                Close();
            }
        };
        ShowDialog();
        return result;
    }
}
