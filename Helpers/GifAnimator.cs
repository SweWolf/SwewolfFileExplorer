using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyFileExplorer.Helpers;

/// <summary>One frame of an animated GIF: its pixels, where they go on the canvas, how long it stays, and what happens after it.</summary>
public sealed record GifFrame(BitmapSource Source, Int32Rect Rect, int DelayMs, int Disposal);

/// <summary>
/// Puts the frames of an animated GIF together, one at a time and looping, the way a browser does:
/// a frame may cover only a part of the canvas, and the one before it may have to be removed
/// (disposal 2: clear its area, 3: go back to what was there before it). Use it on the UI thread.
/// </summary>
public sealed class GifAnimator
{
    private readonly IReadOnlyList<GifFrame> _frames;
    private readonly int _width;
    private readonly int _height;

    private int _next;
    private BitmapSource? _canvas;       // the canvas as the last frame left it
    private BitmapSource? _beforeLast;   // the canvas before the last frame was drawn
    private GifFrame? _last;

    public GifAnimator(IReadOnlyList<GifFrame> frames, int width, int height)
    {
        _frames = frames;
        _width  = width;
        _height = height;
    }

    /// <summary>The next picture of the animation, and how long to show it (in milliseconds).</summary>
    public BitmapSource Next(out int delayMs)
    {
        if (_next >= _frames.Count)   // the end: start over
        {
            _next = 0;
            _canvas = _beforeLast = null;
            _last = null;
        }

        var frame = _frames[_next++];

        // What the canvas looks like once the previous frame has been "disposed".
        var baseline = _canvas;
        if (_last != null)
        {
            if (_last.Disposal == 2)      baseline = Render(_canvas, null, _last.Rect);
            else if (_last.Disposal == 3) baseline = _beforeLast;
        }

        var composed = Render(baseline, frame, null);
        _beforeLast = baseline;
        _canvas     = composed;
        _last       = frame;

        delayMs = frame.DelayMs;
        return composed;
    }

    private BitmapSource Render(BitmapSource? baseline, GifFrame? frame, Int32Rect? clear)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var whole = new Rect(0, 0, _width, _height);

            if (baseline != null)
            {
                if (clear is { } c)
                {
                    var keep = Geometry.Combine(new RectangleGeometry(whole),
                        new RectangleGeometry(new Rect(c.X, c.Y, c.Width, c.Height)),
                        GeometryCombineMode.Exclude, null);
                    dc.PushClip(keep);
                    dc.DrawImage(baseline, whole);
                    dc.Pop();
                }
                else
                {
                    dc.DrawImage(baseline, whole);
                }
            }

            if (frame != null)
                dc.DrawImage(frame.Source, new Rect(frame.Rect.X, frame.Rect.Y, frame.Rect.Width, frame.Rect.Height));
        }

        var bitmap = new RenderTargetBitmap(_width, _height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
