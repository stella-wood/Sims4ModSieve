using Sims4ModSieve.Desktop.Infrastructure;
using Sims4ModSieve.Desktop.Services;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private AvailableUpdate? availableUpdate;
    private bool updateCheckStarted;

    public bool HasAvailableUpdate => availableUpdate is not null;
    public string AvailableUpdateText => $"发现新版本 v{availableUpdate?.Version}";
    public string CurrentVersionText => $"当前版本 v{ProductVersion}";
    public RelayCommand OpenUpdateReleaseCommand { get; private set; } = null!;

    public async Task CheckForUpdatesAsync(UpdateCheckService service, CancellationToken cancellationToken = default)
    {
        if (updateCheckStarted) return;
        updateCheckStarted = true;
        availableUpdate = await Task.Run(() => service.CheckAsync(ProductVersion, cancellationToken));
        if (cancellationToken.IsCancellationRequested) availableUpdate = null;
        OnPropertyChanged(nameof(AvailableUpdateText));
        OnPropertyChanged(nameof(HasAvailableUpdate));
    }

    private void OpenUpdateRelease()
    {
        if (availableUpdate is null || !UpdateCheckService.IsReleaseUrl(availableUpdate.ReleaseUrl)) return;
        try
        {
            settingsSupport.OpenReleasePage(availableUpdate.ReleaseUrl);
        }
        catch (Exception) { }
    }
}
