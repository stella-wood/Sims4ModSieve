using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class DetectionLayoutTests
{
    [TestMethod]
    [DataRow("complete")]
    [DataRow("cancel")]
    [DataRow("failure")]
    public Task DuplicateResultPlaceholderTracksScanLifecycleAndSourceReset(string outcome) => WpfTestHost.RunAsync(async () =>
    {
        var service = new ControlledRunService();
        var model = new MainWindowViewModel(service, new NoFolderPicker(), new TemporarySettings());
        model.OpenDuplicateScannerCommand.Execute(null);
        var window = OpenWindow(model);
        await WpfTestHost.LayoutAsync(window);
        var placeholder = Descendants<TextBlock>(window).Single(text => text.Name == "DuplicatePlaceholderText");
        Assert.IsTrue(placeholder.IsVisible);
        Assert.AreEqual("尚未扫描", placeholder.Text);
        var scan = model.StartScanCommand.ExecuteAsync();
        Assert.IsTrue(model.IsBusy);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual("正在准备扫描…", placeholder.Text);
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Progress!.Report(new(DuplicateScanPhase.Discovering, "正在查找文件…", DiscoveredFileCount: 7));
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual("正在查找文件…", placeholder.Text);
        service.Progress.Report(new(DuplicateScanPhase.Hashing, "正在核对文件内容（3/7）", 3, 7, 7));
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual("正在核对文件内容（3/7）…", placeholder.Text);
        Assert.IsTrue(placeholder.IsVisible);
        Save(window, $"duplicate-scanning-{outcome}.png");
        if (outcome == "complete")
        {
            window.WindowState = WindowState.Maximized;
            await WpfTestHost.LayoutAsync(window);
            Save(window, "duplicate-scanning-maximized.png");
            window.WindowState = WindowState.Normal;
        }
        if (outcome == "cancel") model.CancelScanCommand.Execute(null);
        else if (outcome == "failure") service.Completion.SetException(new IOException("测试读取失败"));
        else service.Completion.SetResult(new(DateTime.UnixEpoch, DateTime.UnixEpoch, [], 7, 7, [], []));
        await scan.WaitAsync(TimeSpan.FromSeconds(5));
        await WpfTestHost.LayoutAsync(window);
        var expected = outcome switch
        {
            "complete" => "扫描完成，没有发现重复文件",
            "cancel" => "扫描已取消",
            _ => "扫描没有完成",
        };
        Assert.AreEqual(expected, placeholder.Text);
        Assert.IsFalse(model.IsBusy);
        Assert.IsTrue(placeholder.IsVisible);
        Save(window, $"duplicate-scan-{outcome}.png");
        model.Sources.Clear();
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual("尚未扫描", placeholder.Text);
        Assert.IsTrue(placeholder.IsVisible);
    });

    [TestMethod]
    public Task DetectionPagesRemainUsableAcrossNormalNarrowAndMaximizedWindows() => WpfTestHost.RunAsync(async () =>
    {
        var duplicateSession = new DuplicateResultSession();
        duplicateSession.Replace(duplicateSession.PrepareGroups(ConflictV2SessionTests.Duplicates(
            @"C:\DemoMods\MouthScaleSlider_Fixed.package", @"D:\DemoBackup\MouthScaleSlider_Fixed.package").Groups));
        var model = new MainWindowViewModel(new DuplicateRunService(DuplicateScanner.CreateDefault()),
            new NoFolderPicker(), new TemporarySettings(), resultSession: duplicateSession);
        var window = OpenWindow(model);
        window.Topmost = true;
        foreach (var state in new[] { "normal", "narrow", "short", "maximized", "restored" })
        {
            window.WindowState = WindowState.Normal;
            window.MinHeight = state == "short" ? 1 : 640;
            window.Width = state is "narrow" or "short" ? 920 : 1180;
            window.Height = state == "short" ? 480 : state == "narrow" ? 640 : 780;
            window.Left = 50;
            window.Top = 40;
            if (state == "maximized") window.WindowState = WindowState.Maximized;
            window.Activate();
            model.GoHomeCommand.Execute(null);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(model.IsHomeVisible);
            Save(window, $"home-{state}.png");
            model.OpenConflictCommand.Execute(null);
            model.ConflictBackCommand.Execute(null);
            await WpfTestHost.LayoutAsync(window);
            foreach (var title in new[] { "基础排查", "报错日志分析", "健康性检查", "二分排查" })
                Assert.IsTrue(Descendants<TextBlock>(window).Any(text => text.IsVisible && text.Text == title), title);
            var overview = Descendants<ScrollViewer>(window).Single(viewer => viewer.Name == "ConflictOverviewViewport");
            overview.ScrollToTop();
            await WpfTestHost.LayoutAsync(window);
            Save(window, $"conflict-overview-{state}.png");
            overview.ScrollToBottom();
            await WpfTestHost.LayoutAsync(window);
            var overviewTitle = Descendants<TextBlock>(window).Single(text => text.Name == "ConflictOverviewTitle");
            Assert.IsTrue(IsInside(window.InputHitTest(overviewTitle.TranslatePoint(new Point(1, overviewTitle.ActualHeight / 2), window)) as DependencyObject, overviewTitle),
                "滚动入口卡片后总览标题仍应可见且未被内容遮挡");
            Save(window, $"conflict-overview-scrolled-{state}.png");
            overview.ScrollToTop();
            await WpfTestHost.LayoutAsync(window);
            var entry = Descendants<Button>(window).Single(button => button.IsVisible
                && ReferenceEquals(button.Command, model.OpenConflictSectionCommand) && Equals(button.CommandParameter, "basic"));
            entry.BringIntoView();
            await WpfTestHost.LayoutAsync(window);
            foreach (var fraction in new[] { 0.1, 0.5, 0.9 })
                Assert.IsTrue(IsInside(window.InputHitTest(entry.TranslatePoint(new Point(entry.ActualWidth * fraction, entry.ActualHeight * fraction), window)) as DependencyObject, entry));
            ((IInvokeProvider)new ButtonAutomationPeer(entry).GetPattern(PatternInterface.Invoke)!).Invoke();
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(model.IsConflictBasic);
            Assert.IsTrue(Descendants<Button>(window).Any(button => button.IsVisible && button.IsEnabled && ReferenceEquals(button.Command, model.StartConflictScanCommand)));
            Save(window, $"conflict-range-{state}.png");
            model.OpenDuplicateScannerCommand.Execute(null);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(model.IsScannerVisible);
            var duplicateList = Descendants<ListBox>(window).Single(list => list.Name == "DuplicateGroupsList");
            Assert.IsTrue(duplicateList.IsVisible);
            var toggle = Descendants<System.Windows.Controls.Primitives.ToggleButton>(duplicateList).First(button => button.Name == "DuplicateHeaderToggle");
            var expanded = duplicateSession.Groups.Single().IsExpanded;
            toggle.IsChecked = !expanded;
            Assert.AreEqual(!expanded, duplicateSession.Groups.Single().IsExpanded);
            toggle.IsChecked = expanded;
            Save(window, $"duplicate-range-{state}.png");
            model.OpenConflictCommand.Execute(null);
            await SetSessionAsync(model, ConflictTechnicalDetailsTests.TechnicalSession(6, 3), "all");
            var list = Descendants<ListBox>(window).Single(element => element.Name == "ConflictRowsList");
            list.ScrollIntoView(model.ConflictRows.Last(row => row.TechnicalEntry is not null));
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(Descendants<TextBlock>(list).Any(text => text.IsVisible && text.Name == "Version" && !string.IsNullOrEmpty(text.Text)));
            Save(window, $"technical-versions-{state}.png");
            model.ConflictBackCommand.Execute(null);
        }
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

    private sealed class ControlledRunService : IDuplicateRunService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DuplicateReport> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<DuplicateScanProgress>? Progress { get; private set; }
        public Task<DuplicateReport> RunAsync(DuplicateRunInput input, IProgress<DuplicateScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Progress = progress;
            Started.SetResult();
            return Completion.Task.WaitAsync(cancellationToken);
        }
    }
    private sealed class NoFolderPicker : IFolderPickerService
    {
        public string? PickFolder(string title, string? initialDirectory = null) => null;
    }
    private sealed class TemporarySettings : IDuplicateSettingsStore
    {
        public DuplicateSettings? Load() => new(Path.GetTempPath(), []);
        public void Save(DuplicateSettings settings) { }
    }
}
