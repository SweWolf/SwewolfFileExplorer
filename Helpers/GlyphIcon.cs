using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// A window icon drawn from a glyph of Windows' symbol font (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10: the
/// same code points in both). Nothing is shipped and nothing is read from a DLL, so it looks the same on both. White glyph on a dark
/// rounded square, so it shows on a light and on a colored title bar.
/// </summary>
public static class GlyphIcon
{
    public static ImageSource Make(string glyph, int size = 64)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)), null, new Rect(0, 0, size, size), size * 0.2, size * 0.2);
            var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size * 0.64, Brushes.White, 1.0);
            dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }
}
