using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Small things every text box of the program's dialogs should do: a right-click menu with Clear (made in code: the standard menu has none), and
/// all the text selected when the box gets the keyboard focus with the Tab key (a click with the mouse puts the caret where it was clicked, as
/// everywhere in Windows).
/// </summary>
public static class TextBoxMenu
{
    /// <summary>Adds the menu (Cut, Copy, Paste, Select All, Clear) and the select-all-on-Tab behavior. A read-only box keeps its own menu.</summary>
    public static void Attach(TextBox box)
    {
        box.GotKeyboardFocus += (_, _) =>
        {
            // Not while the mouse button is down: that is a click, and the caret goes where the click was.
            if (Mouse.LeftButton != MouseButtonState.Pressed) box.SelectAll();
        };

        if (box.IsReadOnly) return;

        var menu = new ContextMenu();
        foreach (var command in new RoutedUICommand[] { ApplicationCommands.Cut, ApplicationCommands.Copy, ApplicationCommands.Paste })
            menu.Items.Add(new MenuItem { Command = command, CommandTarget = box });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Command = ApplicationCommands.SelectAll, CommandTarget = box });
        var clear = new MenuItem { Header = "Clear" };
        clear.Click += (_, _) => { box.Clear(); box.Focus(); };
        menu.Items.Add(clear);
        menu.Opened += (_, _) => clear.IsEnabled = box.Text.Length > 0;
        box.ContextMenu = menu;
    }
}
