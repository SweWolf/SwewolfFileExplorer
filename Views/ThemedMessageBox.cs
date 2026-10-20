using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// A message box that follows the app's style (light, dark, Cyber Cyan). Windows' own <c>MessageBox</c> is a system dialog that always
/// draws light, whatever the style is (found by the user: a light box on a dark window). It takes the place of
/// <c>System.Windows.MessageBox</c> in the whole app through one global alias (<c>GlobalUsings.cs</c>), with the same
/// <c>Show</c> overloads and the same <see cref="MessageBoxButton"/>, <see cref="MessageBoxImage"/> and <see cref="MessageBoxResult"/>.
/// Like Windows' box it plays the sound of its icon, copies its text with Ctrl+C, can be called from any thread and goes on top of the
/// window that is in front. Closing it with Esc or the X gives Cancel (OK for OK only, No for Yes/No).
/// </summary>
public static class ThemedMessageBox
{
    public static MessageBoxResult Show(string text) => Show(null, text, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption) => Show(null, text, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button) => Show(null, text, caption, button, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage image) => Show(null, text, caption, button, image, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage image, MessageBoxResult defaultResult) => Show(null, text, caption, button, image, defaultResult);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage image, MessageBoxResult defaultResult, ImageSource? windowIcon) => Show(null, text, caption, button, image, defaultResult, windowIcon);

    public static MessageBoxResult Show(Window? owner, string text) => Show(owner, text, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption) => Show(owner, text, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button) => Show(owner, text, caption, button, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage image) => Show(owner, text, caption, button, image, MessageBoxResult.None);

    /// <param name="windowIcon">The icon in the title bar; null = the app's own (the delete questions show the Recycle Bin).</param>
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage image, MessageBoxResult defaultResult,
                                        ImageSource? windowIcon = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return System.Windows.MessageBox.Show(text, caption, button, image, defaultResult);   // no WPF yet: Windows' own box
        if (!dispatcher.CheckAccess())
            return dispatcher.Invoke(() => Show(owner, text, caption, button, image, defaultResult, windowIcon));

        // The window in front, if it is on screen (an owner that is not shown yet cannot own a window).
        owner ??= Application.Current!.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
                  ?? Application.Current.MainWindow;
        if (owner is { IsVisible: false } or { IsLoaded: false }) owner = null;

        try
        {
            var box = new Box(owner, text, caption, button, image, defaultResult, windowIcon);
            PlaySound(image);
            box.ShowDialog();
            return box.Result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The window could not be made or shown (an owner in a bad state): the message must still reach the user.
            return System.Windows.MessageBox.Show(text, caption, button, image, defaultResult);
        }
    }

    private static void PlaySound(MessageBoxImage image)
    {
        try
        {
            switch (image)
            {
                case MessageBoxImage.Error:       SystemSounds.Hand.Play(); break;
                case MessageBoxImage.Warning:     SystemSounds.Exclamation.Play(); break;
                case MessageBoxImage.Information: SystemSounds.Asterisk.Play(); break;
            }
        }
        catch { /* no sound */ }
    }

    private sealed class Box : Window
    {
        private readonly MessageBoxButton _buttons;
        public MessageBoxResult Result { get; private set; }

        public Box(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult, ImageSource? windowIcon)
        {
            if (windowIcon != null) Icon = windowIcon;
            _buttons = buttons;
            Result = CloseResult();

            Title = string.IsNullOrEmpty(caption) ? AppInfo.Name : caption;
            FontSize = 14;
            SizeToContent = SizeToContent.WidthAndHeight;
            MinWidth = 380;
            MaxWidth = 700;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = owner == null;
            Topmost = owner == null;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            if (owner != null) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // The message, with the icon of Windows beside it.
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 24, 24, 24) };
            var icon = IconFor(image);
            if (icon != null) top.Children.Add(new Image { Source = icon, Width = 32, Height = 32, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Top });
            top.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetRow(top, 0);
            root.Children.Add(top);

            // The buttons, on a band of their own as in Windows.
            var band = new Border { Padding = new Thickness(16, 12, 16, 12) };
            band.SetResourceReference(Border.BackgroundProperty, "PaneBrush");
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var list = ButtonList(buttons);
            var def = list.Contains(defaultResult) ? defaultResult : list[0];
            foreach (var r in list)
            {
                var b = new Button { Content = Label(r), MinWidth = 88, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0), IsDefault = r == def };
                if (r == def)   // the default button shows it, as in the other dialogs
                {
                    b.SetResourceReference(Control.BorderBrushProperty, SystemColors.AccentColorBrushKey);
                    b.BorderThickness = new Thickness(2);
                }
                var result = r;
                b.Click += (_, _) => { Result = result; Close(); };
                panel.Children.Add(b);
            }
            band.Child = panel;
            Grid.SetRow(band, 1);
            root.Children.Add(band);
            Content = root;

            Loaded += (_, _) => { foreach (var b in panel.Children.OfType<Button>().Where(x => x.IsDefault)) b.Focus(); };
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { Result = CloseResult(); Close(); }
                else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)   // as Windows' box: the text goes to the clipboard
                {
                    try { Clipboard.SetText($"{Title}{Environment.NewLine}{text}"); } catch { }
                    e.Handled = true;
                }
            };
        }

        private MessageBoxResult CloseResult() => _buttons switch
        {
            MessageBoxButton.OK          => MessageBoxResult.OK,
            MessageBoxButton.OKCancel    => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo       => MessageBoxResult.No,
            _                            => MessageBoxResult.Cancel,
        };

        private static List<MessageBoxResult> ButtonList(MessageBoxButton b) => b switch
        {
            MessageBoxButton.OK          => new() { MessageBoxResult.OK },
            MessageBoxButton.OKCancel    => new() { MessageBoxResult.OK, MessageBoxResult.Cancel },
            MessageBoxButton.YesNo       => new() { MessageBoxResult.Yes, MessageBoxResult.No },
            _                            => new() { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel },
        };

        private static string Label(MessageBoxResult r) => r switch
        {
            MessageBoxResult.OK => "OK", MessageBoxResult.Cancel => "Cancel", MessageBoxResult.Yes => "Yes", MessageBoxResult.No => "No", _ => r.ToString(),
        };

        private static ImageSource? IconFor(MessageBoxImage image) => image switch
        {
            MessageBoxImage.Error       => IconHelper.GetStockIcon(IconHelper.SIID_ERROR),
            MessageBoxImage.Warning     => IconHelper.GetStockIcon(IconHelper.SIID_WARNING),
            MessageBoxImage.Information => IconHelper.GetStockIcon(IconHelper.SIID_INFO),
            MessageBoxImage.Question    => IconHelper.GetStockIcon(IconHelper.SIID_HELP),
            _ => null,
        };
    }
}
