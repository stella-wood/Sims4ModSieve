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
