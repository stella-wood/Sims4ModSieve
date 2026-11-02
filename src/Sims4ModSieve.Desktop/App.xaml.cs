using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Sims4ModSieve.Desktop;

public partial class App : Application
{
    private SingleInstanceLock? instanceLock;

    static App() => ToolTipService.InitialShowDelayProperty.OverrideMetadata(
        typeof(FrameworkElement), new FrameworkPropertyMetadata(650));

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);

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
