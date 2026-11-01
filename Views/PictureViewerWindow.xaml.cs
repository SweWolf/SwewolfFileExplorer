using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// The picture viewer: opens full screen on a black background, shows one picture of the list at a time.
/// Left/Right (and the wheel, click, Space, Enter, Backspace ...) go to the next or previous picture, Ctrl + wheel
/// or numpad +/- zoom, dragging pans a zoomed picture, F11 switches between full screen and a maximized window.
/// Rotating is for viewing only: no file is ever written.
/// </summary>
public partial class PictureViewerWindow : Window
{
    private readonly List<string> _files;
    private int _index;

    /// <summary>Raised when another picture is shown, so the Explorer can select it in its list.</summary>
    public event Action<string>? PictureChanged;

    /// <summary>The path of the picture shown now.</summary>
    public string CurrentPath => _files.Count > 0 ? _files[_index] : "";

    /// <summary>True if a picture was moved to the Recycle Bin from the viewer: the Explorer must read its folder again.</summary>
    public bool AnyDeleted { get; private set; }

    // "Close" in the right-click menu means "go on to the Explorer"; Esc, X, Alt+F4 and the window's own close button end the program
    // when the program was started with a picture (as TeraTool did). Only the program that started with a picture cares.
    private bool _closeToExplorer;
    public bool ExitApplication => !_closeToExplorer;

    // What is on show, and how.
    private LoadedPicture? _picture;
    private int _loadVersion;                // a picture loads in the background: only the last one asked for is shown
    private double _zoom = 1;                // on top of the size that fits the window
    private bool _normalSize;                // "Normal Size": fit the window, but never enlarge past 100 %
    private int _quarterTurns;               // rotation clockwise, for viewing only
    private double _offsetX, _offsetY;       // panning, in device-independent pixels

    private GifAnimator? _animator;
    private readonly DispatcherTimer _animationTimer = new();

    private bool _fullscreen = true;

    // ── Slide show ─────────────────────────────────────────────────────────
    // Set by the Explorer's Play bar: the next picture comes by itself every N seconds. The Pause key (and P, while a slide show runs) pauses
    // and resumes it; Left / Right and the other keys still work and start the count again. At the end of the list: what the Play bar says.
    /// <summary>How the slide show runs; null = an ordinary viewer (the pictures change when the user asks).</summary>
    public SlideshowOptions? Slideshow { get; set; }

    /// <summary>True if the slide show ran to the end of the list and closed by itself (end action Close), as opposed to the user closing it.</summary>
    public bool ReachedEnd { get; private set; }

    private readonly DispatcherTimer _slideTimer = new();
    private bool _playing;   // a slide show is on (it may be paused)
    private bool _paused;
    private bool _closed;    // the window is closing or closed: nothing may be started again (a timer that ticked after the end-of-list question closed it crashed)

    private readonly DispatcherTimer _cursorTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _clickTimer = new();   // a click waits to see if it becomes a double click
    private int _pendingClickDirection;
    private int _pendingClickTimestamp;   // when the click was made (input time), see UserNavigate

    // Mouse state for dragging and clicking.
    private Point _pressPoint;
    private Point _pressOffset;
    private int _pressClickCount;
    private bool _dragged;
    private bool _pressed;   // the button went down in the viewer: a release that did not is not a click (see Viewport_MouseLeftButtonUp)

    public PictureViewerWindow(IEnumerable<string> files, string current)
    {
        InitializeComponent();

        _files = files.ToList();
        _index = Math.Max(0, _files.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase)));

        _slideTimer.Tick     += (_, _) => { _slideTimer.Stop(); Navigate(+1); };
        _animationTimer.Tick += (_, _) => ShowNextAnimationFrame();
        _cursorTimer.Tick    += (_, _) => HideCursor();
        _overlayTimer.Tick   += (_, _) => { _overlayTimer.Stop(); Overlay.Visibility = Visibility.Collapsed; };
        _clickTimer.Interval  = TimeSpan.FromMilliseconds(GetDoubleClickTime());
        _clickTimer.Tick     += (_, _) =>
        {
            _clickTimer.Stop();
            UserNavigate(_pendingClickDirection, _pendingClickTimestamp);
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // No screensaver or sleeping screen while looking at pictures.
        SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED);
        _cursorTimer.Start();
        if (Slideshow != null) _playing = true;
        ShowCurrent(Slideshow != null ? $"Slide show, {Slideshow.Seconds} s per picture. The Pause key pauses." : null);
        Focus();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SetThreadExecutionState(ES_CONTINUOUS);
        FinishRating();
        _closed = true;
        StopNewestWatch();
        StopNextFolderSearch();
        _slideTimer.Stop();
        _animationTimer.Stop();
        _cursorTimer.Stop();
        _overlayTimer.Stop();
        _clickTimer.Stop();
    }

    /// <summary>The next picture comes after the time per picture, counted from when this one is on screen (not while paused or while the end-of-list question is open).</summary>
    private void RestartSlideTimer()
    {
        _slideTimer.Stop();
        if (_closed || !_playing || _paused || _askingAtEnd || Slideshow == null) return;
        _slideTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, Slideshow.Seconds));
        _slideTimer.Start();
    }

    private void TogglePause()
    {
        if (!_playing) return;
        _paused = !_paused;
        ShowOverlay(_paused ? "Paused" : "Playing");
        RestartSlideTimer();
    }

    // ── Showing a picture ────────────────────────────────────────────────────

    private async void ShowCurrent(string? message = null)
    {
        int version = ++_loadVersion;
        _loading = true;
        string path = CurrentPath;
        PictureChanged?.Invoke(path);
        WatchFolderOfCurrent();
        UpdateHostUrl();

        if (_index == _files.Count - 1) StartNextFolderSearch();   // the end is here: look ahead while the user looks at the picture

        // Off the UI thread: a big photo takes a moment to decode, and a quick key press must not wait for it.
        var loaded = await Task.Run(() => PictureLoader.Load(path));
        if (version != _loadVersion) return;   // another picture was asked for meanwhile

        _animationTimer.Stop();
        _animator = null;
        _picture  = loaded;

        _zoom = 1;
        _quarterTurns = 0;
        _offsetX = _offsetY = 0;

        if (loaded == null)
        {
            Picture.Source = null;
            // A JPEG XL picture with nothing to read it: say what is needed, not just that it failed.
            ShowOverlay(JxlSupport.IsJxl(path) && JxlSupport.FindMagick() == null
                ? $"Cannot show {Path.GetFileName(path)}: {JxlSupport.MissingHint}"
                : $"Cannot show {Path.GetFileName(path)}");
        }
        else
        {
            Picture.Width  = loaded.Width;    // one pixel of the picture is one unit here; the scale does the rest
            Picture.Height = loaded.Height;

            if (loaded.Frames != null)
            {
                _animator = new GifAnimator(loaded.Frames, loaded.Width, loaded.Height);
                ShowNextAnimationFrame();
            }
            else
            {
                Picture.Source = loaded.Image;
            }

            if (message != null) ShowOverlay(message);
            else ShowCurrentRating();
        }

        UpdateTransform();
        RestartSlideTimer();   // also after a picture that could not be shown: the slide show goes on to the next one

        // The picture counts as shown when the window has drawn it (a big one takes a moment): keys pressed before that are dropped.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
        {
            if (version != _loadVersion) return;   // another picture was asked for meanwhile; that one ends the wait
            _shownAtTick = Environment.TickCount;
            _loading = false;
        });
    }

    private void ShowNextAnimationFrame()
    {
        if (_animator == null) return;
        Picture.Source = _animator.Next(out int delay);
        _animationTimer.Interval = TimeSpan.FromMilliseconds(delay);
        _animationTimer.Start();
    }

    private void ShowOverlay(string text)
    {
        OverlayText.Text = text;
        Overlay.Visibility = Visibility.Visible;
        _overlayTimer.Stop();
        _overlayTimer.Start();
    }

    // ── Size, zoom, panning ──────────────────────────────────────────────────

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>The size of the picture on screen as the window is turned, in pixels of the picture.</summary>
    private (double Width, double Height) TurnedSize =>
        _picture == null ? (0, 0)
        : _quarterTurns % 2 == 0 ? (_picture.Width, _picture.Height)
        : (_picture.Height, _picture.Width);

    /// <summary>Screen pixels per picture pixel when the picture fits the window (and zoom is 1).</summary>
    private double FitScale()
    {
        var (w, h) = TurnedSize;
        if (w <= 0 || h <= 0) return 1;

        double dpi  = DpiScale;
        double fit  = Math.Min(Viewport.ActualWidth * dpi / w, Viewport.ActualHeight * dpi / h);
        return _normalSize && fit > 1 ? 1 : fit;
    }

    /// <summary>Screen pixels per picture pixel now: 1 shows the picture at its real size.</summary>
    private double CurrentScale => FitScale() * _zoom;

    private void UpdateTransform()
    {
        if (_picture == null) return;

        double dips = CurrentScale / DpiScale;   // units of this window per pixel of the picture
        var (w, h) = TurnedSize;

        // A picture bigger than the window can be moved until its edge meets the window's; a smaller one stays in the middle.
        double maxX = Math.Max(0, (w * dips - Viewport.ActualWidth) / 2);
        double maxY = Math.Max(0, (h * dips - Viewport.ActualHeight) / 2);
        _offsetX = Math.Clamp(_offsetX, -maxX, maxX);
        _offsetY = Math.Clamp(_offsetY, -maxY, maxY);

        // The picture sits at the top left of its Canvas, at its real size: put its middle on the origin, turn it,
        // scale it, then move the middle to the middle of the window (plus the panning).
        var transform = new TransformGroup();
        transform.Children.Add(new TranslateTransform(-_picture.Width / 2.0, -_picture.Height / 2.0));
        transform.Children.Add(new RotateTransform(_quarterTurns * 90));
        transform.Children.Add(new ScaleTransform(dips, dips));
        transform.Children.Add(new TranslateTransform(Viewport.ActualWidth / 2 + _offsetX,
                                                      Viewport.ActualHeight / 2 + _offsetY));
        Picture.RenderTransform = transform;

        UpdateTitle();
    }

    private bool CanPan
    {
        get
        {
            var (w, h) = TurnedSize;
            double dips = CurrentScale / DpiScale;
            return w * dips > Viewport.ActualWidth + 0.5 || h * dips > Viewport.ActualHeight + 0.5;
        }
    }

    /// <summary>The title is not visible in full screen, but shows in the taskbar and when the window is not full screen.</summary>
    private void UpdateTitle()
    {
        if (_picture == null) { Title = "Picture Viewer"; return; }

        string size = $"{_picture.Width}x{_picture.Height} pixels";
        long percent = (long)Math.Round(CurrentScale * 100);
        if (percent != 100) size += $" - Scaled {percent}%";
        Title = $"{CurrentPath} ({size})";
    }

    /// <summary>Zooms in or out; the point under <paramref name="anchor"/> (the mouse) stays where it is.</summary>
    private void ZoomBy(double factor, Point? anchor = null)
    {
        if (_picture == null) return;

        double zoom = Math.Clamp(_zoom * factor, 0.1, 50);
        if (Math.Abs(zoom - 1) < 0.001) zoom = 1;
        double k = zoom / _zoom;

        var center = new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);
        var point  = anchor ?? center;
        _offsetX = (point.X - center.X) * (1 - k) + k * _offsetX;
        _offsetY = (point.Y - center.Y) * (1 - k) + k * _offsetY;

        _zoom = zoom;
        UpdateTransform();
    }

    private void ResetView(bool normalSize)
    {
        _normalSize = normalSize;
        _zoom = 1;
        _offsetX = _offsetY = 0;
        UpdateTransform();
    }

    private void Rotate(int quarterTurns)
    {
        if (_picture == null) return;
        _quarterTurns = (_quarterTurns + quarterTurns + 4) % 4;   // only what is on screen: the file is not touched
        _zoom = 1;
        _offsetX = _offsetY = 0;
        UpdateTransform();
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTransform();

    // ── Moving through the list ──────────────────────────────────────────────

    /// <summary>
    /// Going to the next / previous picture because the user asked (key, wheel, click). While a picture is still being loaded and
    /// put on screen, and for input made before it was shown, nothing happens: on a slow computer the user would otherwise press
    /// again and again, think nothing happens, and then fly through all the pictures that were queued. <paramref name="timestamp"/>
    /// is the input event's own time (<see cref="InputEventArgs.Timestamp"/>, the system's tick count).
    /// </summary>
    private void UserNavigate(int direction, int timestamp)
    {
        if (_loading || unchecked(timestamp - _shownAtTick) < 0) return;
        Navigate(direction);
    }

    private bool _loading;     // a picture is being read, or has been read and is not on screen yet
    private int _shownAtTick;  // Environment.TickCount of the moment the last picture was on screen

    private void Navigate(int direction)
    {
        if (_files.Count == 0) return;

        int next = _index + direction;
        if (next < 0)
        {
            ShowOverlay("This is the first item in the list");   // stays on the picture; no wrapping around
            return;
        }
        // Another program may have added pictures to the folder while this window was open: look again before calling this the end.
        if (next >= _files.Count && TryRefreshList()) next = _index + direction;

        if (next >= _files.Count)
        {
            if (_playing) EndOfSlideshow();
            else AskAtEndOfList();
            return;
        }

        _index = next;
        ShowCurrent();
    }

    /// <summary>
    /// The pictures of a folder as they are on the disk now (in the order the Explorer's list shows them), or null when that cannot be
    /// told. Set by the Explorer. Used at the end of the list, so pictures that another program made meanwhile are found.
    /// </summary>
    public Func<string, string, List<string>?>? RefreshPictures { get; set; }

    /// <summary>
    /// Reads the folder of the picture on show again. If the list is different the viewer goes on with the new one (staying on the same
    /// picture) and this returns true. Not for a playlist, a picture inside an archive, or when the picture on show is not found in
    /// the new list (it was moved or deleted outside): the list then stays as it was.
    /// </summary>
    private bool TryRefreshList()
    {
        if (RefreshPictures == null || IsPlaylist || _files.Count == 0) return false;

        string current = CurrentPath;
        if (!File.Exists(current)) return false;   // inside an archive: nothing changes there while it is open

        List<string>? fresh;
        try { fresh = RefreshPictures(Path.GetDirectoryName(current) ?? "", current); }
        catch { return false; }
        if (fresh == null || fresh.Count == 0) return false;

        int index = fresh.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        if (fresh.SequenceEqual(_files, StringComparer.OrdinalIgnoreCase)) return false;

        _files.Clear();
        _files.AddRange(fresh);
        _index = index;
        return true;
    }

    // ── The end of the list: the next folder ─────────────────────────────────

    /// <summary>
    /// Finds the next folder with pictures (on a background thread; see <see cref="NextFolderFinder"/>). Set by the Explorer.
    /// Arguments: the folder shown now, and a token that is canceled when the viewer closes or moves to another folder.
    /// </summary>
    public Func<string, CancellationToken, string?>? FindNextFolder { get; set; }

    /// <summary>
    /// Makes the Explorer go to a folder and returns its pictures in the order its list shows them
    /// (null if it cannot be opened). Set by the Explorer.
    /// </summary>
    public Func<string, List<string>?>? OpenFolder { get; set; }

    private CancellationTokenSource? _nextFolderCts;
    private Task<string?>? _nextFolderTask;
    private string? _nextFolderFor;      // the folder the search was started for
    private bool _askingAtEnd;

    private const string NoNextFolderText = "There is no next folder.";

    /// <summary>
    /// Starts looking for the next folder as soon as the last picture is on show, so the answer is ready (or nearly)
    /// when the user goes on. One search per folder.
    /// </summary>
    private void StartNextFolderSearch()
    {
        if (FindNextFolder == null || OpenFolder == null || _files.Count == 0) return;

        string folder = Path.GetDirectoryName(CurrentPath) ?? "";
        if (folder.Length == 0 || (_nextFolderTask != null && string.Equals(_nextFolderFor, folder, StringComparison.OrdinalIgnoreCase)))
            return;

        _nextFolderCts?.Cancel();
        var cts = _nextFolderCts = new CancellationTokenSource();
        var find = FindNextFolder;
        _nextFolderFor  = folder;
        _nextFolderTask = Task.Run(() => find(folder, cts.Token), cts.Token);
    }

    private void StopNextFolderSearch()
    {
        _nextFolderCts?.Cancel();
        _nextFolderCts  = null;
        _nextFolderTask = null;
        _nextFolderFor  = null;
    }

    /// <summary>
    /// Past the last picture: a choice, as TeraTool had. Go to the next folder, start again from the first picture, or close.
    /// With no one touching the mouse or keyboard, the next folder is chosen after ten seconds.
    /// </summary>
    /// <summary>The pictures are those of an M3U playlist: there is no folder after it, so the end of the list only offers Start from Beginning or Close.</summary>
    public bool IsPlaylist { get; set; }

    private void AskAtEndOfList(int defaultIndex = 0, int countdownSeconds = 10)
    {
        if (_askingAtEnd || _closed) return;
        _askingAtEnd = true;
        _slideTimer.Stop();   // no picture changes while the question is open

        try
        {
            if (IsPlaylist)
            {
                var ask = new ChoiceDialog("This is the last picture in the playlist.", new[] { "Start from Beginning", "Close" },
                                           defaultIndex: defaultIndex == (int)EndOfListAction.StartOver ? 0 : 1, cancelIndex: 1)
                {
                    Owner = this,
                    CountdownSeconds = countdownSeconds,
                };
                ShowCursor();
                ask.ShowDialog();
                if (ask.Result == 0)
                {
                    _index = 0;
                    ShowCurrent("Start of file list");
                }
                else
                {
                    ReachedEnd = true;
                    Close();
                }
                return;
            }

            StartNextFolderSearch();
            var search = _nextFolderTask;   // null if the Explorer gave no way to look

            const string Intro = "This is the last picture in this folder.";
            string textFor(string? found) => found == null
                ? $"{Intro}\n\n{NoNextFolderText}"
                : $"{Intro}\n\nNext folder:\n{found}";

            bool done = search == null || search.IsCompleted;
            string? found = search != null && search.IsCompletedSuccessfully ? search.Result : null;

            var dialog = new ChoiceDialog(
                done ? textFor(found) : $"{Intro}\n\nLooking for the next folder...",
                new[] { "Go to Next Folder", "Start from Beginning", "Close" },
                defaultIndex: defaultIndex, cancelIndex: 2)
            {
                Owner = this,
                CountdownSeconds = countdownSeconds,
            };
            dialog.SetButtonEnabled(0, done && found != null);

            // The search may finish while the dialog is open: then the text and the button follow.
            if (!done)
            {
                _ = search!.ContinueWith(t =>
                {
                    string? result = t.IsCompletedSuccessfully ? t.Result : null;
                    dialog.Dispatcher.BeginInvoke(() =>
                    {
                        dialog.SetMessage(textFor(result));
                        dialog.SetButtonEnabled(0, result != null);
                    });
                }, TaskScheduler.Default);
            }

            ShowCursor();
            dialog.ShowDialog();

            switch (dialog.Result)
            {
                case 0:
                    var target = search != null && search.IsCompletedSuccessfully ? search.Result : null;
                    if (target != null) GoToFolder(target);
                    break;

                case 1:
                    _index = 0;
                    ShowCurrent("Start of file list");
                    break;

                default:
                    Close();
                    break;
            }
        }
        finally
        {
            _askingAtEnd = false;
            RestartSlideTimer();   // a slide show goes on after the answer (a new picture restarts it too)
        }
    }

    /// <summary>
    /// A slide show reached the end of the list. With "Ask First" the question comes (its countdown takes the chosen action by itself); without it
    /// the chosen action is taken at once: the next folder (if there is none, the question explains), start over, or close.
    /// </summary>
    private async void EndOfSlideshow()
    {
        var options = Slideshow;
        if (_closed) return;
        if (options == null) { AskAtEndOfList(); return; }

        if (options.AskFirst)
        {
            AskAtEndOfList((int)options.End, options.TimeoutSeconds);
            return;
        }

        _slideTimer.Stop();
        switch (options.End)
        {
            case EndOfListAction.StartOver:
                _index = 0;
                ShowCurrent("Start of file list");
                break;

            case EndOfListAction.Close:
                ReachedEnd = true;   // the playlist of the Play bar goes on after a run of pictures
                Close();
                break;

            default:
                if (IsPlaylist)   // nothing after a playlist: stop
                {
                    ReachedEnd = true;
                    Close();
                    break;
                }
                StartNextFolderSearch();
                var search = _nextFolderTask;
                string? target = null;
                try { if (search != null) target = await search; } catch { /* no next folder */ }
                if (_closed) return;   // closed while looking
                if (target != null) GoToFolder(target);
                else AskAtEndOfList(0, 0);   // nowhere to go: the question says so (no countdown)
                break;
        }
    }

    private void GoToFolder(string folder)
    {
        var pictures = OpenFolder?.Invoke(folder);
        if (pictures == null || pictures.Count == 0)
        {
            ShowOverlay("Cannot open the next folder");
            return;
        }

        StopNextFolderSearch();
        _files.Clear();
        _files.AddRange(pictures);
        _index = 0;
        ShowCurrent(Path.GetFileName(folder.TrimEnd('\\', '/')));
    }

    private void GoTo(int index)
    {
        if (index == _index || index < 0 || index >= _files.Count) return;
        _index = index;
        ShowCurrent();
    }

    private bool _deleteAsking;

    private async void DeleteCurrent()
    {
        if (_deleteAsking) return;
        if (_files.Count == 0) return;
        string path = CurrentPath;

        // A picture in an archive cannot be deleted: archives are read-only here.
        if (!File.Exists(path))
        {
            ShowOverlay("Cannot delete from an archive");
            return;
        }

        ShowCursor();
        // The same question and the same "don't ask again" as in the list (DeleteConfirm).
        bool go;
        _deleteAsking = true;
        try { go = await DeleteConfirm.AskAsync(this, new[] { (path, Path.GetFileName(path), false) }, permanent: false); }
        finally { _deleteAsking = false; }
        if (!go || _closed || _files.Count == 0 || CurrentPath != path) return;

        try
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cannot delete '{Path.GetFileName(path)}': {ex.Message}",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        AnyDeleted = true;
        LibraryService.Forget(new[] { path });   // its read mark and rating go with it
        _files.RemoveAt(_index);
        if (_files.Count == 0)
        {
            Close();
            return;
        }

        if (_index >= _files.Count) _index = _files.Count - 1;
        ShowCurrent();
    }

    // ── Full screen ──────────────────────────────────────────────────────────

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;

        // Both are maximized: full screen covers the whole screen without a frame; the other keeps the title bar
        // and the taskbar. Going through Normal first makes the window lay itself out again for the new style.
        WindowState = WindowState.Normal;
        if (_fullscreen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode  = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _cursorTimer.Start();
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode  = ResizeMode.CanResize;
            WindowState = WindowState.Maximized;
            _cursorTimer.Stop();
            ShowCursor();
        }
    }

    // The mouse pointer is hidden in full screen after two seconds without moving, as a picture is best seen without it.
    private void HideCursor()
    {
        _cursorTimer.Stop();
        if (_fullscreen && !Viewport.ContextMenu.IsOpen)
            Cursor = Cursors.None;
    }

    private void ShowCursor()
    {
        Cursor = null;
        if (_fullscreen)
        {
            _cursorTimer.Stop();
            _cursorTimer.Start();
        }
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool plain = Keyboard.Modifiers == ModifierKeys.None;
        bool ctrl  = Keyboard.Modifiers == ModifierKeys.Control;

        if (HandleRatingKey(e)) return;

        // The viewer opens on Enter in the list: a key held down a moment too long repeats here and must not count.
        if (e.IsRepeat && e.Key is Key.Return or Key.Space)
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Right or Key.PageDown or Key.Space or Key.Return when plain:
            case Key.N when plain:
                UserNavigate(+1, e.Timestamp);
                break;

            case Key.Pause when plain:
            case Key.P when plain && _playing:   // while a slide show runs P pauses (otherwise it is "previous")
                TogglePause();
                break;

            case Key.Left or Key.PageUp or Key.Back when plain:
            case Key.P when plain:
                UserNavigate(-1, e.Timestamp);
                break;

            case Key.Home when plain: GoTo(0); break;
            case Key.End when plain:  GoTo(_files.Count - 1); break;

            case Key.Escape or Key.X when plain:
                Close();
                break;

            case Key.F11:
            case Key.F when plain:
                ToggleFullscreen();
                break;

            case Key.Add or Key.OemPlus:       ZoomBy(1.1);       break;
            case Key.Subtract or Key.OemMinus: ZoomBy(1 / 1.1);   break;
            case Key.Multiply: ResetView(normalSize: false); break;
            case Key.Divide:   ResetView(normalSize: true);  break;

            case Key.R when plain: AskRating(); break;

            case Key.M when plain: MoveOrCopyCurrent(); break;
            case Key.T when plain: MoveToTrash(); break;
            case Key.Z when ctrl:  UndoTrash();   break;

            case Key.K when ctrl: Rotate(+1); break;
            case Key.L when ctrl: Rotate(-1); break;

            case Key.Delete when plain:
                DeleteCurrent();
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    // ── Mouse ────────────────────────────────────────────────────────────────

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
            ZoomBy(e.Delta > 0 ? 1.1 : 1 / 1.1, e.GetPosition(Viewport));
        else
            UserNavigate(e.Delta < 0 ? +1 : -1, e.Timestamp);   // wheel down: the next picture

        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        ShowCursor();

        if (!Viewport.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;

        var point = e.GetPosition(Viewport);
        var move  = point - _pressPoint;
        if (!_dragged && (Math.Abs(move.X) > SystemParameters.MinimumHorizontalDragDistance
                          || Math.Abs(move.Y) > SystemParameters.MinimumVerticalDragDistance))
            _dragged = true;

        if (_dragged && CanPan)
        {
            _offsetX = _pressOffset.X + move.X;
            _offsetY = _pressOffset.Y + move.Y;
            UpdateTransform();
        }
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A click that only closes the right-click menu does nothing else (the user clicked away to get rid of the menu).
        // Depending on the order of the events the menu is still open here or was closed a moment ago.
        if (_menuOpen || Viewport.ContextMenu.IsOpen || Environment.TickCount64 - _menuClosedAt < 200)
        {
            _pressed = false;
            _clickTimer.Stop();
            return;
        }

        _pressPoint      = e.GetPosition(Viewport);
        _pressOffset     = new Point(_offsetX, _offsetY);
        _pressClickCount = e.ClickCount;
        _dragged         = false;
        _pressed         = true;
        Viewport.CaptureMouse();
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        Viewport.ReleaseMouseCapture();

        // The viewer opens on the second click of a double click in the list: that button is released here,
        // and without this check it would count as a click on the picture and go to the next one at once.
        if (!_pressed) return;
        _pressed = false;

        if (_dragged) return;

        if (_pressClickCount >= 2)
        {
            // A double click: switch full screen, and do not go on to the next picture.
            _clickTimer.Stop();
            ToggleFullscreen();
            return;
        }

        // A click: the left third goes back, the rest goes on. It waits a moment, in case it is the first of a double click.
        _pendingClickDirection = _pressPoint.X / Viewport.ActualWidth <= 1.0 / 3 ? -1 : +1;
        _pendingClickTimestamp = e.Timestamp;
        _clickTimer.Stop();
        _clickTimer.Start();
    }

    // ── Context menu ─────────────────────────────────────────────────────────

    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        _menuOpen = true;
        UndoTrashMenuItem.IsEnabled = _trashed.Count > 0;
        ShowCursor();
    }

    private void Menu_Closed(object sender, RoutedEventArgs e)
    {
        _menuClosedAt = Environment.TickCount64;
        // Not at once: the click that closed the menu may still be waiting in the input queue, and it must see the menu as open
        // (the first version looked at IsOpen and a timestamp only, and the click still went on to the next picture).
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => _menuOpen = false);
        ShowCursor();
    }

    private bool _menuOpen;   // the right-click menu is open, or closed so a moment ago that its closing click has not been handled yet

    private long _menuClosedAt = long.MinValue / 2;   // when the right-click menu last closed (TickCount64), see Viewport_MouseLeftButtonDown

    /// <summary>
    /// Set by the program when it started with a picture: "Close" in the menu then asks it to get the Explorer ready first (it takes
    /// a few seconds), and it closes this window when that is done, so the viewer stays on screen with an hourglass meanwhile.
    /// Null: the viewer just closes.
    /// </summary>
    public Action? CloseToExplorerHandler { get; set; }

    private void MenuClose_Click(object sender, RoutedEventArgs e)
    {
        _closeToExplorer = true;
        if (CloseToExplorerHandler != null) CloseToExplorerHandler();
        else Close();
    }
    private void MenuRotateClockwise_Click(object sender, RoutedEventArgs e) => Rotate(+1);
    private void MenuRotateCounterclockwise_Click(object sender, RoutedEventArgs e) => Rotate(-1);
    private void MenuFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void MenuNormalSize_Click(object sender, RoutedEventArgs e) => ResetView(normalSize: true);
    private void MenuStretch_Click(object sender, RoutedEventArgs e) => ResetView(normalSize: false);
    private void MenuDelete_Click(object sender, RoutedEventArgs e) => DeleteCurrent();
    private void MenuRating_Click(object sender, RoutedEventArgs e) => AskRating();

    // ── Rating ───────────────────────────────────────────────────────────────

    /// <summary>The rating and expectation of a picture; set by the Explorer (its list follows). Without it the library is read directly.</summary>
    public Func<string, LibraryStore.Marks>? GetMarks { get; set; }

    /// <summary>Sets (null removes) the rating of a picture; set by the Explorer (its list and column follow). Without it the library is written directly.</summary>
    public Action<string, int?>? SetRating { get; set; }

    /// <summary>R / "Rating..." in the menu: the same box as the Explorer's Ctrl+R, for the picture shown.</summary>
    private void AskRating()
    {
        string path = CurrentPath;
        if (path.Length == 0 || _closed) return;

        var marks = GetMarks != null ? GetMarks(path) : LibraryStore.GetMark(path);

        _slideTimer.Stop();   // a slide show waits while the box is open
        try
        {
            var dialog = new InputDialog("Rating", "Rating from 1 to 10. Leave it empty to remove it:", RatingRules.Format(marks.Rating)) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var value = RatingRules.ParseRating(dialog.InputText);
            if (SetRating != null) SetRating(path, value);
            else LibraryService.Queue(() => LibraryStore.SetRating(new[] { path }, value));

            ShowOverlay(value == null ? "Rating removed" : $"Rating: {RatingRules.Format(value)}");
        }
        finally
        {
            RestartSlideTimer();
            ShowCursor();
        }
    }

    // ── Windows ──────────────────────────────────────────────────────────────

    private const uint ES_CONTINUOUS       = 0x80000000;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}
