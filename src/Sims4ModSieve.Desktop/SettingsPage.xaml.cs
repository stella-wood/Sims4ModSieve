using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Sims4ModSieve.Desktop;

public partial class SettingsPage : UserControl
{
    private readonly DispatcherTimer scrollbarHideTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ScrollBar? outerScrollBar;

    public SettingsPage()
    {
        InitializeComponent();
        scrollbarHideTimer.Tick += (_, _) =>
        {
            scrollbarHideTimer.Stop();
            if (outerScrollBar is null || outerScrollBar.IsMouseOver || outerScrollBar.IsMouseCaptureWithin) return;
            outerScrollBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
        };
    }

    private void OuterScrollBar_Loaded(object sender, RoutedEventArgs e)
    {
        if (((ScrollBar)sender).Orientation != Orientation.Vertical) return;
        outerScrollBar = (ScrollBar)sender;
        scrollbarHideTimer.Stop();
        outerScrollBar.BeginAnimation(OpacityProperty, null);
        outerScrollBar.Opacity = 0;
    }

    private void OuterScrollBar_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, outerScrollBar)) return;
        scrollbarHideTimer.Stop();
        outerScrollBar = null;
    }

    private void OuterScrollBar_Activity(object sender, RoutedEventArgs e) => ShowOuterScrollBar();

    private void SettingsContentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SettingsContentScroll) && e.VerticalChange != 0)
            ShowOuterScrollBar();
    }

    private void ShowOuterScrollBar()
    {
        if (outerScrollBar is null) return;
        outerScrollBar.BeginAnimation(OpacityProperty, null);
        outerScrollBar.Opacity = 1;
        scrollbarHideTimer.Stop();
        if (!outerScrollBar.IsMouseOver && !outerScrollBar.IsMouseCaptureWithin) scrollbarHideTimer.Start();
    }
}
