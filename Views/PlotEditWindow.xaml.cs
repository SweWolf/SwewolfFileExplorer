using System.IO;
using System.Windows;
using System.Windows.Input;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// Edit Plot: a box for the plot of a video or music file, which is kept in its .nfo file (<see cref="NfoWriter"/>). Says which
/// file it will be saved in, and whether that file is made new. An empty text removes the plot. Enter makes a new line;
/// Ctrl+Enter is OK. The window does the saving: it stays open with a message if the file cannot be written.
/// </summary>
public partial class PlotEditWindow : Window
{
    private readonly string _mediaPath;

    public PlotEditWindow(string mediaPath)
    {
        InitializeComponent();

        _mediaPath = mediaPath;
        Title = $"Edit Plot - {Path.GetFileNameWithoutExtension(mediaPath)}";
        PromptText.Text = $"Plot of \"{Path.GetFileName(mediaPath)}\":";

        PlotBox.Text = NfoWriter.CurrentPlot(mediaPath);

        var target = NfoWriter.TargetFile(mediaPath, out bool exists);
        FileText.Text = exists
            ? $"It is saved in \"{Path.GetFileName(target)}\", next to the file. Nothing else in that file is changed."
            : $"There is no .nfo file yet: \"{Path.GetFileName(target)}\" will be made next to the file.";

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                OkButton_Click(this, new RoutedEventArgs());
            }
        };
        Loaded += (_, _) => { PlotBox.Focus(); PlotBox.CaretIndex = PlotBox.Text.Length; };
    }

    /// <summary>True if the plot was saved (or there was nothing to change).</summary>
    public bool Saved { get; private set; }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PlotBox.Text.Trim() != NfoWriter.CurrentPlot(_mediaPath).Trim())
                NfoWriter.SetPlot(_mediaPath, PlotBox.Text);

            Saved = true;
            DialogResult = true;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            PlotBox.Focus();
        }
    }
}
