using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

// Rating mode (right-click menu, a check mark; Shift+R; never saved: the viewer always starts with it off). While it is on, the number keys
// (the row above the letters or the numeric keypad) rate the picture on show: 2 to 9 at once, 1 waits 2 seconds for a 0 (1 then 0 = 10), a 0 on its own removes the rating.
// The rating is shown ("Rating: 7") and a second later the viewer goes on to the next picture, as the Space key does. A picture that
// already has a rating shows "Current rating: 5" when it comes up. The R key (the rating box) works as before.
public partial class PictureViewerWindow
{
    private bool _ratingMode;
    private string? _ratingPendingPath;                 // 1 was pressed for this picture and a 0 may follow
    private DispatcherTimer? _ratingWaitTimer;          // the 2 seconds after a 1
    private DispatcherTimer? _ratingJumpTimer;          // the second after a rating, then the next picture
    private string? _ratedPath;                         // the picture the jump belongs to
    private readonly Dictionary<string, int?> _ratedHere = new(StringComparer.OrdinalIgnoreCase);   // what was rated in this window: the database may not have it yet

    private static readonly TimeSpan RatingWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RatingShow = TimeSpan.FromSeconds(1);

    private void MenuRatingMode_Click(object sender, RoutedEventArgs e) => SetRatingMode(RatingModeMenuItem.IsChecked);

    /// <summary>Shift+R and the digits, before the other keys. True when the key was used.</summary>
    private bool HandleRatingKey(KeyEventArgs e)
    {
        if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            RatingModeMenuItem.IsChecked = !_ratingMode;
            SetRatingMode(RatingModeMenuItem.IsChecked);
            e.Handled = true;
            return true;
        }

        if (!_ratingMode || Keyboard.Modifiers != ModifierKeys.None) return false;
        int digit = DigitOf(e.Key);
        if (digit < 0) return false;

        e.Handled = true;
        if (e.IsRepeat || _closed) return true;

        if (_ratingPendingPath != null)
        {
            if (digit == 0) { Rate(_ratingPendingPath, 10); return true; }   // 1 then 0
            CommitPendingRating();                                           // something else after the 1: it was a 1
        }
        if (digit == 0) { Rate(CurrentPath, null); return true; }   // a 0 on its own removes the rating

        if (digit == 1)
        {
            _ratingPendingPath = CurrentPath;
            StopRatingTimers();
            _slideTimer.Stop();
            ShowOverlay("Rating: 1 ...");
            _ratingWaitTimer ??= MakeTimer(RatingWait, CommitPendingRating);
            _ratingWaitTimer.Stop();
            _ratingWaitTimer.Start();
            return true;
        }

        Rate(CurrentPath, digit);
        return true;
    }

    private static int DigitOf(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9           => key - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => -1,
    };

    private static DispatcherTimer MakeTimer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => { timer.Stop(); tick(); };
        return timer;
    }

    private void SetRatingMode(bool on)
    {
        if (!on) CommitPendingRating();
        _ratingMode = on;
        if (!on)
        {
            StopRatingTimers();
            ShowOverlay("Rating mode: off");
            RestartSlideTimer();
            return;
        }

        int? current = CurrentRating(CurrentPath);
        ShowOverlay("Rating mode: on\nKeys 1 to 9, 1 then 0 for 10, 0 removes" + (current != null ? $"\nCurrent rating: {current}" : ""));
    }

    /// <summary>"Current rating: 5" when a picture that has a rating comes up while rating mode is on.</summary>
    private void ShowCurrentRating()
    {
        if (!_ratingMode) return;
        if (CurrentRating(CurrentPath) is { } rating) ShowOverlay($"Current rating: {rating}");
    }

    private int? CurrentRating(string path)
    {
        if (path.Length == 0) return null;
        if (_ratedHere.TryGetValue(path, out var here)) return here;
        try { return (GetMarks != null ? GetMarks(path) : LibraryStore.GetMark(path)).Rating; }
        catch { return null; }
    }

    private void CommitPendingRating()
    {
        _ratingWaitTimer?.Stop();
        if (_ratingPendingPath is { } path) Rate(path, 1);
    }

    private void Rate(string path, int? value)
    {
        _ratingPendingPath = null;
        StopRatingTimers();
        _slideTimer.Stop();   // the slide show goes on with the next picture

        _ratedHere[path] = value;
        if (SetRating != null) SetRating(path, value);
        else LibraryService.Queue(() => LibraryStore.SetRating(new[] { path }, value));

        if (!_closed && string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            ShowOverlay(value == null ? "Rating removed" : $"Rating: {value}");
            _ratedPath = path;
            _ratingJumpTimer ??= MakeTimer(RatingShow, JumpAfterRating);
            _ratingJumpTimer.Stop();
            _ratingJumpTimer.Start();
        }
        else RestartSlideTimer();
    }

    private void JumpAfterRating()
    {
        // Only if the user is still on the picture that was rated (a key of their own has priority).
        if (_closed || !string.Equals(_ratedPath, CurrentPath, StringComparison.OrdinalIgnoreCase)) { RestartSlideTimer(); return; }
        Navigate(+1);
    }

    private void StopRatingTimers()
    {
        _ratingWaitTimer?.Stop();
        _ratingJumpTimer?.Stop();
    }

    /// <summary>Called when the window closes: a 1 still waiting for its 0 counts as a 1.</summary>
    private void FinishRating()
    {
        if (_ratingPendingPath != null) CommitPendingRating();
        StopRatingTimers();
    }
}
