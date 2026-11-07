using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// The question before the explorer starts a program (Settings > Security): the name and the whole path at once, then what can be read
/// from the file (version information, size, date, where it was downloaded from) filled in from a background thread, so the question never
/// waits for a slow disk. Start (the default) or Cancel; for a program inside a compressed file Cancel is the default and the text is
/// stronger. Two checkboxes, applied only when Start is pressed: "this file" (kept in the library, by path) and "any file" (the Settings
/// checkbox; for a program inside a compressed file it is the separate "inside a compressed file" setting). Built in code.
/// Plan: D:\Dev\dev-notes\FileExplorer-ask-before-starting-programs-plan.md
/// </summary>
public sealed class StartProgramDialog : Window
{
    private readonly TextBlock _details = new() { Opacity = 0.85, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), MaxWidth = 640, Text = "Reading details..." };
    private readonly CheckBox _thisFile = new() { Margin = new Thickness(0, 14, 0, 0) };
    private readonly CheckBox _anyFile = new() { Content = "Don't Ask Again for Any File", Margin = new Thickness(0, 6, 0, 0) };

    private readonly CheckBox _fileType = new() { Margin = new Thickness(0, 6, 0, 0) };

    /// <summary>Set by the main window: checks a file on VirusTotal (the right-click menu's Send to VirusTotal), with the given window as the owner of
    /// its messages. When it is not set (a test) the dialog has no such button.</summary>
    public static Func<string, Window, Task>? VirusTotalCheck { get; set; }

    private readonly CheckBox _thisArchive = new() { Margin = new Thickness(0, 6, 0, 0) };

    /// <summary>The packed file on the disk (the outermost one) that holds the program, or null for a program on the disk.</summary>
    public string? DiskArchive { get; }

    public bool DontAskForThisArchive => _thisArchive.IsChecked == true;
    public bool DontAskForThisFile => _thisFile.IsChecked == true;
    public bool DontAskForThisFileType => _fileType.IsChecked == true;
    public bool DontAskForAnyFile => _anyFile.IsChecked == true;

    /// <param name="program">The file to start (for a program inside a compressed file, the unpacked copy: details are read from it).</param>
    /// <param name="archivePath">The path in the compressed file, or null for a program on the disk.</param>
    /// <param name="argumentCount">How many files are given to it (a drop on a program), 0 if none.</param>
    public StartProgramDialog(string program, string? archivePath, int argumentCount)
    {
        bool inArchive = archivePath != null;
        if (inArchive) DiskArchive = ArchiveSupport.DiskArchiveOf(archivePath!);
        Title = "Security Check";
        FontSize = 14;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 760;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(24) };

        var head = new DockPanel();
        var icon = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        var shownName = Path.GetFileName(archivePath ?? program);
        try { icon.Source = IconHelper.GetLargeIconForName(shownName); } catch { }   // the icon of the file type at once (no disk access)
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var question = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 600 };
        question.Inlines.Add(new System.Windows.Documents.Run(inArchive
            ? "This program is inside a compressed file. Do you want to start it?"
            : "Do you want to start this program?") { FontWeight = FontWeights.SemiBold });
        head.Children.Add(question);
        panel.Children.Add(head);

        panel.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(archivePath ?? program),
            FontWeight = FontWeights.Bold, FontSize = 16, Margin = new Thickness(46, 10, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 600,
        });
        panel.Children.Add(new TextBox
        {
            Text = archivePath ?? program, IsReadOnly = true, BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(46, 2, 0, 0), MaxWidth = 600,
        });
        if (argumentCount > 0)
            panel.Children.Add(new TextBlock
            {
                Text = argumentCount == 1 ? "It is given the file you dropped on it." : $"It is given the {argumentCount} files you dropped on it.",
                Margin = new Thickness(46, 6, 0, 0), Opacity = 0.85,
            });
        _details.Margin = new Thickness(46, 10, 0, 0);
        panel.Children.Add(_details);

        if (inArchive)
            panel.Children.Add(new TextBlock
            {
                Text = "A program from a compressed file has not been checked by Windows. Only start it if you trust where the compressed file came from.",
                TextWrapping = TextWrapping.Wrap, MaxWidth = 600, Margin = new Thickness(46, 10, 0, 0), Foreground = Brushes.IndianRed,
            });

        // Send to VirusTotal, as in the right-click menu (only this file: a program's DLLs and other files are not looked at).
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(46, 14, 0, 0) };
        if (VirusTotalCheck is { } check)
        {
            var panelRow = new StackPanel { Orientation = Orientation.Horizontal };
            if (VirusTotal.Icon is { } vtIcon) panelRow.Children.Add(new Image { Source = vtIcon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
            panelRow.Children.Add(new TextBlock { Text = "Send to VirusTotal", VerticalAlignment = VerticalAlignment.Center });
            var vt = new Button
            {
                Content = panelRow, Padding = new Thickness(12, 5, 12, 5),
                ToolTip = "Works out the SHA-256 of this file and checks it on VirusTotal, as in the right-click menu. Only this file is checked, not the files (such as DLL files) the program uses.",
            };
            vt.Click += async (_, _) =>
            {
                vt.IsEnabled = false;
                try { await check(program, this); }
                finally { vt.IsEnabled = true; }
            };
            tools.Children.Add(vt);
        }

        // A script (a batch file, Python, VBScript ...) can be read before it is started: "Open in Visual Studio Code" or "Open in Notepad", the same
        // editor as for .nfo files (the one in settings.json, else VS Code, else Notepad). Not for programs (binary files).
        var foundEditor = TextEditor.FindFor(shownName);
        void AddEditButton(TextEditor.Editor editor)
        {
            var edit = new Button
            {
                Content = ButtonContent(editor.Icon, "Open in " + editor.Name), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(tools.Children.Count > 0 ? 8 : 0, 0, 0, 0),
                ToolTip = "Opens this script in " + editor.Name + ", so you can read what it does before you start it. The file is not changed.",
            };
            edit.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(editor.Path) { ArgumentList = { program }, UseShellExecute = false }); }
                catch (Exception ex) { MessageBox.Show(this, $"Cannot open the file in {editor.Name}: {ex.Message}", "Security Check", MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
            tools.Children.Add(edit);
            tools.Visibility = Visibility.Visible;
        }
        // A known script type gets the button at once. A file type of the user's own (the "other" box in Settings) gets it when a quick look at the start of
        // the file, done in the background after the question is up, says it is text (see below).
        if (foundEditor != null && ProgramTypes.IsScript(shownName)) AddEditButton(foundEditor);
        tools.Visibility = tools.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(tools);

        _thisFile.Content = "Don't Ask Again for This File";
        _thisFile.Margin = new Thickness(46, 14, 0, 0);
        panel.Children.Add(_thisFile);
        // "This file type" names the kind and its extensions exactly (the kinds of Settings > Security). Not for a program inside a compressed file:
        // the list is the same for programs on the disk, so a program from an archive is only stopped by its own choice or by its own setting.
        if (!inArchive && ProgramTypes.KindOf(program) is { } kind)
        {
            _fileType.Content = "Don't Ask Again for " + kind.Label;
            _fileType.ToolTip = "Takes " + string.Join(", ", kind.Extensions.Select(e => "." + e)) + " off the list of file types that count as programs. You can put them back in Tools > Settings > Security.";
            _fileType.Margin = new Thickness(46, 6, 0, 0);
            panel.Children.Add(_fileType);
        }
        // For a program inside a compressed file: "any program inside THIS compressed file", named by the packed file on the disk (in the middle, the user's idea).
        if (inArchive && DiskArchive != null)
        {
            _thisArchive.Content = "Don't Ask Again for Any Program Inside " + Path.GetFileName(DiskArchive);
            _thisArchive.ToolTip = "Programs inside " + DiskArchive + " start without this question from now on. You can undo it in Tools > Settings > Security (Ask Again About All Programs).";
            _thisArchive.Margin = new Thickness(46, 6, 0, 0);
            panel.Children.Add(_thisArchive);
        }
        _anyFile.Margin = new Thickness(46, 6, 0, 0);
        if (inArchive) _anyFile.Content = "Don't Ask Again for Any Program Inside a Compressed File";
        panel.Children.Add(_anyFile);
        // One short line for all of the boxes instead of a text after one of them.
        panel.Children.Add(new TextBlock
        {
            Text = "You can change these choices in Tools > Settings > Security.",
            FontSize = 12, Opacity = 0.7, Margin = new Thickness(46, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 600,
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var start = new Button { Content = "Start", MinWidth = 115, Padding = new Thickness(16, 6, 16, 6), IsDefault = !inArchive };
        var cancel = new Button { Content = "Cancel", MinWidth = 115, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true, IsDefault = inArchive };
        foreach (var b in new[] { inArchive ? cancel : start })   // the default button shows it, as in the other dialogs
        {
            b.SetResourceReference(Control.BorderBrushProperty, SystemColors.AccentColorBrushKey);
            b.BorderThickness = new Thickness(2);
        }
        start.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(start);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => (inArchive ? cancel : start).Focus();
        Loaded += async (_, _) =>
        {
            // The question is on screen already; the details come when the disk has answered (a slow or sleeping drive must not hold it up).
            // A program with an icon of its own (an .exe, a .scr ...) shows that one instead of the icon of its type, as soon as it is read.
            if (IconHelper.HasOwnIcon(shownName))
                _ = Task.Run(() => IconHelper.GetIcon(program, small: false)).ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && t.Result is { } own) icon.Source = own;
                }, TaskScheduler.FromCurrentSynchronizationContext());
            var text = await Task.Run(() => DetailsOf(program, archivePath));
            _details.Text = text;

            if (foundEditor != null && !ProgramTypes.IsScript(shownName) && !ProgramTypes.IsBinaryType(shownName)
                && await Task.Run(() => ProgramTypes.LooksLikeText(program)))
                AddEditButton(foundEditor);
        };
    }

    /// <summary>A button's content: a 16 px icon (when there is one) and the text.</summary>
    private static StackPanel ButtonContent(ImageSource? icon, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null) row.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    /// <summary>What can be said about the file: version information, size, date, where it was downloaded from.</summary>
    public static string DetailsOf(string program, string? archivePath)
    {
        var lines = new List<string>();
        try
        {
            var info = new FileInfo(program);
            if (!info.Exists) return "The file is not there any more.";

            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(program);
            void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) lines.Add($"{label}: {value.Trim()}"); }
            if (v.ProductName is { } product && !product.Contains("Operating System", StringComparison.OrdinalIgnoreCase))
                Add("Product", product);   // Windows' own programs all say "Microsoft Windows Operating System": the description names them
            Add("Description", v.FileDescription);
            Add("Company", v.CompanyName);
            // "0.1.0+fe40263d..." : the part after the plus is the source revision a build tool added, no use to the reader.
            var version = v.ProductVersion ?? v.FileVersion;
            if (version != null && version.IndexOf('+') is var plus and > 0) version = version[..plus];
            Add("Version", version);
            lines.Add($"Size: {DriveRow.FormatSize(info.Length)}");
            lines.Add($"Date modified: {info.LastWriteTime:yyyy-MM-dd HH:mm}");

            // Where it was downloaded from: the file's own Mark of the Web, or the compressed file's (everything in it came with that download).
            var origin = MarkOfTheWeb.Read(archivePath != null ? ArchiveSupport.DiskArchiveOf(archivePath) ?? "" : program);
            if (origin?.HostUrl is { Length: > 0 } host) lines.Add($"Downloaded from: {host}");
            else if (origin?.ZoneId is 3 or 4) lines.Add("Downloaded from the internet");
        }
        catch { /* what could not be read is left out */ }
        return lines.Count == 0 ? "No details could be read." : string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The question as the explorer asks it. True when the program may start: it is not one (by the list in Settings > Security), the
    /// question is turned off or was turned off for this file, or the user pressed Start. The checkboxes are saved only on Start.
    /// </summary>
    /// <param name="program">The file that will start (the unpacked copy for a program from a compressed file).</param>
    /// <param name="archivePath">The path inside the compressed file, else null.</param>
    public static bool Confirm(string program, string? archivePath = null, int argumentCount = 0)
    {
        var settings = AppSettingsStore.Current;
        var shown = archivePath ?? program;
        if (!ProgramTypes.IsProgram(shown)) return true;

        bool inArchive = archivePath != null;
        if (!(inArchive ? settings.AskBeforeStartingProgramsInArchives : settings.AskBeforeStartingPrograms)) return true;   // two settings, one for each kind
        if (LibraryStore.GetNoAskStart(shown)) return true;
        var diskArchive = inArchive ? ArchiveSupport.DiskArchiveOf(archivePath!) : null;
        if (diskArchive != null && LibraryStore.GetNoAskStart(diskArchive)) return true;   // "any program inside this compressed file": the choice sits on the packed file

        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
                    ?? Application.Current?.MainWindow;
        var dialog = new StartProgramDialog(program, archivePath, argumentCount) { Owner = owner is { IsVisible: true } ? owner : null };
        if (dialog.Owner == null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (dialog.ShowDialog() != true) return false;

        if (dialog.DontAskForThisFile) LibraryService.SetNoAskStart(shown);
        if (dialog.DontAskForThisArchive && diskArchive != null) LibraryService.SetNoAskStart(diskArchive);
        if (dialog.DontAskForThisFileType && !inArchive)
        {
            settings.ProgramExtensions = ProgramTypes.RemoveKind(settings.ProgramExtensions, program);
            AppSettingsStore.Save(settings);
        }
        if (dialog.DontAskForAnyFile)
        {
            if (inArchive) settings.AskBeforeStartingProgramsInArchives = false;
            else settings.AskBeforeStartingPrograms = false;
            AppSettingsStore.Save(settings);
        }
        return true;
    }
}
