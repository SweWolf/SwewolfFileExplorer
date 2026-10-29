using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The right-click menus of the Fluent theme are tall (a menu item is about 35 px high). Every item of a menu (not the items of the menu
/// bar itself) is made <see cref="ReducePerItem"/> pixel(s) lower: half a pixel is taken off at the top and at the bottom, as a negative
/// margin, so the theme's own style and template are left alone (a style of our own for <c>MenuItem</c> would replace them: the same
/// trap as for buttons, see "Dark mode" in CLAUDE.md). Menus made in code are covered too. Change the constant to change the spacing.
/// </summary>
public static class MenuSpacing
{
    /// <summary>How many pixels lower every menu item gets.</summary>
    public const double ReducePerItem = 4.0;

    private static readonly ConditionalWeakTable<MenuItem, object> Done = new();

    /// <summary>Call once at the start of the program.</summary>
    public static void Install()
    {
        // Loaded is not raised for the items of a menu that is only measured in a popup, so the menus are looked at when they open:
        // a right-click menu (its items) and a submenu of an item in such a menu (Send to ...).
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => Apply((ContextMenu)s)));
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler((s, e) =>
        {
            if (s is MenuItem owner && e.OriginalSource == owner && IsInContextMenu(owner)) Apply(owner);
        }));
    }

    private static bool IsInContextMenu(MenuItem item)
    {
        for (ItemsControl? parent = ItemsControl.ItemsControlFromItemContainer(item); parent != null; )
        {
            if (parent is ContextMenu) return true;
            parent = parent is MenuItem mi ? ItemsControl.ItemsControlFromItemContainer(mi) : null;
        }
        return false;
    }

    private static void Apply(ItemsControl menu)
    {
        foreach (var entry in menu.Items)
        {
            if (entry is not MenuItem item || Done.TryGetValue(item, out _)) continue;   // a separator, or already done (menu opened again)
            Done.Add(item, new object());
            var m = item.Margin;
            item.Margin = new Thickness(m.Left, m.Top - ReducePerItem / 2, m.Right, m.Bottom - ReducePerItem / 2);
        }
    }
}
