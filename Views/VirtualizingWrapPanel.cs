using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SwewolfFileExplorer.Helpers;
using SwewolfFileExplorer.ViewModels;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// The panel of the Icons view: tiles of one fixed size, laid out in rows from the left, scrolling by pixels. It makes (virtualizes)
/// only the tiles that are on screen, so a folder of tens of thousands of files costs no more than a screenful, and it tells the
/// <see cref="ThumbnailService"/> which items are on screen so that only their thumbnails are read.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(140.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double ItemWidth  { get => (double)GetValue(ItemWidthProperty);  set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    /// <summary>Tiles per row at the moment (for the arrow keys).</summary>
    public int Columns => _columns;

    private int _columns = 1;
    private int _firstIndex, _lastIndex = -1;
    private Size _extent, _viewport;
    private Vector _offset;

    private ItemsControl? Owner => ItemsControl.GetItemsOwner(this);

    // ── Layout ────────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size available)
    {
        _ = InternalChildren;   // the item container generator exists only once this has been touched
        var owner = Owner;
        int count = owner?.Items.Count ?? 0;
        double itemW = Math.Max(1, ItemWidth), itemH = Math.Max(1, ItemHeight);

        double width = double.IsInfinity(available.Width) ? itemW : available.Width;
        double height = double.IsInfinity(available.Height) ? itemH * 6 : available.Height;
        _columns = Math.Max(1, (int)(width / itemW));

        int rows = (count + _columns - 1) / _columns;
        var extent = new Size(width, rows * itemH);
        var viewport = new Size(width, height);
        bool changed = extent != _extent || viewport != _viewport;
        _extent = extent;
        _viewport = viewport;
        _offset.Y = Math.Max(0, Math.Min(_offset.Y, Math.Max(0, extent.Height - viewport.Height)));
        if (changed) ScrollOwner?.InvalidateScrollInfo();

        // The tiles that are (even partly) on screen.
        if (count == 0 || owner == null)
        {
            Realize(0, -1);
            return new Size(width, Math.Min(height, extent.Height));
        }
        int firstRow = (int)(_offset.Y / itemH);
        int lastRow = (int)Math.Ceiling((_offset.Y + viewport.Height) / itemH) - 1;
        int first = Math.Min(count - 1, firstRow * _columns);
        int last = Math.Min(count - 1, (lastRow + 1) * _columns - 1);
        Realize(first, last);

        var cell = new Size(itemW, itemH);
        foreach (UIElement child in InternalChildren) child.Measure(cell);
        return new Size(width, Math.Min(height, extent.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double itemW = Math.Max(1, ItemWidth), itemH = Math.Max(1, ItemHeight);
        var generator = ItemContainerGenerator;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0) continue;
            int row = index / _columns, col = index % _columns;
            InternalChildren[i].Arrange(new Rect(col * itemW, row * itemH - _offset.Y, itemW, itemH));
        }
        return finalSize;
    }

    /// <summary>Makes the tiles of items first..last and drops the others.</summary>
    private void Realize(int first, int last)
    {
        var generator = ItemContainerGenerator;
        var owner = Owner;
        if (owner == null) return;

        if (last >= first)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
            {
                for (int i = first; i <= last; i++, childIndex++)
                {
                    var child = (UIElement?)generator.GenerateNext(out bool isNew);
                    if (child == null) continue;
                    if (isNew)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    if (owner.Items[i] is FileItemViewModel item) ThumbnailService.Wanted(item);
                }
            }
        }

        // Drop the tiles outside the range (their thumbnails are let go; the memory cache keeps the recent ones).
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;

            if (owner.ItemContainerGenerator.ItemFromContainer(InternalChildren[i]) is FileItemViewModel item) ThumbnailService.Unwanted(item);
            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
        _firstIndex = first;
        _lastIndex = last;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
        }
        base.OnItemsChanged(sender, args);
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        double itemH = Math.Max(1, ItemHeight);
        double top = index / _columns * itemH, bottom = top + itemH;
        if (top < _offset.Y) SetVerticalOffset(top);
        else if (bottom > _offset.Y + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height);
    }

    // ── IScrollInfo (pixels) ──────────────────────────────────────────────────

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, Math.Max(0, _extent.Height - _viewport.Height)));
        if (offset == _offset.Y) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetHorizontalOffset(double offset) { }
    public void LineUp() => SetVerticalOffset(_offset.Y - 24);
    public void LineDown() => SetVerticalOffset(_offset.Y + 24);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - ItemHeight / 2);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + ItemHeight / 2);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is UIElement element && ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(InternalChildren.IndexOf(element), 0)) is var index and >= 0)
            BringIndexIntoView(index);
        return rectangle;
    }
}
