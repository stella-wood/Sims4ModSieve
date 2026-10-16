using System.Windows;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;

namespace Sims4ModSieve.Desktop;

public partial class MainWindow : Window
{
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
        viewModel.DuplicateGroupLocationRequested += LocateDuplicateGroup;
        Closed += (_, _) => viewModel.DuplicateGroupLocationRequested -= LocateDuplicateGroup;
    }

    private void LocateDuplicateGroup(DuplicateGroupViewModel group) => Dispatcher.BeginInvoke(() =>
    {
        DuplicateGroupsList.ScrollIntoView(group);
        DuplicateGroupsList.UpdateLayout();
    }, System.Windows.Threading.DispatcherPriority.Loaded);
}
