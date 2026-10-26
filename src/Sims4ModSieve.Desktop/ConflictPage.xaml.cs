using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Sims4ModSieve.Desktop.ViewModels;

namespace Sims4ModSieve.Desktop;

public partial class ConflictPage : UserControl
{
    private Window? filterPopupWindow;
    private ScrollViewer? technicalResultsScroll;
    private ConflictTechnicalTableState? activeTechnicalTable;
    private bool technicalScrollActive;
    private TimeSpan? technicalScrollFrame;
    private double technicalScrollRemaining;
    private double technicalScrollVelocity;
    private readonly HashSet<ListBoxItem> technicalFadedRows = [];
    private readonly LinearGradientBrush technicalEdge = new(Colors.Transparent, Colors.Black, new Point(0, 0), new Point(24, 0))
        { MappingMode = BrushMappingMode.Absolute };

    public ConflictPage()
    {
        InitializeComponent();
        technicalEdge.Freeze();
        IsVisibleChanged += (_, _) => { if (!IsVisible) ConflictFilterPopup.IsOpen = false; };
        Unloaded += (_, _) => ConflictFilterPopup.IsOpen = false;
        ((INotifyCollectionChanged)ConflictRowsList.Items).CollectionChanged += (_, _) => StopTechnicalScroll();
        ConflictRowsList.RequestBringIntoView += (_, _) => StopTechnicalScroll();
        ConflictRowsList.IsVisibleChanged += (_, _) => { if (!ConflictRowsList.IsVisible) StopTechnicalScroll(); };
        ConflictFilterPopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height), PopupPrimaryAxis.Horizontal)];
        DataContextChanged += (_, e) =>
        {
            StopTechnicalScroll();
            HideTechnicalOverlays();
            if (e.OldValue is MainWindowViewModel oldModel)
            {
                oldModel.ConflictGroupLocationRequested -= LocateGroup;
                oldModel.ConflictViewportCaptureRequested -= CaptureConflictViewport;
            }
            if (e.NewValue is MainWindowViewModel newModel)
            {
                newModel.ConflictGroupLocationRequested += LocateGroup;
                newModel.ConflictViewportCaptureRequested += CaptureConflictViewport;
            }
        };
    }

    private void ConflictFilterPopup_Opened(object? sender, EventArgs e)
    {
        filterPopupWindow = Window.GetWindow(this);
        if (filterPopupWindow is null) return;
        filterPopupWindow.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(FilterPopupWindow_PreviewMouseDown), true);
        filterPopupWindow.PreviewKeyDown += FilterPopupWindow_PreviewKeyDown;
        filterPopupWindow.LocationChanged += FilterPopupWindow_LocationChanged;
        filterPopupWindow.Deactivated += FilterPopupWindow_LocationChanged;
    }

    private void ConflictFilterPopup_Closed(object? sender, EventArgs e)
    {
        if (filterPopupWindow is null) return;
        filterPopupWindow.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(FilterPopupWindow_PreviewMouseDown));
        filterPopupWindow.PreviewKeyDown -= FilterPopupWindow_PreviewKeyDown;
        filterPopupWindow.LocationChanged -= FilterPopupWindow_LocationChanged;
        filterPopupWindow.Deactivated -= FilterPopupWindow_LocationChanged;
        filterPopupWindow = null;
    }

    private void FilterPopupWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ConflictFilterButton.IsMouseOver && ConflictFilterPopup.Child?.IsMouseOver != true)
            ConflictFilterPopup.IsOpen = false;
    }

    private void FilterPopupWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) ConflictFilterPopup.IsOpen = false;
    }

    private void FilterPopupWindow_LocationChanged(object? sender, EventArgs e) => ConflictFilterPopup.IsOpen = false;

    private async void FloatingTechnicalCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel model || FloatingTechnicalCollapse.DataContext is not ConflictResultRow toggle) return;
        StopTechnicalScroll();
        var resources = model.ConflictResources;
        var scripts = model.ConflictScripts;
        model.ToggleConflictExpandedCommand.Execute(toggle);
        var refresh = model.ConflictRowsRefreshTask;
        await refresh;
        if (!ReferenceEquals(DataContext, model) || !ConflictRowsList.IsVisible || !model.IsConflictComplete
            || !ReferenceEquals(model.ConflictRowsRefreshTask, refresh)
            || !ReferenceEquals(model.ConflictResources, resources) || !ReferenceEquals(model.ConflictScripts, scripts)) return;
        var card = model.ConflictRows.FirstOrDefault(row => row.Key == toggle.OwnerKey);
        if (card is not null) ConflictRowsList.ScrollIntoView(card);
    }

    private void LocateGroup(string key) => Dispatcher.BeginInvoke(() =>
    {
        if (DataContext is not MainWindowViewModel model) return;
        StopTechnicalScroll();
        var row = model.ConflictRows.FirstOrDefault(row => row.Key == key);
        if (row is null) return;
        ConflictRowsList.ScrollIntoView(row);
        ConflictRowsList.UpdateLayout();
        if (ConflictRowsList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item)
            item.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
    }, DispatcherPriority.Loaded);

    private Action<IReadOnlyList<ConflictResultRow>>? CaptureConflictViewport(bool anchorToGroup)
    {
        if (DataContext is not MainWindowViewModel model || !ConflictRowsList.IsVisible || technicalResultsScroll is not { } scroll) return null;
        var presenter = TechnicalVisuals<ScrollContentPresenter>(scroll).FirstOrDefault();
        var panel = TechnicalVisuals<VirtualizingStackPanel>(ConflictRowsList).FirstOrDefault();
        if (presenter is null || panel is null) return null;
        StopTechnicalScroll();
        var originalRows = model.ConflictRows;
        // 禁用当前有焦点的删除按钮可能触发 BringIntoView，必须在 IsBusy 改变前记录位置。
        var candidates = panel.Children.OfType<ListBoxItem>()
            .Where(item => item.DataContext is ConflictResultRow)
            .Select(item => (Row: (ConflictResultRow)item.DataContext, Top: item.TranslatePoint(new Point(), presenter).Y, Height: item.ActualHeight))
            .OrderBy(item => item.Top).ToArray();
        return replacement =>
        {
            if (!ReferenceEquals(DataContext, model) || !ConflictRowsList.IsVisible || !ReferenceEquals(model.ConflictRows, originalRows)) return;
            var survivingKeys = replacement.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
            var surviving = candidates.Where(item => survivingKeys.Contains(item.Row.Key)).ToArray();
            // 顶行被删除时，保留相邻存活行原来的位置，而不是将下一组顶到视口顶部。
            var visible = surviving.Where(item => item.Top + item.Height > 0 && item.Top < presenter.ActualHeight).ToArray();
            var anchor = visible.Length > 0 ? visible[0] : surviving.OrderBy(item => Math.Abs(item.Top)).FirstOrDefault();
            var row = anchor.Row is null ? null : replacement.First(item => item.Key == anchor.Row.Key);
            var anchorTop = anchor.Top;
            if (anchorToGroup)
            {
                var source = candidates.FirstOrDefault(item => item.Top + item.Height > 0 && item.Top < presenter.ActualHeight);
                if (source.Row is not null)
                {
                    var ownerKey = source.Row.IsCardStart ? source.Row.Key : source.Row.OwnerKey;
                    var owner = replacement.FirstOrDefault(item => item.IsCardStart && item.Key == ownerKey);
                    if (owner is not null)
                    {
                        row = owner;
                        anchorTop = source.Row.IsCardStart ? source.Top : 0;
                    }
                }
            }
            if (row is null) return;
            bool IsCurrent() => ReferenceEquals(DataContext, model) && ConflictRowsList.IsVisible
                && ReferenceEquals(model.ConflictRows, replacement);
            void RestoreAnchor()
            {
                if (!IsCurrent()) return;
                if (ConflictRowsList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem)
                    ConflictRowsList.ScrollIntoView(row);
                ConflictRowsList.UpdateLayout();
                if (ConflictRowsList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item)
                {
                    var delta = item.TranslatePoint(new Point(), presenter).Y - anchorTop;
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset + delta);
                    scroll.UpdateLayout();
                }
            }
            Dispatcher.BeginInvoke(() =>
            {
                RestoreAnchor();
                Dispatcher.BeginInvoke(RestoreAnchor, DispatcherPriority.ApplicationIdle);
            }, DispatcherPriority.Loaded);
        };
    }

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
            {
                scroll.ScrollToHorizontalOffset(table.HorizontalOffset);
                scroll.OpacityMask = table.HorizontalOffset > 0 ? technicalEdge : null;
            }
        };
        scroll.Tag = (table, handler);
        table.PropertyChanged += handler;
        scroll.ScrollToHorizontalOffset(table.HorizontalOffset);
        scroll.OpacityMask = table.HorizontalOffset > 0 ? technicalEdge : null;
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
        var scroll = (ScrollViewer)sender;
        if (scroll.DataContext is not ConflictResultRow { TechnicalTable: { } table }) return;
        if (scroll.ActualWidth > 0)
        {
            var widthChanged = table.ViewportWidth != scroll.ActualWidth;
            table.ViewportWidth = scroll.ActualWidth;
            if (widthChanged && table.HeaderRow?.TechnicalEntry is { } header)
            {
                var height = 26d;
                foreach (var cell in header.Cells.Where(cell => cell.IsFile))
                {
                    var text = new TextBlock { Text = cell.FileName, FontFamily = scroll.FontFamily, FontSize = 10, TextWrapping = TextWrapping.Wrap };
                    text.Measure(new Size(Math.Max(1, table.CellWidth - 12), double.PositiveInfinity));
                    height = Math.Max(height, text.DesiredSize.Height + 12);
                }
                table.HeaderHeight = height;
            }
        }
        // 种类与文件列宽不同，虚拟化会估算未实现列的宽度；按实际列位置校正，避免回收后错列。
        var panel = TechnicalVisuals<VirtualizingStackPanel>(scroll).FirstOrDefault();
        if (scroll.TemplatedParent is ItemsControl columns && panel?.Children.OfType<FrameworkElement>().FirstOrDefault() is { } cellContainer)
        {
            var index = columns.ItemContainerGenerator.IndexFromContainer(cellContainer);
            var prefix = index == 0 ? 0 : ConflictTechnicalTableState.TypeWidth + (index - 1) * table.CellWidth;
            var correction = cellContainer.TranslatePoint(new Point(), scroll).X - (prefix - table.HorizontalOffset);
            if (Math.Abs(correction) > 0.01)
                scroll.ScrollToHorizontalOffset(Math.Clamp(scroll.HorizontalOffset + correction, 0, scroll.ScrollableWidth));
        }
    }

    private void TechnicalColumns_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 横向 ScrollViewer 也会吞掉上下滚轮；浮动表头同样转交结果列表。
        TechnicalOverlay_PreviewMouseWheel(sender, e);
    }

    private void TechnicalOverlay_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (technicalResultsScroll is null) return;
        e.Handled = true;
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0 || e.Delta == 0) return;
        var scroll = technicalResultsScroll;
        var distance = -(e.Delta / 120d) * (lines < 0 ? scroll.ViewportHeight : lines * 16d);
        // 同向滚轮累加到目标；反向时从当前位置开始，避免继续向旧方向滑行。
        var continuing = technicalScrollActive && Math.Sign(technicalScrollRemaining) == Math.Sign(distance);
        var remaining = (continuing ? technicalScrollRemaining : 0) + distance;
        if (!continuing) technicalScrollVelocity = 0;
        technicalScrollRemaining = Math.Clamp(scroll.VerticalOffset + remaining, 0, scroll.ScrollableHeight) - scroll.VerticalOffset;
        if (technicalScrollRemaining == 0) { StopTechnicalScroll(); return; }
        if (!technicalScrollActive)
        {
            technicalScrollActive = true;
            CompositionTarget.Rendering += TechnicalResults_Rendering;
        }
    }

    private void TechnicalResults_Rendering(object? sender, EventArgs e)
    {
        var frame = ((RenderingEventArgs)e).RenderingTime;
        if (technicalScrollFrame is not { } previous) { technicalScrollFrame = frame; return; }
        if (frame <= previous) return;
        technicalScrollFrame = frame;
        var elapsed = (frame - previous).TotalSeconds;
        const double rate = 50;
        var remaining = technicalScrollRemaining;
        var motion = (technicalScrollVelocity - rate * remaining) * elapsed;
        var decay = Math.Exp(-rate * elapsed);
        technicalScrollRemaining = (remaining - motion) * decay;
        technicalScrollVelocity = (technicalScrollVelocity - rate * motion) * decay;
        var finished = Math.Abs(technicalScrollRemaining) < 0.05 && Math.Abs(technicalScrollVelocity) < 5;
        // 回收行会重估绝对滚动坐标；只推进本帧位移，不能追赶重估前的坐标。
        technicalResultsScroll!.ScrollToVerticalOffset(technicalResultsScroll.VerticalOffset + remaining - (finished ? 0 : technicalScrollRemaining));
        if (finished) StopTechnicalScroll();
    }

    private void StopTechnicalScroll()
    {
        CompositionTarget.Rendering -= TechnicalResults_Rendering;
        technicalScrollActive = false;
        technicalScrollFrame = null;
        technicalScrollRemaining = 0;
        technicalScrollVelocity = 0;
    }

    private void TechnicalResults_PreviewMouseDown(object sender, MouseButtonEventArgs e) => StopTechnicalScroll();
    private void TechnicalResults_PreviewKeyDown(object sender, KeyEventArgs e) => StopTechnicalScroll();

    private void TechnicalResults_Loaded(object sender, RoutedEventArgs e)
    {
        if (!ConflictRowsList.IsVisible) return;
        ConflictRowsList.ApplyTemplate();
        technicalResultsScroll = TechnicalVisuals<ScrollViewer>(ConflictRowsList).FirstOrDefault();
        if (technicalResultsScroll is null) return;
        technicalResultsScroll.ScrollChanged += TechnicalResults_ScrollChanged;
        UpdateTechnicalOverlays();
    }

    private void TechnicalResults_Unloaded(object sender, RoutedEventArgs e)
    {
        StopTechnicalScroll();
        if (technicalResultsScroll is not null) technicalResultsScroll.ScrollChanged -= TechnicalResults_ScrollChanged;
        technicalResultsScroll = null;
        HideTechnicalOverlays();
    }

    private void TechnicalResults_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, technicalResultsScroll)) return;
        UpdateTechnicalOverlays();
    }

    private void TechnicalResults_LayoutUpdated(object? sender, EventArgs e)
    {
        if (technicalResultsScroll is null && ConflictRowsList.IsLoaded && ConflictRowsList.IsVisible)
            TechnicalResults_Loaded(ConflictRowsList, new RoutedEventArgs());
        UpdateTechnicalOverlays();
    }

    private void UpdateTechnicalOverlays()
    {
        if (technicalResultsScroll is null || !ConflictRowsList.IsVisible) { HideTechnicalOverlays(); return; }
        var presenter = TechnicalVisuals<ScrollContentPresenter>(technicalResultsScroll).FirstOrDefault();
        var panel = TechnicalVisuals<VirtualizingStackPanel>(ConflictRowsList).FirstOrDefault();
        if (presenter is null || panel is null) { HideTechnicalOverlays(); return; }
        var viewport = new Rect(presenter.TranslatePoint(new Point(), TechnicalViewport), presenter.RenderSize);
        // 只检查已实现的行，滚动时不遍历或展开整份技术详情。
        var rows = panel.Children.OfType<ListBoxItem>()
            .Where(item => item.DataContext is ConflictResultRow)
            .Select(item => (Item: item, Row: (ConflictResultRow)item.DataContext,
                Bounds: new Rect(item.TranslatePoint(new Point(), TechnicalViewport), item.RenderSize))).ToArray();
        var topTechnical = rows.FirstOrDefault(row => row.Bounds.Bottom > viewport.Top && row.Bounds.Top < viewport.Bottom
            && (row.Row.Kind is ConflictRowKind.TechnicalHeader or ConflictRowKind.TechnicalListHeader or ConflictRowKind.TechnicalListFile
                || row.Row.Kind == ConflictRowKind.Technical && !row.Row.IsExpandable));
        var first = rows.FirstOrDefault(row => row.Row.TechnicalTable is not null && row.Row.Kind != ConflictRowKind.TechnicalScroll && row.Bounds.Bottom > viewport.Top && row.Bounds.Top < viewport.Bottom);
        if (first.Row?.TechnicalTable is not { HeaderRow: { } header } table)
        {
            HideTechnicalOverlays(includeCollapse: false);
            UpdateTechnicalCollapse(topTechnical.Row, viewport);
            return;
        }
        UpdateTechnicalCollapse(topTechnical.Row, viewport);
        var headerItem = rows.FirstOrDefault(row => ReferenceEquals(row.Row.TechnicalTable, table) && row.Row.Kind == ConflictRowKind.TechnicalHeader);
        var footer = rows.FirstOrDefault(row => ReferenceEquals(row.Row.TechnicalTable, table) && row.Row.Kind == ConflictRowKind.TechnicalScroll);
        var bottom = footer.Row is null ? viewport.Bottom : Math.Min(viewport.Bottom, footer.Bounds.Bottom);
        var headerHeight = table.HeaderHeight + 16;
        var top = headerItem.Row is null ? viewport.Top : Math.Max(viewport.Top, headerItem.Bounds.Top);
        top = Math.Min(top, bottom - headerHeight);
        StickyTechnicalContent.Content = header;
        StickyTechnicalHeader.Width = first.Item.ActualWidth;
        StickyTechnicalHeader.Height = headerHeight;
        var boundary = top + headerHeight;
        var fadingRows = rows.Where(row => (headerItem.Row is null || headerItem.Bounds.Top < viewport.Top)
            && ReferenceEquals(row.Row.TechnicalTable, table)
            && row.Row.Kind == ConflictRowKind.Technical && !row.Row.IsExpandable
            && row.Bounds.Top < boundary + 24 && row.Bounds.Bottom > top).ToArray();
        foreach (var item in technicalFadedRows.Except(fadingRows.Select(row => row.Item)).ToArray())
        {
            item.OpacityMask = null;
            technicalFadedRows.Remove(item);
        }
        foreach (var row in fadingRows)
        {
            var start = new Point(0, boundary - row.Bounds.Top);
            var end = new Point(0, start.Y + 24);
            if (row.Item.OpacityMask is not LinearGradientBrush mask || mask.StartPoint != start || mask.EndPoint != end)
            {
                var fade = new LinearGradientBrush(Colors.Transparent, Colors.Black, start, end) { MappingMode = BrushMappingMode.Absolute };
                fade.Freeze();
                row.Item.OpacityMask = fade;
            }
            technicalFadedRows.Add(row.Item);
        }
        Canvas.SetLeft(StickyTechnicalHeader, first.Bounds.Left);
        Canvas.SetTop(StickyTechnicalHeader, top);
        StickyTechnicalHeader.Visibility = Visibility.Visible;
        FloatingTechnicalScroll.DataContext = table;
        FloatingTechnicalScroll.Width = Math.Max(0, first.Item.ActualWidth - 26 - 36);
        Canvas.SetLeft(FloatingTechnicalScroll, first.Bounds.Left + 26 + 18);
        Canvas.SetTop(FloatingTechnicalScroll, bottom - FloatingTechnicalScroll.Height);
        FloatingTechnicalScroll.Visibility = table.ScrollableWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(activeTechnicalTable, table))
        {
            activeTechnicalTable = table;
            UpdateTechnicalScrollOpacity();
        }
    }

    private void UpdateTechnicalCollapse(ConflictResultRow? row, Rect viewport)
    {
        var toggle = row is null || DataContext is not MainWindowViewModel model ? null
            : model.ConflictRows.FirstOrDefault(candidate => candidate.IsTechnicalToggle && candidate.IsExpanded && candidate.OwnerKey == row.OwnerKey);
        if (toggle is null)
        {
            FloatingTechnicalCollapse.Visibility = Visibility.Collapsed;
            FloatingTechnicalCollapse.DataContext = null;
            return;
        }
        var toggleItem = ConflictRowsList.ItemContainerGenerator.ContainerFromItem(toggle) as ListBoxItem;
        var scrolledPastToggle = toggleItem is null || toggleItem.TranslatePoint(new Point(0, toggleItem.ActualHeight), TechnicalViewport).Y <= viewport.Top;
        FloatingTechnicalCollapse.DataContext = toggle;
        FloatingTechnicalCollapse.Visibility = scrolledPastToggle ? Visibility.Visible : Visibility.Collapsed;
        FloatingTechnicalCollapse.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(FloatingTechnicalCollapse, Math.Max(viewport.Left, viewport.Right - FloatingTechnicalCollapse.DesiredSize.Width - 8));
        Canvas.SetTop(FloatingTechnicalCollapse, viewport.Top + 8);
    }

    private void HideTechnicalOverlays(bool includeCollapse = true)
    {
        foreach (var item in technicalFadedRows) item.OpacityMask = null;
        technicalFadedRows.Clear();
        activeTechnicalTable = null;
        if (includeCollapse)
        {
            FloatingTechnicalCollapse.Visibility = Visibility.Collapsed;
            FloatingTechnicalCollapse.DataContext = null;
        }
        StickyTechnicalContent.Content = null;
        StickyTechnicalHeader.Visibility = Visibility.Collapsed;
        FloatingTechnicalScroll.Visibility = Visibility.Collapsed;
        FloatingTechnicalScroll.DataContext = null;
    }

    private void UpdateTechnicalScrollOpacity()
    {
        var opacity = TechnicalTableScrollBar.IsMouseOver || TechnicalTableScrollBar.IsMouseCaptureWithin ? 0.75 : 0.25;
        FloatingTechnicalScroll.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(140)));
    }

    private void TechnicalTableScrollBar_MouseOverChanged(object sender, MouseEventArgs e) => UpdateTechnicalScrollOpacity();
    private void TechnicalTableScrollBar_IsMouseCaptureWithinChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateTechnicalScrollOpacity();

    private static IEnumerable<T> TechnicalVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in TechnicalVisuals<T>(child)) yield return nested;
        }
    }
}
