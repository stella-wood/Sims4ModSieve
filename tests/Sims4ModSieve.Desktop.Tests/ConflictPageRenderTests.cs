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
    public Task ResourceCardsAndFilterPopupSupportKeyboardAndNarrowWindows() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        var window = OpenWindow(model);
        model.OpenConflictCommand.Execute(null);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(4, Descendants<Button>(window).Count(button => Equals(button.Content, "进入排查→")));
        Save(window, "overview-1180x780.png");
        model.OpenConflictSectionCommand.Execute("basic");
        await WpfTestHost.LayoutAsync(window);
        var sourceList = Descendants<ListBox>(window).Single(list => list.Name == "ConflictSourcesList");
        Assert.AreSame(model.ConflictSources, sourceList.ItemsSource);
        var sourceCheck = Descendants<CheckBox>(sourceList).Single();
        model.Sources[0].IsEnabled = false;
        Assert.AreEqual(true, sourceCheck.IsChecked);
        sourceCheck.IsChecked = false;
        Assert.IsFalse(model.ConflictSources[0].IsEnabled);
        model.Sources[0].IsEnabled = true;
        Assert.AreEqual(false, sourceCheck.IsChecked);
        sourceCheck.IsChecked = true;
        var first = @"C:\Mods\【装扮】长发连衣裙合集 版本 2.6 最终修订 作者小葵.package";
        var second = @"C:\Mods\【功能】更好的园艺与种植体验 版本 1.8 汉化修订版.package";
        var session = Session([Resource(1, (first, "a"), (second, "b")), Resource(2, (A, "same"), (B, "same")),
            Resource(3, (first, "x"), (second, null))], [(A, 1), (B, 1)],
            Scripts([Collision("garden.tools", (first + ".ts4script", "a"), (second + ".ts4script", "b"))]));
        await SetSessionAsync(model, session, "all");
        foreach (var (width, height) in new[] { (1180, 780), (920, 640) })
        {
            window.Width = width;
            window.Height = height;
            await WpfTestHost.LayoutAsync(window);
            var list = Descendants<ListBox>(window).Single(list => list.Name == "ConflictRowsList");
            Assert.IsTrue(list.ActualHeight > 100 && list.ActualHeight < height);
            Assert.AreEqual(4, model.VisibleConflictParentCount);
            Assert.AreEqual(1, Descendants<TextBlock>(window).Count(text => text.IsVisible && text.Text == "扫描完成"));
            var quickBar = Descendants<Border>(window).Single(border => border.Name == "ConflictQuickFilterBar");
            Assert.AreEqual(32d, quickBar.ActualHeight);
            var allFilter = Descendants<Button>(quickBar).Single(button => button.Name == "ConflictAllFilter");
            var resultHeading = Descendants<TextBlock>(window).Single(text => text.IsVisible && text.Text == "扫描结果");
            var allLabel = Descendants<TextBlock>(allFilter).Single();
            var headingPoint = resultHeading.TransformToAncestor(window).Transform(new Point());
            var allPoint = allLabel.TransformToAncestor(window).Transform(new Point());
            var barPoint = quickBar.TransformToAncestor(window).Transform(new Point());
            Assert.AreEqual(18d, allPoint.X - headingPoint.X, 0.01);
            Assert.AreEqual(12d, allPoint.Y - headingPoint.Y - resultHeading.ActualHeight, 0.01);
            var toolbarFilter = Descendants<System.Windows.Controls.Primitives.ToggleButton>(quickBar).Single(button => button.Name == "ConflictFilterButton");
            var filterPoint = toolbarFilter.TransformToAncestor(quickBar).Transform(new Point());
            Assert.AreEqual(quickBar.ActualWidth, filterPoint.X + toolbarFilter.ActualWidth, 0.01);
            Console.WriteLine($"Layout {width}x{height}: headingHeight={resultHeading.ActualHeight:F2}; headingToBar={barPoint.Y - headingPoint.Y - resultHeading.ActualHeight:F2}; headingToAllText={allPoint.Y - headingPoint.Y - resultHeading.ActualHeight:F2}; headingTopToAllTop={allPoint.Y - headingPoint.Y:F2}; allTextHeight={allLabel.ActualHeight:F2}");
            var quickFilters = Descendants<Button>(quickBar).Where(button => button != allFilter).ToArray();
            Assert.AreEqual("all", model.ConflictFilter);
            Assert.IsNull(allFilter.Tag);
            Assert.IsFalse(Descendants<Border>(allFilter).Any(border => border.Name == "Underline"));
            Assert.AreEqual(3, quickFilters.Length);
            Assert.IsTrue(quickFilters.All(button => Equals(button.Tag, false)));
            Assert.AreEqual(Colors.Transparent, ((SolidColorBrush)quickBar.Background).Color);
            Assert.AreEqual(new Thickness(0, 0, 0, 1), quickBar.BorderThickness);
            foreach (var quickFilter in quickFilters)
                foreach (var point in new[] { new Point(2, 2), new Point(quickFilter.ActualWidth - 2, quickFilter.ActualHeight - 2) })
                    Assert.IsTrue(IsInside(quickFilter.InputHitTest(point) as DependencyObject, quickFilter));
            foreach (var row in model.ConflictRows)
            {
                list.ScrollIntoView(row);
                await WpfTestHost.LayoutAsync(window);
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
                var border = Descendants<Border>(container).Single(element => element.Name == "Row");
                Assert.AreEqual(new Thickness(0), border.BorderThickness);
                var title = Descendants<TextBlock>(container).Single(text => text.Name == "Title");
                var label = Descendants<TextBlock>(container).Single(text => text.Name == "GroupLabel");
                Assert.AreEqual(row.GroupLabel, label.Text);
                var dot = Descendants<System.Windows.Shapes.Ellipse>(container).Single(dot => dot.Name == "StateDot");
                Assert.AreEqual(10d, dot.ActualWidth);
                var stateColor = row.Priority switch
                {
                    ConflictPairPriority.NeedsReview => "#D33C2E",
                    ConflictPairPriority.CanOrganize => "#D99316",
                    _ => "#948C84",
                };
                Assert.AreEqual((Color)ColorConverter.ConvertFromString(stateColor), ((SolidColorBrush)dot.Fill).Color);
                Assert.IsTrue(dot.TransformToAncestor(border).Transform(new Point(dot.ActualWidth, 0)).X < label.TransformToAncestor(border).Transform(new Point()).X);
                var separator = Descendants<System.Windows.Shapes.Line>(container).Single(line => line.Name == "GroupSeparator");
                Assert.IsTrue(separator.IsVisible);
                CollectionAssert.AreEqual(new[] { 2d, 4d }, separator.StrokeDashArray.ToArray());
                Assert.AreEqual(20d, separator.Margin.Top + separator.ActualHeight + separator.Margin.Bottom + border.Margin.Top);
                if (row == model.ConflictRows[0])
                {
                    var header = Descendants<Border>(container).Single(element => element.Name == "RowContent");
                    Console.WriteLine($"Group layout: total={container.ActualHeight:F2}; header={header.ActualHeight:F2}; text={title.ActualHeight:F2}; rowTopMargin={border.Margin.Top:F2}; separatorHeight={separator.ActualHeight:F2}; separatorTopMargin={separator.Margin.Top:F2}; separatorBottomMargin={separator.Margin.Bottom:F2}");
                }
            }
            list.ScrollIntoView(model.ConflictRows[0]);
            await WpfTestHost.LayoutAsync(window);
            Save(window, $"basic-cards-{width}x{height}.png");
            var button = Descendants<Button>(list).First(button => button.IsVisible && button.DataContext is ConflictResultRow { Kind: ConflictRowKind.Resource });
            foreach (var point in new[] { new Point(2, 2), new Point(22, 22), new Point(12, 12) })
                Assert.IsTrue(IsInside(button.InputHitTest(point) as DependencyObject, button), $"Expand hit {point}");
            Assert.IsTrue(Descendants<System.Windows.Shapes.Path>(button).All(path => !path.IsHitTestVisible));
            button.Focus();
            Assert.IsTrue(button.IsKeyboardFocused);
            var source = PresentationSource.FromVisual(button)!;
            button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
            button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
            await model.ConflictRowsRefreshTask;
            await WpfTestHost.LayoutAsync(window);
            Assert.AreEqual(2, model.ConflictRows.Count(row => row.Kind == ConflictRowKind.File));
            Assert.IsTrue(Descendants<TextBlock>(list).Any(text => Equals(text.ToolTip, first)));
            AssertNoTechnicalValues(window);
            AssertVisibleWords(window);
            Save(window, $"basic-resource-{width}x{height}.png");
            Assert.IsTrue(Descendants<System.Windows.Shapes.Ellipse>(list).Any(dot => dot.IsVisible && dot.Fill is SolidColorBrush brush && brush.Color == (Color)ColorConverter.ConvertFromString("#D33C2E")));
            var technical = model.ConflictRows.Single(row => row.IsTechnicalToggle);
            var action = Descendants<Button>(list).Single(button => button.IsVisible && Equals(button.Content, "技术详情"));
            Assert.AreEqual(HorizontalAlignment.Right, action.HorizontalAlignment);
            action.Command!.Execute(action.CommandParameter);
            await model.ConflictRowsRefreshTask;
            await WpfTestHost.LayoutAsync(window);
            Save(window, $"basic-technical-{width}x{height}.png");
            foreach (var row in model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable))
            {
                list.ScrollIntoView(row);
                await WpfTestHost.LayoutAsync(window);
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
                var border = Descendants<Border>(container).Single(element => element.Name == "Row");
                Assert.AreEqual(0d, border.Margin.Left);
                var text = Descendants<TextBlock>(container).Single(text => text.Name == "Title");
                Assert.IsTrue(text.TransformToAncestor(border).Transform(new Point()).X >= 40);
            }
            model.ToggleConflictExpandedCommand.Execute(technical);
            await model.ConflictRowsRefreshTask;
            list.ScrollIntoView(model.ConflictRows[0]);
            await WpfTestHost.LayoutAsync(window);
            AssertNoTechnicalValues(window);
            var filter = Descendants<System.Windows.Controls.Primitives.ToggleButton>(window).Single(button => button.Name == "ConflictFilterButton");
            foreach (var point in new[] { new Point(2, 2), new Point(22, 22) })
                Assert.IsTrue(IsInside(filter.InputHitTest(point) as DependencyObject, filter));
            filter.Focus();
            filter.IsChecked = true;
            await WpfTestHost.LayoutAsync(window);
            Assert.IsNull(filter.FocusVisualStyle);
            var filterBorder = Descendants<Border>(filter).Single(border => border.Name == "HitArea");
            Assert.AreEqual(Colors.Transparent, ((SolidColorBrush)filterBorder.BorderBrush).Color);
            var popup = Descendants<System.Windows.Controls.Primitives.Popup>(window).Single();
            Assert.IsTrue(popup.IsOpen);
            Assert.AreEqual(System.Windows.Controls.Primitives.PlacementMode.Custom, popup.Placement);
            var popupContent = (FrameworkElement)popup.Child;
            var placement = popup.CustomPopupPlacementCallback!(popupContent.RenderSize, filter.RenderSize, new Point()).Single();
            var popupPoint = filter.TransformToAncestor(window).Transform(placement.Point);
            Assert.AreEqual(filter.ActualWidth - popupContent.ActualWidth, placement.Point.X, 0.01);
            Assert.AreEqual(filter.ActualHeight, placement.Point.Y, 0.01);
            Assert.IsTrue(popupPoint.X >= 0 && popupPoint.X + popupContent.ActualWidth <= window.ActualWidth);
            Assert.IsTrue(popupPoint.Y >= 0 && popupPoint.Y + popupContent.ActualHeight <= window.ActualHeight);
            var checks = Descendants<CheckBox>(popup.Child).ToArray();
            CollectionAssert.AreEqual(new[] { "全部", "需查看", "可整理", "未完成", "全部", "资源", "脚本" }, checks.Select(box => (string)box.Content).ToArray());
            var choiceGrids = Descendants<Grid>(popup.Child).Where(grid => grid.Name is "ConflictStatusChoices" or "ConflictTypeChoices").ToArray();
            Assert.AreEqual(2, choiceGrids.Length);
            foreach (var choices in choiceGrids)
            {
                Assert.AreEqual(3, choices.ColumnDefinitions.Count);
                Assert.AreEqual(72d, choices.ColumnDefinitions[0].ActualWidth, 0.01);
                Assert.AreEqual(72d, choices.ColumnDefinitions[1].ActualWidth, 0.01);
            }
            var longestChoice = checks.Single(box => Equals(box.Content, "可整理"));
            Assert.AreEqual(longestChoice.ActualWidth, choiceGrids[0].ColumnDefinitions[2].ActualWidth, 0.01);
            Assert.AreEqual(choiceGrids[0].ColumnDefinitions[2].ActualWidth, choiceGrids[1].ColumnDefinitions[2].ActualWidth, 0.01);
            var choicePoint = longestChoice.TransformToAncestor(popupContent).Transform(new Point());
            Assert.AreEqual(16d, popupContent.ActualWidth - choicePoint.X - longestChoice.ActualWidth - 1, 0.01);
            var allStates = checks.Single(box => AutomationProperties.GetName(box) == "全部状态");
            var allTypes = checks.Single(box => AutomationProperties.GetName(box) == "全部类型");
            Assert.AreEqual(true, allStates.IsChecked);
            Assert.AreEqual(true, allTypes.IsChecked);
            foreach (var quickFilter in quickFilters)
            {
                quickFilter.Command!.Execute(quickFilter.CommandParameter);
                await model.ConflictRowsRefreshTask;
                await WpfTestHost.LayoutAsync(window);
                var expected = (string)quickFilter.CommandParameter == "different" ? ConflictPairPriority.NeedsReview
                    : (string)quickFilter.CommandParameter == "same" ? ConflictPairPriority.CanOrganize : ConflictPairPriority.Incomplete;
                Assert.IsTrue(model.ConflictRows.Count > 0);
                Assert.IsTrue(model.ConflictRows.Where(row => row.Depth == 0).All(row => row.Priority == expected));
                Assert.AreEqual(true, quickFilter.Tag);
                Assert.AreEqual((Color)ColorConverter.ConvertFromString("#6F5A91"), ((SolidColorBrush)quickFilter.Foreground).Color);
                Assert.IsTrue(Descendants<Border>(quickFilter).Single(border => border.Name == "Underline").IsVisible);
                quickFilter.Command.Execute(quickFilter.CommandParameter);
                await model.ConflictRowsRefreshTask;
                Assert.AreEqual(true, quickFilter.Tag);
            }
            allFilter.Command!.Execute(allFilter.CommandParameter);
            await model.ConflictRowsRefreshTask;
            await WpfTestHost.LayoutAsync(window);
            Assert.AreEqual("all", model.ConflictFilter);
            Assert.AreEqual(4, model.VisibleConflictParentCount);
            Assert.IsNull(allFilter.Tag);
            Assert.AreEqual(true, allStates.IsChecked);
            Assert.AreEqual((Color)ColorConverter.ConvertFromString("#2B2927"), ((SolidColorBrush)allFilter.Foreground).Color);
            checks.Single(box => Equals(box.Content, "需查看")).IsChecked = false;
            await model.ConflictRowsRefreshTask;
            Assert.IsFalse(model.IncludeConflictNeedsReview);
            Assert.AreEqual(false, allStates.IsChecked);
            Assert.IsTrue(quickFilters.All(button => Equals(button.Tag, false)));
            Assert.IsTrue(model.HasActiveConflictFilter);
            checks.Single(box => Equals(box.Content, "需查看")).IsChecked = true;
            await model.ConflictRowsRefreshTask;
            Assert.IsFalse(model.HasActiveConflictFilter);
            allStates.IsChecked = false;
            await model.ConflictRowsRefreshTask;
            Assert.AreEqual(0, model.ConflictRows.Count);
            allStates.IsChecked = true;
            await model.ConflictRowsRefreshTask;
            Assert.IsTrue(model.IncludeConflictNeedsReview && model.IncludeConflictCanOrganize && model.IncludeConflictIncomplete);
            checks.Single(box => Equals(box.Content, "资源")).IsChecked = false;
            await model.ConflictRowsRefreshTask;
            Assert.AreEqual(false, allTypes.IsChecked);
            allTypes.IsChecked = true;
            await model.ConflictRowsRefreshTask;
            Assert.IsTrue(model.IncludeConflictResources && model.IncludeConflictScripts);
            Assert.IsFalse(model.HasActiveConflictFilter);
            filter.IsChecked = false;
            foreach (var row in model.ConflictRows.Where(row => row.Depth == 0 && row.IsExpanded).ToArray()) model.ToggleConflictExpandedCommand.Execute(row);
            await model.ConflictRowsRefreshTask;
        }
        model.ConflictBackCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        Assert.AreSame(session.Resources, model.ConflictResources);
        await SetSessionAsync(model, new(), "all");
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
            if (state == "scanning") Assert.IsTrue(Descendants<CheckBox>(window).All(box => !box.IsEnabled));
            Save(window, $"basic-{state}-920x640.png");
        }
    });

    internal static MainWindowViewModel CreateModel() => new(new DuplicateRunService(DuplicateScanner.CreateDefault()),
        new NoFolderPicker(), new InMemorySettings(new DuplicateSettings(Path.GetTempPath(), [])));

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
            foreach (var forbidden in new[] { "内容分组", "没比完", "资源组编号" })
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
