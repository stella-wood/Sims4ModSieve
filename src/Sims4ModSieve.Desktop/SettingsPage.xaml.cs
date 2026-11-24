using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Sims4ModSieve.Desktop;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private void CacheClearContent_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var content = (UIElement)sender;
        if (content.IsVisible && SystemParameters.ClientAreaAnimation)
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            { FillBehavior = FillBehavior.Stop });
    }

    private void CacheClearSpinner_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var spinner = (UIElement)sender;
        var rotation = (RotateTransform)spinner.RenderTransform;
        if (rotation.IsFrozen)
            spinner.RenderTransform = rotation = rotation.Clone();
        var animation = spinner.IsVisible && SystemParameters.ClientAreaAnimation
            ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(700)) { RepeatBehavior = RepeatBehavior.Forever }
            : null;
        rotation.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void ManagementList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ListBox list && FindScroll(list) is { } scroll)
        {
            if (e.Delta > 0) scroll.LineUp(); else scroll.LineDown();
            e.Handled = true;
        }
    }

    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindScroll(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
        return null;
    }
}
