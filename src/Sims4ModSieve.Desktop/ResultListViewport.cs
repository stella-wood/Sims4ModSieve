using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Sims4ModSieve.Desktop;

internal sealed class ResultListViewport(ListBox list, ScrollViewer scroll, ScrollContentPresenter presenter, VirtualizingStackPanel panel)
{
    public double Height => presenter.ActualHeight;

    public static ResultListViewport? Capture(ListBox list)
    {
        if (!list.IsVisible || FindChild<ScrollViewer>(list) is not { } scroll
            || FindChild<ScrollContentPresenter>(scroll) is not { } presenter
            || FindChild<VirtualizingStackPanel>(presenter) is not { } panel) return null;
        return new(list, scroll, presenter, panel);
    }

    public IEnumerable<ListBoxItem> RealizedItems => panel.Children.OfType<ListBoxItem>();
    public IEnumerable<ListBoxItem> VisibleItems => RealizedItems.Where(IsVisible).OrderBy(Top);
    public ListBoxItem? Container(object item) => list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
    public double Top(FrameworkElement item) => item.TranslatePoint(new Point(), presenter).Y;

    public double VisibleExpansionHeight(FrameworkElement header, double contentHeight)
    {
        // 即使标题靠近视口底部，短组也沿用完整的展开曲线。
        return contentHeight > Height / 2 ? Math.Max(0, Height - Top(header) - header.ActualHeight) : 0;
    }

    public bool IsVisible(FrameworkElement element)
    {
        if (!element.IsVisible || !element.IsDescendantOf(presenter) || element.ActualHeight <= 0) return false;
        var top = Top(element);
        return top < Height && top + element.ActualHeight > 0;
    }
    public ScrollViewer Scroll => scroll;
    public ListBoxItem? LastItem => list.Items.Count == 0 ? null : Container(list.Items[^1]);

    public ListBoxItem? CenterItem() => RealizedItems.FirstOrDefault(item =>
    {
        var top = Top(item);
        return top <= Height / 2 && top + item.ActualHeight > Height / 2;
    });

    public void Restore(object item, double top, bool realize = true)
    {
        if (realize && Container(item) is null) list.ScrollIntoView(item);
        list.UpdateLayout();
        if (Container(item) is not { } container) return;
        var delta = Top(container) - top;
        if (Math.Abs(delta) < 0.5) return;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + delta);
        scroll.UpdateLayout();
    }

    public void RestoreAfterLayout(object item, double top, Func<bool> isCurrent)
    {
        list.Dispatcher.BeginInvoke(() =>
        {
            if (!isCurrent()) return;
            Restore(item, top);
            // 像素滚动会在后续布局中修正虚拟行的高度估计。
            list.Dispatcher.BeginInvoke(() =>
            {
                if (isCurrent()) Restore(item, top, realize: top >= 0);
            }, DispatcherPriority.ApplicationIdle);
        }, DispatcherPriority.Loaded);
    }

    public static void SuppressMouseFocusScroll(object sender, RequestBringIntoViewEventArgs e)
    {
        // 事件先经过按钮，再到处理 MakeVisible 的 ScrollViewer。
        if (ReferenceEquals(sender, e.TargetObject) && Mouse.LeftButton == MouseButtonState.Pressed)
            e.Handled = true;
    }

    public static void AnimateHeight(FrameworkElement body, double from, double to, bool expanding, double visibleHeight, Action completed)
    {
        body.Height = from;
        var animation = CreateHeightAnimation(from, to, expanding, visibleHeight);
        animation.Completed += (_, _) => completed();
        body.BeginAnimation(FrameworkElement.HeightProperty, animation);
    }

    internal static AnimationTimeline CreateHeightAnimation(double from, double to, bool expanding, double visibleHeight)
    {
        var duration = TimeSpan.FromMilliseconds(expanding ? 190 : 140);
        var easing = new CubicEase { EasingMode = expanding ? EasingMode.EaseOut : EasingMode.EaseIn };
        if (expanding && from < visibleHeight && visibleHeight < to)
        {
            return new DoubleAnimationUsingKeyFrames
            {
                Duration = duration,
                KeyFrames =
                [
                    new EasingDoubleKeyFrame(visibleHeight, TimeSpan.FromMilliseconds(135), easing),
                    new EasingDoubleKeyFrame(to, duration, new CubicEase { EasingMode = EasingMode.EaseIn }),
                ],
            };
        }
        return new DoubleAnimation(from, to, duration) { EasingFunction = easing };
    }

    internal static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
}
