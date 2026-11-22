using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Sims4ModSieve.Desktop;

internal sealed class ResultListTransitions
{
    private readonly FrameworkElement surface;
    private TaskCompletionSource? fadeOut;
    private bool applyingSearch;
    private IReadOnlyList<FrameworkElement> leaving = [];
    private readonly List<ListBoxItem> compensatingItems = [];
    private TaskCompletionSource? exit;
    private (ListBoxItem Item, Thickness Original, ScrollViewer Scroll)? space;

    public ResultListTransitions(FrameworkElement surface, ListBox list)
    {
        this.surface = surface;
        ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, _) =>
        {
            ClearSpace();
            if (!applyingSearch) CancelSwitch();
        };
        list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ReleaseSpace));
        surface.IsVisibleChanged += (_, _) =>
        {
            if (surface.IsVisible) return;
            fadeOut?.TrySetResult();
            exit?.TrySetResult();
        };
    }

    public bool CanAnimate => surface.IsVisible && SystemParameters.ClientAreaAnimation;

    public async Task SwitchAsync(Action apply, CancellationToken cancellation = default)
    {
        var from = surface.Opacity;
        fadeOut?.TrySetCanceled();
        if (!CanAnimate)
        {
            fadeOut = null;
            ResetSurface();
            if (!cancellation.IsCancellationRequested) apply();
            return;
        }

        var fade = fadeOut = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) => fade.TrySetResult();
        surface.BeginAnimation(UIElement.OpacityProperty, animation);
        try
        {
            await fade.Task.WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!ReferenceEquals(fadeOut, fade)) return;
            applyingSearch = true;
            try
            {
                apply();
                surface.UpdateLayout();
            }
            finally { applyingSearch = false; }
            if (!ReferenceEquals(fadeOut, fade)) return;
            fadeOut = null;
            if (CanAnimate) Reveal();
            else ResetSurface();
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(fadeOut, fade)) return;
            fadeOut = null;
            ResetSurface();
        }
    }

    public void CancelSwitch()
    {
        var previous = fadeOut;
        if (previous is null && surface.RenderTransform is not TranslateTransform) return;
        fadeOut = null;
        previous?.TrySetCanceled();
        ResetSurface();
    }

    private void Reveal()
    {
        var duration = TimeSpan.FromMilliseconds(150);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new TranslateTransform();
        surface.RenderTransform = slide;
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        surface.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    private void ResetSurface()
    {
        surface.BeginAnimation(UIElement.OpacityProperty, null);
        surface.ClearValue(UIElement.RenderTransformProperty);
    }

    public async Task ExitAsync(ResultListViewport viewport, IReadOnlyList<FrameworkElement> elements, ListBoxItem? anchor,
        Action apply, CancellationToken cancellation = default)
    {
        FinishExit();
        if (elements.Count == 0 || !CanAnimate)
        {
            if (!cancellation.IsCancellationRequested) apply();
            return;
        }

        leaving = elements;
        var finished = exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(200);
        var exits = elements.Select(element =>
        {
            var top = viewport.Top(element);
            // 保留上沿外的布局空间，避免虚拟化面板在离场结束前切换首个可见容器。
            var hidden = element is ListBoxItem && top < 0 ? Math.Min(element.ActualHeight, -top + 0.01) : 0;
            return (Element: element, Top: top, Height: element.ActualHeight, Hidden: hidden);
        }).ToArray();
        if (anchor is not null)
        {
            // 与离场高度用同一段动画，让上方行收缩时的补位在同一次布局中完成。
            var anchorTop = viewport.Top(anchor);
            var before = exits.Where(item => item.Top < anchorTop).Sum(item => item.Height - item.Hidden);
            var after = exits.Sum(item => item.Height - item.Hidden) - before;
            var last = viewport.LastItem;
            if (ReferenceEquals(anchor, last)) AnimateMargin(anchor, before, after);
            else
            {
                AnimateMargin(anchor, before, 0);
                if (last is not null) AnimateMargin(last, 0, after);
            }
        }
        foreach (var item in exits)
        {
            var element = item.Element;
            element.SetCurrentValue(UIElement.IsHitTestVisibleProperty, false);
            element.SetCurrentValue(UIElement.ClipToBoundsProperty, true);
            if (element is ListBoxItem container) VirtualizingPanel.SetIsContainerVirtualizable(container, false);
            var collapse = new DoubleAnimation(item.Height, item.Hidden, duration) { EasingFunction = ease };
            if (ReferenceEquals(element, elements[0])) collapse.Completed += (_, _) => finished.TrySetResult();
            element.BeginAnimation(FrameworkElement.HeightProperty, collapse);
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
        }

        try
        {
            await finished.Task.WaitAsync(cancellation);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(exit, finished))
            {
                ClearExitVisuals();
                apply();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(exit, finished)) FinishExit();
        }

        void AnimateMargin(ListBoxItem item, double before, double after)
        {
            if (before == 0 && after == 0) return;
            var original = item.Margin;
            var target = new Thickness(original.Left, original.Top + before, original.Right, original.Bottom + after);
            compensatingItems.Add(item);
            VirtualizingPanel.SetIsContainerVirtualizable(item, false);
            item.BeginAnimation(FrameworkElement.MarginProperty, new ThicknessAnimation(original, target, duration) { EasingFunction = ease });
        }
    }

    public void Position(ResultListViewport viewport, object anchor, double top, bool realize = true)
    {
        viewport.Restore(anchor, top, realize);
        if (viewport.Container(anchor) is not { } item) return;
        var delta = viewport.Top(item) - top;
        var scroll = viewport.Scroll;
        var before = delta < -0.5 && scroll.VerticalOffset <= 0.5;
        var after = delta > 0.5 && scroll.ScrollableHeight - scroll.VerticalOffset <= 0.5;
        // 内容缩短到滚动边界时，留出维持锚点位置所需的空间。
        var edge = before ? item : after ? viewport.LastItem : null;
        if (edge is null) return;
        if (space is not { } current || !ReferenceEquals(current.Item, edge))
        {
            ClearSpace();
            space = (edge, edge.Margin, scroll);
            edge.DataContextChanged += SpaceItem_DataContextChanged;
        }
        var margin = edge.Margin;
        edge.Margin = before ? new(margin.Left, margin.Top - delta, margin.Right, margin.Bottom)
            : new(margin.Left, margin.Top, margin.Right, margin.Bottom + delta);
        viewport.Restore(anchor, top, realize);
    }

    private void ReleaseSpace(object sender, ScrollChangedEventArgs e)
    {
        if (exit is not null || space is not { } current || !ReferenceEquals(e.OriginalSource, current.Scroll) || e.ExtentHeightChange != 0) return;
        var before = current.Item.Margin.Top - current.Original.Top;
        var after = current.Item.Margin.Bottom - current.Original.Bottom;
        if (before > 0 && e.VerticalChange > 0 && e.VerticalOffset >= before)
        {
            ClearSpace();
            current.Scroll.ScrollToVerticalOffset(e.VerticalOffset - before);
        }
        else if (after > 0 && e.VerticalChange < 0 && e.VerticalOffset + after <= current.Scroll.ScrollableHeight + 0.5)
            ClearSpace();
    }

    private void SpaceItem_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => ClearSpace();

    private void ClearSpace()
    {
        if (space is not { } current) return;
        space = null;
        current.Item.DataContextChanged -= SpaceItem_DataContextChanged;
        current.Item.Margin = current.Original;
    }

    private void FinishExit()
    {
        exit?.TrySetResult();
        exit = null;
        ClearExitVisuals();
    }

    private void ClearExitVisuals()
    {
        foreach (var item in compensatingItems)
        {
            item.BeginAnimation(FrameworkElement.MarginProperty, null);
            item.ClearValue(VirtualizingPanel.IsContainerVirtualizableProperty);
        }
        compensatingItems.Clear();
        foreach (var element in leaving)
        {
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.ClearValue(UIElement.IsHitTestVisibleProperty);
            element.ClearValue(UIElement.ClipToBoundsProperty);
            if (element is ListBoxItem item) item.ClearValue(VirtualizingPanel.IsContainerVirtualizableProperty);
        }
        leaving = [];
    }
}
