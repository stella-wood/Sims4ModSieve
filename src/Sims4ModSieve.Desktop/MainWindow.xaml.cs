using System.ComponentModel;
using System.Windows;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;

namespace Sims4ModSieve.Desktop;

public partial class MainWindow : Window
{
    private MainWindowViewModel? closingAfterWork;

    public MainWindow() : this(new MainWindowViewModel(
        new DuplicateRunService(DuplicateScanner.CreateDefault()),
        new FolderPickerService(),
        new DuplicateSettingsStore()))
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
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
            viewModel.DuplicateGroupLocationRequested -= LocateDuplicateGroup;
            viewModel.PropertyChanged -= CloseAfterWork;
            closingAfterWork = null;
        };
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
}
