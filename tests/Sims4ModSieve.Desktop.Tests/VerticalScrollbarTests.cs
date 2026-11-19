using System.Runtime.InteropServices;
using System.Windows;
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
public sealed class VerticalScrollbarTests
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point point);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [TestMethod]
    public Task NativeInputWindowFitsWorkAreaAndKeepsEdgeControlClickable() => WpfTestHost.RunAsync(async () =>
    {
        var workArea = SystemParameters.WorkArea;
        var smallArea = new Rect(workArea.Left + workArea.Width / 4, workArea.Top + workArea.Height / 4,
            workArea.Width / 2, workArea.Height / 2);
        var clicked = false;
        var button = new Button { Content = "边缘按钮", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        button.Click += (_, _) => clicked = true;
        var window = new Window { MinWidth = workArea.Width, MinHeight = workArea.Height, Content = button, ShowInTaskbar = false };
        Assert.IsTrue(GetCursorPos(out var previous));
        try
        {
            window.Show();
            WpfTestHost.PlaceOnScreen(window, workArea: smallArea);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(smallArea.Contains(new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)),
                "原最小尺寸超过工作区时，测试窗口仍应完整处于工作区内。");
            await WpfTestHost.ClickAsync(button);
            Assert.IsTrue(clicked, "适配后窗口右下角控件应可被真实鼠标点击。");
        }
        finally { SetCursorPos(previous.X, previous.Y); }
    });

    [TestMethod]
    [DataRow("home")]
    [DataRow("overview")]
    public Task OuterPageScrollbarsSupportNativeInputAndIdleVisibility(string page) => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        if (page == "overview") model.OpenConflictCommand.Execute(null);
        var window = OpenWindow(model);
        Place(window);
        await WpfTestHost.LayoutAsync(window);
        var viewer = Viewer(window, page);
        var bar = Bar(viewer);
        Assert.IsTrue(viewer.ScrollableHeight > 0, "用短窗口构造页面溢出。");
        Assert.IsTrue(bar.IsVisible, "隐藏滑块时仍保留可命中的滚动区域。");
        Assert.IsTrue(GetCursorPos(out var previous));
        try
        {
            await WpfTestHost.MoveMouseAsync(viewer, new Point(viewer.ActualWidth / 2, viewer.ActualHeight / 2));
            await Task.Delay(100);
            Assert.IsTrue(IsInside(Mouse.DirectlyOver as DependencyObject, viewer),
                $"真实鼠标应命中页面滚动区域，实际命中 {Mouse.DirectlyOver}。");
            await HiddenAsync(bar);
            Save(window, page + "-outer-resting.png");
            mouse_event(0x0800, 0, 0, unchecked((uint)-120), UIntPtr.Zero);
            await Task.Delay(150);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(viewer.VerticalOffset > 0, "页面应响应真实滚轮。");
            Assert.IsTrue(bar.Opacity > 0, "页面滚动时应显示滑块。");
            Save(window, page + "-outer-scrolling.png");
            viewer.ScrollToTop();
            await WpfTestHost.LayoutAsync(window);
            await HiddenAsync(bar);

            var track = Descendants<Track>(bar).Single();
            var thumb = track.Thumb;
            await WpfTestHost.MoveMouseAsync(thumb, new Point(thumb.ActualWidth * 0.1, thumb.ActualHeight / 2));
            await Task.Delay(100);
            Assert.IsTrue(IsInside(Mouse.DirectlyOver as DependencyObject, thumb), "滑块透明边缘也应命中。");
            Assert.IsTrue(bar.Opacity > 0, "鼠标进入隐藏滑块热区时应显示。");
            Save(window, page + "-outer-hover.png");
            await WpfTestHost.BeginDragAsync(thumb, new Point(thumb.ActualWidth * 0.1, thumb.ActualHeight / 2));
            await WpfTestHost.MoveMouseAsync(bar, new Point(bar.ActualWidth * 0.1, bar.ActualHeight * 0.7));
            await Task.Delay(1300);
            Assert.IsTrue(thumb.IsDragging);
            Assert.IsTrue(bar.Opacity > 0, "拖动期间经过闲置延迟也应保持显示。");
            Assert.IsTrue(viewer.VerticalOffset > 0, "从透明边缘开始拖动应滚动页面。");
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            await WpfTestHost.MoveMouseAsync(viewer, new Point(viewer.ActualWidth / 2, viewer.ActualHeight / 2));
            await HiddenAsync(bar);

            viewer.ScrollToTop();
            await WpfTestHost.LayoutAsync(window);
            await ClickAsync(track.IncreaseRepeatButton!);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(viewer.VerticalOffset > 0, "透明轨道应支持翻页。");
            window.Close();

            window = OpenWindow(model);
            Place(window);
            await WpfTestHost.LayoutAsync(window);
            viewer = Viewer(window, page);
            bar = Bar(viewer);
            await WpfTestHost.MoveMouseAsync(viewer, new Point(viewer.ActualWidth / 2, viewer.ActualHeight / 2));
            await HiddenAsync(bar);
            viewer.ScrollToEnd();
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(bar.Opacity > 0, "卸载重建后应重新响应当前页面滚动。");
            Save(window, page + "-outer-reopened.png");
        }
        finally
        {
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos(previous.X, previous.Y);
        }
    });

    [TestMethod]
    public Task SettingsInnerScrollbarStaysVisibleAndScrollsWithoutWakingOuter() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var support = new SettingsSupportService(files.Root);
        Directory.CreateDirectory(support.LogDirectory);
        File.WriteAllText(Path.Combine(support.LogDirectory, "application-error.log"),
            string.Join("\n", Enumerable.Range(1, 240).Select(index => $"滚动条验证日志第 {index} 行")));
        var model = files.Model(support: support);
        var window = OpenWindow(model);
        WpfTestHost.PlaceOnScreen(window, height: 640);
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("diagnostics");
        await model.RefreshDiagnosticsAsync();
        await WpfTestHost.LayoutAsync(window);
        var page = Descendants<SettingsPage>(window).Single();
        var outer = (ScrollViewer)page.FindName("SettingsContentScroll");
        var summary = (TextBox)page.FindName("DiagnosticLogSummary");
        summary.BringIntoView();
        await WpfTestHost.LayoutAsync(window);
        var outerBar = Bar(outer);
        var inner = Descendants<ScrollViewer>(summary).First();
        var innerBar = Bar(inner);
        Assert.IsTrue(inner.ScrollableHeight > 0);
        Assert.IsTrue(GetCursorPos(out var previous));
        try
        {
            await WpfTestHost.MoveMouseAsync(summary, new Point(summary.ActualWidth / 2, summary.ActualHeight / 2));
            await HiddenAsync(outerBar);
            Assert.IsTrue(innerBar.Opacity > 0, "文本框滚动条不应继承页面外层自动隐藏。");
            Save(window, "settings-inner-resting.png");
            var outerOffset = outer.VerticalOffset;
            var track = Descendants<Track>(innerBar).Single();
            var thumb = track.Thumb;
            await WpfTestHost.MoveMouseAsync(thumb, new Point(thumb.ActualWidth * 0.1, thumb.ActualHeight / 2));
            await Task.Delay(100);
            Assert.IsTrue(IsInside(Mouse.DirectlyOver as DependencyObject, thumb));
            await WpfTestHost.BeginDragAsync(thumb, new Point(thumb.ActualWidth * 0.1, thumb.ActualHeight / 2));
            await WpfTestHost.MoveMouseAsync(innerBar, new Point(innerBar.ActualWidth * 0.1, innerBar.ActualHeight * 0.7));
            await Task.Delay(100);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(summary.VerticalOffset > 0, "内层滑块透明边缘应支持拖动。");
            Assert.AreEqual(outerOffset, outer.VerticalOffset);
            Assert.IsTrue(outerBar.Opacity <= 0, "文本框内滚动不应点亮外层滑块。");
            Save(window, "settings-inner-drag.png");
            inner.ScrollToTop();
            await WpfTestHost.LayoutAsync(window);
            await ClickAsync(track.IncreaseRepeatButton!);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(summary.VerticalOffset > 0, "内层透明轨道应支持翻页。");
            Assert.AreEqual(outerOffset, outer.VerticalOffset);
            Assert.IsTrue(outerBar.Opacity <= 0);
            await WpfTestHost.MoveMouseAsync(summary, new Point(summary.ActualWidth / 2, summary.ActualHeight / 2));
            await Task.Delay(1300);
            Assert.IsTrue(innerBar.Opacity > 0, "内层在闲置后仍应可见。");
            Save(window, "settings-inner-after-idle.png");
        }
        finally
        {
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos(previous.X, previous.Y);
        }
    });

    private static ScrollViewer Viewer(Window window, string page) => Descendants<ScrollViewer>(window)
        .Single(viewer => viewer.Name == (page == "home" ? "HomeContentScroll" : "ConflictOverviewViewport"));

    private static ScrollBar Bar(ScrollViewer viewer) => (ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);

    private static void Place(Window window)
    {
        WpfTestHost.PlaceOnScreen(window, height: 480);
    }

    private static async Task HiddenAsync(ScrollBar bar)
    {
        for (var attempt = 0; attempt < 150 && bar.Opacity > 0; attempt++) await Task.Delay(20);
        Assert.IsTrue(bar.Opacity <= 0, "停止操作后外层滑块应淡出。");
    }

    private static Task ClickAsync(FrameworkElement element) => WpfTestHost.ClickAsync(element,
        new Point(element.ActualWidth * 0.1, element.ActualHeight / 2));

    private static bool IsInside(DependencyObject? current, DependencyObject ancestor)
    {
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static void Save(Window window, string filename)
    {
        var output = Environment.GetEnvironmentVariable("SIMS4_SCROLLBAR_REVIEW_DIR");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, filename));
        encoder.Save(stream);
    }
}
