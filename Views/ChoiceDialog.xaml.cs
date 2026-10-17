using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// A message with any number of buttons; the answer is the number of the button pressed. A reusable form
/// (the message box of Windows only has fixed button sets). Modeled on TeraTool's choice box.
///
/// Optional countdown: the default button is pressed by itself after <c>CountdownSeconds</c> seconds, so a slideshow
/// can go on while nobody is at the computer. The number is shown in the button's text: "Go to Next Folder [8]".
/// Any mouse movement, mouse click or key press stops it, and the number disappears.
///
/// The dialog can be changed while it is open (<see cref="SetMessage"/>, <see cref="SetButtonEnabled"/>): the
/// next-folder search of the picture viewer may finish after the dialog is up.
/// </summary>
public partial class ChoiceDialog : Window
{
    private readonly string[] _texts;
    private readonly Button[] _buttons;
    private readonly int _defaultIndex;
    private readonly int _cancelIndex;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private int _remaining;
    private bool _countdownRunning;
    private bool _userActive;        // the user did something: no countdown any more
    private bool _ready;             // set after the dialog opened: the mouse movement of the opening does not count
    private Point? _startMouse;

    /// <summary>The index of the button pressed; <c>cancelIndex</c> if the dialog was closed with Esc or the window's own close.</summary>
    public int Result { get; private set; }

    /// <summary>Seconds before the default button is pressed by itself; 0 = no countdown.</summary>
    public int CountdownSeconds { get; init; }

    /// <param name="buttons">The button texts, left to right.</param>
    /// <param name="defaultIndex">The button that has the focus and that the countdown presses.</param>
    /// <param name="cancelIndex">The button that Esc presses; -1 if none.</param>
    public ChoiceDialog(string message, IList<string> buttons, int defaultIndex = 0, int cancelIndex = -1)
    {
        InitializeComponent();

        Title = AppInfo.Name;
        MessageText.Text = message;

        _texts        = buttons.ToArray();
        _defaultIndex = defaultIndex;
        _cancelIndex  = cancelIndex;
        Result        = cancelIndex;
        _buttons      = new Button[_texts.Length];

        for (int i = 0; i < _texts.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Content   = _texts[i],
                Padding   = new Thickness(24, 8, 24, 8),
                Margin    = new Thickness(i == 0 ? 0 : 16, 0, 0, 0),
                IsDefault = i == defaultIndex,
                IsCancel  = i == cancelIndex,
            };
            button.Click += (_, _) => Choose(index);
            _buttons[i] = button;
            ButtonPanel.Children.Add(button);
        }

        _timer.Tick += (_, _) => Tick();

        // All buttons get the same fixed width: that of the widest text, counting the countdown "[10]" the default
        // button may show, so nothing shrinks or grows when the number goes away.
        _buttons[defaultIndex].Content = $"{_texts[defaultIndex]} [10]";
        double widest = 0;
        foreach (var button in _buttons)
        {
            button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, button.DesiredSize.Width);
        }
        foreach (var button in _buttons) button.Width = Math.Ceiling(widest);
        _buttons[defaultIndex].Content = _texts[defaultIndex];
    }

    /// <summary>Shows the dialog and returns the number of the button pressed.</summary>
    public static int Ask(Window? owner, string message, IList<string> buttons, int defaultIndex = 0, int cancelIndex = -1,
                          int countdownSeconds = 0)
    {
        var dialog = new ChoiceDialog(message, buttons, defaultIndex, cancelIndex) { CountdownSeconds = countdownSeconds };
        if (owner != null) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Result;
    }

    public void SetMessage(string text) => MessageText.Text = text;

    /// <summary>Greys a button out or enables it. The countdown starts when the default button becomes enabled.</summary>
    public void SetButtonEnabled(int index, bool enabled)
    {
        _buttons[index].IsEnabled = enabled;

        if (index != _defaultIndex) return;

        if (!enabled)
        {
            StopCountdown();
        }
        else if (_ready && !_userActive && !_countdownRunning && CountdownSeconds > 0)
        {
            StartCountdown();
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_buttons[_defaultIndex].IsEnabled)
            _buttons[_defaultIndex].Focus();
        else
            _buttons.FirstOrDefault(b => b.IsEnabled)?.Focus();

        // The pointer may be moving when the dialog opens, or the owner may have just hidden it: only movement
        // after this moment counts as "the user is here".
        _startMouse = Mouse.GetPosition(this);
        _ready = true;

        if (CountdownSeconds > 0 && _buttons[_defaultIndex].IsEnabled)
            StartCountdown();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => _timer.Stop();

    // ── Countdown ────────────────────────────────────────────────────────────

    private void StartCountdown()
    {
        _remaining = CountdownSeconds;
        _countdownRunning = true;
        ShowCountdown();
        _timer.Start();
    }

    private void StopCountdown()
    {
        _timer.Stop();
        _countdownRunning = false;
        _buttons[_defaultIndex].Content = _texts[_defaultIndex];   // the number goes away
    }

    private void ShowCountdown() => _buttons[_defaultIndex].Content = $"{_texts[_defaultIndex]} [{_remaining}]";

    private void Tick()
    {
        _remaining--;
        if (_remaining <= 0)
        {
            Choose(_defaultIndex);
            return;
        }
        ShowCountdown();
    }

    /// <summary>The user touched the keyboard or the mouse: nobody is away, so the dialog waits.</summary>
    private void UserIsHere()
    {
        if (!_ready || _userActive) return;
        _userActive = true;
        StopCountdown();
    }

    private void Window_Activity(object sender, EventArgs e) => UserIsHere();

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_ready) return;

        var position = e.GetPosition(this);
        if (_startMouse is { } start)
        {
            // A real movement, not the rounding of a still mouse.
            if (Math.Abs(position.X - start.X) <= 2 && Math.Abs(position.Y - start.Y) <= 2) return;
        }
        UserIsHere();
    }

    private void Choose(int index)
    {
        Result = index;
        _timer.Stop();
        DialogResult = true;
    }
}
