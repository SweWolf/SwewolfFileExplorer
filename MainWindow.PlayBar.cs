using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwewolfFileExplorer.Helpers;
using SwewolfFileExplorer.ViewModels;
using SwewolfFileExplorer.Views;

namespace SwewolfFileExplorer;

/// <summary>
/// The Play bar: a row of its own in the info panel, under the "File 35/100" line and above the preview picture, shown and hidden by the "Play"
/// button of the toolbar (and Ctrl+P). It holds what TeraTool's box under the list held: Play mode, Time per file, End of list action and "Ask first"
/// with its time-out, and Previous / Play / Next. Phase 1 plays the pictures of the list in the picture viewer (see <see cref="SlideshowOptions"/>);
/// music and video, through the player, come later. See docs/slideshow-and-play-modes-plan.md.
/// </summary>
public partial class MainWindow
{
    // The values of AppSettings.PlayMode (the order of the Play Mode box).
    private const int ModeSlideShow = 0, ModeSingle = 1, ModeRepeat = 2, ModeDemo = 3, ModeIntro = 4;

    /// <summary>Demo Play starts each music or video file this far into it (the player starts at 0 if the file is shorter).</summary>
    private const long DemoStartMs = 60_000;

    /// <summary>Seconds a file plays in Demo Play and Intro Play: the box "Time per File", and 10 if that is not a positive number.</summary>
    private static int DemoSeconds => AppSettingsStore.Current.PlaySeconds > 0 ? AppSettingsStore.Current.PlaySeconds : 10;

    private double _playBarHeight;   // the room the bar takes in the info panel while it is shown (0 while hidden)
    private bool _playBarReady;      // the controls have been filled from the settings: only then does a change save

    private void InitPlayBar()
    {
        var s = AppSettingsStore.Current;
        PlayModeBox.SelectedIndex = ModeSlideShow;   // the program always starts in Slide Show (the mode is not kept between runs)
        s.PlayMode = ModeSlideShow;
        PlaySecondsBox.Text       = Math.Clamp(s.PlaySeconds, 1, 3600).ToString();
        PlayEndBox.SelectedIndex  = Math.Clamp(s.PlayEndAction, 0, 2);
        PlayAskBox.IsChecked      = s.PlayAskFirst;
        PlayTimeoutBox.Text       = Math.Clamp(s.PlayTimeout, 0, 600).ToString();
        UpdatePlayControls();
        PlayBar.SizeChanged += (_, e) => ApplyPlayBarSpace(PlayBar.Visibility == Visibility.Visible ? e.NewSize.Height : 0);
        _playBarReady = true;
    }

    private void TogglePlayBar() => ShowPlayBar(PlayBar.Visibility != Visibility.Visible);

    // The Play bar was opened by entering a playlist (not by the user): it closes again when a folder is shown.
    private bool _playBarOpenedByPlaylist;

    private void ShowPlayBar(bool show, bool byPlaylist = false)
    {
        _playBarOpenedByPlaylist = show && byPlaylist;
        if (!show) ResetPlayMode();   // closing the bar: the next time it is opened the mode is Slide Show again
        PlayToggle.IsChecked = show;
        PlayBar.Visibility   = show ? Visibility.Visible : Visibility.Collapsed;
        PlayLineDivider.Visibility = PlayBar.Visibility;
        if (!show) ApplyPlayBarSpace(0);   // a collapsed element raises no size change
    }

    private bool _resetModeAfterRun;

    /// <summary>
    /// Back to Slide Show: when the Play bar is closed, when a tab is opened, and at the start of the program. A list that is playing keeps its
    /// mode until it ends.
    /// </summary>
    private void ResetPlayMode()
    {
        if (_playRunning) { _resetModeAfterRun = true; return; }
        _resetModeAfterRun = false;
        if (PlayModeBox.SelectedIndex == ModeSlideShow && AppSettingsStore.Current.PlayMode == ModeSlideShow) return;
        PlayModeBox.SelectedIndex = ModeSlideShow;   // PlaySetting_Changed saves it and updates the controls
    }

    private void PlayToggle_Click(object sender, RoutedEventArgs e) => ShowPlayBar(PlayToggle.IsChecked == true);

    /// <summary>The info panel is as much taller as the bar is high (and not smaller than the picture needs), so the preview picture keeps its size.</summary>
    private void ApplyPlayBarSpace(double height)
    {
        if (Math.Abs(height - _playBarHeight) < 0.5) return;
        double delta = height - _playBarHeight;
        _playBarHeight = height;
        InfoRow.MinHeight = InfoPanelMinHeight + height;
        InfoRow.Height    = new GridLength(Math.Max(InfoRow.MinHeight, InfoRow.Height.Value + delta));
        FitPreviewBox();
    }

    // ── What the controls say ─────────────────────────────────────────────────

    private void PlaySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_playBarReady) return;
        SavePlaySettings();
        UpdatePlayControls();
    }

    private void PlayNumber_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsDigit);

    /// <summary>Time per file and the time-out are whole numbers: a blank or silly text becomes a usable one when the box is left.</summary>
    private void PlayNumber_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender == PlaySecondsBox) PlaySecondsBox.Text = ParseOr(PlaySecondsBox.Text, 1, 3600, 10).ToString();
        else PlayTimeoutBox.Text = ParseOr(PlayTimeoutBox.Text, 0, 600, 10).ToString();
        PlaySetting_Changed(sender, e);
    }

    private static int ParseOr(string text, int min, int max, int fallback) =>
        int.TryParse(text.Trim(), out var n) ? Math.Clamp(n, min, max) : fallback;

    private void SavePlaySettings()
    {
        var s = AppSettingsStore.Current;
        s.PlayMode      = Math.Max(0, PlayModeBox.SelectedIndex);
        s.PlaySeconds   = ParseOr(PlaySecondsBox.Text, 1, 3600, 10);
        s.PlayEndAction = Math.Max(0, PlayEndBox.SelectedIndex);
        s.PlayAskFirst  = PlayAskBox.IsChecked == true;
        s.PlayTimeout   = ParseOr(PlayTimeoutBox.Text, 0, 600, 10);
        AppSettingsStore.Save(s);
    }

    /// <summary>Single Play has no time per file and no end of the list: those controls are of no use then.</summary>
    private void UpdatePlayControls()
    {
        int mode = PlayModeBox.SelectedIndex;
        bool continuous = mode != ModeSingle;
        PlaySecondsBox.IsEnabled = mode is ModeSlideShow or ModeDemo or ModeIntro;   // Repeat has no time
        PlayEndBox.IsEnabled     = continuous;
        PlayAskBox.IsEnabled     = continuous;
        PlayTimeoutBox.IsEnabled = continuous && PlayAskBox.IsChecked == true;
    }

    // ── Play, Previous, Next ──────────────────────────────────────────────────

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playRunning) { TogglePausePlayer(); return; }   // a playlist is running: the button is Pause / Resume
        StartPlay();
    }

    /// <summary>Everything the Play bar can play, in the order of the list: pictures, music and video (media only on the disk).</summary>
    private List<string> PlaylistInList() =>
        FileListView.Items.OfType<FileItemViewModel>()
            .Where(i => !i.IsDirectory && !i.IsMissing && (ThumbnailLoader.IsImage(i.FullPath) || (!i.IsInArchive && MediaPlayerLauncher.IsMedia(i.FullPath))))
            .Select(i => i.FullPath)
            .ToList();

    /// <summary>
    /// Plays the list as it is shown (sorted and filtered), from the selected file (else the first). Only pictures: the picture viewer, as before.
    /// With music or video in the list (Slide Show): the explorer runs the playlist, pictures in the viewer and music and video in SweWolf Media
    /// Player (<see cref="RunPlaylistAsync"/>). Single Play opens the one file as Enter does.
    /// </summary>
    private void StartPlay()
    {
        SavePlaySettings();

        var items = PlaylistInList();
        if (items.Count == 0)
        {
            MessageBox.Show(this, "There are no pictures, music or video files in the list to play.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = FileListView.SelectedItem as FileItemViewModel;
        int start = selected != null ? items.FindIndex(p => string.Equals(p, selected.FullPath, StringComparison.OrdinalIgnoreCase)) : -1;
        if (start < 0) start = 0;

        bool slideshow = AppSettingsStore.Current.PlayMode != ModeSingle;   // Slide Show, Repeat, Demo Play and Intro Play go on by themselves
        bool anyMedia = items.Any(MediaPlayerLauncher.IsMedia);

        if (!slideshow && MediaPlayerLauncher.IsMedia(items[start]))
        {
            // Single Play of a video or song: as Enter does (no player: the file's own program)
            if (MediaPlayerLauncher.Open(items[start], null) == LaunchResult.UseDefaultProgram) FolderTabViewModel.OpenFile(items[start]);
            return;
        }

        if (!slideshow || !anyMedia)
        {
            PlayPictures(items.Where(ThumbnailLoader.IsImage).ToList(), items[start]);
            return;
        }

        _ = RunPlaylistAsync(items, start);
    }

    /// <summary>
    /// A video or music file opened in the list (Enter, double-click): with the Play bar shown and Slide Show mode the list goes on after it, with no need to press Play.
    /// Returns false when this does not apply (Single Play, or not a file on the disk): the caller opens the file as usual.
    /// </summary>
    private bool TryPlayFromItem(FileItemViewModel item)
    {
        if (item.IsDirectory || item.IsInArchive || !MediaPlayerLauncher.IsMedia(item.FullPath)) return false;
        if (AppSettingsStore.Current.PlayMode == ModeSingle) return false;

        // Continuous playing is only on while the Play bar is shown (the user's rule, 2026-10-21); with the bar hidden a double-click plays
        // the one file as before. A list that is already running goes on whatever the bar does (it is not stopped by hiding the bar).
        if (PlayBar.Visibility != Visibility.Visible && !_playRunning) return false;

        if (_playRunning)
        {
            if (_mediaWait == null) return false;
            _jumpTo = item.FullPath;
            _mediaWait.TrySetResult(MediaOutcome.Jump);
            return true;
        }

        var items = PlaylistInList();
        int start = items.FindIndex(p => string.Equals(p, item.FullPath, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return false;
        _ = RunPlaylistAsync(items, start);
        return true;
    }

    /// <summary>Only pictures: the viewer runs the slide show itself, including the end-of-list question.</summary>
    private void PlayPictures(List<string> pictures, string start)
    {
        if (pictures.Count == 0) return;
        if (!pictures.Contains(start, StringComparer.OrdinalIgnoreCase)) start = pictures[0];

        var viewer = CreateViewer(pictures, start, owner: this);
        var s = AppSettingsStore.Current;
        if (s.PlayMode != ModeSingle)
            viewer.Slideshow = new SlideshowOptions(Math.Clamp(s.PlaySeconds, 1, 3600), (EndOfListAction)Math.Clamp(s.PlayEndAction, 0, 2),
                                                    s.PlayAskFirst, Math.Clamp(s.PlayTimeout, 0, 600));
        viewer.ShowDialog();
        AfterViewer(viewer);
    }

    // ── The playlist: the explorer decides everything ─────────────────────────

    private enum MediaOutcome { Ended, Next, Previous, Stop, Jump, Failed }

    private bool _resumeAllowed;   // the next file is one the user chose (the first, or a double-click): its resume point may be used or asked about
    private string? _jumpTo;   // a video or song double-clicked while a playlist runs

    private PlayerLink? _link;
    private bool _playRunning;
    private bool _playerPaused;
    private TaskCompletionSource<MediaOutcome>? _mediaWait;
    private string? _mediaWaitPath;

    private void SetPlayButton(bool running, bool paused)
    {
        _playRunning = running;
        PlayButtonIcon.Text = running && !paused ? "⏸" : "▶";
        PlayButtonText.Text = running && !paused ? "Pause" : "Play";
        PlayButton.ToolTip = running ? "Pause or resume the music or video" : "Play the list from the selected file";
    }

    private void TogglePausePlayer()
    {
        if (_link == null || _mediaWait == null) return;   // a picture is shown in the viewer: it has its own Pause
        _playerPaused = !_playerPaused;
        _link.Send(new PipeCommand(_playerPaused ? "pause" : "resume"));
        SetPlayButton(true, _playerPaused);
    }

    /// <summary>Plays the items in order: a run of pictures in the viewer, a music or video file in the player, until the end of the list.</summary>
    private bool _runInPlaylist;   // the run in progress plays an M3U playlist (no next folder at its end)

    private async Task RunPlaylistAsync(List<string> items, int index)
    {
        _runInPlaylist = VM.InPlaylist;
        SetPlayButton(true, false);
        _resumeAllowed = true;
        try
        {
            while (true)
            {
                if (index >= items.Count)
                {
                    var again = await EndOfPlaylistAsync(items);
                    if (again == null) break;
                    items = again;
                    index = 0;
                    if (items.Count == 0) break;
                    continue;
                }
                if (index < 0) index = 0;

                string path = items[index];
                if (ThumbnailLoader.IsImage(path))
                {
                    int end = index;
                    while (end + 1 < items.Count && ThumbnailLoader.IsImage(items[end + 1])) end++;

                    var viewer = CreateViewer(items.GetRange(index, end - index + 1), path, owner: this);
                    var s = AppSettingsStore.Current;
                    // The viewer closes by itself at the end of the run (end action Close, no question); the playlist goes on from here.
                    viewer.Slideshow = new SlideshowOptions(Math.Clamp(s.PlaySeconds, 1, 3600), EndOfListAction.Close, false, 0);
                    viewer.ShowDialog();
                    AfterViewer(viewer);
                    if (!viewer.ReachedEnd) break;   // the user closed the viewer: the playlist is over
                    index = end + 1;
                    continue;
                }

                var outcome = await PlayMediaAsync(path);
                if (outcome == MediaOutcome.Stop) break;   // the player was closed
                if (outcome == MediaOutcome.Jump)
                {
                    int jump = items.FindIndex(p => string.Equals(p, _jumpTo, StringComparison.OrdinalIgnoreCase));
                    if (jump < 0) { items = PlaylistInList(); jump = items.FindIndex(p => string.Equals(p, _jumpTo, StringComparison.OrdinalIgnoreCase)); }
                    index = Math.Max(0, jump);
                    _resumeAllowed = true;
                    continue;
                }
                bool repeat = AppSettingsStore.Current.PlayMode == ModeRepeat && outcome == MediaOutcome.Ended;   // Repeat: the same file again
                index = outcome == MediaOutcome.Previous ? Math.Max(0, index - 1) : repeat ? index : index + 1;
            }
        }
        finally
        {
            _mediaWait = null;
            _playerPaused = false;
            SetPlayButton(false, false);
            if (_resetModeAfterRun) ResetPlayMode();
            _link?.Send(new PipeCommand("release"));   // the player's own end actions count again
        }
    }

    /// <summary>Plays one file in the player and waits until it has ended, or Previous / Next was pressed, or the player closed.</summary>
    private async Task<MediaOutcome> PlayMediaAsync(string path)
    {
        if (_link == null)
        {
            _link = new PlayerLink();
            _link.Ended  += ended => { var w = _mediaWait; if (w != null && string.Equals(ended, _mediaWaitPath, StringComparison.OrdinalIgnoreCase)) w.TrySetResult(MediaOutcome.Ended); };
            _link.Closed += () => _mediaWait?.TrySetResult(MediaOutcome.Stop);
            _link.Skipped += next => _mediaWait?.TrySetResult(next ? MediaOutcome.Next : MediaOutcome.Previous);   // the buttons of the player
            _link.Error  += (_, _) => _mediaWait?.TrySetResult(MediaOutcome.Failed);   // the file could not be played: on to the next one (Repeat does not repeat it)
        }

        _playerPaused = false;
        SetPlayButton(true, false);

        var wait = new TaskCompletionSource<MediaOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediaWait = wait;
        _mediaWaitPath = path;

        // Demo Play: 10 seconds (or the time per file) from 1 minute in; Intro Play: from the start; both ignore the saved resume point.
        int mode = AppSettingsStore.Current.PlayMode;
        bool timed = mode is ModeDemo or ModeIntro;
        long startMs = mode == ModeDemo ? DemoStartMs : 0;
        bool noResume = timed || !_resumeAllowed;

        if (!_link.Connected && !await _link.ConnectAsync(300))
        {
            // No player is running: start it with the file, then take it over (it only reports the end of the file when told to).
            if (MediaPlayerLauncher.Open(path, null, startMs, timed) != LaunchResult.Started)
            {
                _mediaWait = null;
                MessageBox.Show(this, "SweWolf Media Player was not found (next to this program or in ..\\SwewolfMediaPlayer), so a list cannot be played in it.",
                                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
                return MediaOutcome.Stop;
            }
            if (!await _link.ConnectAsync(25000))
            {
                _mediaWait = null;
                MessageBox.Show(this, "SweWolf Media Player did not answer. Please use the latest version of it, so the explorer can play a list in it.",
                                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
                return MediaOutcome.Stop;
            }
            _link.Send(new PipeCommand("control", Playlist: true));
        }
        else
        {
            _link.Send(new PipeCommand("play", path, path, StartMs: startMs, NoResume: noResume, Front: true, Playlist: true));
        }

        _resumeAllowed = false;   // from here on the list plays every file from its start
        SelectInList(path);

        using var limit = new CancellationTokenSource();
        if (timed) _ = StopAfterAsync(DemoSeconds, wait, limit.Token);
        MediaOutcome outcome;
        try { outcome = await wait.Task; }
        finally { limit.Cancel(); }
        _mediaWait = null;
        return outcome;
    }

    /// <summary>Demo Play and Intro Play: after the time the file has been playing (a pause does not count) the list goes on with the next file.</summary>
    private async Task StopAfterAsync(int seconds, TaskCompletionSource<MediaOutcome> wait, CancellationToken cancel)
    {
        double played = 0;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(100, cancel);
                if (!_playerPaused) played += 0.1;
                if (played >= seconds)
                {
                    wait.TrySetResult(MediaOutcome.Next);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // the file ended or the user chose another one first
        }
    }

    /// <summary>The end of the list: asks (or not) and takes the chosen action. Null = stop; otherwise the list to play from its start.</summary>
    private async Task<List<string>?> EndOfPlaylistAsync(List<string> items)
    {
        var s = AppSettingsStore.Current;
        var action = (EndOfListAction)Math.Clamp(s.PlayEndAction, 0, 2);

        // An M3U playlist has nothing after it: no next folder, only Start from Beginning or Close (Go to Next Folder means Close).
        if (_runInPlaylist)
        {
            if (s.PlayAskFirst)
            {
                var ask = new ChoiceDialog("This is the last file in the playlist.", new[] { "Start from Beginning", "Close" },
                                           defaultIndex: action == EndOfListAction.StartOver ? 0 : 1, cancelIndex: 1)
                {
                    Owner = this,
                    CountdownSeconds = Math.Clamp(s.PlayTimeout, 0, 600),
                };
                ask.Topmost = true;   // a video may be playing full screen in front of the explorer
                ask.ShowDialog();
                action = ask.Result == 0 ? EndOfListAction.StartOver : EndOfListAction.Close;
            }
            return action == EndOfListAction.StartOver ? items : null;
        }

        string? next = null;
        string? folder = VM.ActiveTab.CurrentPath;
        if (!string.IsNullOrEmpty(folder))
        {
            var shared = VM.Shared;
            var root = VM.TreeRootPath;
            next = await Task.Run(() => NextFolderFinder.Find(folder, shared.GetTreeSort, shared.ShowsHiddenInTree, CancellationToken.None, root,
                                                              name => ThumbnailLoader.IsImage(name) || MediaPlayerLauncher.IsMedia(name)));
        }

        if (s.PlayAskFirst)
        {
            string text = "This is the last file in this folder.\n\n" + (next != null ? "Next folder:\n" + next : "There is no next folder.");
            var dialog = new ChoiceDialog(text, new[] { "Go to Next Folder", "Start from Beginning", "Close" }, defaultIndex: (int)action, cancelIndex: 2)
            {
                Owner = this,
                CountdownSeconds = Math.Clamp(s.PlayTimeout, 0, 600),
            };
            dialog.SetButtonEnabled(0, next != null);
            dialog.Topmost = true;   // a video may be playing full screen in front of the explorer
            dialog.ShowDialog();
            action = dialog.Result switch { 0 => EndOfListAction.NextFolder, 1 => EndOfListAction.StartOver, _ => EndOfListAction.Close };
        }

        switch (action)
        {
            case EndOfListAction.StartOver:
                return items;

            case EndOfListAction.NextFolder:
                if (next == null)
                {
                    if (!s.PlayAskFirst) MessageBox.Show(this, "There is no next folder.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                    return null;
                }
                VM.Navigate(next);   // the list, the tree and the address bar follow
                var list = PlaylistInList();
                return list.Count == 0 ? null : list;

            default:
                return null;
        }
    }

    private void PlayPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaWait != null) _mediaWait.TrySetResult(MediaOutcome.Previous);
        else MoveSelection(-1);
    }

    private void PlayNext_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaWait != null) _mediaWait.TrySetResult(MediaOutcome.Next);
        else MoveSelection(+1);
    }

    /// <summary>Selects the previous or next item of the list (Previous / Next of the bar when no playlist is playing).</summary>
    private void MoveSelection(int direction)
    {
        int count = FileListView.Items.Count;
        if (count == 0) return;

        int index = Math.Clamp(FileListView.SelectedIndex + direction, 0, count - 1);
        if (FileListView.SelectedIndex < 0) index = direction > 0 ? 0 : count - 1;
        FileListView.SelectedIndex = index;
        FileListView.ScrollIntoView(FileListView.SelectedItem);
    }
}
