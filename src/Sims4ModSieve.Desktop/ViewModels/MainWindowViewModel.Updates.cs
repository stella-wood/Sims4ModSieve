using System.ComponentModel;
using System.IO;
using System.Net.Http;
using Sims4ModSieve.Desktop.Infrastructure;
using Sims4ModSieve.Desktop.Services;

namespace Sims4ModSieve.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private enum UpdateState { Available, Downloading, Ready, Waiting, Starting }
    private AvailableUpdate? availableUpdate;
    private UpdateCheckService? updateService;
    private bool updateCheckStarted;
    private bool isCheckingForUpdates;
    private UpdateCheckResult? manualUpdateCheckResult;
    private UpdateState updateState;
    private DownloadedUpdate? downloadedUpdate;
    private CancellationTokenSource? updateDownloadCancellation;
    private readonly CancellationTokenSource updateLifetime = new();
    private Func<DownloadedUpdate, Task> startUpdate = null!;
    private string updateStatusText = string.Empty;
    private double updateProgress;

    public bool HasAvailableUpdate => availableUpdate is not null;
    public string AvailableUpdateText => $"发现新版本 v{availableUpdate?.Version.Split('+')[0]}";
    public string CurrentVersionText => $"当前版本 v{ProductVersion}";
    public string UpdateStatusText => updateStatusText;
    public double UpdateDownloadProgress => updateProgress;
    public bool IsUpdateDownloading => updateState == UpdateState.Downloading;
    public bool ShowUpdateDownload => downloadedUpdate is null && !IsUpdateDownloading;
    public bool ShowUpdateRestart => downloadedUpdate is not null;
    public string ManualUpdateCheckText { get; private set; } = string.Empty;
    public bool ShowManualUpdate => manualUpdateCheckResult?.Status == UpdateCheckStatus.Available && HasAvailableUpdate;
    public bool ShowUpdateReleasePage => manualUpdateCheckResult?.Status == UpdateCheckStatus.Failed;
    public string ManualUpdateDownloadText => IsUpdateDownloading && updateProgress > 0
        ? $"{UpdateStatusText}（{Math.Floor(updateProgress)}%）" : UpdateStatusText;
    public AsyncRelayCommand CheckUpdateCommand { get; private set; } = null!;
    public RelayCommand OpenUpdateReleasePageCommand { get; private set; } = null!;
    public AsyncRelayCommand DownloadUpdateCommand { get; private set; } = null!;
    public AsyncRelayCommand RestartUpdateCommand { get; private set; } = null!;
    public RelayCommand CancelUpdateDownloadCommand { get; private set; } = null!;
    public event EventHandler? UpdateRestartRequested;

    private void InitializeUpdates(Func<DownloadedUpdate, Task>? start)
    {
        startUpdate = start ?? (download => UpdateCheckService.StartInstallerAsync(download, AppContext.BaseDirectory, updateLifetime.Token));
        CheckUpdateCommand = new(() => CheckForUpdatesCoreAsync(manual: true),
            _ => SetManualUpdateCheckResult(new(UpdateCheckStatus.Failed, FailureReason: "暂时无法检查，请重试")),
            () => updateService is not null && !isCheckingForUpdates && updateState == UpdateState.Available);
        OpenUpdateReleasePageCommand = new(_ => RunSettingsOperation(
            () => settingsSupport.OpenReleasePage(UpdateCheckService.LatestReleaseUrl),
            message => AboutFeedback = message, string.Empty, "无法打开发布页"));
        DownloadUpdateCommand = new(DownloadUpdateAsync, ShowUpdateError,
            () => availableUpdate is not null && !isCheckingForUpdates && updateState == UpdateState.Available);
        RestartUpdateCommand = new(RestartUpdateAsync, ShowUpdateError, () => updateState == UpdateState.Ready);
        CancelUpdateDownloadCommand = new(_ => updateDownloadCancellation?.Cancel(), _ => IsUpdateDownloading);
    }

    public async Task CheckForUpdatesAsync(UpdateCheckService service, CancellationToken cancellationToken = default)
    {
        if (updateCheckStarted) return;
        updateCheckStarted = true;
        updateService = service;
        await CheckForUpdatesCoreAsync(manual: false, cancellationToken);
    }

    private async Task CheckForUpdatesCoreAsync(bool manual, CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token, cancellationToken);
        isCheckingForUpdates = true;
        if (manual) SetManualUpdateCheckResult(null, checking: true);
        NotifyUpdateState();
        try
        {
            var result = await updateService!.CheckAsync(ProductVersion, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            if (result.Status != UpdateCheckStatus.Failed && availableUpdate != result.Update)
            {
                availableUpdate = result.Update;
                updateStatusText = string.Empty;
                OnPropertyChanged(nameof(AvailableUpdateText));
                OnPropertyChanged(nameof(HasAvailableUpdate));
            }
            if (manual) SetManualUpdateCheckResult(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            isCheckingForUpdates = false;
            NotifyUpdateState();
        }
    }

    private void SetManualUpdateCheckResult(UpdateCheckResult? result, bool checking = false)
    {
        manualUpdateCheckResult = result;
        ManualUpdateCheckText = checking ? "正在检查更新" : result?.Status switch
        {
            UpdateCheckStatus.Available => AvailableUpdateText,
            UpdateCheckStatus.UpToDate => $"已是最新版本 v{ProductVersion}",
            UpdateCheckStatus.Failed => $"检查失败：{result.FailureReason}",
            _ => string.Empty,
        };
        OnPropertyChanged(nameof(ManualUpdateCheckText));
        OnPropertyChanged(nameof(ShowManualUpdate));
        OnPropertyChanged(nameof(ShowUpdateReleasePage));
    }

    private async Task DownloadUpdateAsync()
    {
        if (availableUpdate is null || updateService is null) return;
        updateState = UpdateState.Downloading;
        updateStatusText = "正在下载更新";
        updateProgress = 0;
        NotifyUpdateState();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
        updateDownloadCancellation = cancellation;
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (updateState != UpdateState.Downloading || cancellation.IsCancellationRequested) return;
                updateProgress = value;
                OnPropertyChanged(nameof(UpdateDownloadProgress));
                OnPropertyChanged(nameof(ManualUpdateDownloadText));
            });
            downloadedUpdate = await updateService.DownloadAsync(availableUpdate, progress, cancellation.Token);
            updateState = UpdateState.Ready;
            updateStatusText = "下载完成";
        }
        catch (OperationCanceledException)
        {
            updateState = UpdateState.Available;
            updateStatusText = cancellation.IsCancellationRequested ? "已取消下载" : "下载超时，请重试";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException)
        {
            updateState = UpdateState.Available;
            updateStatusText = exception is InvalidDataException ? exception.Message : "下载失败，请重试";
        }
        finally
        {
            updateDownloadCancellation = null;
            NotifyUpdateState();
        }
    }

    private async Task RestartUpdateAsync()
    {
        if (downloadedUpdate is null) return;
        updateState = UpdateState.Waiting;
        updateStatusText = "正在等待当前操作完成";
        NotifyUpdateState();
        if (IsBusy)
        {
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(IsBusy) && !IsBusy) idle.TrySetResult();
            }
            PropertyChanged += Changed;
            try { await idle.Task.WaitAsync(updateLifetime.Token); }
            finally { PropertyChanged -= Changed; }
        }
        updateLifetime.Token.ThrowIfCancellationRequested();
        updateState = UpdateState.Starting;
        updateStatusText = "正在准备更新";
        IsBusy = true;
        NotifyUpdateState();
        var handedOff = false;
        try
        {
            await startUpdate(downloadedUpdate);
            handedOff = true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or Win32Exception)
        {
            updateState = UpdateState.Ready;
            updateStatusText = exception is InvalidDataException or InvalidOperationException
                ? exception.Message : "未能开始更新，请重试";
            if (exception is InvalidDataException)
            {
                UpdateCheckService.DiscardDownload(downloadedUpdate.Path);
                downloadedUpdate = null;
                updateState = UpdateState.Available;
            }
        }
        finally
        {
            IsBusy = false;
            NotifyUpdateState();
        }
        if (handedOff) UpdateRestartRequested?.Invoke(this, EventArgs.Empty);
    }

    public void StopUpdates()
    {
        updateLifetime.Cancel();
        if (downloadedUpdate is not null && updateState != UpdateState.Starting)
            UpdateCheckService.DiscardDownload(downloadedUpdate.Path);
    }

    private void ShowUpdateError(Exception exception)
    {
        updateStatusText = "更新未完成，请重试";
        NotifyUpdateState();
    }

    private void NotifyUpdateState()
    {
        foreach (var name in new[] { nameof(UpdateStatusText), nameof(UpdateDownloadProgress), nameof(IsUpdateDownloading),
            nameof(ShowUpdateDownload), nameof(ShowUpdateRestart), nameof(ManualUpdateDownloadText) }) OnPropertyChanged(name);
        CheckUpdateCommand.RaiseCanExecuteChanged();
        DownloadUpdateCommand.RaiseCanExecuteChanged();
        RestartUpdateCommand.RaiseCanExecuteChanged();
        CancelUpdateDownloadCommand.RaiseCanExecuteChanged();
    }
}
