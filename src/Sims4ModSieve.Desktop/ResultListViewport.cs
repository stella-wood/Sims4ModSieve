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
    public ListBoxItem? Container(object item) => list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
    public double Top(ListBoxItem item) => item.TranslatePoint(new Point(), presenter).Y;

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

    public static void AnimateHeight(FrameworkElement body, double from, double to, bool expanding, Action completed)
    {
        body.Height = from;
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(expanding ? 200 : 150))
        {
            EasingFunction = new CubicEase { EasingMode = expanding ? EasingMode.EaseOut : EasingMode.EaseIn },
        };
        animation.Completed += (_, _) => completed();
        body.BeginAnimation(FrameworkElement.HeightProperty, animation);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
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
