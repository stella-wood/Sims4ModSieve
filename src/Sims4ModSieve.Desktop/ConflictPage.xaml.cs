using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Sims4ModSieve.Desktop.ViewModels;

namespace Sims4ModSieve.Desktop;

public partial class ConflictPage : UserControl
{
    public ConflictPage()
    {
        InitializeComponent();
        ConflictFilterPopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height), PopupPrimaryAxis.Horizontal)];
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainWindowViewModel oldModel) oldModel.ConflictGroupLocationRequested -= LocateGroup;
            if (e.NewValue is MainWindowViewModel newModel) newModel.ConflictGroupLocationRequested += LocateGroup;
        };
    }

    private void LocateGroup(string key) => Dispatcher.BeginInvoke(() =>
    {
        if (DataContext is not MainWindowViewModel model) return;
        var row = model.ConflictRows.FirstOrDefault(row => row.Key == key);
        if (row is null) return;
        ConflictRowsList.ScrollIntoView(row);
        ConflictRowsList.UpdateLayout();
        if (ConflictRowsList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item)
            item.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
    }, DispatcherPriority.Loaded);

    private void TechnicalColumns_Loaded(object sender, RoutedEventArgs e) => SubscribeTechnicalColumns((ScrollViewer)sender);

    private void TechnicalColumns_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var scroll = (ScrollViewer)sender;
        TechnicalColumns_Unloaded(scroll, new RoutedEventArgs());
        if (scroll.IsLoaded) SubscribeTechnicalColumns(scroll);
    }

    private void SubscribeTechnicalColumns(ScrollViewer scroll)
    {
        TechnicalColumns_Unloaded(scroll, new RoutedEventArgs());
        if (scroll.DataContext is not ConflictResultRow { TechnicalTable: { } table }) return;
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(ConflictTechnicalTableState.HorizontalOffset))
                scroll.ScrollToHorizontalOffset(table.HorizontalOffset);
        };
        scroll.Tag = (table, handler);
        table.PropertyChanged += handler;
        scroll.ScrollToHorizontalOffset(table.HorizontalOffset);
    }

    private void TechnicalColumns_Unloaded(object sender, RoutedEventArgs e)
    {
        var scroll = (ScrollViewer)sender;
        if (scroll.Tag is ValueTuple<ConflictTechnicalTableState, PropertyChangedEventHandler> subscription)
            subscription.Item1.PropertyChanged -= subscription.Item2;
        scroll.Tag = null;
    }

    private void TechnicalColumns_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource)) return;
        if (((ScrollViewer)sender).DataContext is not ConflictResultRow { TechnicalTable: { } table }) return;
        if (e.ViewportWidth > 0) table.ViewportWidth = e.ViewportWidth;
        if (e.HorizontalChange != 0) table.HorizontalOffset = e.HorizontalOffset;
    }

    private void TechnicalColumns_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 横向 ScrollViewer 也会吞掉上下滚轮，交给外层结果列表按系统滚动设置处理。
        for (var parent = VisualTreeHelper.GetParent((ScrollViewer)sender); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer outer) continue;
            e.Handled = true;
            outer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = Mouse.MouseWheelEvent,
                Source = outer
            });
            return;
        }
    }

    private void ConflictOverviewViewport_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange == 0 && e.ViewportHeightChange == 0) return;

        var window = Window.GetWindow(this);
        if (window is null) return;

        var scale = Math.Min(window.ActualWidth / 1180, window.ActualHeight / 780);
        scale = Math.Min(scale, (ConflictOverviewViewport.ViewportWidth - 20) / (430 * 2));
        scale = Math.Min(scale, (ConflictOverviewViewport.ViewportHeight - 20) / (210 * 2));
        // 保持文字和图标原尺寸，窄窗口下保留内容所需的最低卡片高度。
        ConflictOverviewCards.Width = Math.Max(0, 430 * scale * 2 + 20);
        ConflictOverviewCards.Height = Math.Max(200, 210 * scale) * 2 + 20;
    }

}
