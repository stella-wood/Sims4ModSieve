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
        Closed += (_, _) =>
        {
            viewModel.UpdateRestartRequested -= RestartForUpdate;
            viewModel.StopUpdates();
            viewModel.DuplicateGroupLocationRequested -= LocateDuplicateGroup;
            viewModel.DuplicateViewportCaptureRequested -= CaptureDuplicateViewport;
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
}
