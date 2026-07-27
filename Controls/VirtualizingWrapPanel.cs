using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ZDesk.Controls;

/// <summary>Fixed-size wrapping panel that realizes only rows near the viewport.</summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(88d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(76d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(4d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(4d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private ScrollViewer? _scrollOwner;
    private Size _viewport;
    private Size _extent;
    private double _verticalOffset;
    private int _realizedFirstIndex = -1;
    private int _realizedLastIndex = -1;
    private int _realizedColumns;

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }
    public double HorizontalSpacing { get => (double)GetValue(HorizontalSpacingProperty); set => SetValue(HorizontalSpacingProperty, value); }
    public double VerticalSpacing { get => (double)GetValue(VerticalSpacingProperty); set => SetValue(VerticalSpacingProperty, value); }

    public VirtualizingWrapPanel()
    {
        CanHorizontallyScroll = false;
        CanVerticallyScroll = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? ItemWidth : Math.Max(1, availableSize.Width);
        var cellWidth = Math.Max(1, ItemWidth + HorizontalSpacing);
        var cellHeight = Math.Max(1, ItemHeight + VerticalSpacing);
        var columns = Math.Max(1, (int)Math.Floor((width + HorizontalSpacing) / cellWidth));
        var itemCount = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        var rows = itemCount == 0 ? 0 : (itemCount + columns - 1) / columns;
        var viewportHeight = double.IsInfinity(availableSize.Height) ? cellHeight : Math.Max(0, availableSize.Height);

        _viewport = new Size(width, viewportHeight);
        _extent = new Size(width, rows * cellHeight);
        SetVerticalOffset(_verticalOffset);
        RealizeVisibleItems(columns, cellHeight, viewportHeight);
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(ItemWidth, ItemHeight));

        return new Size(width, Math.Min(_extent.Height, viewportHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cellWidth = Math.Max(1, ItemWidth + HorizontalSpacing);
        var cellHeight = Math.Max(1, ItemHeight + VerticalSpacing);
        var columns = Math.Max(1, (int)Math.Floor((finalSize.Width + HorizontalSpacing) / cellWidth));
        var firstRow = cellHeight <= 0 ? 0 : (int)Math.Floor(_verticalOffset / cellHeight);
        var firstIndex = firstRow * columns;
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            var itemIndex = firstIndex + index;
            var row = itemIndex / columns;
            var column = itemIndex % columns;
            InternalChildren[index].Arrange(new Rect(
                column * cellWidth,
                row * cellHeight - _verticalOffset,
                ItemWidth,
                ItemHeight));
        }
        return finalSize;
    }

    private void RealizeVisibleItems(int columns, double cellHeight, double viewportHeight)
    {
        var itemCount = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        var firstRow = Math.Max(0, (int)Math.Floor(_verticalOffset / cellHeight) - 1);
        var lastRow = Math.Min(
            itemCount == 0 ? 0 : (int)Math.Ceiling((_verticalOffset + viewportHeight) / cellHeight) + 1,
            itemCount == 0 ? 0 : (itemCount + columns - 1) / columns);
        var firstIndex = Math.Min(itemCount, firstRow * columns);
        var lastIndex = Math.Min(itemCount, lastRow * columns);

        if (firstIndex == _realizedFirstIndex && lastIndex == _realizedLastIndex &&
            columns == _realizedColumns && InternalChildren.Count == lastIndex - firstIndex)
            return;

        _realizedFirstIndex = firstIndex;
        _realizedLastIndex = lastIndex;
        _realizedColumns = columns;

        if (InternalChildren.Count > 0)
        {
            ItemContainerGenerator.RemoveAll();
            RemoveInternalChildRange(0, InternalChildren.Count);
        }

        if (firstIndex >= lastIndex) return;
        var position = ItemContainerGenerator.GeneratorPositionFromIndex(firstIndex);
        using (ItemContainerGenerator.StartAt(position, GeneratorDirection.Forward, true))
        {
            for (var index = firstIndex; index < lastIndex; index++)
            {
                var child = (UIElement)ItemContainerGenerator.GenerateNext(out var newlyRealized);
                if (newlyRealized)
                {
                    AddInternalChild(child);
                    ItemContainerGenerator.PrepareItemContainer(child);
                }
                else if (!InternalChildren.Contains(child)) AddInternalChild(child);
            }
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        _realizedFirstIndex = -1;
        _realizedLastIndex = -1;
        InvalidateMeasure();
    }

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public ScrollViewer? ScrollOwner { get => _scrollOwner; set => _scrollOwner = value; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _verticalOffset;

    public void LineUp() => SetVerticalOffset(_verticalOffset - ItemHeight);
    public void LineDown() => SetVerticalOffset(_verticalOffset + ItemHeight);
    public void PageUp() => SetVerticalOffset(_verticalOffset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_verticalOffset + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_verticalOffset - ItemHeight * 3);
    public void MouseWheelDown() => SetVerticalOffset(_verticalOffset + ItemHeight * 3);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        var max = Math.Max(0, _extent.Height - _viewport.Height);
        var value = Math.Clamp(offset, 0, max);
        if (Math.Abs(value - _verticalOffset) < 0.1) return;
        _verticalOffset = value;
        _scrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is not UIElement element) return rectangle;
        var index = IndexFromContainer(element);
        if (index < 0) return rectangle;
        var cellHeight = Math.Max(1, ItemHeight + VerticalSpacing);
        var columns = Math.Max(1, (int)Math.Floor((_viewport.Width + HorizontalSpacing) /
                                                   Math.Max(1, ItemWidth + HorizontalSpacing)));
        var top = index / columns * cellHeight;
        if (top < _verticalOffset) SetVerticalOffset(top);
        else if (top + ItemHeight > _verticalOffset + _viewport.Height)
            SetVerticalOffset(top + ItemHeight - _viewport.Height);
        return rectangle;
    }

    protected override void BringIndexIntoView(int index)
    {
        var itemCount = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        if (index < 0 || index >= itemCount) return;
        var cellHeight = Math.Max(1, ItemHeight + VerticalSpacing);
        var columns = Math.Max(1, (int)Math.Floor((_viewport.Width + HorizontalSpacing) /
                                                   Math.Max(1, ItemWidth + HorizontalSpacing)));
        SetVerticalOffset(index / columns * cellHeight);
    }

    private int IndexFromContainer(UIElement element)
    {
        var position = ItemContainerGenerator.IndexFromGeneratorPosition(ItemContainerGenerator.GeneratorPositionFromIndex(0));
        for (var index = 0; index < InternalChildren.Count; index++)
            if (ReferenceEquals(InternalChildren[index], element)) return position + index;
        return -1;
    }
}
