using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Sims4ModSieve.Desktop;

public partial class App : Application
{
    private const int ActivateInstanceMessage = 0x8001;
    private static readonly IntPtr MessageOnlyWindow = new(-3);
    private readonly bool startUserInterface = true;
    private readonly SplashScreen? splashScreen;
    private readonly Func<MainWindow> createMainWindow;
    private readonly string dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sims4ModSieve");
    private SingleInstanceLock? instanceLock;
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly HttpClient updateClient;
    private readonly string activationWindowName;
    private HwndSource? activationSource;

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
        activationWindowName = "Sims4ModSieve.Activate." + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(this.dataDirectory)).ToUpperInvariant())));
    }

    public App(bool startUserInterface) : this(null) => this.startUserInterface = startUserInterface;

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        if (!startUserInterface) return;

        try
        {
            instanceLock = SingleInstanceLock.TryAcquire(dataDirectory);
            if (instanceLock is null)
            {
                await ActivateRunningInstanceAsync();
                Shutdown();
                return;
            }

            activationSource = new HwndSource(new HwndSourceParameters(activationWindowName)
            {
                ParentWindow = MessageOnlyWindow,
                WindowStyle = 0,
            });
            activationSource.AddHook(ReceiveActivation);

            var version = new SettingsSupportService().ProductVersion;
            var splashShown = false;
            try
            {
                if (splashScreen is not null && ShouldShowSplash(version))
                {
                    splashScreen.Show(autoClose: false);
                    splashShown = true;
                }
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
            window.ContentRendered += (_, _) =>
            {
                splashScreen?.Close(TimeSpan.Zero);
                if (splashShown)
                {
                    splashShown = false;
                    RememberSplashVersion(version);
                }
            };
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
        activationSource?.Dispose();
        instanceLock?.Dispose();
        base.OnExit(eventArgs);
    }

    private async Task ActivateRunningInstanceAsync()
    {
        var timer = Stopwatch.StartNew();
        do
        {
            var receiver = FindWindowEx(MessageOnlyWindow, IntPtr.Zero, null, activationWindowName);
            if (receiver != IntPtr.Zero)
            {
                GetWindowThreadProcessId(receiver, out var processId);
                AllowSetForegroundWindow(processId);
                PostMessage(receiver, ActivateInstanceMessage, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            // 首个进程可能刚获得运行锁，尚未创建消息接收端。
            await Task.Delay(25);
        } while (timer.Elapsed < TimeSpan.FromSeconds(1));
    }

    private IntPtr ReceiveActivation(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != ActivateInstanceMessage) return IntPtr.Zero;
        handled = true;
        Dispatcher.BeginInvoke(ActivateMainWindow, DispatcherPriority.ApplicationIdle);
        return IntPtr.Zero;
    }

    private void ActivateMainWindow()
    {
        var handle = MainWindow is { IsVisible: true } window
            ? new WindowInteropHelper(window).Handle
            : IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            EnumThreadWindows(GetCurrentThreadId(), (candidate, _) =>
            {
                if (!IsWindowVisible(candidate)) return true;
                handle = candidate;
                return false;
            }, IntPtr.Zero);
        }
        if (handle == IntPtr.Zero || !IsWindowVisible(handle)) return;
        if (IsIconic(handle)) ShowWindow(handle, 9);
        var popup = GetLastActivePopup(handle);
        SetForegroundWindow(popup != IntPtr.Zero && IsWindowVisible(popup) ? popup : handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    private delegate bool EnumThreadWindowProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumThreadWindowProc callback, IntPtr parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

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

    private bool ShouldShowSplash(string version)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "startup-version.txt");
            return !File.Exists(path) || File.ReadAllText(path).Trim() != version;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryWriteStartupFailure(exception);
            return true;
        }
    }

    private void RememberSplashVersion(string version)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(dataDirectory, "startup-version.txt"), version);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryWriteStartupFailure(exception);
        }
    }
}
