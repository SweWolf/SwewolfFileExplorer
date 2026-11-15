using System.Windows;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

public partial class SettingsWindow : Window
{
    private static readonly string[] Styles = { "Light", "Dark", "System", "Cyber Cyan" };

    // "When the Program Is Started Again": the box lists Use the Running Window, Start a New Window, Ask; the saved values are
    // SingleInstance.ModeUseRunning (1), ModeNewWindow (0), ModeAsk (2), so the box's order is not the value (an old settings.json stays valid).
    private static int StartAgainIndex(int value) => value switch
    {
        SingleInstance.ModeNewWindow => 1,
        SingleInstance.ModeAsk       => 2,
        _                            => 0,   // use the running window, also for an unknown value
    };

    private static int StartAgainValue(int index) => index switch
    {
        1 => SingleInstance.ModeNewWindow,
        2 => SingleInstance.ModeAsk,
        _ => SingleInstance.ModeUseRunning,
    };

    private const string DefaultFontText = "Default";

    private static string DefaultFontLine() =>
        $"{DefaultFontText} ({(Application.Current?.TryFindResource("ContentControlThemeFontFamily") as System.Windows.Media.FontFamily ?? SystemFonts.MessageFontFamily).Source})";

    /// <summary>Every installed font, each written in itself so the list shows what the user will get. A symbol font (Wingdings ...) would show
    /// only symbols, so its name is written in the usual font and the font is shown in a sample after it.</summary>
    private void FillFontList()
    {
        var chosen = (FontBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "";
        var items = new List<System.Windows.Controls.ComboBoxItem> { new() { Content = DefaultFontLine(), Tag = "" } };
        foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies.OrderBy(f => f.Source, StringComparer.CurrentCultureIgnoreCase))
        {
            // Only fonts that write letters: a symbol font (Wingdings ...) or an icon font (Segoe MDL2 Assets ...) means nothing here.
            bool letters;
            try
            {
                letters = new System.Windows.Media.Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var g)
                          && !g.Symbol && g.CharacterToGlyphMap.ContainsKey('A') && g.CharacterToGlyphMap.ContainsKey('a') && g.CharacterToGlyphMap.ContainsKey('e');
            }
            catch { letters = false; }
            if (!letters) continue;
            var item = new System.Windows.Controls.ComboBoxItem { Content = family.Source, Tag = family.Source, FontFamily = family };
            items.Add(item);
        }
        FontBox.Items.Clear();
        foreach (var item in items) FontBox.Items.Add(item);
        FontBox.SelectedIndex = 0;   // a saved font that is not installed any more stays on the default
        for (int i = 1; i < items.Count; i++)
            if (string.Equals((string)items[i].Tag, chosen, StringComparison.OrdinalIgnoreCase)) { FontBox.SelectedIndex = i; break; }
    }

    public SettingsWindow()
    {
        InitializeComponent();

        // Never taller than the screen: the tab pages scroll when they do not fit (a low-resolution screen). The window is also kept
        // inside the working area, as it is centered on its owner and a tall window could end up with its buttons below the screen.
        var work = SystemParameters.WorkArea;
        MaxHeight = work.Height - 24;
        Loaded += (_, _) =>
        {
            if (Top + ActualHeight > work.Bottom) Top = work.Bottom - ActualHeight;
            if (Top < work.Top) Top = work.Top;
        };

        var current = AppSettingsStore.Current;
        StyleBox.SelectedIndex = Math.Max(0, Array.IndexOf(Styles, current.Style));
        // The font list: the first line is the program's usual font. Only that and the chosen font are there at first; the whole list
        // (every font written in itself) is made once the window is up, as making it takes about a second.
        FontBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = DefaultFontLine(), Tag = "" });
        if (!string.IsNullOrWhiteSpace(current.FontFamily))
            FontBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = current.FontFamily, Tag = current.FontFamily });
        FontBox.SelectedIndex = FontBox.Items.Count - 1;
        Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, FillFontList);
        UnreadBoldBox.IsChecked   = current.UnreadBold;
        UnreadItalicBox.IsChecked = current.UnreadItalic;
        HiddenExtensionsBox.Text  = current.HiddenExtensions;
        HiddenExtensionsInArchivesBox.Text = current.HiddenExtensionsInArchives;
        HideShellExtensionsBox.IsChecked   = current.HideShellExtensions;
        HideKnownExtensionsBox.IsChecked   = current.HideKnownExtensions;
        OwnFileTypesBox.IsChecked          = current.UseOwnFileTypeNames;
        OwnFileIconsBox.IsEnabled          = FileTypeOverrides.IsWindows11;
        OwnFileIconsBox.IsChecked          = FileTypeOverrides.IsWindows11 && current.UseOwnFileTypeIcons;
        if (!FileTypeOverrides.IsWindows11) OwnFileIconsBox.ToolTip = "These icons are the ones of Windows 11, so this choice is only available there. On this Windows the icons are as Windows Explorer shows them.";
        RegisteredTypeNamesBox.IsChecked   = current.UseRegisteredTypeNames;
        SuggestMoveBox.IsChecked           = current.SuggestMoveAfterSmartRename;
        RightClickSelectsNodeBox.IsChecked = current.RightClickSelectsTreeNode;
        DisableDragDropBox.IsChecked       = current.DisableInternalDragDrop;
        PreloadPlayerBox.IsChecked         = current.PreloadMediaPlayer;
        CheckUpdatesBox.IsChecked          = current.CheckForUpdatesOnStartup;
        StartAgainBox.SelectedIndex        = StartAgainIndex(current.WhenStartedAgain);
        (current.RoundedProgressBars ? RoundedBarsBox : SquareBarsBox).IsChecked = true;
        VirusTotalKeyBox.Password          = VirusTotal.GetApiKey() ?? "";
        TmdbKeyBox.Password                = Tmdb.GetApiKey() ?? "";
        MediaPlayerTab.Visibility          = MediaPlayerLauncher.FindPlayer() != null ? Visibility.Visible : Visibility.Collapsed;   // no player, no tab
        WarnHiddenNotSelectedBox.IsChecked = current.WarnHiddenNotSelected;
        AskBeforeProgramsBox.IsChecked     = current.AskBeforeStartingPrograms;
        AskInArchivesBox.IsChecked         = current.AskBeforeStartingProgramsInArchives;
        foreach (var (label, extensions) in ProgramTypes.Groups)
            ProgramKindsPanel.Children.Add(new System.Windows.Controls.CheckBox
            {
                Content = label, Margin = new Thickness(0, 2, 12, 2),
                ToolTip = "Counts as programs: " + string.Join(", ", extensions.Select(e => "." + e)),
            });
        ShowProgramTypes(current.ProgramExtensions);
        MakeExtensionsMenu();
        TerminalBox.IsChecked              = current.ShowOpenInTerminal;
        PowerShellBox.IsChecked            = current.ShowOpenInPowerShell;
        CommandPromptBox.IsChecked         = current.ShowOpenInCommandPrompt;

        // A program that is not installed is never offered in the menu, so its box cannot do anything.
        foreach (var (box, kind, name) in new[]
                 { (TerminalBox, TerminalKind.WindowsTerminal, "Windows Terminal"), (PowerShellBox, TerminalKind.PowerShell, "Windows PowerShell"),
                   (CommandPromptBox, TerminalKind.CommandPrompt, "The Command Prompt") })
        {
            if (TerminalLauncher.IsInstalled(kind)) continue;
            box.IsEnabled = false;
            box.ToolTip   = $"{name} is not installed on this computer, so it is not shown in the menu.";
        }
    }

    /// <summary>The eye button: a PasswordBox cannot show its text, so a plain TextBox takes its place and the text is passed across.</summary>
    private void VirusTotalKeyEye_Changed(object sender, RoutedEventArgs e)
    {
        bool show = VirusTotalKeyEye.IsChecked == true;
        if (show)
        {
            VirusTotalKeyText.Text = VirusTotalKeyBox.Password;
            VirusTotalKeyBox.Visibility = Visibility.Collapsed;
            VirusTotalKeyText.Visibility = Visibility.Visible;
            VirusTotalKeyText.Focus();
            VirusTotalKeyText.CaretIndex = VirusTotalKeyText.Text.Length;
        }
        else
        {
            VirusTotalKeyBox.Password = VirusTotalKeyText.Text;
            VirusTotalKeyText.Visibility = Visibility.Collapsed;
            VirusTotalKeyBox.Visibility = Visibility.Visible;
            VirusTotalKeyBox.Focus();
        }
        VirusTotalKeyEye.Content = show ? "" : "";   // eye / eye with a slash
        VirusTotalKeyEye.ToolTip = show ? "Hide the API key" : "Show the API key";
    }

    private void TmdbKeyEye_Changed(object sender, RoutedEventArgs e)
    {
        bool show = TmdbKeyEye.IsChecked == true;
        if (show)
        {
            TmdbKeyText.Text = TmdbKeyBox.Password;
            TmdbKeyBox.Visibility = Visibility.Collapsed;
            TmdbKeyText.Visibility = Visibility.Visible;
            TmdbKeyText.Focus();
            TmdbKeyText.CaretIndex = TmdbKeyText.Text.Length;
        }
        else
        {
            TmdbKeyBox.Password = TmdbKeyText.Text;
            TmdbKeyText.Visibility = Visibility.Collapsed;
            TmdbKeyBox.Visibility = Visibility.Visible;
            TmdbKeyBox.Focus();
        }
        TmdbKeyEye.Content = show ? "" : "";   // eye with a slash / eye
        TmdbKeyEye.ToolTip = show ? "Hide the API key" : "Show the API key";
    }

    private void TmdbKeyLink_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Tmdb.KeyPage, UseShellExecute = true }); }
        catch { }
    }

    private void VirusTotalKeyLink_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = VirusTotal.KeyPage, UseShellExecute = true }); }
        catch { }
    }

    /// <summary>Checks the kinds that a list contains and puts the rest in the "other" box.</summary>
    private void ShowProgramTypes(string list)
    {
        var (checks, other) = ProgramTypes.Split(list);
        var boxes = ProgramKindsPanel.Children.OfType<System.Windows.Controls.CheckBox>().ToList();
        for (int i = 0; i < boxes.Count; i++) boxes[i].IsChecked = checks[i];
        ProgramExtensionsBox.Text = other;
    }

    /// <summary>The usual text box menu plus Clear (made in code: a menu with handlers does not belong in a style).</summary>
    private void MakeExtensionsMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var command in new System.Windows.Input.RoutedUICommand[] { System.Windows.Input.ApplicationCommands.Cut, System.Windows.Input.ApplicationCommands.Copy, System.Windows.Input.ApplicationCommands.Paste })
            menu.Items.Add(new System.Windows.Controls.MenuItem { Command = command, CommandTarget = ProgramExtensionsBox });
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Command = System.Windows.Input.ApplicationCommands.SelectAll, CommandTarget = ProgramExtensionsBox });
        var clear = new System.Windows.Controls.MenuItem { Header = "Clear" };
        clear.Click += (_, _) => { ProgramExtensionsBox.Clear(); ProgramExtensionsBox.Focus(); };
        menu.Items.Add(clear);
        ProgramExtensionsBox.ContextMenu = menu;
    }

    private const string ForgetCaption = "Security Settings - Ask Again About All Programs";

    private void ForgetChoices_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "You will be asked about every program again, also the ones you told the program not to ask about." + Environment.NewLine + Environment.NewLine
                + "Do you want to continue?", ForgetCaption, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        LibraryService.Flush();   // a choice still waiting to be written must not come back
        var count = LibraryStore.ClearAllNoAskStart();
        MessageBox.Show(this, count == 0 ? "No program was set to start without a question."
                                         : count == 1 ? "1 program will be asked about again."
                                                      : $"{count} programs will be asked about again.",
                        ForgetCaption, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var key = (VirusTotalKeyEye.IsChecked == true ? VirusTotalKeyText.Text : VirusTotalKeyBox.Password).Trim();
        if (key.Length > 0 && !VirusTotal.IsValidKeyFormat(key))
        {
            MessageBox.Show(this, "A VirusTotal API key has 64 letters and digits. Copy it again from VirusTotal's website, or leave the box empty.",
                            AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            (VirusTotalKeyEye.IsChecked == true ? (System.Windows.Controls.Control)VirusTotalKeyText : VirusTotalKeyBox).Focus();
            return;
        }

        var tmdbKey = (TmdbKeyEye.IsChecked == true ? TmdbKeyText.Text : TmdbKeyBox.Password).Trim();
        if (tmdbKey.Length > 0 && !Tmdb.IsValidKeyFormat(tmdbKey))
        {
            MessageBox.Show(this, "A TMDB API key has 32 letters and digits, and the API Read Access Token is a long text with two dots in it. Copy it again from your TMDB account, or leave the box empty.",
                            AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            (TmdbKeyEye.IsChecked == true ? (System.Windows.Controls.Control)TmdbKeyText : TmdbKeyBox).Focus();
            return;
        }

        var settings = AppSettingsStore.Current;
        VirusTotal.SetApiKey(key);
        Tmdb.SetApiKey(tmdbKey);
        settings.Style = Styles[StyleBox.SelectedIndex];
        settings.FontFamily = (FontBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "";
        settings.UnreadBold   = UnreadBoldBox.IsChecked == true;
        settings.UnreadItalic = UnreadItalicBox.IsChecked == true;
        settings.HiddenExtensions = string.Join(";", HiddenItems.Parse(HiddenExtensionsBox.Text));   // tidied: no dots or blanks
        settings.HiddenExtensionsInArchives = string.Join(";", HiddenItems.Parse(HiddenExtensionsInArchivesBox.Text));
        settings.HideShellExtensions = HideShellExtensionsBox.IsChecked == true;
        settings.HideKnownExtensions = HideKnownExtensionsBox.IsChecked == true;
        settings.UseOwnFileTypeNames = OwnFileTypesBox.IsChecked == true;
        if (FileTypeOverrides.IsWindows11) settings.UseOwnFileTypeIcons = OwnFileIconsBox.IsChecked == true;   // on Windows 10 the saved choice is left alone
        FileTypeOverrides.SetBuiltIn(settings.UseOwnFileTypeNames, settings.UseOwnFileTypeIcons);   // forgets the names and icons found so far
        settings.UseRegisteredTypeNames = RegisteredTypeNamesBox.IsChecked == true;
        IconHelper.UseRegisteredTypeNames = settings.UseRegisteredTypeNames;   // forgets the type names found so far
        settings.SuggestMoveAfterSmartRename = SuggestMoveBox.IsChecked == true;
        settings.RightClickSelectsTreeNode = RightClickSelectsNodeBox.IsChecked == true;
        settings.DisableInternalDragDrop = DisableDragDropBox.IsChecked == true;
        settings.PreloadMediaPlayer      = PreloadPlayerBox.IsChecked == true;
        settings.CheckForUpdatesOnStartup = CheckUpdatesBox.IsChecked == true;
        settings.WhenStartedAgain         = StartAgainValue(StartAgainBox.SelectedIndex);
        settings.RoundedProgressBars     = RoundedBarsBox.IsChecked == true;
        settings.WarnHiddenNotSelected   = WarnHiddenNotSelectedBox.IsChecked == true;
        settings.AskBeforeStartingPrograms = AskBeforeProgramsBox.IsChecked == true;
        settings.AskBeforeStartingProgramsInArchives = AskInArchivesBox.IsChecked == true;
        settings.ProgramExtensions         = ProgramTypes.Join(ProgramKindsPanel.Children.OfType<System.Windows.Controls.CheckBox>().Select(c => c.IsChecked == true).ToList(),
                                                               ProgramExtensionsBox.Text);   // tidied: no dots or blanks
        settings.ShowOpenInTerminal      = TerminalBox.IsChecked == true;
        settings.ShowOpenInPowerShell    = PowerShellBox.IsChecked == true;
        settings.ShowOpenInCommandPrompt = CommandPromptBox.IsChecked == true;
        AppSettingsStore.Save(settings);

        ThemeService.Apply(settings.Style);
        UnreadStyle.Instance.Refresh();
        DialogResult = true;
    }
}
