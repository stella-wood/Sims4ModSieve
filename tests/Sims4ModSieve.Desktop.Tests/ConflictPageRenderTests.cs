using System.Reflection;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictResultSessionTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ConflictPageRenderTests
{
    [TestMethod]
    public Task RenderReviewStatesWithRealWindow() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        var window = OpenWindow(model);
        model.OpenConflictCommand.Execute(null);
        await WpfTestHost.LayoutAsync(window);
        var entries = Descendants<Button>(window).Where(button => Equals(button.Content, "进入排查→")).ToArray();
        Assert.AreEqual(4, entries.Length);
        foreach (var entry in entries)
        {
            Assert.IsTrue(entry.ActualWidth >= 60);
            foreach (var point in new[] { new Point(2, 2), new Point(entry.ActualWidth - 2, entry.ActualHeight - 2) })
                Assert.IsTrue(IsInside(entry.InputHitTest(point) as DependencyObject, entry));
        }
        Save(window, "overview-1180x780.png");
        model.OpenConflictSectionCommand.Execute("basic");
        var first = @"C:\Mods\【装扮】长发连衣裙合集 版本 2.6 最终修订 作者小葵.package";
        var second = @"C:\Mods\【功能】更好的园艺与种植体验 版本 1.8 汉化修订版.package";
        var session = Session([Resource(1, (first, "a"), (second, "b")), Resource(2, (first, "same"), (second, "same")),
            Resource(3, (first, "x"), (second, null)), Resource(4, (A, "same"), (B, "same"))], [(A, 1), (B, 2)],
            Scripts([Collision("garden.tools", (first + ".ts4script", "a"), (second + ".ts4script", "b")),
                Collision("garden.shared", (first + ".ts4script", "same"), (second + ".ts4script", "same"))]));
        await SetSessionAsync(model, session);
        foreach (var (width, height) in new[] { (1180, 780), (920, 640) })
        {
            window.Width = width;
            window.Height = height;
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(Descendants<TextBlock>(window).Any(text => Equals(text.ToolTip, first)));
            Assert.IsTrue(Descendants<TextBlock>(window).Any(text => Equals(text.ToolTip, second)));
            var list = Descendants<ListBox>(window).Single(list => list.Name == "ConflictRowsList");
            Assert.IsTrue(list.ActualHeight > 100 && list.ActualHeight < height);
            Save(window, $"basic-pairs-{width}x{height}.png");
            var button = Descendants<Button>(list).First(button => button.IsVisible);
            foreach (var point in new[] { new Point(2, 2), new Point(22, 22), new Point(12, 12) })
                Assert.IsTrue(IsInside(button.InputHitTest(point) as DependencyObject, button), $"Expand hit {point}");
            Assert.IsTrue(Descendants<System.Windows.Shapes.Path>(button).All(path => !path.IsHitTestVisible));
            // 路由键盘事件走 ButtonBase 的 Space 操作路径。
            button.Focus();
            Assert.IsTrue(button.IsKeyboardFocused);
            var source = PresentationSource.FromVisual(button)!;
            button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
            button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
            await model.ConflictRowsRefreshTask;
            Assert.IsTrue(model.ConflictRows.Any(row => row.Depth == 1));
            AssertNoTechnicalValues(window);
            // 普通展开看不到 TGI 和 SHA256，只有显式展开技术详情后才出现。
            await ExpandAsync(model, ConflictRowKind.Resource);
            await ExpandAsync(model, ConflictRowKind.Content);
            await WpfTestHost.LayoutAsync(window);
            AssertVisibleWords(window);
            AssertNoTechnicalValues(window);
            Assert.IsTrue(Descendants<System.Windows.Shapes.Ellipse>(list).Any(dot => dot.Fill is SolidColorBrush brush && brush.Color == (Color)ColorConverter.ConvertFromString("#A65F52")));
            Save(window, $"basic-resource-{width}x{height}.png");
            var fileRow = model.ConflictRows.First(row => row.Kind == ConflictRowKind.File);
            model.ToggleConflictExpandedCommand.Execute(fileRow);
            await model.ConflictRowsRefreshTask;
            var fifth = model.ConflictRows.First(row => row.Depth == 5);
            list.ScrollIntoView(fifth);
            await WpfTestHost.LayoutAsync(window);
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(fifth);
            var border = Descendants<Border>(container).Single(element => element.Name == "Row");
            Assert.AreEqual(80d, border.Margin.Left);
            model.ToggleConflictExpandedCommand.Execute(model.ConflictRows.Single(row => row.Key == fileRow.Key));
            await model.ConflictRowsRefreshTask;
            list.ScrollIntoView(model.ConflictRows[0]);

            await ExpandAsync(model, ConflictRowKind.Technical);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(model.ConflictRows.Any(row => row.Kind == ConflictRowKind.Technical && row.IsExpanded));
            Assert.IsTrue(model.ConflictRows.Any(row => row.Title.StartsWith("TGI", StringComparison.Ordinal)));
            Assert.IsTrue(model.ConflictRows.Any(row => row.Title.Contains("SHA256", StringComparison.Ordinal)));
            Save(window, $"basic-technical-{width}x{height}.png");
            foreach (var detail in model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && row.IsExpanded).ToArray())
            {
                model.ToggleConflictExpandedCommand.Execute(detail);
                await model.ConflictRowsRefreshTask;
            }
            await WpfTestHost.LayoutAsync(window);
            Assert.IsFalse(model.ConflictRows.Any(row => row.Kind == ConflictRowKind.Technical && row.Title != "技术详情"));
            AssertNoTechnicalValues(window);
            Save(window, $"basic-technical-collapsed-{width}x{height}.png");
            model.SelectConflictFilterCommand.Execute("same");
            await model.ConflictRowsRefreshTask;
            Assert.AreEqual(2, model.VisibleConflictParentCount);
            model.SelectConflictViewCommand.Execute("scripts");
            await model.ConflictRowsRefreshTask;
            await ExpandAsync(model, ConflictRowKind.ScriptPair);
            await ExpandAsync(model, ConflictRowKind.Module);
            await ExpandAsync(model, ConflictRowKind.Content);
            await WpfTestHost.LayoutAsync(window);
            Assert.AreEqual(2, model.ConflictRows.Count(row => row.Kind == ConflictRowKind.Module));
            AssertVisibleWords(window);
            AssertNoTechnicalValues(window);
            Save(window, $"basic-script-{width}x{height}.png");
            await ExpandAsync(model, ConflictRowKind.Content);
            await ExpandAsync(model, ConflictRowKind.Technical);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(model.ConflictRows.Any(row => row.Kind == ConflictRowKind.Technical && row.Title.StartsWith("SHA256", StringComparison.Ordinal)));
            Save(window, $"basic-script-technical-{width}x{height}.png");
            foreach (var detail in model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && row.IsExpanded).ToArray())
            {
                model.ToggleConflictExpandedCommand.Execute(detail);
                await model.ConflictRowsRefreshTask;
            }
            model.SelectConflictViewCommand.Execute("problems");
            await model.ConflictRowsRefreshTask;
            await ExpandAsync(model, ConflictRowKind.Problem);
            await WpfTestHost.LayoutAsync(window);
            AssertVisibleWords(window);
            AssertNoTechnicalValues(window);
            Save(window, $"basic-problems-{width}x{height}.png");
            await ExpandAsync(model, ConflictRowKind.Technical);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(model.ConflictRows.Any(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable
                && row.Tooltip.Contains("034aeecb", StringComparison.OrdinalIgnoreCase)));
            Save(window, $"basic-problems-technical-{width}x{height}.png");
            foreach (var detail in model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && row.IsExpanded).ToArray())
            {
                model.ToggleConflictExpandedCommand.Execute(detail);
                await model.ConflictRowsRefreshTask;
            }
            model.SelectConflictFilterCommand.Execute("all");
            model.SelectConflictViewCommand.Execute("resources");
            await model.ConflictRowsRefreshTask;
            // 第二个尺寸从折叠状态验证键盘展开。
            foreach (var row in model.ConflictRows.Where(row => row.Depth == 0 && row.IsExpanded).ToArray())
            {
                model.ToggleConflictExpandedCommand.Execute(row);
                await model.ConflictRowsRefreshTask;
            }
        }
        model.ConflictBackCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        Assert.AreSame(session.Resources, model.ConflictResources);
        await SetSessionAsync(model, new());
        Assert.IsTrue(model.ShowConflictWarning);
        Save(window, "basic-empty-920x640.png");
        foreach (var state in new[] { "initial", "scanning", "cancelled", "failed" })
        {
            SetField(model, "conflictRunState", state);
            SetField(model, "conflictStatus", state switch { "cancelled" => "扫描已取消", "failed" => "扫描没有完成", "scanning" => "正在比较资源…", _ => "尚未扫描" });
            typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.IsBusy))!.SetValue(model, state == "scanning");
            Refresh(model);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsFalse(model.ShowConflictWarning);
            AssertVisibleWords(window);
            if (state == "scanning") Assert.IsTrue(Descendants<CheckBox>(window).All(box => !box.IsEnabled));
            Save(window, $"basic-{state}-920x640.png");
        }
    });

    internal static MainWindowViewModel CreateModel() => new(new DuplicateRunService(DuplicateScanner.CreateDefault()),
        new NoFolderPicker(), new InMemorySettings(new DuplicateSettings(null, [new SavedScanSource(Path.GetTempPath(), true)])));

    internal static MainWindow OpenWindow(MainWindowViewModel model)
    {
        // 与正式应用一样加载 Assets/Sims4ModSieve.ico，标题栏图标才有证据。
        var window = new MainWindow(model)
        {
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
            Icon = BitmapFrame.Create(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Sims4ModSieve.ico"))),
        };
        return Show(window);
    }
    private static MainWindow Show(MainWindow window) { window.Show(); return window; }

    internal static async Task SetSessionAsync(MainWindowViewModel model, ConflictResultSession session, string view = "resources")
    {
        SetField(model, "conflictSession", session);
        SetField(model, "conflictRunState", "complete");
        SetField(model, "conflictStatus", "扫描完成");
        Refresh(model);
        model.SelectConflictViewCommand.Execute(view);
        await model.ConflictRowsRefreshTask;
    }
    internal static void SetField(MainWindowViewModel model, string name, object value) =>
        typeof(MainWindowViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, value);
    internal static void Refresh(MainWindowViewModel model) =>
        typeof(MainWindowViewModel).GetMethod("RefreshConflictResults", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null);

    internal static async Task ExpandAsync(MainWindowViewModel model, ConflictRowKind kind)
    {
        foreach (var row in model.ConflictRows.Where(row => row.Kind == kind && !row.IsExpanded).ToArray())
        {
            model.ToggleConflictExpandedCommand.Execute(row);
            await model.ConflictRowsRefreshTask;
        }
    }
    internal static void AssertNoTechnicalValues(Window window)
    {
        foreach (var text in Descendants<TextBlock>(window).Where(text => text.Visibility == Visibility.Visible))
        {
            Assert.IsFalse(TechnicalText.IsMatch(text.Text), text.Text);
            Assert.IsFalse(TechnicalText.IsMatch(text.ToolTip?.ToString() ?? ""), text.ToolTip?.ToString());
        }
        foreach (var item in Descendants<ListBoxItem>(window).Where(item => item.DataContext is ConflictResultRow))
        {
            var row = (ConflictResultRow)item.DataContext;
            Assert.AreEqual(row.Title, AutomationProperties.GetName(item));
            Assert.AreEqual(row.Key, AutomationProperties.GetAutomationId(item));
        }
        foreach (var button in Descendants<Button>(window).Where(button => button.DataContext is ConflictResultRow))
        {
            var row = (ConflictResultRow)button.DataContext;
            Assert.AreEqual(row.ExpandName, AutomationProperties.GetName(button));
            Assert.AreEqual(row.Key, AutomationProperties.GetAutomationId(button));
        }
    }
    internal static void AssertVisibleWords(Window window)
    {
        foreach (var text in Descendants<TextBlock>(window).Where(text => text.IsVisible))
            foreach (var forbidden in new[] { "内容相同", "内容不同", "没比完" })
            {
                Assert.IsFalse(text.Text.Contains(forbidden, StringComparison.Ordinal), text.Text);
                Assert.IsFalse((text.ToolTip?.ToString() ?? "").Contains(forbidden, StringComparison.Ordinal));
            }
    }
    internal static void Save(Window window, string name)
    {
        var output = Environment.GetEnvironmentVariable("SIMS4_CONFLICT_REVIEW_DIR");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name));
        encoder.Save(stream);
        Assert.IsTrue(stream.Length > 1000);
    }
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
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
