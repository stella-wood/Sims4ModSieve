using Sims4ModSieve.Core.Settings;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;

namespace Sims4ModSieve.Desktop;

public partial class MainWindow : Window
{
    private MainWindowViewModel? closingAfterWork;
    private Border? animatedDuplicateBody;
    private readonly ResultListTransitions duplicateTransitions;

    private void ShowUpdatePopup(object sender, RoutedEventArgs eventArgs) => UpdatePopup.IsOpen = !UpdatePopup.IsOpen;

    private void UpdatePopupOpened(object? sender, EventArgs eventArgs) =>
        (UpdateDownloadButton.IsVisible ? UpdateDownloadButton : UpdateRestartButton.IsVisible ? UpdateRestartButton : UpdateCancelButton).Focus();

    private void UpdatePopupKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape) return;
        UpdatePopup.IsOpen = false;
        UpdateButton.Focus();
        eventArgs.Handled = true;
    }

    private void UpdateButtonVisibilityChanged(object sender, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (!UpdateButton.IsVisible) UpdatePopup.IsOpen = false;
    }

    public MainWindow() : this(CreateDefaultViewModel())
    {
    }

    private static MainWindowViewModel CreateDefaultViewModel()
    {
        var settings = new DuplicateSettingsStore();
        return new(new DuplicateRunService(DuplicateScanner.CreateDefault()), new FolderPickerService(), settings,
            scanCacheDirectory: settings.DataDirectory);
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        duplicateTransitions = new(DuplicateResultsBody, DuplicateGroupsList);
        UpdatePopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new CustomPopupPlacement(new Point(-popupSize.Width - 8, targetSize.Height - popupSize.Height), PopupPrimaryAxis.Vertical)];
        UpdateToolTip.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, -popupSize.Height - 6), PopupPrimaryAxis.Horizontal)];
        DataContext = viewModel;
        Activated += (_, _) => viewModel.RefreshSavedFolders();
        viewModel.UpdateRestartRequested += RestartForUpdate;
        Closing += (_, eventArgs) =>
        {
            if (!viewModel.IsBusy) return;
            eventArgs.Cancel = true;
            if (closingAfterWork is not null) return;
            closingAfterWork = viewModel;
            viewModel.PropertyChanged += CloseAfterWork;
            viewModel.CancelActiveScan();
        };
        viewModel.DuplicateGroupLocationRequested += LocateDuplicateGroup;
        viewModel.DuplicateViewportCaptureRequested += CaptureDuplicateViewport;
        viewModel.DuplicateSearchTransitionRequested += SwitchDuplicateResultsAsync;
        viewModel.DuplicateRemovalTransitionRequested += AnimateDuplicateRemovalAsync;
        Closed += (_, _) =>
        {
            viewModel.UpdateRestartRequested -= RestartForUpdate;
            viewModel.StopUpdates();
            viewModel.DuplicateGroupLocationRequested -= LocateDuplicateGroup;
            viewModel.DuplicateViewportCaptureRequested -= CaptureDuplicateViewport;
            viewModel.DuplicateSearchTransitionRequested -= SwitchDuplicateResultsAsync;
            viewModel.DuplicateRemovalTransitionRequested -= AnimateDuplicateRemovalAsync;
            viewModel.PropertyChanged -= CloseAfterWork;
            closingAfterWork = null;
        };
    }

    private void RestartForUpdate(object? sender, EventArgs eventArgs) => Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var search = DuplicatePathSearch.IsVisible ? DuplicatePathSearch : ConflictDetectionPage.ConflictPathSearch;
        if (e.Handled || !search.IsVisible || !search.IsEnabled) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            search.FocusInput();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
            e.Handled = search.ClearQuery();
    }

    private void CloseAfterWork(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(MainWindowViewModel.IsBusy) || closingAfterWork?.IsBusy != false) return;
        closingAfterWork.PropertyChanged -= CloseAfterWork;
        closingAfterWork = null;
        Dispatcher.BeginInvoke(Close);
    }

    private void LocateDuplicateGroup(DuplicateGroupViewModel group) => Dispatcher.BeginInvoke(() =>
    {
        DuplicateGroupsList.ScrollIntoView(group);
        DuplicateGroupsList.UpdateLayout();
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    private static bool IsDuplicateGroupToggle(object source) => source is ToggleButton
        { Name: "DuplicateHeaderToggle" or "ExpanderArrowToggle" };

    private void DuplicateGroup_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (IsDuplicateGroupToggle(e.TargetObject)) ResultListViewport.SuppressMouseFocusScroll(e.TargetObject, e);
    }

    private void DuplicateGroup_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Expander group && ReferenceEquals(group.Template?.FindName("ExpandedContent", group), animatedDuplicateBody))
            FinishDuplicateAnimation();
    }

    private void DuplicateGroupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDuplicateGroupToggle(e.OriginalSource) || sender is not Expander group
            || group.Template.FindName("ExpandedContent", group) is not Border { Child: StackPanel content } body) return;

        var viewport = ResultListViewport.Capture(DuplicateGroupsList);
        var item = group.DataContext;
        var top = viewport?.Container(item) is { } container ? viewport.Top(container) : (double?)null;
        var animatedHeight = ReferenceEquals(body, animatedDuplicateBody) ? body.Height : (double?)null;
        FinishDuplicateAnimation();
        if (SystemParameters.ClientAreaAnimation && group.ActualWidth > 0)
        {
            body.Visibility = Visibility.Visible;
            content.Measure(new Size(group.ActualWidth, double.PositiveInfinity));
            var fullHeight = content.DesiredSize.Height;
            var from = animatedHeight ?? (group.IsExpanded ? 0 : fullHeight);
            animatedDuplicateBody = body;
            ResultListViewport.AnimateHeight(body, from, group.IsExpanded ? fullHeight : 0, group.IsExpanded, FinishDuplicateAnimation);
        }
        if (top is { } position)
            viewport!.RestoreAfterLayout(item, position, () => DuplicateGroupsList.IsVisible && ReferenceEquals(group.DataContext, item));
    }

    private void FinishDuplicateAnimation()
    {
        if (animatedDuplicateBody is not { } body) return;
        animatedDuplicateBody = null;
        body.ClearValue(VisibilityProperty);
        body.BeginAnimation(HeightProperty, null);
        body.ClearValue(HeightProperty);
    }

    private Action? CaptureDuplicateViewport()
    {
        var viewport = ResultListViewport.Capture(DuplicateGroupsList);
        var center = viewport?.CenterItem();
        var top = center is null ? 0 : viewport!.Top(center);
        FinishDuplicateAnimation();
        if (center?.DataContext is not DuplicateGroupViewModel group) return null;
        return () => viewport!.RestoreAfterLayout(group, top,
            () => DuplicateGroupsList.IsVisible && DuplicateGroupsList.Items.Contains(group));
    }

    private Task SwitchDuplicateResultsAsync(Action apply) => duplicateTransitions.SwitchAsync(apply);

    private async Task AnimateDuplicateRemovalAsync(IReadOnlySet<string> paths, Action apply)
    {
        if (DataContext is not MainWindowViewModel model || ResultListViewport.Capture(DuplicateGroupsList) is not { } viewport)
        {
            apply();
            return;
        }

        FinishDuplicateAnimation();
        var groups = model.VisibleDuplicateGroups;
        var disappearing = groups.Where(group =>
        {
            if (!group.Files.Any(file => paths.Contains(file.Path))) return false;
            var remaining = group.Files.Where(file => !paths.Contains(file.Path)).Select(file => file.Path).ToArray();
            return remaining.Length < 2 || model.DuplicateSearch.Rank(remaining) < 0;
        }).ToHashSet();
        var visible = viewport.VisibleItems.ToArray();
        var visibleGroups = visible.Select(item => item.DataContext).OfType<DuplicateGroupViewModel>().ToHashSet();
        var anchorItem = visible.FirstOrDefault(item => item.DataContext is DuplicateGroupViewModel group && !disappearing.Contains(group));
        var anchor = anchorItem?.DataContext as DuplicateGroupViewModel;
        var top = anchorItem is null ? 0 : viewport.Top(anchorItem);
        void Publish()
        {
            apply();
            if (anchor is not null && DuplicateResultsBody.IsVisible && model.VisibleDuplicateGroups.FirstOrDefault(group =>
                group.Model.Sha256 == anchor.Model.Sha256 && group.Model.FileSize == anchor.Model.FileSize) is { } surviving)
                duplicateTransitions.Position(viewport, surviving, top);
        }
        if (!duplicateTransitions.CanAnimate) { Publish(); return; }

        var visibleFiles = new HashSet<DuplicateFileViewModel>();
        foreach (var item in visible)
        {
            if (item.DataContext is not DuplicateGroupViewModel group || disappearing.Contains(group)
                || ResultListViewport.FindChild<ItemsControl>(item) is not { } files) continue;
            visibleFiles.UnionWith(group.Files.Where(file => paths.Contains(file.Path)
                && files.ItemContainerGenerator.ContainerFromItem(file) is FrameworkElement element && viewport.IsVisible(element)));
        }
        if (visibleFiles.Count == 0 && !disappearing.Overlaps(visibleGroups)) { Publish(); return; }

        var selection = new DuplicateSelectionService();
        var preview = groups.Where(group => !disappearing.Contains(group) || visibleGroups.Contains(group)).Select(group =>
        {
            if (visibleGroups.Contains(group) || !group.Files.Any(file => paths.Contains(file.Path))) return group;
            var remaining = group.Model.Files.Where(file => !paths.Contains(file.Path)).ToArray();
            var suggestion = selection.Select(remaining);
            var selected = group.Files.Where(file => file.IsSelected).Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var previewGroup = new DuplicateGroupViewModel(group.Model with
            {
                Files = remaining, SuggestedKeepPath = suggestion.KeepPath, SuggestedDeletePaths = suggestion.DeletePaths,
            }, group.Index) { IsExpanded = group.IsExpanded };
            foreach (var file in previewGroup.Files) file.IsSelected = selected.Contains(file.Path);
            return previewGroup;
        }).ToArray();
        var filteredFiles = new List<ItemsControl>();
        void Position() { if (anchor is not null) duplicateTransitions.Position(viewport, anchor, top, realize: false); }
        try
        {
            if (!preview.SequenceEqual(groups))
            {
                var first = visible[0];
                var firstGroup = first.DataContext;
                var firstTop = viewport.Top(first);
                DuplicateGroupsList.SetCurrentValue(ItemsControl.ItemsSourceProperty, preview);
                if (anchor is not null) duplicateTransitions.Position(viewport, anchor, top);
                else viewport.Restore(firstGroup, firstTop);
            }
            foreach (var item in viewport.RealizedItems.ToArray())
            {
                if (item.DataContext is not DuplicateGroupViewModel group || disappearing.Contains(group)
                    || ResultListViewport.FindChild<ItemsControl>(item) is not { } files) continue;
                var display = group.Files.Where(file => !paths.Contains(file.Path) || visibleFiles.Contains(file)).ToArray();
                if (display.Length == group.Files.Count) continue;
                files.SetCurrentValue(ItemsControl.ItemsSourceProperty, display);
                filteredFiles.Add(files);
            }
            DuplicateGroupsList.UpdateLayout();
            Position();

            var elements = preview.Where(group => visibleGroups.Contains(group)).SelectMany(group =>
            {
                if (viewport.Container(group) is not { } container) return Enumerable.Empty<FrameworkElement>();
                if (disappearing.Contains(group)) return [container];
                return ResultListViewport.FindChild<ItemsControl>(container) is { } files
                    ? group.Files.Where(visibleFiles.Contains).Select(files.ItemContainerGenerator.ContainerFromItem).OfType<FrameworkElement>() : [];
            }).ToArray();
            await duplicateTransitions.ExitAsync(viewport, elements, anchor is null ? null : viewport.Container(anchor), Publish);
        }
        finally
        {
            if (ReferenceEquals(DuplicateGroupsList.ItemsSource, preview))
                DuplicateGroupsList.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.UpdateTarget();
            foreach (var files in filteredFiles) files.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.UpdateTarget();
        }
    }
}
