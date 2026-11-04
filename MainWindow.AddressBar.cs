using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SwewolfFileExplorer;

/// <summary>
/// The width of the address bar (2026-11-03, asked by the user): 40 % wider than the old 456 px, and when the window gets narrower it is
/// the address bar that shrinks first, down to a minimum; only below that do the toolbar buttons go to the overflow arrow. A toolbar
/// cannot give one item "the rest of the room", so the width is worked out here from the room the other items need.
/// </summary>
public partial class MainWindow
{
    private const double AddressBarWidth    = 638;   // 456 x 1.4
    private const double AddressBarMinWidth = 150;

    private void MainToolBarTray_SizeChanged(object sender, SizeChangedEventArgs e) => FitAddressBar();

    /// <summary>Gives the address bar the room that is left in the toolbar after the other items, at most <see cref="AddressBarWidth"/>.</summary>
    internal void FitAddressBar()
    {
        if (MainToolBar == null || AddressFrame == null || !IsLoaded) return;

        // What the other items need at their natural size (measured, so an item that is in the overflow panel counts too).
        double others = 0;
        foreach (var item in MainToolBar.Items)
        {
            if (item is not FrameworkElement fe || ReferenceEquals(fe, AddressFrame)) continue;
            fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            others += fe.DesiredSize.Width;
        }

        double frame = AddressFrame.Margin.Left + AddressFrame.Margin.Right
                       + AddressFrame.BorderThickness.Left + AddressFrame.BorderThickness.Right
                       + AddressFrame.Padding.Left + AddressFrame.Padding.Right;
        double chrome = ToolBarChrome();

        double width = MainToolBarTray.ActualWidth - others - frame - chrome;
        width = Math.Clamp(Math.Floor(width), AddressBarMinWidth, AddressBarWidth);
        if (Math.Abs(AddressBar.Width - width) >= 1) AddressBar.Width = width;
    }

    /// <summary>The room the toolbar itself takes besides its items: the overflow button, the padding and the border.</summary>
    private double ToolBarChrome()
    {
        double overflow = MainToolBar.Template?.FindName("OverflowButton", MainToolBar) is FrameworkElement b ? b.ActualWidth : 16;
        return overflow + MainToolBar.Padding.Left + MainToolBar.Padding.Right
               + MainToolBar.BorderThickness.Left + MainToolBar.BorderThickness.Right + 4;
    }
}
