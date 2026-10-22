using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// A small message that shows for half a second and closes by itself (e.g. "Moving 'x' to the Recycle Bin..." when the delete question is
/// switched off). A window of its own, so it also works over the picture viewer, which has no status bar. It never takes the focus
/// and does not block: the caller goes on at once.
/// </summary>
public static class FlashMessage
{
    public static async Task Show(Window? owner, string text, double seconds = 0.5)
    {
        try
        {
            owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible) ?? Application.Current?.MainWindow;
            if (owner is { IsVisible: false } or { IsLoaded: false }) owner = null;

            var border = new Border { Padding = new Thickness(48, 28, 48, 28), BorderThickness = new Thickness(1) };
            border.SetResourceReference(Border.BackgroundProperty, "PaneBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            var label = new TextBlock { Text = text, FontSize = 30, TextWrapping = TextWrapping.Wrap, MaxWidth = 1120 };
            border.Child = label;

            var window = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
                Topmost = true, SizeToContent = SizeToContent.WidthAndHeight, AllowsTransparency = false, Content = border,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            if (owner != null)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Loaded += (_, _) =>
                {
                    var area = owner.WindowState == WindowState.Maximized
                        ? new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight)
                        : new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
                    window.Left = area.Left + (area.Width - window.ActualWidth) / 2;
                    window.Top  = area.Top  + (area.Height - window.ActualHeight) / 2;
                };
            }
            window.Show();
            // The caller goes on with the delete on this same thread: without this the window is never drawn (it stayed empty).
            window.UpdateLayout();
            await Application.Current!.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) => { timer.Stop(); try { window.Close(); } catch { } };
            timer.Start();
        }
        catch { /* only a courtesy: the delete goes on without it */ }
    }
}
