using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Desktop.Services;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;
using static Sims4ModSieve.Desktop.Tests.SettingsViewModelTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class UpdateCheckTests
{
    private const string Url = UpdateCheckService.ReleasePrefix + "tag/v99.0.0";

    [TestMethod]
    [DataRow("0.1.0-alpha.1", "0.1.0-alpha.2", -1)]
    [DataRow("0.1.0-alpha.2", "0.1.0", -1)]
    [DataRow("0.1.0", "0.2.0", -1)]
    [DataRow("0.9.0", "0.10.0", -1)]
    [DataRow("v0.10.0", "0.9.0", 1)]
    [DataRow("v0.1.0+abc.123", "0.1.0+xyz", 0)]
    [DataRow("0.1.0-alpha.2", "0.1.0-alpha.10", -1)]
    [DataRow("0.1.0-alpha.10", "0.1.0-alpha.beta", -1)]
    [DataRow("0.1.0-alpha", "0.1.0-alpha.1", -1)]
    [DataRow("0.1.0-beta", "0.1.0-rc.1", -1)]
    [DataRow("0.1.0-A", "0.1.0-a", -1)]
    [DataRow("999999999999999999999.0.0", "1.0.0", 1)]
    public void SemVerOrdering(string left, string right, int expected)
    {
        Assert.IsTrue(UpdateCheckService.TryCompareVersions(left, right, out var result));
        Assert.AreEqual(expected, Math.Sign(result));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("0.1")]
    [DataRow("01.1.0")]
    [DataRow("0.1.0-alpha.01")]
    [DataRow("0.1.0-")]
    [DataRow("0.1.0+")]
    [DataRow("0.1.0-alpha..1")]
    [DataRow("0.1.0\n")]
    [DataRow("latest")]
    public void InvalidSemVerIsRejected(string version)
    {
        Assert.IsFalse(UpdateCheckService.TryCompareVersions(version, "0.1.0", out _));
        Assert.IsFalse(UpdateCheckService.TryCompareVersions("0.1.0", version, out _));
    }

    [TestMethod]
    [DataRow("v99.0.0", UpdateCheckStatus.UpToDate)]
    [DataRow("v98.0.0", UpdateCheckStatus.UpToDate)]
    [DataRow("v99.0.1", UpdateCheckStatus.Available)]
    public async Task ReleaseRedirectDeterminesUpdateStatus(string tag, UpdateCheckStatus expected)
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("https://github.com/stella-wood/Sims4ModSieve/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("Sims4ModSieve/99.0.0", request.Headers.UserAgent.ToString());
            return Task.FromResult(Redirect("/stella-wood/Sims4ModSieve/releases/tag/" + tag));
        });
        using var client = new HttpClient(handler);
        var result = await new UpdateCheckService(client).CheckAsync("99.0.0");
        Assert.AreEqual(expected, result.Status);
        Assert.IsNull(result.FailureReason);
        Assert.AreEqual(1, handler.Calls);
        if (expected == UpdateCheckStatus.Available)
        {
            var update = result.Update!;
            Assert.AreEqual("99.0.1", update.Version);
            Assert.AreEqual(UpdateCheckService.ReleasePrefix + "tag/v99.0.1", update.ReleaseUrl);
            var installer = update.Installer!;
            Assert.AreEqual(UpdateCheckService.ReleasePrefix + "download/v99.0.1/Sims4ModSieve-99.0.1-setup.exe", installer.DownloadUrl);
            Assert.AreEqual(installer.DownloadUrl + ".sha256", installer.ChecksumUrl);
            Assert.IsNull(installer.Size);
        }
        else Assert.IsNull(result.Update);
    }

    [TestMethod]
    [DataRow(302, null)]
    [DataRow(302, "https://example.com/releases/tag/v99.0.0")]
    [DataRow(302, "http://github.com/stella-wood/Sims4ModSieve/releases/tag/v99.0.0")]
    [DataRow(302, "https://github.com/other/Sims4ModSieve/releases/tag/v99.0.0")]
    [DataRow(302, UpdateCheckService.LatestReleaseUrl)]
    [DataRow(302, Url + "/extra")]
    [DataRow(302, Url + "%2Fextra")]
    [DataRow(302, Url + "?source=proxy")]
    [DataRow(302, UpdateCheckService.ReleasePrefix + "tag/garbage")]
    [DataRow(200, Url)]
    [DataRow(301, Url)]
    [DataRow(403, null)]
    [DataRow(404, null)]
    [DataRow(429, null)]
    public async Task UnexpectedReleaseResponseReportsFailure(int status, string? location)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Headers = { Location = location is null ? null : new Uri(location) },
        }));
        using var client = new HttpClient(handler);
        var result = await new UpdateCheckService(client).CheckAsync("0.1.0");
        Assert.AreEqual(UpdateCheckStatus.Failed, result.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.FailureReason));
        Assert.IsNull(result.Update);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public Task AutomaticCheckFailureStaysSilentAndOnlyRunsOnce() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        using var handler = new Handler((_, _) => throw new HttpRequestException("offline"));
        using var client = new HttpClient(handler);
        await model.CheckForUpdatesAsync(new UpdateCheckService(client));
        await model.CheckForUpdatesAsync(new UpdateCheckService(client));
        Assert.AreEqual(1, handler.Calls);
        Assert.IsFalse(model.HasAvailableUpdate);
        Assert.AreEqual(string.Empty, model.ManualUpdateCheckText);
        Assert.IsFalse(File.Exists(Path.Combine(files.Root, "Sims4ModSieve", "application-error.log")));
    });

    [TestMethod]
    public async Task RequestTimesOutWithoutCallerCancellation()
    {
        using var handler = new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Redirect(Url);
        });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var result = await new UpdateCheckService(client).CheckAsync("0.1.0").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(UpdateCheckStatus.Failed, result.Status);
        Assert.AreEqual("连接 GitHub 超时", result.FailureReason);
    }

    [TestMethod]
    public Task PendingRequestIsInvisibleAndCancellationIsSilent() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Redirect(Url);
        });
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var check = model.CheckForUpdatesAsync(new UpdateCheckService(client), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(check.IsCompleted);
        Assert.IsFalse(model.HasAvailableUpdate);
        cancellation.Cancel();
        await check.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(model.HasAvailableUpdate);
    });

    [TestMethod]
    [DataRow(UpdateCheckStatus.Available, "about-update-available.png")]
    [DataRow(UpdateCheckStatus.UpToDate, "about-update-current.png")]
    [DataRow(UpdateCheckStatus.Failed, "about-update-failed.png")]
    public Task AboutUpdateCheckShowsResultAndAction(UpdateCheckStatus status, string screenshot) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        string? opened = null;
        var model = files.Model(support: new SettingsSupportService(files.Root, files.Folder("Documents"), openTarget: url => opened = url));
        var manualResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var assets = new HttpMessageInvoker(UpdateDownloadTests.ValidHandler());
        var checks = 0;
        using var handler = new Handler((request, token) =>
        {
            if (request.RequestUri!.AbsoluteUri != UpdateCheckService.LatestReleaseUrl) return assets.SendAsync(request, token);
            return ++checks == 1 ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)) : manualResponse.Task.WaitAsync(token);
        });
        using var client = new HttpClient(handler);
        await model.CheckForUpdatesAsync(new UpdateCheckService(client));
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        WpfTestHost.ShowOffScreen(window);
        if (status is UpdateCheckStatus.Available or UpdateCheckStatus.Failed) WpfTestHost.PlaceOnScreen(window);
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("about");
        await WpfTestHost.LayoutAsync(window);

        var page = Descendants<SettingsPage>(window).Single();
        var check = (Button)page.FindName("CheckUpdateButton");
        var result = (TextBlock)page.FindName("UpdateCheckResultText");
        var spinner = (FrameworkElement)check.Template.FindName("CheckUpdateSpinner", check);
        var current = (FrameworkElement)check.Template.FindName("CurrentUpdateLabel", check);
        var release = (Button)page.FindName("OpenUpdateReleasePageButton");
        var popup = (Popup)page.FindName("AboutUpdatePopup");
        var download = (Button)page.FindName("AboutUpdateDownloadButton");
        Assert.IsFalse(result.IsVisible);
        Assert.IsTrue(check.IsVisible && check.IsEnabled && check.IsTabStop);
        Assert.AreEqual("检查更新", AutomationProperties.GetName(check));
        if (status == UpdateCheckStatus.UpToDate) SaveScreenshot(window, null, "about-update-idle.png");
        PressEnter(check);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsFalse(check.IsEnabled);
        Assert.IsTrue(spinner.IsVisible);
        if (SystemParameters.ClientAreaAnimation) Assert.IsTrue(((RotateTransform)spinner.RenderTransform).HasAnimatedProperties);
        Assert.AreEqual("检查中", AutomationProperties.GetName(check));
        SaveScreenshot(window, null, "about-update-checking.png");
        await model.CheckUpdateCommand.ExecuteAsync();
        Assert.AreEqual(2, checks);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        spinner.IsVisibleChanged += (_, _) => { if (!spinner.IsVisible) finished.TrySetResult(); };
        manualResponse.SetResult(status switch
        {
            UpdateCheckStatus.Available => Redirect(Url),
            UpdateCheckStatus.UpToDate => Redirect(UpdateCheckService.ReleasePrefix + "tag/v" + model.ProductVersion),
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden),
        });
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(status != UpdateCheckStatus.UpToDate, check.IsEnabled);
        Assert.AreEqual(status == UpdateCheckStatus.UpToDate, current.IsVisible);
        Assert.AreEqual(status == UpdateCheckStatus.UpToDate ? "已是最新版本" : "检查更新", AutomationProperties.GetName(check));
        Assert.AreEqual(status == UpdateCheckStatus.Failed, release.IsVisible);
        Assert.AreEqual(status == UpdateCheckStatus.Failed, result.IsVisible);
        Assert.AreEqual(status == UpdateCheckStatus.Available, popup.IsOpen);
        Assert.AreEqual(status == UpdateCheckStatus.Available, download.IsVisible);
        if (!release.IsVisible) SaveScreenshot(window, popup, screenshot);

        if (release.IsVisible)
        {
            var toast = (Border)page.FindName("AboutUpdateFailureToast");
            Assert.AreEqual(SystemParameters.ClientAreaAnimation, toast.HasAnimatedProperties);
            Assert.AreEqual("检查失败：GitHub 拒绝了请求", result.Text);
            Assert.IsTrue(release.PointToScreen(new Point()).Y >= check.PointToScreen(new Point(0, check.ActualHeight)).Y);
            await WpfTestHost.ClickAsync(release);
            Assert.AreEqual(UpdateCheckService.LatestReleaseUrl, opened);
            SaveScreenshot(window, null, screenshot);
            var hidden = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            result.IsVisibleChanged += (_, _) => { if (!result.IsVisible) hidden.TrySetResult(); };
            await hidden.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.IsFalse(toast.HasAnimatedProperties);
            Assert.IsTrue(release.IsVisible);
        }
        if (download.IsVisible)
        {
            var heading = (TextBlock)page.FindName("AboutAvailableUpdateText");
            Assert.AreEqual("发现新版本 v99.0.0", heading.Text);
            var panel = (FrameworkElement)popup.Child;
            Assert.IsTrue(panel.PointToScreen(new Point(0, panel.ActualHeight)).Y <= check.PointToScreen(new Point()).Y);
            var restart = (Button)page.FindName("AboutUpdateRestartButton");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            restart.IsVisibleChanged += (_, _) => { if (restart.IsVisible) ready.TrySetResult(); };
            PressEnter(download);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(restart.IsEnabled);
            restart.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(restart)!, 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert.IsFalse(popup.IsOpen);
            PressEnter(check);
            Assert.IsTrue(popup.IsOpen);
            Assert.AreEqual(2, checks);
        }
        model.GoHomeCommand.Execute(null);
        model.OpenSettingsCommand.Execute(null);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual("检查更新", AutomationProperties.GetName(check));
        Assert.IsTrue(check.IsEnabled);
        Assert.IsFalse(current.IsVisible);
        Assert.IsFalse(result.IsVisible);
        Assert.IsFalse(release.IsVisible);
        Assert.IsFalse(popup.IsOpen);
        window.Close();
    });

    [TestMethod]
    public Task LeavingAboutCancelsPendingUpdateCheck() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var requests = 0;
        using var handler = new Handler(async (_, token) =>
        {
            if (++requests == 2) await Task.Delay(Timeout.Infinite, token);
            return Redirect(UpdateCheckService.ReleasePrefix + "tag/v" + model.ProductVersion);
        });
        using var client = new HttpClient(handler);
        await model.CheckForUpdatesAsync(new UpdateCheckService(client));
        var window = new MainWindow(model);
        WpfTestHost.ShowOffScreen(window);
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("about");
        await WpfTestHost.LayoutAsync(window);

        var pending = model.CheckUpdateCommand.ExecuteAsync();
        Assert.IsTrue(model.IsManualUpdateChecking);
        model.SelectSettingsSectionCommand.Execute("credits");
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        model.SelectSettingsSectionCommand.Execute("about");
        Assert.IsFalse(model.IsManualUpdateChecking);
        Assert.IsTrue(model.CheckUpdateCommand.CanExecute(null));
        await model.CheckUpdateCommand.ExecuteAsync();
        Assert.IsTrue(model.IsManualUpdateCurrent);
        Assert.AreEqual(3, requests);
        window.Close();
    });

    [TestMethod]
    public Task FailedManualCheckKeepsPreviouslyDiscoveredUpdate() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        var checks = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++checks == 2
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Redirect(Url)));
        using var client = new HttpClient(handler);
        await model.CheckForUpdatesAsync(new UpdateCheckService(client));
        Assert.IsTrue(model.HasAvailableUpdate);
        Assert.AreEqual(string.Empty, model.ManualUpdateCheckText);

        await model.CheckUpdateCommand.ExecuteAsync();
        Assert.IsTrue(model.ShowUpdateReleasePage);
        Assert.IsTrue(model.HasAvailableUpdate);
        Assert.IsTrue(model.DownloadUpdateCommand.CanExecute(null));

        await model.CheckUpdateCommand.ExecuteAsync();
        Assert.IsFalse(model.ShowUpdateReleasePage);
        Assert.IsTrue(model.ShowManualUpdate);
        Assert.AreEqual(3, checks);
    });

    [TestMethod]
    public Task HomeIconPopupAndDownloadWork() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        string? opened = null;
        var model = files.Model(support: new SettingsSupportService(files.Root, files.Folder("Documents"), openTarget: url => opened = url),
            startUpdate: _ => Task.FromException(new IOException("test launch failure")));
        using var handler = UpdateDownloadTests.ValidHandler();
        using var client = new HttpClient(handler);
        var window = new MainWindow(model);
        WpfTestHost.ShowOffScreen(window);
        WpfTestHost.PlaceOnScreen(window);
        await WpfTestHost.LayoutAsync(window);
        var icon = (Button)window.FindName("UpdateButton");
        var popup = (Popup)window.FindName("UpdatePopup");
        Assert.IsFalse(icon.IsVisible);
        await model.CheckForUpdatesAsync(new UpdateCheckService(client));
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(icon.IsVisible);
        var tooltip = (ToolTip)icon.ToolTip;
        Assert.AreEqual(System.Windows.Input.Cursors.Hand, icon.Cursor);
        tooltip.PlacementTarget = icon;
        tooltip.IsOpen = true;
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual("发现新版本 v99.0.0", ((TextBlock)tooltip.Content).Text);
        var content = (FrameworkElement)window.Content;
        var tooltipOffset = content.PointFromScreen(tooltip.PointToScreen(new Point()));
        Assert.IsTrue(new Rect(content.RenderSize).Contains(new Rect(tooltipOffset, tooltip.RenderSize)));
        Assert.IsTrue(tooltip.PointToScreen(new Point(0, tooltip.ActualHeight)).Y <= icon.PointToScreen(new Point()).Y);
        tooltip.IsOpen = false;
        SaveScreenshot(window, popup, "home-update-icon.png");
        await WpfTestHost.ClickAsync(icon, new Point(2, 2));
        Assert.IsTrue(popup.IsOpen);
        await WpfTestHost.LayoutAsync(window);
        AssertPopupInsideWindow(window, popup);
        SaveScreenshot(window, popup, "home-update-popup.png");
        var download = (Button)window.FindName("UpdateDownloadButton");
        await WpfTestHost.MoveMouseAsync(download);
        SaveScreenshot(window, popup, "home-update-download-hover.png");
        await WpfTestHost.ClickAsync(download);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsNull(opened);
        Assert.IsTrue(model.ShowUpdateRestart);
        Assert.IsFalse(download.IsVisible);
        var restart = (Button)window.FindName("UpdateRestartButton");
        Assert.IsTrue(restart.IsVisible);
        SaveScreenshot(window, popup, "home-update-ready.png");
        await WpfTestHost.ClickAsync(restart);
        StringAssert.Contains(model.UpdateStatusText, "未能开始更新");
        Assert.IsTrue(window.IsVisible);
        keybd_event(0x1B, 0, 0, UIntPtr.Zero);
        keybd_event(0x1B, 0, 2, UIntPtr.Zero);
        await Task.Delay(100);
        Assert.IsFalse(popup.IsOpen);
        await WpfTestHost.ClickAsync(icon);
        Assert.IsTrue(popup.IsOpen);
        var outside = ((FrameworkElement)window.Content).PointToScreen(new Point(30, 100));
        Assert.IsTrue(SetCursorPos((int)outside.X, (int)outside.Y));
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(150);
        Assert.IsFalse(popup.IsOpen);
        window.Width = 920;
        window.Height = 640;
        await WpfTestHost.LayoutAsync(window);
        await WpfTestHost.ClickAsync(icon, new Point(icon.ActualWidth - 2, icon.ActualHeight - 2));
        Assert.IsTrue(popup.IsOpen);
        AssertPopupInsideWindow(window, popup);
        model.OpenDuplicateScannerCommand.Execute(null);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsFalse(icon.IsVisible);
        Assert.IsFalse(popup.IsOpen);
        model.GoHomeCommand.Execute(null);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(icon.IsVisible);
        window.Close();
    });

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    private static void AssertPopupInsideWindow(Window window, Popup popup)
    {
        var content = (FrameworkElement)window.Content;
        var child = (FrameworkElement)popup.Child;
        var offset = content.PointFromScreen(child.PointToScreen(new Point()));
        Assert.IsTrue(new Rect(content.RenderSize).Contains(new Rect(offset, child.RenderSize)));
    }

    internal static void SaveScreenshot(Window window, Popup? popup, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SIMS4_UPDATE_SCREENSHOTS");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var content = (FrameworkElement)window.Content;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(content.RenderSize));
            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            if (popup is { IsOpen: true, Child: FrameworkElement child })
            {
                var offset = content.PointFromScreen(child.PointToScreen(new Point()));
                drawing.DrawRectangle(new VisualBrush(child), null, new Rect(offset.X, offset.Y, child.ActualWidth, child.ActualHeight));
            }
        }
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) },
    };

    private static void PressEnter(Button button)
    {
        Assert.IsTrue(button.Focus(), button.Name);
        var source = PresentationSource.FromVisual(button)!;
        button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}
