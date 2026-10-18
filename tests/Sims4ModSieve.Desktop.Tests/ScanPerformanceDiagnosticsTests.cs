using System.Buffers.Binary;
using System.IO.Compression;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Services;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;
using static Sims4ModSieve.Desktop.Tests.SettingsViewModelTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ScanPerformanceDiagnosticsTests
{
    [TestMethod]
    public Task EmptyTimingPanelTracksFirstRecordAndReturnToEmpty() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        string? copied = null;
        var support = new SettingsSupportService(files.Root, copyText: text => copied = text);
        var model = files.Model(support: support);
        var window = new MainWindow(model);
        window.Show();
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("diagnostics");
        await model.RefreshDiagnosticsAsync();
        var page = Descendants<SettingsPage>(window).Single();
        var summary = (TextBox)page.FindName("ScanTimingSummary");
        var button = (Button)page.FindName("CopyScanTimingButton");
        async Task CheckEmptyAsync()
        {
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(model.IsScanTimingEmpty);
            Assert.AreEqual("尚无扫描计时", summary.Text);
            Assert.IsTrue(summary.IsVisible && button.IsVisible && button.IsEnabled);
            foreach (var x in new[] { 0.05, 0.95 })
            foreach (var y in new[] { 0.05, 0.95 })
            {
                var hit = page.InputHitTest(button.TranslatePoint(new Point(button.ActualWidth * x, button.ActualHeight * y), page)) as DependencyObject;
                while (hit is not null && !ReferenceEquals(hit, button)) hit = VisualTreeHelper.GetParent(hit);
                Assert.AreSame(button, hit);
            }
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Assert.AreEqual("尚无扫描计时", copied);
        }
        await CheckEmptyAsync();
        await support.WriteScanTimingAsync("[2026-10-18T16:13:57+08:00] 重复检测 · 完成\n总耗时：1000.000 ms");
        await model.RefreshDiagnosticsAsync();
        await WpfTestHost.LayoutAsync(window);
        Assert.IsFalse(model.IsScanTimingEmpty);
        StringAssert.Contains(summary.Text, "类型：重复检测");
        await support.WriteScanTimingAsync(string.Empty);
        await model.RefreshDiagnosticsAsync();
        await CheckEmptyAsync();
        window.Close();
    });

    [TestMethod]
    public Task BothScanTimingsReachTheLogAndVisibleDiagnosticsCopyButton() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        WritePackage(Path.Combine(mods, "a.package"));
        File.Copy(Path.Combine(mods, "a.package"), Path.Combine(mods, "b.package"));
        foreach (var name in new[] { "a.ts4script", "b.ts4script" })
        {
            using var zip = ZipFile.Open(Path.Combine(mods, name), ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry("library/example.py").Open());
            writer.Write(name == "a.ts4script" ? "value = 1\n" : "value = 2\n");
        }
        files.Store.Save(new(mods, []));
        string? copied = null;
        var support = new SettingsSupportService(files.Root, copyText: text => copied = text);
        var model = files.Model(support: support);
        var window = new MainWindow(model);
        window.Show();
        model.OpenDuplicateScannerCommand.Execute(null);
        await model.StartScanCommand.ExecuteAsync();
        StringAssert.Contains(model.LatestScanTiming, "重复检测 · 完成");
        StringAssert.Contains(model.LatestScanTiming, "绑定、首屏布局和绘制提交");
        Assert.AreEqual(1, model.DuplicateGroupCount);

        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        await model.StartConflictScanCommand.ExecuteAsync();
        Assert.AreEqual("complete", model.ConflictRunState);
        StringAssert.Contains(model.LatestScanTiming, "冲突检测 · 完成");
        StringAssert.Contains(model.LatestScanTiming, "索引资源条目=2；候选比较条目=2");
        StringAssert.Contains(model.LatestScanTiming, "解压合计：尝试=2；成功=2");
        StringAssert.Contains(model.LatestScanTiming, "并发上限=");
        Assert.IsTrue(model.ConflictRows.Count > 0);
        var log = await File.ReadAllTextAsync(Path.Combine(support.LogDirectory, SettingsSupportService.RuntimeLogName));
        Assert.AreEqual(1, log.Split("] 重复检测 · 完成").Length - 1);
        Assert.AreEqual(1, log.Split("] 冲突检测 · 完成").Length - 1);
        Assert.AreEqual(model.LatestScanTiming, await new SettingsSupportService(files.Root).ReadLatestScanTimingAsync());

        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("diagnostics");
        await model.RefreshDiagnosticsAsync();
        await WpfTestHost.LayoutAsync(window);
        var page = Descendants<SettingsPage>(window).Single();
        var summary = (TextBox)page.FindName("ScanTimingSummary");
        Assert.IsTrue(summary.IsVisible);
        Assert.AreEqual(model.ScanTimingText, summary.Text);
        var button = (Button)page.FindName("CopyScanTimingButton");
        button.BringIntoView();
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(button.IsVisible && button.IsEnabled);
        var frame = (Border)page.FindName("ScanTimingFrame");
        var position = button.TranslatePoint(new Point(), frame);
        var scrollbar = Descendants<ScrollBar>(summary).Single(bar => bar.IsVisible);
        ScrollBar.PageDownCommand.Execute(null, scrollbar);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsGreaterThan(0, summary.VerticalOffset);
        summary.ScrollToEnd();
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(position, button.TranslatePoint(new Point(), frame), "Copy button moved with the text.");
        foreach (var x in new[] { 0.05, 0.5, 0.95 })
        foreach (var y in new[] { 0.05, 0.5, 0.95 })
        {
            var hit = page.InputHitTest(button.TranslatePoint(new Point(button.ActualWidth * x, button.ActualHeight * y), page)) as DependencyObject;
            while (hit is not null && !ReferenceEquals(hit, button)) hit = VisualTreeHelper.GetParent(hit);
            Assert.AreSame(button, hit);
        }
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Assert.AreEqual(model.ScanTimingText, copied);
        Assert.IsTrue(model.HasNoDiagnosticErrors);
        StringAssert.Contains(model.DiagnosticsFeedback, "扫描计时已复制");
        if (Environment.GetEnvironmentVariable("SIMS4_TIMING_REVIEW_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            if (Environment.GetEnvironmentVariable("SIMS4_TIMING_REVIEW_SOURCE") is { Length: > 0 } source)
            {
                await support.WriteScanTimingAsync(await File.ReadAllTextAsync(source));
                await model.RefreshDiagnosticsAsync();
            }
            foreach (var (width, height) in new[] { (1180, 780), (920, 640) })
            {
                window.Width = width;
                window.Height = height;
                ((ScrollViewer)page.FindName("SettingsContentScroll")).ScrollToTop();
                summary.ScrollToHome();
                await WpfTestHost.LayoutAsync(window);
                if (width == 1180 && summary.Text.IndexOf("结果项：25,115", StringComparison.Ordinal) is >= 0 and var resultIndex)
                {
                    var resultBounds = summary.GetRectFromCharacterIndex(resultIndex);
                    Assert.IsFalse(resultBounds.IsEmpty);
                    Assert.IsTrue(resultBounds.Bottom <= summary.ActualHeight - summary.Padding.Bottom,
                        "The third stage result item is outside the initial viewport.");
                }
                var content = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(directory, $"scan-timing-diagnostics-{width}.png"));
                encoder.Save(stream);
            }
            File.WriteAllText(Path.Combine(directory, "scan-timing-example.log"), log);
        }
        window.Close();
    });

    [TestMethod]
    public async Task TimingTextPreservesExecutionOrderCountsAndCopiedContent()
    {
        using var files = new Scenario();
        const string raw = """
            [2026-10-18T16:13:57.1644972+08:00] 冲突检测 · 完成
            总耗时：18806.485 ms
            机器：逻辑 CPU 32；Windows；X64
            扫描范围：文件=1398；总字节=10453223384；索引资源条目=181256；候选比较条目=76225；未进入候选比较=105031
            Package · 排序和读取索引：661.728 ms（3.52%）；并发上限=4；文件=1381；输入字节=10448459100；资源/模块条目=181256；解压尝试=0
            资源内容 · 复核索引、读取、解压和哈希：14827.782 ms（78.84%）；并发上限=2；文件=682；输入字节=5349661792；资源/模块条目=76225；读取条目=76225；解压尝试=65123；解压成功=65118；未进入解压=11102；未完成条目=5；处理字节=10391928497
            """;
        string? copied = null;
        var support = new SettingsSupportService(files.Root, copyText: value => copied = value);
        await support.WriteScanTimingAsync(raw);
        var model = files.Model(support: support);
        await model.RefreshDiagnosticsAsync();
        var text = model.ScanTimingText;
        StringAssert.Contains(text, "类型：冲突检测    状态：完成");
        StringAssert.Contains(text, "总耗时：18.806 s（18,806.485 ms）");
        StringAssert.Contains(text, "文件：1,398 个 / 9.74 GiB");
        StringAssert.Contains(text, "主要耗时：资源内容 · 复核索引、读取、解压和哈希，14.828 s，占 78.84%");
        StringAssert.Contains(text, "并发上限：4");
        StringAssert.Contains(text, "01. Package · 排序和读取索引");
        StringAssert.Contains(text, "条目 181,256 条" + Environment.NewLine + "02. 资源内容");
        StringAssert.Contains(text, "解压尝试 65,123 条；解压成功 65,118 条");
        StringAssert.Contains(text, "未进入解压 11,102 条；未完成比较 5 条");
        StringAssert.Contains(text, "10,391,928,497 bytes");
        StringAssert.Contains(text, "资源内容候选：76,225 条；未进入候选比较：105,031 条");
        StringAssert.Contains(text, "机器：逻辑 CPU 32；Windows；X64");
        await model.CopyScanTimingCommand.ExecuteAsync();
        Assert.AreEqual(text, copied);
        Assert.AreEqual(raw, model.LatestScanTiming);
        Assert.AreEqual(raw, await new SettingsSupportService(files.Root).ReadLatestScanTimingAsync());
    }

    [TestMethod]
    [DataRow("尚无扫描计时")]
    [DataRow("总耗时：not-a-number ms\n资源内容：invalid ms（invalid%）；文件=bad")]
    public async Task UnrecognizedTimingTextKeepsRawCopyAvailable(string raw)
    {
        using var files = new Scenario();
        string? copied = null;
        var support = new SettingsSupportService(files.Root, copyText: value => copied = value);
        await support.WriteScanTimingAsync(raw);
        var model = files.Model(support: support);
        await model.RefreshDiagnosticsAsync();
        Assert.AreEqual(raw, model.ScanTimingText);
        await model.CopyScanTimingCommand.ExecuteAsync();
        Assert.AreEqual(raw, copied);
    }

    [TestMethod]
    public async Task TimingLogFailuresKeepLatestSummaryAvailableAndErrorsSeparate()
    {
        using var files = new Scenario();
        var support = new SettingsSupportService(files.Root);
        Assert.AreEqual("尚无扫描计时", await support.ReadLatestScanTimingAsync());
        await support.WriteScanTimingAsync("first\n");
        using (File.Open(Path.Combine(support.LogDirectory, SettingsSupportService.RuntimeLogName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var latest = await support.WriteScanTimingAsync("second\n");
            StringAssert.Contains(latest, "计时日志写入失败");
            Assert.AreEqual(latest, await support.ReadLatestScanTimingAsync());
        }
        var errors = await support.ReadDiagnosticsAsync();
        Assert.IsTrue(errors.All(log => !log.HasContent));
        await support.WriteScanTimingAsync("third\n");
        Assert.AreEqual("third\n", await new SettingsSupportService(files.Root).ReadLatestScanTimingAsync());
    }

    [TestMethod]
    public async Task LatestTimingReadIsBoundedAndRedactsUserPaths()
    {
        using var files = new Scenario();
        var directory = files.Folder("Sims4ModSieve");
        File.WriteAllText(Path.Combine(directory, SettingsSupportService.LatestScanTimingName),
            @"C:\Users\TimingSecret\test" + new string('x', SettingsSupportService.MaximumLogCharacters));
        var summary = await new SettingsSupportService(files.Root).ReadLatestScanTimingAsync();
        Assert.IsFalse(summary.Contains("TimingSecret", StringComparison.Ordinal));
        StringAssert.Contains(summary, "仅显示／复制前 65536");
    }

    [TestMethod]
    [DataRow(true, "已取消")]
    [DataRow(false, "失败")]
    public async Task UnfinishedScanWritesItsOutcomeWithoutChangingScanState(bool cancelled, string expected)
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        files.Store.Save(new(mods, []));
        var model = files.Model(run: new UnfinishedRun(cancelled));
        await model.StartScanCommand.ExecuteAsync();
        StringAssert.Contains(model.LatestScanTiming, "重复检测 · " + expected);
        Assert.IsFalse(model.IsBusy);
        Assert.AreEqual(0, model.DuplicateGroupCount);
    }

    private sealed class UnfinishedRun(bool cancelled) : IDuplicateRunService
    {
        public Task<DuplicateReport> RunAsync(DuplicateRunInput input, IProgress<DuplicateScanProgress>? progress = null,
            CancellationToken cancellationToken = default) => cancelled
                ? throw new OperationCanceledException()
                : throw new IOException("temporary test failure");
    }

    private static void WritePackage(string path)
    {
        const int index = 100;
        var bytes = new byte[index + 36];
        "DBPF"u8.CopyTo(bytes);
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        Put(4, 2); Put(8, 1); Put(0x24, 1); Put(0x2c, 36); Put(0x3c, 3);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x40), index);
        "same"u8.CopyTo(bytes.AsSpan(96));
        Put(index + 4, 0x0904DF10); Put(index + 16, 1); Put(index + 20, 96);
        Put(index + 24, 0x80000004); Put(index + 28, 4); Put(index + 32, 0x00010000);
        File.WriteAllBytes(path, bytes);
    }
}
