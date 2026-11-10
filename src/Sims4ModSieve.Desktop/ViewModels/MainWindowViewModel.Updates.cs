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
    public AsyncRelayCommand DownloadUpdateCommand { get; private set; } = null!;
    public AsyncRelayCommand RestartUpdateCommand { get; private set; } = null!;
    public RelayCommand CancelUpdateDownloadCommand { get; private set; } = null!;
    public event EventHandler? UpdateRestartRequested;

    private void InitializeUpdates(Func<DownloadedUpdate, Task>? start)
    {
        startUpdate = start ?? (download => UpdateCheckService.StartInstallerAsync(download, AppContext.BaseDirectory, updateLifetime.Token));
        DownloadUpdateCommand = new(DownloadUpdateAsync, ShowUpdateError,
            () => availableUpdate is not null && updateState == UpdateState.Available);
        RestartUpdateCommand = new(RestartUpdateAsync, ShowUpdateError, () => updateState == UpdateState.Ready);
        CancelUpdateDownloadCommand = new(_ => updateDownloadCancellation?.Cancel(), _ => IsUpdateDownloading);
    }

    public async Task CheckForUpdatesAsync(UpdateCheckService service, CancellationToken cancellationToken = default)
    {
        if (updateCheckStarted) return;
        updateCheckStarted = true;
        updateService = service;
        availableUpdate = await Task.Run(() => service.CheckAsync(ProductVersion, cancellationToken));
        if (cancellationToken.IsCancellationRequested) availableUpdate = null;
        OnPropertyChanged(nameof(AvailableUpdateText));
        OnPropertyChanged(nameof(HasAvailableUpdate));
        NotifyUpdateState();
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
            nameof(ShowUpdateDownload), nameof(ShowUpdateRestart) }) OnPropertyChanged(name);
        DownloadUpdateCommand.RaiseCanExecuteChanged();
        RestartUpdateCommand.RaiseCanExecuteChanged();
        CancelUpdateDownloadCommand.RaiseCanExecuteChanged();
    }
}
