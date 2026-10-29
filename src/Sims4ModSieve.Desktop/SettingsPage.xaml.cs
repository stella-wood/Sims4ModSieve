using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Sims4ModSieve.Desktop;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
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
