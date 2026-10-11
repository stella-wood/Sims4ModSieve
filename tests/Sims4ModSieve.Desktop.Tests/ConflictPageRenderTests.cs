using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ConflictPageRenderTests
{
    [TestMethod]
    public void RenderReviewStatesWithRealWindow()
    {
        var output = Environment.GetEnvironmentVariable("SIMS4_CONFLICT_REVIEW_DIR");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var temporary = Path.Combine(Path.GetTempPath(), $"s4ms-review-{Guid.NewGuid():N}");
                Directory.CreateDirectory(temporary);
                try
                {
                    var model = new MainWindowViewModel(
                        new DuplicateRunService(DuplicateScanner.CreateDefault()),
                        new NoFolderPicker(),
                        new InMemorySettings(new DuplicateSettings(temporary,
                            [new SavedScanSource(temporary, true)])));
                    var window = new MainWindow(model)
                    {
                        Left = -10000,
                        Top = -10000,
                        ShowActivated = false,
                        ShowInTaskbar = false,
                    };
                    window.Show();
                    model.OpenConflictCommand.Execute(null);
                    window.UpdateLayout();
                    var entries = Descendants<Button>(window).Where(button => Equals(button.Content, "进入排查→")).ToArray();
                    Assert.AreEqual(4, entries.Length);
                    foreach (var entry in entries)
                    {
                        Assert.IsTrue(entry.ActualWidth >= 60);
                        foreach (var point in new[] { new Point(2, 2), new Point(entry.ActualWidth - 2, entry.ActualHeight - 2) })
                            Assert.IsTrue(IsInside(entry.InputHitTest(point) as DependencyObject, entry));
                    }
                    Save(window, Path.Combine(output, "overview-1180x780.png"));
                    window.Width = 920;
                    window.Height = 640;
                    Save(window, Path.Combine(output, "overview-920x640.png"));
                    Assert.IsTrue(entries.All(entry => entry.IsVisible && entry.ActualHeight > 10));
                    Assert.IsTrue(entries.All(entry => entry.TransformToAncestor(window).Transform(new Point(0, entry.ActualHeight)).Y < window.ActualHeight));
                    model.OpenConflictSectionCommand.Execute("basic");
                    Assert.IsTrue(model.IsConflictBasic);
                    Save(window, Path.Combine(output, "basic-initial-920x640.png"));
                    SetReviewResults(model, withConflict: true);
                    ExpandVisible(window);
                    Assert.IsTrue(model.VisibleConflictResources.Single().IsDifferent && model.VisibleConflictResources.Single().IsPartial);
                    Assert.IsTrue(Descendants<TextBlock>(window).Any(text => Equals(text.ToolTip, Path.Combine(model.ModsRoot!, "A.package"))));
                    Save(window, Path.Combine(output, "basic-resource-920x640.png"));
                    window.Width = 1180;
                    window.Height = 780;
                    Save(window, Path.Combine(output, "basic-resource-1180x780.png"));
                    window.Width = 920;
                    window.Height = 640;
                    model.SelectConflictFilterCommand.Execute("partial");
                    Assert.AreEqual(1, model.VisibleConflictResources.Count);
                    model.SelectConflictFilterCommand.Execute("all");
                    model.SelectConflictViewCommand.Execute("scripts");
                    ExpandVisible(window);
                    Save(window, Path.Combine(output, "basic-script-920x640.png"));
                    model.SelectConflictViewCommand.Execute("problems");
                    ExpandVisible(window);
                    Save(window, Path.Combine(output, "basic-problems-920x640.png"));
                    SetReviewResults(model, withConflict: false);
                    Save(window, Path.Combine(output, "basic-empty-with-problems-920x640.png"));
                    SetRunState(model, "cancelled", "扫描已取消", "本次分析未完成");
                    Save(window, Path.Combine(output, "basic-cancelled-920x640.png"));
                    SetRunState(model, "failed", "扫描没有完成", "请检查来源后重试");
                    Save(window, Path.Combine(output, "basic-failed-920x640.png"));
                    window.Close();
                }
                finally
                {
                    Directory.Delete(temporary, recursive: true);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void SetReviewResults(MainWindowViewModel model, bool withConflict)
    {
        var now = DateTime.UtcNow;
        var stamp = new FileStamp(1, now);
        var key = new Sims4ModSieve.Core.Packages.ResourceKey(1, 2, 3);
        ResourceContentOccurrence Resource(string path, string? hash, ResourceContentIssue? issue = null) =>
            new(key, path, 0, stamp, hash, hash is null ? null : 1, issue);
        var folder = model.ModsRoot!;
        var first = Path.Combine(folder, "A.package");
        var second = Path.Combine(folder, "B.package");
        var failed = Path.Combine(folder, "Unfinished.package");
        var packageReport = new PackageConflictScanReport(now, now, [], 3, 2, [failed], [],
            [new("index-failed", PackageConflictScanIssueStage.IndexRead, failed, "索引读取失败")]);
        var contentReport = new ResourceContentComparisonReport(now, now, new ResourceContentLimits(),
            withConflict ? [new ResourceContentComparison(key,
                [new("A", 1, [Resource(first, "A")]), new("B", 1, [Resource(second, "B")])],
                [Resource(failed, null, new("read-failed", ResourceContentStage.Read, "资源读取失败"))])] : [],
            [], 0);
        ScriptModuleOccurrence Module(string path, string hash) =>
            new("garden.tools", path, stamp, [], "garden/tools.py", ScriptModuleKind.Module,
                ScriptModuleFormat.Source, hash, 1, null);
        var scriptReport = new ScriptModuleScanReport(now, now, [], new ScriptModuleLimits(), 2, 2, [], 1,
            withConflict ? [new ScriptModuleCollision("garden.tools",
                [new("A", [Module(Path.Combine(folder, "A.ts4script"), "A")]),
                 new("B", [Module(Path.Combine(folder, "B.ts4script"), "B")])], [])] : [], [], 0);
        var session = new ConflictResultSession();
        session.Replace(packageReport, contentReport, scriptReport);
        typeof(MainWindowViewModel).GetField("conflictSession", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, session);
        SetRunState(model, "complete", withConflict ? "扫描完成" : "已分析范围未发现冲突",
            "1 个文件未完成分析 · 0 项范围问题");
        model.SelectConflictViewCommand.Execute(withConflict ? "resources" : "problems");
    }

    private static void SetRunState(MainWindowViewModel model, string state, string status, string phase)
    {
        if (state is "cancelled" or "failed")
            typeof(MainWindowViewModel).GetField("conflictSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(model, new ConflictResultSession());
        typeof(MainWindowViewModel).GetField("conflictRunState", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, state);
        typeof(MainWindowViewModel).GetField("conflictStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, status);
        typeof(MainWindowViewModel).GetField("conflictPhase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, phase);
        typeof(MainWindowViewModel).GetMethod("RefreshConflictResults", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null);
    }

    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(content.ActualWidth);
        var height = (int)Math.Ceiling(content.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Assert.IsTrue(new FileInfo(path).Length > 1000);
    }

    private static void ExpandVisible(Window window)
    {
        for (var depth = 0; depth < 5; depth++)
        {
            window.UpdateLayout();
            var collapsed = Descendants<Expander>(window).Where(item => item.IsVisible && !item.IsExpanded).ToArray();
            if (collapsed.Length == 0) break;
            foreach (var expander in collapsed) expander.IsExpanded = true;
        }
        window.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static bool IsInside(DependencyObject? child, DependencyObject ancestor)
    {
        while (child is not null)
        {
            if (ReferenceEquals(child, ancestor)) return true;
            child = VisualTreeHelper.GetParent(child);
        }
        return false;
    }

    private sealed class NoFolderPicker : IFolderPickerService
    {
        public string? PickFolder(string title, string? initialDirectory = null) => null;
    }

    private sealed class InMemorySettings(DuplicateSettings settings) : IDuplicateSettingsStore
    {
        public DuplicateSettings? Load() => settings;
        public void Save(DuplicateSettings value) { }
    }
}
