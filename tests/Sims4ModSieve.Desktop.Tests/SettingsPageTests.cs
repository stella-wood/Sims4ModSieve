using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Desktop.Services;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;
using static Sims4ModSieve.Desktop.Tests.SettingsViewModelTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class SettingsPageTests
{
    [TestMethod]
    public Task DiagnosticHeaderActionsOpenLogDirectoryAndRefreshBothPanels() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var targets = new List<string>();
        var support = new SettingsSupportService(files.Root, openTarget: targets.Add);
        var logs = files.Folder("Sims4ModSieve");
        var model = files.Model(support: support);
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        window.Show();
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("diagnostics");
        await model.RefreshDiagnosticsAsync();
        var page = Descendants<SettingsPage>(window).Single();
        var timing = (TextBox)page.FindName("ScanTimingSummary");
        var errors = (TextBox)page.FindName("DiagnosticLogSummary");
        async Task InvokeAsync(string name)
        {
            var button = (Button)page.FindName(name);
            button.BringIntoView();
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(button.IsVisible && button.IsEnabled);
            foreach (var x in new[] { 0.05, 0.5, 0.95 })
            foreach (var y in new[] { 0.05, 0.5, 0.95 })
            {
                var hit = page.InputHitTest(button.TranslatePoint(new Point(button.ActualWidth * x, button.ActualHeight * y), page)) as DependencyObject;
                while (hit is not null && !ReferenceEquals(hit, button)) hit = VisualTreeHelper.GetParent(hit);
                Assert.AreSame(button, hit);
            }
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        await InvokeAsync("OpenScanTimingFolderButton");
        await InvokeAsync("OpenDiagnosticLogFolderButton");
        CollectionAssert.AreEqual(new[] { logs, logs }, targets);
        foreach (var name in new[] { "RefreshScanTimingButton", "RefreshDiagnosticLogsButton" })
        {
            await support.WriteScanTimingAsync(name);
            File.WriteAllText(Path.Combine(logs, "application-error.log"), "测试错误：" + name);
            await InvokeAsync(name);
            for (var attempt = 0; attempt < 100 && (timing.Text != name || !errors.Text.Contains(name, StringComparison.Ordinal)); attempt++)
                await Task.Delay(10);
            Assert.AreEqual(name, timing.Text);
            StringAssert.Contains(errors.Text, "测试错误：" + name);
        }
        Assert.IsFalse(Descendants<Button>(page).Any(button => Equals(button.Content, "打开日志文件夹")));
        window.Close();
    });

    [TestMethod]
    public Task ErrorLogFrameRefreshesScrollsAndCopiesRedactedDiagnostics() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        string? copied = null;
        var support = new SettingsSupportService(files.Root, copyText: value => copied = value);
        var model = files.Model(support: support);
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        window.Show();
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("diagnostics");
        await model.RefreshDiagnosticsAsync();
        var page = Descendants<SettingsPage>(window).Single();
        var summary = (TextBox)page.FindName("DiagnosticLogSummary");
        var frame = (Border)page.FindName("DiagnosticLogFrame");
        var button = (Button)page.FindName("CopyDiagnosticsButton");
        async Task CopyFromIconAsync()
        {
            button.BringIntoView();
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(button.IsVisible && button.IsEnabled);
            foreach (var x in new[] { 0.05, 0.5, 0.95 })
            foreach (var y in new[] { 0.05, 0.5, 0.95 })
            {
                var hit = page.InputHitTest(button.TranslatePoint(new Point(button.ActualWidth * x, button.ActualHeight * y), page)) as DependencyObject;
                while (hit is not null && !ReferenceEquals(hit, button)) hit = VisualTreeHelper.GetParent(hit);
                Assert.AreSame(button, hit);
            }
            copied = null;
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            for (var attempt = 0; attempt < 100 && copied is null; attempt++)
                await Task.Delay(10);
            Assert.AreEqual(support.BuildDiagnosticText(model.DiagnosticLogs), copied);
            StringAssert.Contains(model.DiagnosticsFeedback, "诊断信息已复制");
        }
        Assert.IsTrue(model.HasNoDiagnosticErrors);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual("暂时没有错误记录", summary.Text);
        await CopyFromIconAsync();
        SaveReview(window, "error-logs-applied-empty.png");

        var logs = files.Folder("Sims4ModSieve");
        var applicationLog = Path.Combine(logs, "application-error.log");
        var startupLog = Path.Combine(logs, "startup-error.log");
        var raw = "[2026-10-18T17:42:08+08:00] 扫描失败\nSystem.IO.IOException: 文件正由另一进程使用。\n" +
            @"文件：C:\Users\SecretUiName\Documents\Mods\demo.package" + "\n" +
            string.Join("\n", Enumerable.Range(1, 40).Select(index => $"   at Demo.Scanner.ReadResource{index}(String path)"));
        File.WriteAllText(applicationLog, raw);
        File.WriteAllText(startupLog, "System.InvalidOperationException: 演示启动异常。\n   at Demo.App.OnStartup()");
        await model.RefreshDiagnosticsCommand.ExecuteAsync();
        await WpfTestHost.LayoutAsync(window);
        Assert.IsFalse(model.HasNoDiagnosticErrors);
        Assert.IsTrue(summary.IsVisible && summary.IsReadOnly);
        Assert.AreEqual(model.DiagnosticText, summary.Text);
        StringAssert.Contains(summary.Text, "[application-error.log · 运行错误]");
        StringAssert.Contains(summary.Text, "[startup-error.log · 启动错误]");
        Assert.IsFalse(summary.Text.Contains("SecretUiName", StringComparison.Ordinal));
        Assert.IsFalse(Descendants<Expander>(page).Any());
        SaveReview(window, "error-logs-applied-filled.png");
        var position = button.TranslatePoint(new Point(), frame);
        var scrollbar = Descendants<ScrollBar>(summary).Single(bar => bar.IsVisible);
        ScrollBar.PageDownCommand.Execute(null, scrollbar);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsGreaterThan(0, summary.VerticalOffset);
        Assert.AreEqual(position, button.TranslatePoint(new Point(), frame));
        await CopyFromIconAsync();
        Assert.AreEqual(summary.Text, copied);
        Assert.AreEqual(raw, File.ReadAllText(applicationLog));

        using (File.Open(applicationLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await model.RefreshDiagnosticsCommand.ExecuteAsync();
            await WpfTestHost.LayoutAsync(window);
            Assert.IsFalse(model.HasNoDiagnosticErrors);
            StringAssert.Contains(summary.Text, "无法读取此日志");
            StringAssert.Contains(summary.Text, "演示启动异常");
            await CopyFromIconAsync();
        }
        File.WriteAllText(applicationLog, string.Empty);
        File.Delete(startupLog);
        await model.RefreshDiagnosticsCommand.ExecuteAsync();
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(model.HasNoDiagnosticErrors);
        Assert.AreEqual("暂时没有错误记录", summary.Text);
        await CopyFromIconAsync();
        window.Close();
    });

    [TestMethod]
    public Task SettingsControlsSupportKeyboardAndRectangularHitTargets() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("我的游戏与自定义内容", "这是用于验证中文长路径可读性和省略显示的文件夹", "Mods");
        var tray = files.Folder("图库与家庭存档", "Tray");
        files.Folder("Documents", "Electronic Arts", "The Sims 4", "Mods");
        files.Store.Save(new(mods, []));
        var picker = new Picker(null, tray);
        var targets = new List<string>();
        string? copied = null;
        var support = new SettingsSupportService(files.Root, files.Folder("Documents"), openTarget: targets.Add, copyText: value => copied = value);
        var model = files.Model(picker, support: support);
        var window = new MainWindow(model);
        window.Show();
        window.Activate();
        var page = Descendants<SettingsPage>(window).Single();
        var settingNavigation = Descendants<Button>(window).Single(button => Equals(button.Content, "设置"));
        await WpfTestHost.LayoutAsync(window);
        PressSpace(settingNavigation);
        Assert.IsTrue(model.IsSettingsVisible);
        foreach (var (width, height) in new[] { (1180, 780), (920, 640) })
        {
            window.Width = width;
            window.Height = height;
            model.SelectSettingsSectionCommand.Execute("folders");
            await WpfTestHost.LayoutAsync(window);
            foreach (var label in new[] { "首页", "重复检测", "冲突检测", "设置" })
                Assert.AreEqual(1, Descendants<Button>(window).Count(button => button.IsVisible && Equals(button.Content, label)));
            foreach (var radio in Descendants<RadioButton>(page)) AssertHitRectangle(radio);
            foreach (var button in Descendants<Button>(page).Where(button => button.IsVisible)) AssertHitRectangle(button);
            var modsText = Descendants<TextBlock>(page).Single(text => text.IsVisible && text.Text == mods);
            Assert.AreEqual(mods, modsText.ToolTip);
            SaveReview(window, $"folders-{width}x{height}.png");
            foreach (var radio in Descendants<RadioButton>(page))
            {
                PressSpace(radio);
                Assert.AreEqual(radio.CommandParameter, model.SettingsSection);
                await WpfTestHost.LayoutAsync(window);
            }
            Assert.IsTrue(model.IsAboutSettings);
            PressSpace((Button)page.FindName("LicenseButton"));
            Assert.IsTrue(model.HasLicenseText);
            Assert.AreEqual(support.OpenLicenseDocument("LICENSE"), model.LicenseText);
            SaveReview(window, $"about-{width}x{height}.png");
        }
        model.SelectSettingsSectionCommand.Execute("folders");
        await WpfTestHost.LayoutAsync(window);
        PressSpace((Button)page.FindName("ChangeDefaultModsButton"));
        Assert.AreEqual(mods, model.ModsRoot);
        PressSpace((Button)page.FindName("ChangeTrayButton"));
        Assert.AreEqual(tray, model.TrayRoot);
        await WpfTestHost.LayoutAsync(window);
        PressSpace((Button)page.FindName("OpenTrayButton"));
        CollectionAssert.AreEqual(new[] { tray }, targets);
        SaveReview(window, "folders-tray-configured-920x640.png");
        PressSpace((Button)page.FindName("FindModsButton"));
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(model.HasModsFolderCandidate);
        var useCandidate = Descendants<Button>(page).Single(button => button.IsVisible && Equals(button.Content, "使用此目录"));
        AssertHitRectangle(useCandidate);
        SaveReview(window, "folders-candidate-920x640.png");
        PressSpace(Descendants<Button>(page).Single(button => button.IsVisible && Equals(button.Content, "取消")));
        Assert.IsNull(model.FolderCandidate);
        PressSpace((Button)page.FindName("ClearTrayButton"));
        Assert.IsFalse(model.HasTrayRoot);
        Directory.Delete(mods);
        model.GoHomeCommand.Execute(null);
        model.OpenSettingsCommand.Execute(null);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(model.HasModsRootAvailabilityWarning);
        SaveReview(window, "folders-missing-920x640.png");
        model.SelectSettingsSectionCommand.Execute("diagnostics");
        await model.RefreshDiagnosticsCommand.ExecuteAsync();
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(model.HasNoDiagnosticErrors);
        SaveReview(window, "diagnostics-empty-920x640.png");
        var logs = files.Folder("Sims4ModSieve");
        File.WriteAllText(Path.Combine(logs, "application-error.log"), @"测试错误 C:\Users\SecretUiName\Documents\Mods\test.package");
        await model.RefreshDiagnosticsCommand.ExecuteAsync();
        await WpfTestHost.LayoutAsync(window);
        var logContent = (TextBox)page.FindName("DiagnosticLogSummary");
        Assert.IsTrue(logContent.IsVisible);
        Assert.IsFalse(logContent.Text.Contains("SecretUiName", StringComparison.Ordinal));
        SaveReview(window, "diagnostics-filled-920x640.png");
        await model.CopyDiagnosticsCommand.ExecuteAsync();
        Assert.IsNotNull(copied);
        StringAssert.Contains(copied, "<用户>");
        Assert.IsFalse(copied.Contains("SecretUiName", StringComparison.Ordinal));
        var nativeReview = Environment.GetEnvironmentVariable("SIMS4_SETTINGS_NATIVE_REVIEW");
        if (nativeReview is "1" or "clipboard")
        {
            var nativeSupport = new SettingsSupportService(files.Root);
            nativeSupport.CopyDiagnosticText(model.DiagnosticLogs);
            Assert.AreEqual(nativeSupport.BuildDiagnosticText(model.DiagnosticLogs), Clipboard.GetText());
        }
        if (nativeReview is "1" or "folders")
        {
            new SettingsSupportService(files.Root).OpenFolder(logs);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            var opened = false;
            try
            {
                for (var attempt = 0; attempt < 50 && !opened; attempt++)
                {
                    await Task.Delay(100);
                    foreach (dynamic explorer in shell.Windows())
                    {
                        if (Uri.TryCreate((string)explorer.LocationURL, UriKind.Absolute, out Uri? location)
                            && location.IsFile && string.Equals(Path.TrimEndingDirectorySeparator(location.LocalPath), logs, StringComparison.OrdinalIgnoreCase))
                        {
                            opened = true;
                            explorer.Quit();
                            break;
                        }
                    }
                }
                Assert.IsTrue(opened, "Explorer did not open the temporary log directory.");
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        Assert.IsTrue(files.Model().HasNoTrayRoot);
        window.Close();
    });

    private static void PressSpace(ButtonBase button)
    {
        button.BringIntoView();
        button.Focus();
        Assert.IsTrue(button.IsKeyboardFocused, button.Name);
        var source = PresentationSource.FromVisual(button)!;
        button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
        button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
    }

    private static void AssertHitRectangle(FrameworkElement control)
    {
        control.BringIntoView();
        control.UpdateLayout();
        foreach (var x in new[] { 0.01, 0.5, 0.99 })
        foreach (var y in new[] { 0.01, 0.5, 0.99 })
        {
            var hit = control.InputHitTest(new Point(control.ActualWidth * x, control.ActualHeight * y)) as DependencyObject;
            Assert.IsNotNull(hit, control.Name);
            while (hit is not null && !ReferenceEquals(hit, control)) hit = VisualTreeHelper.GetParent(hit);
            Assert.AreSame(control, hit, control.Name);
        }
    }

    private static void SaveReview(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SIMS4_SETTINGS_REVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }
}
