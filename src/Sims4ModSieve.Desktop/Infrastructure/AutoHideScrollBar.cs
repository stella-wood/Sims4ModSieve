using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Sims4ModSieve.Desktop.Infrastructure;

public static class AutoHideScrollBar
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(AutoHideScrollBar), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(AutoHideScrollBar));

    public static bool GetIsEnabled(ScrollBar scrollBar) => (bool)scrollBar.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(ScrollBar scrollBar, bool value) => scrollBar.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var scrollBar = (ScrollBar)sender;
        if (scrollBar.GetValue(StateProperty) is State previous) previous.Detach();
        scrollBar.ClearValue(StateProperty);
        if (!(bool)e.NewValue) return;
        var state = new State(scrollBar);
        scrollBar.SetValue(StateProperty, state);
        state.Attach();
    }

    private sealed class State(ScrollBar scrollBar)
    {
        private readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private ScrollViewer? owner;

        public void Attach()
        {
            hideTimer.Tick += HideTimer_Tick;
            scrollBar.Loaded += ScrollBar_Loaded;
            scrollBar.Unloaded += ScrollBar_Unloaded;
            if (scrollBar.IsLoaded) ScrollBar_Loaded(scrollBar, new RoutedEventArgs());
        }

        public void Detach()
        {
            scrollBar.Loaded -= ScrollBar_Loaded;
            scrollBar.Unloaded -= ScrollBar_Unloaded;
            Disconnect();
            hideTimer.Tick -= HideTimer_Tick;
            scrollBar.ClearValue(UIElement.OpacityProperty);
        }

        private void ScrollBar_Loaded(object sender, RoutedEventArgs e)
        {
            if (scrollBar.Orientation != Orientation.Vertical) return;
            Disconnect();
            for (DependencyObject? parent = VisualTreeHelper.GetParent(scrollBar); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is not ScrollViewer viewer) continue;
                owner = viewer;
                break;
            }
            scrollBar.MouseEnter += ScrollBar_Activity;
            scrollBar.MouseLeave += ScrollBar_Activity;
            scrollBar.IsMouseCaptureWithinChanged += ScrollBar_CaptureChanged;
            scrollBar.IsVisibleChanged += ScrollBar_IsVisibleChanged;
            if (owner is not null) owner.ScrollChanged += Owner_ScrollChanged;
            if (scrollBar.IsMouseOver || scrollBar.IsMouseCaptureWithin) Show();
        }

        private void ScrollBar_Unloaded(object sender, RoutedEventArgs e) => Disconnect();

        private void Disconnect()
        {
            hideTimer.Stop();
            if (owner is not null) owner.ScrollChanged -= Owner_ScrollChanged;
            owner = null;
            scrollBar.MouseEnter -= ScrollBar_Activity;
            scrollBar.MouseLeave -= ScrollBar_Activity;
            scrollBar.IsMouseCaptureWithinChanged -= ScrollBar_CaptureChanged;
            scrollBar.IsVisibleChanged -= ScrollBar_IsVisibleChanged;
            if (scrollBar.Orientation == Orientation.Vertical) Hide();
        }

        private void ScrollBar_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            hideTimer.Stop();
            Hide();
            if ((bool)e.NewValue && (scrollBar.IsMouseOver || scrollBar.IsMouseCaptureWithin)) Show();
        }

        private void ScrollBar_Activity(object sender, MouseEventArgs e) => Show();
        private void ScrollBar_CaptureChanged(object sender, DependencyPropertyChangedEventArgs e) => Show();

        private void Owner_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, owner) && e.VerticalChange != 0) Show();
        }

        private void Show()
        {
            if (!scrollBar.IsVisible) return;
            scrollBar.BeginAnimation(UIElement.OpacityProperty, null);
            scrollBar.Opacity = 1;
            hideTimer.Stop();
            if (!scrollBar.IsMouseOver && !scrollBar.IsMouseCaptureWithin) hideTimer.Start();
        }

        private void Hide()
        {
            scrollBar.BeginAnimation(UIElement.OpacityProperty, null);
            scrollBar.Opacity = 0;
        }

        private void HideTimer_Tick(object? sender, EventArgs e)
        {
            hideTimer.Stop();
            if (!scrollBar.IsVisible || scrollBar.IsMouseOver || scrollBar.IsMouseCaptureWithin) return;
            scrollBar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
        }
    }
}
