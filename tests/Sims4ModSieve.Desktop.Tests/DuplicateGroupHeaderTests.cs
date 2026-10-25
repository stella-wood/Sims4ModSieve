using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class DuplicateGroupHeaderTests
{
    [TestMethod]
    public Task HeadersShowKeepFileNameAndCountAndSupportSelectionAndExpansion() => WpfTestHost.RunAsync(async () =>
    {
        var session = new DuplicateResultSession();
        var groups = new[] { "obscurus_hairline_N1.package", string.Concat(Enumerable.Repeat("很长的建议保留文件名_", 8)) + ".package" }
            .Select((name, index) =>
            {
                DuplicateFile[] files =
                [
                    new(@"D:\Downloads\copy.package", 100, DateTime.UnixEpoch, ["test"], ["test"], false, 2),
                    new(@"D:\Mods\" + name, 100, DateTime.UnixEpoch, ["test"], ["test"], true, 2),
                ];
                var suggestion = new DuplicateSelectionService().Select(files);
                return new DuplicateGroupViewModel(new DuplicateGroup($"hash-{index}", 100, DuplicateSection.FocusCrossScope,
                    files, suggestion.KeepPath, suggestion.DeletePaths), index + 1);
            }).ToArray();
        var model = new MainWindowViewModel(new DuplicateRunService(DuplicateScanner.CreateDefault()),
            new NoFolderPicker(), new EmptySettingsStore(), resultSession: session);
        model.OpenDuplicateScannerCommand.Execute(null);
        session.Replace(groups);
        var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();

        foreach (var (width, height) in new[] { (1180, 780), (920, 640) })
        {
            window.Width = width;
            window.Height = height;
            await WpfTestHost.LayoutAsync(window);
            var expanders = Descendants<Expander>(window).Where(item => item.DataContext is DuplicateGroupViewModel).ToArray();
            Assert.AreEqual(groups.Length, expanders.Length);
            foreach (var expander in expanders)
            {
                var group = (DuplicateGroupViewModel)expander.DataContext;
                var header = (FrameworkElement)expander.Header;
                var title = Descendants<TextBlock>(header).Single(text => text.Text == group.Title);
                var count = Descendants<TextBlock>(header).Single(text => text.Text == group.CopyCountText);
                Assert.IsTrue(title.IsVisible);
                Assert.AreEqual(group.Files.Single(file => file.IsSuggestedKeep).FileName, title.Text);
                Assert.AreEqual(group.Title, title.ToolTip);
                Assert.IsTrue(count.IsVisible);
                Assert.IsFalse(Descendants<TextBlock>(header).Single(text => text.Text == group.SectionTitle).IsVisible);
                Assert.IsTrue(title.TranslatePoint(new Point(title.ActualWidth, 0), header).X <= count.TranslatePoint(new Point(), header).X);
                Assert.IsTrue(count.TranslatePoint(new Point(count.ActualWidth, 0), header).X <= header.ActualWidth);

                var checkBox = Descendants<CheckBox>(header).Single();
                PressSpace(checkBox);
                Assert.IsTrue(group.IsSuggestionSelected);
                Assert.IsFalse(group.Files.Single(file => file.IsSuggestedKeep).IsSelected);
                PressSpace(checkBox);
                Assert.AreEqual(0, group.SelectedCount);

                var toggle = Descendants<ToggleButton>(expander).First(button => button is not CheckBox);
                var wasExpanded = group.IsExpanded;
                PressSpace(toggle);
                Assert.AreEqual(!wasExpanded, group.IsExpanded);
                PressSpace(toggle);
                Assert.AreEqual(wasExpanded, group.IsExpanded);
            }
            Save(window, $"duplicate-headers-{width}x{height}.png");
        }
    });

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point point);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [TestMethod]
    [DataRow(1180, 780, false)]
    [DataRow(920, 640, false)]
    [DataRow(1180, 780, true)]
    [DataRow(920, 640, true)]
    public Task HeaderMouseTargetsToggleExpansionWithoutChangingSelection(int width, int height, bool longName) => WpfTestHost.RunAsync(async () =>
    {
        var name = longName ? string.Concat(Enumerable.Repeat("很长的建议保留文件名_", 8)) + ".package" : "obscurus_hairline_N1.package";
        DuplicateFile[] files =
        [
            new(@"D:\Downloads\copy.package", 100, DateTime.UnixEpoch, ["test"], ["test"], false, 2),
            new(@"D:\Mods\" + name, 100, DateTime.UnixEpoch, ["test"], ["test"], true, 2),
        ];
        var suggestion = new DuplicateSelectionService().Select(files);
        var group = new DuplicateGroupViewModel(new DuplicateGroup("header-hit-test", 100, DuplicateSection.FocusCrossScope,
            files, suggestion.KeepPath, suggestion.DeletePaths), 1) { IsExpanded = false };
        var session = new DuplicateResultSession();
        var model = new MainWindowViewModel(new DuplicateRunService(DuplicateScanner.CreateDefault()),
            new NoFolderPicker(), new EmptySettingsStore(), resultSession: session);
        model.OpenDuplicateScannerCommand.Execute(null);
        session.Replace([group]);
        var window = new MainWindow(model)
        {
            Left = 60, Top = 40, Width = width, Height = height, Topmost = true, ShowInTaskbar = false,
        };
        window.Show();
        window.Activate();
        await WpfTestHost.LayoutAsync(window);
        var expander = Descendants<Expander>(window).Single(item => ReferenceEquals(item.DataContext, group));
        var header = (FrameworkElement)expander.Header;
        var caption = Descendants<ToggleButton>(header).Single(button => button.Name == "DuplicateHeaderToggle");
        var arrow = Descendants<ToggleButton>(expander).Single(button => button.Name == "ExpanderArrowToggle");
        var checkBox = Descendants<CheckBox>(header).Single();
        Assert.IsTrue(GetCursorPos(out var previous));
        try
        {
            var targets = new List<(FrameworkElement Element, Point Point)>
            {
                (caption, new Point(caption.ActualWidth * 0.05, caption.ActualHeight * 0.05)),
                (caption, new Point(caption.ActualWidth * 0.95, caption.ActualHeight * 0.95)),
                (arrow, new Point(arrow.ActualWidth * 0.85, arrow.ActualHeight * 0.15)),
            };
            targets.AddRange(Descendants<TextBlock>(caption).Select(text =>
                ((FrameworkElement)text, new Point(text.ActualWidth / 2, text.ActualHeight / 2))));
            foreach (var (target, point) in targets)
            {
                var hit = window.InputHitTest(target.TranslatePoint(point, window)) as DependencyObject;
                Assert.IsTrue(IsInside(hit, caption) || IsInside(hit, arrow), "标题及其边缘应命中展开按钮");
                await ClickAsync(target, point);
                Assert.IsTrue(group.IsExpanded, "点击标题、统计或箭头留白应展开");
                Assert.AreEqual(0, group.SelectedCount);
                await ClickAsync(target, point);
                Assert.IsFalse(group.IsExpanded, "再次点击应收起");
            }
            await ClickAsync(checkBox, new Point(checkBox.ActualWidth / 2, checkBox.ActualHeight / 2));
            Assert.IsFalse(group.IsExpanded, "组勾选不得触发展开");
            Assert.IsTrue(group.IsSuggestionSelected);
            Assert.IsFalse(group.Files.Single(file => file.IsSuggestedKeep).IsSelected);
            await ClickAsync(checkBox, new Point(checkBox.ActualWidth / 2, checkBox.ActualHeight / 2));
            Assert.AreEqual(0, group.SelectedCount);
            var captionEnd = caption.TranslatePoint(new Point(caption.ActualWidth, 0), header).X;
            if (header.ActualWidth > captionEnd + 1)
            {
                await ClickAsync(header, new Point((captionEnd + header.ActualWidth) / 2, header.ActualHeight / 2));
                Assert.IsFalse(group.IsExpanded, "标题右侧空白不触发展开");
                Assert.AreEqual(0, group.SelectedCount);
            }
            PressSpace(caption);
            Assert.IsTrue(group.IsExpanded, "标题支持键盘空格");
            await WpfTestHost.LayoutAsync(window);
            var fileText = Descendants<TextBlock>(expander).First(text => text.DataContext is DuplicateFileViewModel && text.Text == group.Files[0].FileName);
            await ClickAsync(fileText, new Point(fileText.ActualWidth / 2, fileText.ActualHeight / 2));
            Assert.IsTrue(group.IsExpanded, "组内文件区不触发展开收起");
            Assert.AreEqual(0, group.SelectedCount);
            Save(window, $"duplicate-header-hits-{width}x{height}-{(longName ? "long" : "short")}.png");
            PressSpace(arrow);
            Assert.IsFalse(group.IsExpanded, "箭头继续支持键盘空格");
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

    private static async Task ClickAsync(FrameworkElement element, Point point)
    {
        var screen = element.PointToScreen(point);
        Assert.IsTrue(SetCursorPos((int)screen.X, (int)screen.Y));
        Mouse.Synchronize();
        await Task.Delay(70);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(100);
        await WpfTestHost.LayoutAsync(Window.GetWindow(element)!);
    }
    private static void PressSpace(Control control)
    {
        control.Focus();
        Assert.IsTrue(control.IsKeyboardFocused);
        var source = PresentationSource.FromVisual(control)!;
        control.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
        control.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Save(Window window, string name)
    {
        var output = Environment.GetEnvironmentVariable("SIMS4_DUPLICATE_REVIEW_DIR");
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
    }

    private sealed class NoFolderPicker : IFolderPickerService
    {
        public string? PickFolder(string title, string? initialDirectory = null) => null;
    }

    private sealed class EmptySettingsStore : IDuplicateSettingsStore
    {
        public DuplicateSettings? Load() => new(null, []);
        public void Save(DuplicateSettings settings) { }
    }
}
