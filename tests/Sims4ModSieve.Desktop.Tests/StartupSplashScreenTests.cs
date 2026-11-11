using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class StartupSplashScreenTests
{
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
        if (scenario is "normal" or "missing-splash")
        {
            app.Dispatcher.BeginInvoke(() =>
            {
                Console.WriteLine("RENDERED");
                Console.Out.Flush();
                Console.ReadLine();
                app.MainWindow.Close();
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
}
