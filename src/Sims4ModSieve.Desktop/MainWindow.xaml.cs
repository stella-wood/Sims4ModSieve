using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;

namespace Sims4ModSieve.Desktop;

public partial class MainWindow : Window
{
    private MainWindowViewModel? closingAfterWork;

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

    public MainWindow() : this(new MainWindowViewModel(
        new DuplicateRunService(DuplicateScanner.CreateDefault()),
        new FolderPickerService(),
        new DuplicateSettingsStore()))
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        UpdatePopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new CustomPopupPlacement(new Point(-popupSize.Width - 8, targetSize.Height - popupSize.Height), PopupPrimaryAxis.Vertical)];
        UpdateToolTip.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, -popupSize.Height - 6), PopupPrimaryAxis.Horizontal)];
        DataContext = viewModel;
        viewModel.UpdateRestartRequested += RestartForUpdate;
        Closing += (_, eventArgs) =>
        {
            if (!viewModel.IsBusy) return;
            eventArgs.Cancel = true;
            if (closingAfterWork is not null) return;
            closingAfterWork = viewModel;
            viewModel.PropertyChanged += CloseAfterWork;
        };
        viewModel.DuplicateGroupLocationRequested += LocateDuplicateGroup;
        Closed += (_, _) =>
        {
            viewModel.UpdateRestartRequested -= RestartForUpdate;
            viewModel.StopUpdates();
            viewModel.DuplicateGroupLocationRequested -= LocateDuplicateGroup;
            viewModel.PropertyChanged -= CloseAfterWork;
            closingAfterWork = null;
        };
    }

    private void RestartForUpdate(object? sender, EventArgs eventArgs) => Close();

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
}
