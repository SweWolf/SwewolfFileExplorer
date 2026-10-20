using System.Windows;
using System.Windows.Threading;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// The window of a long copy or move: what is being done, "File 3 of 5", the file's name, a bar and Cancel.
/// It reads a <see cref="TransferProgress"/> ten times a second; <see cref="Finish"/> closes it when the transfer is over.
/// Esc, the close button and Cancel all ask the transfer to stop (the window closes when it has).
/// </summary>
public partial class ProgressDialog : Window
{
    private readonly TransferProgress _progress;
    private readonly Action _cancel;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _finished, _canceling;

    public ProgressDialog(string title, string heading, TransferProgress progress, Action cancel)
    {
        _progress = progress;
        _cancel   = cancel;
        InitializeComponent();
        Title            = title;
        HeadingText.Text = heading;
        Update();
        _timer.Tick += (_, _) => Update();
        _timer.Start();
        Closing += OnClosing;
    }

    /// <summary>The transfer is over: close the window.</summary>
    public void Finish()
    {
        _finished = true;
        _timer.Stop();
        Close();
    }

    private void Update()
    {
        if (_canceling) return;

        if (_progress.Preparing)
        {
            FileCountText.Text = "Preparing...";
            FileNameText.Text  = "";
            Bar.IsIndeterminate = true;
            PercentText.Text = "";
            return;
        }

        FileCountText.Text = $"File {Math.Min(_progress.FileIndex, _progress.FileTotal)} of {_progress.FileTotal}";
        FileNameText.Text  = _progress.CurrentName;

        long total = _progress.BytesTotal;
        if (total > 0)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = Math.Min(100.0, 100.0 * _progress.BytesDone / total);
            PercentText.Text = $"{FormatSize(_progress.BytesDone)} of {FormatSize(total)}";
        }
        else
        {
            // Only renames (nothing to copy): the bar follows the file count.
            Bar.IsIndeterminate = false;
            Bar.Value = 100.0 * Math.Min(_progress.FileIndex, _progress.FileTotal) / _progress.FileTotal;
            PercentText.Text = "";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => RequestCancel();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_finished) return;
        e.Cancel = true;   // not yet: the transfer must stop first; Finish() closes the window
        RequestCancel();
    }

    private void RequestCancel()
    {
        if (_canceling) return;
        _canceling = true;
        CancelButton.IsEnabled = false;
        FileCountText.Text = "Canceling...";
        _cancel();
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{bytes} bytes" : $"{size:0.0} {units[unit]}";   // always one decimal: "74.0", not "74", so the text keeps its width
    }
}
