using System.Reflection;
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
    public Task ResourceCardsAndFilterPopupSupportKeyboardAndNarrowWindows() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        var window = OpenWindow(model);
        model.OpenConflictCommand.Execute(null);
        await WpfTestHost.LayoutAsync(window);
        Save(window, "overview-1180x780.png");
        model.OpenConflictSectionCommand.Execute("basic");
        var first = @"C:\Mods\【装扮】长发连衣裙合集 版本 2.6 最终修订 作者小葵.package";
        var second = @"C:\Mods\【功能】更好的园艺与种植体验 版本 1.8 汉化修订版.package";
        var session = Session([Resource(1, (first, "a"), (second, "b")), Resource(2, (A, "same"), (B, "same")),
            Resource(3, (@"C:\Mods\未完成.package", null))], [(A, 1), (B, 1)],
            Scripts([Collision("garden.tools", (first + ".ts4script", "a"), (second + ".ts4script", "b"))]));
        await SetSessionAsync(model, session, "all");
        foreach (var (width, height) in new[] { (1180, 780), (920, 640) })
        {
            window.Width = width;
            window.Height = height;
            await WpfTestHost.LayoutAsync(window);
            var list = Descendants<ListBox>(window).Single(list => list.Name == "ConflictRowsList");
            Assert.AreEqual(1, Descendants<TextBlock>(window).Count(text => text.IsVisible && text.Text == "扫描完成"));
            AssertNoTechnicalValues(window);
            var quickBar = Descendants<Border>(window).Single(border => border.Name == "ConflictQuickFilterBar");
            foreach (var quickFilter in Descendants<Button>(quickBar))
            {
                foreach (var fraction in new[] { 0.1, 0.5, 0.9 })
                    Assert.IsTrue(IsInside(quickFilter.InputHitTest(new Point(quickFilter.ActualWidth * fraction, quickFilter.ActualHeight * fraction)) as DependencyObject, quickFilter));
                quickFilter.Focus();
                Assert.IsTrue(quickFilter.IsKeyboardFocused);
            }
            Save(window, $"basic-cards-{width}x{height}.png");
            var button = Descendants<Button>(list).First(button => button.IsVisible && button.DataContext is ConflictResultRow { Kind: ConflictRowKind.Resource });
            var expandedKey = ((ConflictResultRow)button.DataContext).Key;
            foreach (var fraction in new[] { 0.1, 0.5, 0.9 })
                Assert.IsTrue(IsInside(button.InputHitTest(new Point(button.ActualWidth * fraction, button.ActualHeight * fraction)) as DependencyObject, button));
            button.Focus();
            Assert.IsTrue(button.IsKeyboardFocused);
            var source = PresentationSource.FromVisual(button)!;
            button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
            button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
            await model.ConflictRowsRefreshTask;
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(model.ConflictRows.Single(row => row.Key == expandedKey).IsExpanded);
            AssertNoTechnicalValues(window);
            Save(window, $"basic-resource-{width}x{height}.png");
            var filter = Descendants<System.Windows.Controls.Primitives.ToggleButton>(window).Single(button => button.Name == "ConflictFilterButton");
            foreach (var fraction in new[] { 0.1, 0.5, 0.9 })
                Assert.IsTrue(IsInside(filter.InputHitTest(new Point(filter.ActualWidth * fraction, filter.ActualHeight * fraction)) as DependencyObject, filter));
            filter.Focus();
            Assert.IsTrue(filter.IsKeyboardFocused);
            var filterSource = PresentationSource.FromVisual(filter)!;
            filter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, filterSource, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
            filter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, filterSource, 0, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
            await WpfTestHost.LayoutAsync(window);
            var popup = Descendants<System.Windows.Controls.Primitives.Popup>(window).Single();
            Assert.IsTrue(popup.IsOpen);
            foreach (var choice in Descendants<CheckBox>(popup.Child))
                foreach (var fraction in new[] { 0.1, 0.5, 0.9 })
                    Assert.IsTrue(IsInside(choice.InputHitTest(new Point(choice.ActualWidth * fraction, choice.ActualHeight * fraction)) as DependencyObject, choice));
            Save(window, $"v2-filter-{width}x{height}.png", (FrameworkElement)popup.Child);
            AssertNoTechnicalValues(window);
            filter.IsChecked = false;
            foreach (var row in model.ConflictRows.Where(row => row.Depth == 0 && row.IsExpanded).ToArray()) model.ToggleConflictExpandedCommand.Execute(row);
            await model.ConflictRowsRefreshTask;
        }
    });

    internal static MainWindowViewModel CreateModel(Action<string>? copy = null) => new(new DuplicateRunService(DuplicateScanner.CreateDefault()),
        new NoFolderPicker(), new InMemorySettings(new DuplicateSettings(Path.GetTempPath(), [])), conflictDetailsCopy: copy);

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
        foreach (var row in model.ConflictRows.Where(row => row.Kind == kind && row.IsExpandable && !row.IsExpanded).ToArray())
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
    }
    internal static void Save(Window window, string name, FrameworkElement? target = null)
    {
        var output = Environment.GetEnvironmentVariable("SIMS4_CONFLICT_REVIEW_DIR");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        window.UpdateLayout();
        var content = target ?? (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name));
        encoder.Save(stream);
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
