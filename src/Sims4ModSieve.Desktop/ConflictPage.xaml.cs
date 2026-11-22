using System.ComponentModel;
using System.Collections.ObjectModel;
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
    private ContextMenu? fileMenu;
    private ScrollViewer? technicalResultsScroll;
    private ConflictTechnicalTableState? activeTechnicalTable;
    private bool technicalScrollActive;
    private TimeSpan? technicalScrollFrame;
    private double technicalScrollRemaining;
    private double technicalScrollVelocity;
    private Action? finishGroupAnimation;
    private ListBoxItem? technicalBottomSpace;
    private readonly HashSet<ListBoxItem> technicalFadedRows = [];
    private readonly LinearGradientBrush technicalEdge = new(Colors.Transparent, Colors.Black, new Point(0, 0), new Point(24, 0))
        { MappingMode = BrushMappingMode.Absolute };

    public ConflictPage()
    {
        InitializeComponent();
        technicalEdge.Freeze();
        IsVisibleChanged += (_, _) => { if (!IsVisible) { ConflictFilterPopup.IsOpen = false; CloseFileMenu(); FinishGroupAnimation(); } };
        Unloaded += (_, _) => { ConflictFilterPopup.IsOpen = false; CloseFileMenu(); FinishGroupAnimation(); };
        ((INotifyCollectionChanged)ConflictRowsList.Items).CollectionChanged += (_, _) =>
        {
            StopTechnicalScroll();
            CloseFileMenu();
            ClearTechnicalBottomSpace();
        };
        ConflictRowsList.RequestBringIntoView += (_, _) => StopTechnicalScroll();
        ConflictRowsList.IsVisibleChanged += (_, _) => { if (!ConflictRowsList.IsVisible) StopTechnicalScroll(); };
        ConflictFilterPopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height), PopupPrimaryAxis.Horizontal)];
        DataContextChanged += (_, e) =>
        {
            FinishGroupAnimation();
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

    private void CloseFileMenu()
    {
        if (fileMenu is not null) fileMenu.IsOpen = false;
        fileMenu = null;
    }

    private void FileContextMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Border { DataContext: ConflictResultRow row, ContextMenu: { } menu }
            || DataContext is not MainWindowViewModel model || model.IsBusy) { e.Handled = true; return; }
        CloseFileMenu();
        fileMenu = menu;
        menu.Items.Clear();
        var batch = model.ConflictActionAppliesToSelection(row);
        void Add(string text, ICommand command) => menu.Items.Add(new MenuItem { Header = text, Command = command, CommandParameter = row, Style = (Style)FindResource("FileMenuItemStyle") });
        Add("打开所在文件夹", model.OpenConflictFolderCommand);
        Add("复制路径", model.CopyConflictPathCommand);
        menu.Items.Add(new Separator { Style = (Style)FindResource("FileMenuSeparatorStyle") });
        Add(batch ? "禁用所选" : row.DisableMenuText, model.ToggleConflictFileEnabledCommand);
        Add(batch ? "删除所选" : row.DeleteMenuText, model.DeleteConflictFileCommand);
        menu.Items.Add(new Separator { Style = (Style)FindResource("FileMenuSeparatorStyle") });
        Add("查看文件关系", model.OpenConflictRelationshipsCommand);
        Add(batch ? "忽略所选" : row.IgnoreMenuText, model.IgnoreConflictCommand);
    }

    private void ConflictFileSelection_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel model || sender is not CheckBox { DataContext: ConflictResultRow row } box) return;
        model.SetConflictFileSelected(row, box.IsChecked == true);
    }

    private void ConflictFileSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { IsConflictMultiSelect: true, IsBusy: false } model
            || sender is not Border { DataContext: ConflictResultRow { IsSelectableFile: true } row }) return;
        model.SetConflictFileSelected(row, row.Selection.IsSelected != true);
        e.Handled = true;
    }

    private void ConflictGroupSelection_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel model || sender is not CheckBox { DataContext: ConflictResultRow row }) return;
        model.ToggleConflictGroupSelection(row);
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
        {
            item.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
            void PositionTitle()
            {
                if (!ReferenceEquals(DataContext, model) || !model.ConflictRows.Contains(row) || !ConflictRowsList.IsVisible) return;
                if (TechnicalVisuals<ScrollViewer>(ConflictRowsList).FirstOrDefault() is not { } scroll
                    || TechnicalVisuals<ScrollContentPresenter>(scroll).FirstOrDefault() is not { } presenter) return;
                if (ConflictRowsList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem target) return;
                var previousRow = model.ConflictRows.TakeWhile(candidate => candidate.Key != key).LastOrDefault(candidate => candidate.IsCardStart);
                var previous = previousRow is null ? null : ConflictRowsList.ItemContainerGenerator.ContainerFromItem(previousRow) as ListBoxItem;
                var headingSpace = Math.Max(target.ActualHeight, previous?.ActualHeight ?? 0);
                var top = target.TranslatePoint(new Point(), presenter).Y;
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + top - headingSpace);
                scroll.UpdateLayout();
            }
            PositionTitle();
            Dispatcher.BeginInvoke(PositionTitle, DispatcherPriority.ApplicationIdle);
        }
    }, DispatcherPriority.Loaded);

    private Action<IReadOnlyList<ConflictResultRow>>? CaptureConflictViewport(bool anchorToGroup)
    {
        if (anchorToGroup) return CaptureBulkGroupViewport();
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

    private Action<IReadOnlyList<ConflictResultRow>>? CaptureBulkGroupViewport()
    {
        StopTechnicalScroll();
        var viewport = ResultListViewport.Capture(ConflictRowsList);
        var center = viewport?.CenterItem();
        var middle = center?.DataContext as ConflictResultRow ?? center?.Tag as ConflictResultRow;
        var key = middle?.IsCardStart == true ? middle.Key : middle?.OwnerKey;
        var item = key is null ? null : viewport!.RealizedItems.FirstOrDefault(item => item.DataContext is ConflictResultRow row && row.Key == key);
        var top = item is null ? 0 : viewport!.Top(item);
        FinishGroupAnimation();
        if (DataContext is not MainWindowViewModel model || viewport is null || key is null) return null;
        return replacement =>
        {
            var updated = replacement.FirstOrDefault(row => row.Key == key);
            if (updated is not null)
                viewport.RestoreAfterLayout(updated, top, () => ConflictRowsList.IsVisible
                    && ReferenceEquals(DataContext, model) && ReferenceEquals(model.ConflictRows, replacement));
        };
    }

    private void ExpandToggle_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e) =>
        ResultListViewport.SuppressMouseFocusScroll(sender, e);

    private void ExpandToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ConflictResultRow { IsExpandable: true } row }
            || DataContext is not MainWindowViewModel model) return;
        StopTechnicalScroll();
        if (ResultListViewport.Capture(ConflictRowsList) is not { } viewport || viewport.Container(row) is not { } item)
        {
            FinishGroupAnimation();
            model.ToggleConflictExpanded(row);
            return;
        }

        var top = viewport.Top(item);
        FinishGroupAnimation();
        var original = model.ConflictRows;
        model.ToggleConflictExpanded(row, replacement => Dispatcher.BeginInvoke(() =>
        {
            bool IsCurrent() => ConflictRowsList.IsVisible && ReferenceEquals(DataContext, model)
                && ReferenceEquals(model.ConflictRows, replacement);
            if (!IsCurrent() || replacement.FirstOrDefault(candidate => candidate.Key == row.Key) is not { } updated) return;
            if (row.IsCardStart && SystemParameters.ClientAreaAnimation && updated.IsExpanded != row.IsExpanded)
                AnimateGroup(viewport, updated, row.IsExpanded ? original : replacement, replacement, top, IsCurrent);
            else if (updated.IsTechnicalToggle)
                RestoreTechnicalToggle(viewport, updated, top, replacement[^1]);
            else
                viewport.Restore(updated, top);
        }, DispatcherPriority.DataBind));
    }

    private void RestoreTechnicalToggle(ResultListViewport viewport, ConflictResultRow toggle, double top, ConflictResultRow lastRow)
    {
        viewport.Restore(toggle, top);
        if (toggle.IsExpanded || technicalResultsScroll is not { } scroll || scroll.ScrollableHeight - scroll.VerticalOffset > 0.5
            || viewport.Container(toggle) is not { } anchor || viewport.Container(lastRow) is not { } last) return;
        var missingSpace = viewport.Top(anchor) - top;
        if (missingSpace <= 0.5) return;

        // 收起末尾的技术详情后，滚动范围可能短到无法保持按钮位置。
        technicalBottomSpace = last;
        last.DataContextChanged += TechnicalBottomSpace_DataContextChanged;
        last.Margin = new Thickness(0, 0, 0, missingSpace);
        viewport.Restore(toggle, top);
    }

    private void TechnicalBottomSpace_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => ClearTechnicalBottomSpace();

    private void ClearTechnicalBottomSpace()
    {
        if (technicalBottomSpace is not { } last) return;
        technicalBottomSpace = null;
        last.DataContextChanged -= TechnicalBottomSpace_DataContextChanged;
        last.ClearValue(MarginProperty);
    }

    private void AnimateGroup(ResultListViewport viewport, ConflictResultRow header, IReadOnlyList<ConflictResultRow> expandedRows,
        IReadOnlyList<ConflictResultRow> replacement, double top, Func<bool> isCurrent)
    {
        var group = expandedRows.SkipWhile(row => row.Key != header.Key);
        var expandedHeader = group.First();
        var children = group.Skip(1).TakeWhile(row => !row.IsCardStart).ToArray();
        var content = new StackPanel();
        var body = new Border { Child = content, ClipToBounds = true, Height = 0, IsHitTestVisible = false };
        var animationItem = new ListBoxItem { Content = body, Tag = header, Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var previewHeader = header with { IsCardEnd = expandedHeader.IsCardEnd };
        var preview = new ObservableCollection<object>();
        var headerIndex = 0;
        foreach (var row in replacement)
        {
            if (row.Key == header.Key)
            {
                headerIndex = preview.Count;
                preview.Add(previewHeader);
                preview.Add(animationItem);
            }
            else if (row.IsCardStart || row.OwnerKey != header.Key) preview.Add(row);
        }

        ConflictRowsList.SetCurrentValue(ItemsControl.ItemsSourceProperty, preview);
        viewport.Restore(previewHeader, top);
        // 只布置动画期间会进入视口的行，大组的其余行继续由列表虚拟化。
        var width = viewport.Container(previewHeader)?.ActualWidth ?? ConflictRowsList.ActualWidth;
        var height = 0d;
        foreach (var row in children)
        {
            var contentRow = new ContentPresenter { Content = row, ContentTemplate = ConflictRowsList.ItemTemplate };
            content.Children.Add(contentRow);
            contentRow.Measure(new Size(width, double.PositiveInfinity));
            height += contentRow.DesiredSize.Height;
            if (height >= viewport.Height) break;
        }

        finishGroupAnimation = () =>
        {
            body.BeginAnimation(HeightProperty, null);
            body.ClearValue(HeightProperty);
            if (!ReferenceEquals(ConflictRowsList.ItemsSource, preview)) return;
            var currentItem = viewport.Container(previewHeader);
            var position = currentItem is null ? top : viewport.Top(currentItem);
            if (header.IsExpanded && content.Children.Count < children.Length)
            {
                // 大组一次发布剩下的虚拟行，避免逐条更新列表。
                ConflictRowsList.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.UpdateTarget();
                if (isCurrent()) viewport.Restore(header, position);
                return;
            }
            // 保留标题控件，动画结束时仍可接住紧接的一次点击。
            preview.RemoveAt(headerIndex + 1);
            if (previewHeader.IsCardEnd != header.IsCardEnd) preview[headerIndex] = header;
            if (header.IsExpanded)
                for (var index = 0; index < children.Length; index++) preview.Insert(headerIndex + 1 + index, children[index]);
        };
        ResultListViewport.AnimateHeight(body, header.IsExpanded ? 0 : height, header.IsExpanded ? height : 0,
            header.IsExpanded, FinishGroupAnimation);
    }

    private void FinishGroupAnimation()
    {
        var finish = finishGroupAnimation;
        finishGroupAnimation = null;
        finish?.Invoke();
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
            else if (args.PropertyName == nameof(ConflictTechnicalTableState.ViewportWidth))
                UpdateTechnicalHeaderHeight(scroll, table);
        };
        scroll.Tag = (table, handler);
        table.PropertyChanged += handler;
        scroll.ScrollToHorizontalOffset(table.HorizontalOffset);
        scroll.OpacityMask = table.HorizontalOffset > 0 ? technicalEdge : null;
        UpdateTechnicalHeaderHeight(scroll, table);
    }

    private void TechnicalColumns_Unloaded(object sender, RoutedEventArgs e)
    {
        var scroll = (ScrollViewer)sender;
        if (scroll.Tag is ValueTuple<ConflictTechnicalTableState, PropertyChangedEventHandler> subscription)
            subscription.Item1.PropertyChanged -= subscription.Item2;
        scroll.Tag = null;
    }

    private static void UpdateTechnicalHeaderHeight(ScrollViewer scroll, ConflictTechnicalTableState table)
    {
        if (table.ViewportWidth > 0 && table.HeaderRow?.TechnicalEntry is { } header)
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
        ClearTechnicalBottomSpace();
        if (technicalResultsScroll is not null) technicalResultsScroll.ScrollChanged -= TechnicalResults_ScrollChanged;
        technicalResultsScroll = null;
        HideTechnicalOverlays();
    }

    private void TechnicalResults_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, technicalResultsScroll)) return;
        if (technicalBottomSpace is { } last && e.VerticalChange < 0 && e.ExtentHeightChange == 0
            && e.VerticalOffset + last.Margin.Bottom <= e.ExtentHeight - e.ViewportHeight + 0.5)
            ClearTechnicalBottomSpace();
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
