using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Desktop.Services;
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
    [DataRow("new", true)]
    [DataRow("latest", false)]
    [DataRow("older", false)]
    [DataRow("timeout", false)]
    [DataRow("offline", false)]
    [DataRow("403", false)]
    [DataRow("429", false)]
    [DataRow("404", false)]
    [DataRow("json", false)]
    [DataRow("missing", false)]
    [DataRow("type", false)]
    [DataRow("version", false)]
    [DataRow("url", false)]
    [DataRow("http", false)]
    [DataRow("other-repo", false)]
    public Task RequestControlsIconState(string scenario, bool visible) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model();
        using var handler = new Handler((request, _) =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("https://api.github.com/repos/stella-wood/Sims4ModSieve/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.IsTrue(request.Headers.UserAgent.ToString().StartsWith("Sims4ModSieve/", StringComparison.Ordinal));
            if (scenario == "timeout") throw new TaskCanceledException();
            if (scenario == "offline") throw new HttpRequestException();
            if (int.TryParse(scenario, out var status)) return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
            var tag = scenario switch { "latest" => model.ProductVersion, "older" => "0.0.1", "version" => "garbage", _ => "v99.0.0+build" };
            var url = scenario switch
            {
                "url" => "https://example.com/releases/tag/v99.0.0",
                "http" => Url.Replace("https:", "http:", StringComparison.Ordinal),
                "other-repo" => "https://github.com/other/Sims4ModSieve/releases/tag/v99.0.0", _ => Url,
            };
            var json = scenario switch { "json" => "{", "missing" => "{}", "type" => "{\"tag_name\":123}", _ => $$"""{"tag_name":"{{tag}}","html_url":"{{url}}"}""" };
            return Task.FromResult(Response(json));
        });
        using var client = new HttpClient(handler);
        Assert.IsFalse(model.HasAvailableUpdate);
        await model.CheckForUpdatesAsync(new UpdateCheckService(client));
        Assert.AreEqual(visible, model.HasAvailableUpdate);
        await model.CheckForUpdatesAsync(new UpdateCheckService(client));
        Assert.AreEqual(1, handler.Calls);
        Assert.IsFalse(File.Exists(Path.Combine(files.Root, "Sims4ModSieve", "application-error.log")));
    });

    [TestMethod]
    public async Task RequestTimesOutWithoutCallerCancellation()
    {
        using var handler = new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Response("{}");
        });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var result = await new UpdateCheckService(client).CheckAsync("0.1.0").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.IsNull(result);
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
            return Response("{}");
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
    public Task HomeIconPopupAndDownloadWork() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        string? opened = null;
        var model = files.Model(support: new SettingsSupportService(files.Root, files.Folder("Documents"), openTarget: url => opened = url));
        using var handler = new Handler((_, _) => Task.FromResult(Response($$"""{"tag_name":"v99.0.0","html_url":"{{Url}}"}""")));
        using var client = new HttpClient(handler);
        var window = new MainWindow(model);
        window.Show();
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
        Assert.AreEqual(Url, opened);
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

    private static void SaveScreenshot(Window window, Popup popup, string name)
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
            if (popup.IsOpen && popup.Child is FrameworkElement child)
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

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

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
