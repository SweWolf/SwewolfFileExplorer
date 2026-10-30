using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// "A file with this name already exists": the file in the folder and the file being pasted side by side (size, date, which is newer or
/// larger), and Replace / Keep Both / Keep Both, Rename Old / Skip / Cancel with "Do this for all conflicts". Built in code (no XAML, so no event
/// connections to get wrong). Keep Both is the default button: it never loses anything.
/// </summary>
public sealed class ConflictDialog : Window
{
    private readonly CheckBox _forAll = new() { Content = "Do This for All Conflicts", Margin = new Thickness(0, 16, 0, 0) };

    public ConflictChoice Choice { get; private set; } = ConflictChoice.Cancel;
    public bool ForAll => _forAll.IsChecked == true;

    public ConflictDialog(ConflictInfo info)
    {
        Title = $"{(info.IsMove ? "Move" : "Copy")} {(info.MultipleFiles ? "Files" : "File")}";
        FontSize = 16;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 640;
        MaxWidth = 900;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;

        var panel = new StackPanel { Margin = new Thickness(28) };

        panel.Children.Add(new TextBlock
        {
            Text = $"A file named \"{info.Name}\" already exists in this folder.",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = info.Folder,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 16),
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 2; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddRow(grid, 0, "Existing file:", Describe(info.ExistingSize, info.ExistingWritten, null));
        AddRow(grid, 1, info.Where + ":", Describe(info.SourceSize, info.SourceWritten, info));
        panel.Children.Add(grid);

        panel.Children.Add(new TextBlock
        {
            Text = info.ReplaceIsPermanent
                ? $"Replace deletes the existing file for good: {info.PermanentReason ?? "it cannot go to the Recycle Bin"}."
                : "Replace puts the existing file in the Recycle Bin, once the new one is complete.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
        });
        var oldName = Path.GetFileName(FileReplace.OldNameFor(Path.Combine(info.Folder, info.Name)));
        var newName = Path.GetFileName(FileTransfer.GetUniqueDestPath(info.Folder, info.Name));
        panel.Children.Add(new TextBlock
        {
            Text = $"Keep Both names the pasted file \"{newName}\". Keep Both, Rename Old gives the pasted file the name \"{info.Name}\" and renames the existing file to \"{oldName}\".",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
        _forAll.Visibility = info.MultipleFiles ? Visibility.Visible : Visibility.Collapsed;   // one file: nothing to apply it to
        panel.Children.Add(_forAll);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0) };
        Button Make(string text, ConflictChoice choice, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(20, 8, 20, 8), Margin = new Thickness(8, 0, 8, 0), MinWidth = 120, IsDefault = isDefault, IsCancel = isCancel };
            if (isDefault)   // the default button shows it, as in the other dialogs
            {
                b.SetResourceReference(Control.BorderBrushProperty, SystemColors.AccentColorBrushKey);
                b.BorderThickness = new Thickness(2);
            }
            b.Click += (_, _) => { Choice = choice; DialogResult = choice != ConflictChoice.Cancel; Close(); };
            return b;
        }
        buttons.Children.Add(Make(info.ReplaceIsPermanent ? "Replace Permanently" : "Replace", ConflictChoice.Replace));
        buttons.Children.Add(Make("Keep Both", ConflictChoice.KeepBoth, isDefault: true));
        buttons.Children.Add(Make("Keep Both, Rename Old", ConflictChoice.KeepBothRenameOld));
        buttons.Children.Add(Make("Skip", ConflictChoice.Skip));
        buttons.Children.Add(Make("Cancel", ConflictChoice.Cancel, isCancel: true));
        panel.Children.Add(buttons);

        Content = panel;

        Loaded += (_, _) => { foreach (var b in buttons.Children.OfType<Button>().Where(x => x.IsDefault)) b.Focus(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Choice = ConflictChoice.Cancel; Close(); } };
    }

    private static void AddRow(Grid grid, int row, string label, string text)
    {
        var l = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 16, 10) };
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(l, row); Grid.SetColumn(l, 0);
        Grid.SetRow(t, row); Grid.SetColumn(t, 1);
        grid.Children.Add(l);
        grid.Children.Add(t);
    }

    /// <summary>"1.2 MB (1,234,567 bytes), modified 2026-10-20 14:05" and, for the pasted file, how it compares with the existing one.</summary>
    private static string Describe(long size, DateTime? written, ConflictInfo? compare)
    {
        var text = size >= 0 ? $"{FormatSize(size)}" : "size not known";
        if (written is { } w) text += $", modified {Converters.DateTimeConverter.Format(w)}";

        if (compare != null)
        {
            var notes = new List<string>();
            if (compare.SourceWritten is { } sw)
            {
                var diff = sw - compare.ExistingWritten;
                notes.Add(Math.Abs(diff.TotalSeconds) < 2 ? "same date" : diff > TimeSpan.Zero ? "newer" : "older");
            }
            if (compare.SourceSize >= 0)
                notes.Add(compare.SourceSize == compare.ExistingSize ? "same size" : compare.SourceSize > compare.ExistingSize ? "larger" : "smaller");
            if (notes.Count > 0) text += "  (" + string.Join(", ", notes) + ")";
        }
        return text;
    }

    private static string FormatSize(long size) =>
        size < 1024 ? $"{size} bytes"
        : size < 1024 * 1024 ? $"{size / 1024.0:F1} KB ({size:N0} bytes)"
        : size < 1024L * 1024 * 1024 ? $"{size / 1024.0 / 1024.0:F1} MB ({size:N0} bytes)"
        : $"{size / 1024.0 / 1024.0 / 1024.0:F2} GB ({size:N0} bytes)";
}
