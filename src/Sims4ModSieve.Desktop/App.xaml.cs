using System.IO;
using System.Net.Http;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace Sims4ModSieve.Desktop;

public partial class App : Application
{
    private readonly bool startUserInterface = true;
    private readonly SplashScreen? splashScreen;
    private readonly Func<MainWindow> createMainWindow;
    private readonly string dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sims4ModSieve");
    private SingleInstanceLock? instanceLock;
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly HttpClient updateClient;

    static App() => ToolTipService.InitialShowDelayProperty.OverrideMetadata(
        typeof(FrameworkElement), new FrameworkPropertyMetadata(650));

    public App() : this(null) { }

    internal App(SplashScreen? splashScreen, string? dataDirectory = null,
        Func<MainWindow>? createMainWindow = null, HttpClient? updateClient = null)
    {
        this.splashScreen = splashScreen;
        if (dataDirectory is not null) this.dataDirectory = dataDirectory;
        this.createMainWindow = createMainWindow ?? (() => new MainWindow());
        this.updateClient = updateClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan };
    }

    public App(bool startUserInterface) : this(null) => this.startUserInterface = startUserInterface;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        if (!startUserInterface) return;

        try
        {
            instanceLock = SingleInstanceLock.TryAcquire(dataDirectory);
            if (instanceLock is null)
            {
                Shutdown();
                return;
            }

            try
            {
                splashScreen?.Show(autoClose: false);
            }
            catch (Exception exception)
            {
                splashScreen?.Close(TimeSpan.Zero);
                TryWriteStartupFailure(exception);
            }
            _ = UpdateCheckService.CleanupDownloadsAsync(updateCancellation.Token);
            var window = createMainWindow();
            MainWindow = window;
            // .NET 10 的 SplashScreen 淡出会丢失 PNG 透明度，首次渲染后直接关闭。
            window.ContentRendered += (_, _) => splashScreen?.Close(TimeSpan.Zero);
            window.Show();
            _ = ((MainWindowViewModel)window.DataContext).CheckForUpdatesAsync(
                new UpdateCheckService(updateClient), updateCancellation.Token);
        }
        catch (Exception exception)
        {
            splashScreen?.Close(TimeSpan.Zero);
            TryWriteStartupFailure(exception);
            MessageBox.Show(
                $"Sims 4 Mod Sieve 启动失败。\n\n{exception.Message}",
                "无法启动",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        updateCancellation.Cancel();
        updateClient.Dispose();
        updateCancellation.Dispose();
        instanceLock?.Dispose();
        base.OnExit(eventArgs);
    }

    private void TryWriteStartupFailure(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(dataDirectory, "startup-error.log"), exception.ToString());
        }
        catch (Exception writeException) when (writeException is IOException or UnauthorizedAccessException)
        {
            // 诊断写入失败不能阻止主窗口启动或原有错误提示。
        }
    }
}
