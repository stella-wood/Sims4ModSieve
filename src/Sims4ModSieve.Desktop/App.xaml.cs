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
    private SingleInstanceLock? instanceLock;
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly HttpClient updateClient = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };

    static App() => ToolTipService.InitialShowDelayProperty.OverrideMetadata(
        typeof(FrameworkElement), new FrameworkPropertyMetadata(650));

    public App() { }

    public App(bool startUserInterface) => this.startUserInterface = startUserInterface;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        if (!startUserInterface) return;

        if (InstallerChecks.TryRun(eventArgs.Args, out var installerExitCode))
        {
            Shutdown(installerExitCode);
            return;
        }

        try
        {
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sims4ModSieve");
            instanceLock = SingleInstanceLock.TryAcquire(dataDirectory);
            if (instanceLock is null)
            {
                Shutdown();
                return;
            }

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            _ = ((MainWindowViewModel)window.DataContext).CheckForUpdatesAsync(
                new UpdateCheckService(updateClient), updateCancellation.Token);
        }
        catch (Exception exception)
        {
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

    private static void TryWriteStartupFailure(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Sims4ModSieve");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "startup-error.log"), exception.ToString());
        }
        catch (Exception writeException) when (writeException is IOException or UnauthorizedAccessException)
        {
            // The original startup error is still shown to the user.
        }
    }
}
