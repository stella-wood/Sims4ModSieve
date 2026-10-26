using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ScopeScrollbarTests
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point point);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("conflict")]
    public Task SourcesScrollbarsKeepNativeScrollingAndSourceActions(string page) => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.Sources.Clear();
        model.ConflictSources.Clear();
        for (var index = 0; index < 40; index++)
        {
            var path = $@"C:\DemoScopes\虚构检查范围-{index:D2}";
            model.Sources.Add(new(path));
            model.ConflictSources.Add(new(path));
        }
        var sources = page == "duplicate" ? model.Sources : model.ConflictSources;
        var otherSources = page == "duplicate" ? model.ConflictSources : model.Sources;
        if (page == "duplicate") model.OpenDuplicateScannerCommand.Execute(null);
        else
        {
            model.OpenConflictCommand.Execute(null);
            model.OpenConflictSectionCommand.Execute("basic");
        }
        var window = OpenWindow(model);
        WpfTestHost.PlaceOnScreen(window);
        await WpfTestHost.LayoutAsync(window);
        FrameworkElement scope = page == "duplicate"
            ? Descendants<ScrollViewer>(window).Single(element => element.Name == "DuplicateSourcesScroll")
            : Descendants<ListBox>(window).Single(element => element.Name == "ConflictSourcesList");
        var viewer = scope as ScrollViewer ?? Descendants<ScrollViewer>(scope).First();
        Assert.IsTrue(viewer.ScrollableHeight > 0, "来源溢出应可滚动");
        Assert.IsTrue(GetCursorPos(out var previous));
        try
        {
            await WpfTestHost.MoveMouseAsync(viewer, new Point(viewer.ActualWidth / 2, viewer.ActualHeight / 2));
            await Task.Delay(100);
            Assert.IsTrue(IsInside(Mouse.DirectlyOver as DependencyObject, scope), "真实鼠标应命中来源区");
            mouse_event(0x0800, 0, 0, unchecked((uint)-360), UIntPtr.Zero);
            await Task.Delay(250);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(viewer.VerticalOffset > 0, "来源区真实滚轮应滚动");
            viewer.ScrollToTop();
            await WpfTestHost.LayoutAsync(window);
            var bar = Descendants<ScrollBar>(viewer).Single(element => element.Orientation == Orientation.Vertical && element.IsVisible);
            var track = Descendants<Track>(bar).Single();
            await ClickAsync(track.IncreaseRepeatButton!);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(viewer.VerticalOffset > 0, "来源区轨道点击应翻页");
            viewer.ScrollToTop();
            await WpfTestHost.LayoutAsync(window);
            var thumb = track.Thumb;
            await WpfTestHost.BeginDragAsync(thumb);
            await WpfTestHost.MoveMouseAsync(bar, new Point(bar.ActualWidth / 2, bar.ActualHeight * 0.7));
            await Task.Delay(100);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(100);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(viewer.VerticalOffset > 0, "来源区滑块拖动应滚动");
            viewer.ScrollToTop();
            await WpfTestHost.LayoutAsync(window);
            var first = sources[0];
            var checkBox = Descendants<CheckBox>(scope).Single(element => ReferenceEquals(element.DataContext, first));
            Assert.IsTrue(checkBox.IsEnabled);
            Assert.IsTrue(IsInside(window.InputHitTest(checkBox.TranslatePoint(new Point(checkBox.ActualWidth / 2, checkBox.ActualHeight / 2), window)) as DependencyObject, checkBox));
            await ClickAsync(checkBox);
            Assert.IsFalse(first.IsEnabled, "勾选来源应只改变当前页的选中状态");
            Assert.IsTrue(otherSources[0].IsEnabled);
            Assert.AreEqual(40, sources.Count);
            var remove = Descendants<Button>(scope).Single(element => ReferenceEquals(element.DataContext, first)
                && ReferenceEquals(element.Command, model.RemoveSourceCommand));
            Assert.IsTrue(IsInside(window.InputHitTest(remove.TranslatePoint(new Point(remove.ActualWidth / 2, remove.ActualHeight / 2), window)) as DependencyObject, remove));
            await ClickAsync(remove);
            Assert.IsFalse(sources.Contains(first), "移除按钮应移除当前来源");
            Assert.AreEqual(39, sources.Count);
            Assert.AreEqual(40, otherSources.Count, "另一检测页的来源应保留");
            foreach (var state in new[] { "normal", "maximized" })
            {
                if (state == "maximized") window.WindowState = WindowState.Maximized;
                await WpfTestHost.LayoutAsync(window);
                viewer.ScrollToTop();
                await WpfTestHost.LayoutAsync(window);
                await WpfTestHost.MoveMouseAsync(window, new Point(window.ActualWidth * 0.75, window.ActualHeight / 2));
                await Task.Delay(100);
                Save(window, $"{page}-scope-scrollbar-{state}.png");
            }
        }
        finally { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); SetCursorPos(previous.X, previous.Y); }
    });

    private static bool IsInside(DependencyObject? current, DependencyObject ancestor)
    {
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }
    private static Task ClickAsync(FrameworkElement element) => WpfTestHost.ClickAsync(element);
    private static void Save(Window window, string filename)
    {
        var output = Environment.GetEnvironmentVariable("SIMS4_DETECTION_REVIEW_DIR");
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
