using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class StartupSplashScreenTests
{
    [TestMethod]
    [DataRow("normal")]
    [DataRow("minimized")]
    [DataRow("maximized-minimized")]
    [DataRow("starting")]
    [DataRow("dialog")]
    [DataRow("startup-error")]
    public Task SecondLaunchActivatesExistingWindowAndRestoresMinimizedState(string state) => WpfTestHost.RunAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "s4ms-activation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process StartInstance(string scenario)
        {
            var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
            var start = new ProcessStartInfo(dotnet)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (var argument in new[] { typeof(SingleInstanceLockProbe).Assembly.Location, "--startup-splash", scenario, directory })
                start.ArgumentList.Add(argument);
            start.Environment["TEMP"] = directory;
            start.Environment["TMP"] = directory;
            return Process.Start(start)!;
        }
        using var first = StartInstance(state == "startup-error" ? "main-window-failure" : "activation-" + state);
        var firstError = first.StandardError.ReadToEndAsync();
        Process? second = null;
        var foregroundWindow = new Window
        {
            Title = "激活测试辅助窗口",
            Content = new Border { Background = Brushes.White },
        };
        Exception? testFailure = null;
        try
        {
            Assert.AreEqual("READY", await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await first.StandardInput.WriteLineAsync();
            Assert.AreEqual("SPLASH", await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            if (state != "starting")
            {
                await first.StandardInput.WriteLineAsync();
                if (state == "startup-error")
                {
                    var errorTimer = Stopwatch.StartNew();
                    while (!VisibleWindows(first.Id).Any(window => window.Title == "无法启动")
                        && errorTimer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
                    Assert.IsTrue(VisibleWindows(first.Id).Any(window => window.Title == "无法启动"));
                }
                else
                {
                    Assert.AreEqual("RENDERED", await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                }
            }
            foregroundWindow.Show();
            WpfTestHost.PlaceOnScreen(foregroundWindow, 320, 180);
            await WpfTestHost.LayoutAsync(foregroundWindow);
            Assert.IsTrue(foregroundWindow.Activate());
            foregroundWindow.Topmost = false;
            Assert.AreEqual(new WindowInteropHelper(foregroundWindow).Handle, GetForegroundWindow());

            second = StartInstance("activation-second");
            var secondError = second.StandardError.ReadToEndAsync();
            Assert.AreEqual("READY", await second.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsTrue(AllowSetForegroundWindow((uint)second.Id));
            await second.StandardInput.WriteLineAsync();
            if (state == "starting")
            {
                await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual(0, second.ExitCode, await secondError);
                await first.StandardInput.WriteLineAsync();
                Assert.AreEqual("RENDERED", await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            }
            var title = state switch
            {
                "dialog" => "激活测试对话框",
                "startup-error" => "无法启动",
                _ => "Sims 4 Mod Sieve",
            };
            var timer = Stopwatch.StartNew();
            IntPtr target = IntPtr.Zero;
            while (timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (!second.HasExited) Assert.IsEmpty(VisibleWindows(second.Id), "第二实例不能显示启动图或主窗口。");
                target = VisibleWindows(first.Id).FirstOrDefault(window => window.Title == title).Handle;
                if (target != IntPtr.Zero && GetForegroundWindow() == target && !IsIconic(target)) break;
                await Task.Delay(10);
            }
            Assert.AreNotEqual(IntPtr.Zero, target);
            Assert.AreEqual(target, GetForegroundWindow(), "再次启动后应将已有窗口切到前台。");
            Assert.IsFalse(IsIconic(target));
            Assert.AreEqual(state == "maximized-minimized", IsZoomed(target));
            Assert.IsFalse(first.HasExited);
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, second.ExitCode, await secondError);
            if (state == "startup-error") PostMessage(target, 0x10, IntPtr.Zero, IntPtr.Zero);
            else await first.StandardInput.WriteLineAsync();
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(state == "startup-error" ? -1 : 0, first.ExitCode, await firstError);
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                if (second is not null)
                {
                    if (!second.HasExited) second.Kill();
                    await second.WaitForExitAsync();
                    second.Dispose();
                }
                if (!first.HasExited) first.Kill();
                await first.WaitForExitAsync();
                foregroundWindow.Close();
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception cleanupException) when (testFailure is not null)
            {
                throw new AggregateException("测试失败且临时数据清理失败。", testFailure, cleanupException);
            }
        }
    });

    [TestMethod]
    [DataRow("normal")]
    [DataRow("second-instance")]
    [DataRow("main-window-failure")]
    [DataRow("missing-splash")]
    public async Task StartupClosesSplashBeforeErrorsAndSkipsItForSecondInstance(string scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), "s4ms-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        var start = new ProcessStartInfo(dotnet)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in new[] { typeof(SingleInstanceLockProbe).Assembly.Location, "--startup-splash", scenario, directory })
            start.ArgumentList.Add(argument);
        start.Environment["TEMP"] = directory;
        start.Environment["TMP"] = directory;
        using var child = Process.Start(start)!;
        var error = child.StandardError.ReadToEndAsync();
        try
        {
            Assert.AreEqual("READY", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var rendered = child.StandardOutput.ReadLineAsync();
            await child.StandardInput.WriteLineAsync();
            var sawSplash = false;
            var sawMain = false;
            var sawError = false;
            var creatingWindow = false;
            var released = false;
            var timer = Stopwatch.StartNew();
            while (!child.HasExited && timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                var windows = VisibleWindows(child.Id);
                var hasSplash = windows.Any(window => window.ClassName == "SplashScreen");
                sawSplash |= hasSplash;
                sawMain |= windows.Any(window => window.Title == "Sims 4 Mod Sieve");
                if (scenario == "second-instance") Assert.IsEmpty(windows, "第二实例不能显示启动画面或主窗口。");
                foreach (var window in windows.Where(window => window.ClassName == "#32770" && window.Title == "无法启动"))
                {
                    sawError = true;
                    Assert.IsFalse(hasSplash, "启动失败提示出现之前必须关闭启动画面。");
                    PostMessage(window.Handle, 0x10, IntPtr.Zero, IntPtr.Zero);
                }
                if (!creatingWindow && rendered.IsCompletedSuccessfully && await rendered is { } splashState)
                {
                    var expectedSplash = scenario is "normal" or "main-window-failure";
                    Assert.AreEqual(expectedSplash ? "SPLASH" : "NO_SPLASH", splashState);
                    Assert.AreEqual(expectedSplash, hasSplash);
                    creatingWindow = true;
                    rendered = child.StandardOutput.ReadLineAsync();
                    await child.StandardInput.WriteLineAsync();
                }
                else if (scenario is "normal" or "missing-splash" && !released && rendered.IsCompletedSuccessfully)
                {
                    Assert.AreEqual("RENDERED", await rendered);
                    Assert.IsFalse(hasSplash, "主窗口渲染后必须关闭启动画面。");
                    released = true;
                    await child.StandardInput.WriteLineAsync();
                }
                await Task.Delay(1);
            }
            Assert.IsTrue(child.HasExited, "启动探针未正常退出。");
            Assert.AreEqual(scenario == "main-window-failure" ? -1 : 0,
                child.ExitCode, await error);
            Assert.AreEqual(scenario is "normal" or "main-window-failure", sawSplash);
            Assert.AreEqual(scenario is "normal" or "missing-splash", sawMain);
            Assert.AreEqual(scenario == "main-window-failure", sawError);
            Assert.AreEqual(scenario != "second-instance", creatingWindow);
            if (sawError || scenario == "missing-splash")
            {
                var log = File.ReadAllText(Path.Combine(directory, "startup-error.log"));
                StringAssert.Contains(log, scenario == "missing-splash" ? "missing-splash.png" : "System.");
            }
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("--installer-check-uninstall", 0)]
    [DataRow("--installer-unknown", 11)]
    public async Task RealProgramEntryHandlesInstallerCommandsWithoutWindows(string command, int expectedExitCode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "s4ms-entry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        var start = new ProcessStartInfo(dotnet)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in new[] { typeof(App).Assembly.Location, command, directory })
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        var error = child.StandardError.ReadToEndAsync();
        try
        {
            var timer = Stopwatch.StartNew();
            while (!child.HasExited && timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                Assert.IsEmpty(VisibleWindows(child.Id), "真实程序检查入口不能显示窗口。");
                await Task.Delay(1);
            }
            Assert.IsTrue(child.HasExited, "真实程序检查入口未退出。");
            Assert.AreEqual(expectedExitCode, child.ExitCode, await error);
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static int RunProbe(string scenario, string directory)
    {
        using var held = scenario == "second-instance" ? SingleInstanceLock.TryAcquire(directory) : null;
        using var files = new SettingsViewModelTests.Scenario();
        using var client = new HttpClient(new OfflineUpdateHandler());
        var resource = scenario == "missing-splash" ? "assets/missing-splash.png" : "assets/splash.png";
        var splash = new SplashScreen(typeof(App).Assembly, resource);
        var app = new App(splash, directory, () =>
        {
            Console.WriteLine(VisibleWindows(Environment.ProcessId).Any(window => window.ClassName == "SplashScreen")
                ? "SPLASH" : "NO_SPLASH");
            Console.Out.Flush();
            Console.ReadLine();
            return new MainWindow(files.Model());
        }, client);
        app.InitializeComponent();
        if (scenario == "main-window-failure") app.Resources["WindowBrush"] = new object();
        var activation = scenario.StartsWith("activation-", StringComparison.Ordinal) && scenario != "activation-second";
        if (scenario is "normal" or "missing-splash" || activation)
        {
            app.Dispatcher.BeginInvoke(() =>
            {
                Window? dialog = null;
                if (scenario == "activation-maximized-minimized") app.MainWindow.WindowState = WindowState.Maximized;
                if (scenario is "activation-minimized" or "activation-maximized-minimized")
                    app.MainWindow.WindowState = WindowState.Minimized;
                Console.WriteLine("RENDERED");
                Console.Out.Flush();
                if (activation)
                {
                    _ = Task.Run(() =>
                    {
                        Console.ReadLine();
                        app.Dispatcher.BeginInvoke(() =>
                        {
                            dialog?.Close();
                            app.MainWindow.Close();
                        });
                    });
                    if (scenario == "activation-dialog")
                    {
                        dialog = new Window { Owner = app.MainWindow, Title = "激活测试对话框", Width = 280, Height = 160 };
                        dialog.ShowDialog();
                    }
                }
                else
                {
                    Console.ReadLine();
                    app.MainWindow.Close();
                }
            }, DispatcherPriority.ApplicationIdle);
        }
        Console.WriteLine("READY");
        Console.Out.Flush();
        Console.ReadLine();
        try { return app.Run(); }
        finally { splash.Close(TimeSpan.Zero); }
    }

    private sealed class OfflineUpdateHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static List<(IntPtr Handle, string ClassName, string Title)> VisibleWindows(int processId)
    {
        var windows = new List<(IntPtr, string, string)>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != processId || !IsWindowVisible(window)) return true;
            var className = new StringBuilder(256);
            var title = new StringBuilder(256);
            GetClassName(window, className, className.Capacity);
            GetWindowText(window, title, title.Capacity);
            windows.Add((window, className.ToString(), title.ToString()));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
}
