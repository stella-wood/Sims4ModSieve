using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Sims4ModSieve.Desktop.ViewModels;

namespace Sims4ModSieve.Desktop.Infrastructure;

public sealed class TechnicalColumnsPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty TableProperty = DependencyProperty.Register(
        nameof(Table), typeof(ConflictTechnicalTableState), typeof(TechnicalColumnsPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, TableChanged));

    private Size extent;
    private Size viewport;
    private double offset;

    public ConflictTechnicalTableState? Table
    {
        get => (ConflictTechnicalTableState?)GetValue(TableProperty);
        set => SetValue(TableProperty, value);
    }

    public TechnicalColumnsPanel() => ClipToBounds = true;

    private static void TableChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var panel = (TechnicalColumnsPanel)sender;
        if (args.OldValue is ConflictTechnicalTableState oldTable)
            PropertyChangedEventManager.RemoveHandler(oldTable, panel.TablePropertyChanged, "");
        if (args.NewValue is ConflictTechnicalTableState newTable)
            PropertyChangedEventManager.AddHandler(newTable, panel.TablePropertyChanged, "");
    }

    private void TablePropertyChanged(object? sender, PropertyChangedEventArgs args) => InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (Table is not { } table || owner is null || owner.Items.Count == 0) return new Size();
        var width = double.IsInfinity(availableSize.Width) ? table.ViewportWidth : availableSize.Width;
        var total = table.ContentWidth;
        offset = Math.Clamp(table.HorizontalOffset, 0, Math.Max(0, total - width));
        var first = ColumnAt(offset, owner.Items.Count);
        var last = ColumnAt(offset + width, owner.Items.Count);
        // 访问子集合会让 WPF 将当前 ItemsPresenter 的生成器连接到自定义面板。
        _ = InternalChildren;
        var generator = ItemContainerGenerator;
        for (var index = InternalChildren.Count - 1; index >= 0; index--)
        {
            var childPosition = new GeneratorPosition(index, 0);
            var item = generator.IndexFromGeneratorPosition(childPosition);
            if (item >= first && item <= last) continue;
            ((IRecyclingItemContainerGenerator)generator).Recycle(childPosition, 1);
            RemoveInternalChildRange(index, 1);
        }
        var position = generator.GeneratorPositionFromIndex(first);
        var childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
        var height = 0d;
        using (generator.StartAt(position, GeneratorDirection.Forward, true))
        {
            for (var index = first; index <= last; index++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out var created);
                // 回收池返回的容器也会报告 created=false，但已不在视觉子集合中。
                if (created || !InternalChildren.Contains(child))
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(ColumnWidth(index), double.PositiveInfinity));
                height = Math.Max(height, child.DesiredSize.Height);
            }
        }
        extent = new Size(total, height);
        viewport = new Size(width, height);
        ScrollOwner?.InvalidateScrollInfo();
        // 内层内容宽度只用于横向滚动，不能成为外层结果行的期望宽度。
        return new Size(0, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 浮动表头沿用正文的视口；只有正文最终排列宽度能更新共享状态。
        if (Table is { } table && FindAncestor<ListBoxItem>() is not null)
        {
            table.ViewportWidth = finalSize.Width;
            table.HorizontalOffset = Math.Clamp(table.HorizontalOffset, 0, table.ScrollableWidth);
        }
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            var item = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(index, 0));
            InternalChildren[index].Arrange(new Rect(ColumnStart(item) - offset, 0, ColumnWidth(item), finalSize.Height));
        }
        return finalSize;
    }

    private T? FindAncestor<T>() where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T ancestor) return ancestor;
        return null;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        if (args.ItemUICount == 0) { InvalidateMeasure(); return; }
        if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace)
            RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
        else if (args.Action == NotifyCollectionChangedAction.Move)
            RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
        InvalidateMeasure();
    }

    private int ColumnAt(double x, int count) => Math.Clamp(x < ConflictTechnicalTableState.TypeWidth
        ? 0 : 1 + (int)((x - ConflictTechnicalTableState.TypeWidth) / Table!.CellWidth), 0, count - 1);

    private double ColumnStart(int index) => index == 0 ? 0
        : ConflictTechnicalTableState.TypeWidth + (index - 1) * Table!.CellWidth;

    private double ColumnWidth(int index)
    {
        var cell = (ConflictTechnicalCell)ItemsControl.GetItemsOwner(this).Items[index];
        return cell.IsType ? ConflictTechnicalTableState.TypeWidth
            : cell.IsRelated ? ConflictTechnicalTableState.RelatedWidth : Table!.CellWidth;
    }

    public void SetHorizontalOffset(double value)
    {
        if (Table is not { } table) return;
        offset = Math.Clamp(value, 0, Math.Max(0, ExtentWidth - ViewportWidth));
        table.HorizontalOffset = offset;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    protected override void BringIndexIntoView(int index)
    {
        var start = ColumnStart(index);
        var end = start + ColumnWidth(index);
        if (start < offset) SetHorizontalOffset(start);
        else if (end > offset + ViewportWidth) SetHorizontalOffset(end - ViewportWidth);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var bounds = visual.TransformToAncestor(this).TransformBounds(rectangle);
        var previous = offset;
        if (bounds.Left < 0) SetHorizontalOffset(offset + bounds.Left);
        else if (bounds.Right > ViewportWidth) SetHorizontalOffset(offset + bounds.Right - ViewportWidth);
        bounds.Offset(previous - offset, 0);
        bounds.Intersect(new Rect(viewport));
        return bounds;
    }

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => extent.Width;
    public double ExtentHeight => extent.Height;
    public double ViewportWidth => viewport.Width;
    public double ViewportHeight => viewport.Height;
    public double HorizontalOffset => offset;
    public double VerticalOffset => 0;
    public ScrollViewer ScrollOwner { get; set; } = null!;
    public void LineLeft() => SetHorizontalOffset(offset - 16);
    public void LineRight() => SetHorizontalOffset(offset + 16);
    public void PageLeft() => SetHorizontalOffset(offset - ViewportWidth);
    public void PageRight() => SetHorizontalOffset(offset + ViewportWidth);
    public void MouseWheelLeft() => SetHorizontalOffset(offset - 48);
    public void MouseWheelRight() => SetHorizontalOffset(offset + 48);
    public void SetVerticalOffset(double value) { }
    public void LineUp() { }
    public void LineDown() { }
    public void PageUp() { }
    public void PageDown() { }
    public void MouseWheelUp() { }
    public void MouseWheelDown() { }
}
