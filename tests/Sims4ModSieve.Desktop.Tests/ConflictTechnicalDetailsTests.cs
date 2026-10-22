using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;
using static Sims4ModSieve.Desktop.Tests.ConflictResultSessionTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ConflictTechnicalDetailsTests
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);

    internal static ConflictResultSession TechnicalSession(int resources, int files)
    {
        var members = Enumerable.Range(0, files)
            .Select(index => (Path: $@"C:\DemoMods\文件夹-{index:D2}\MouthScaleSlider_Fixed_{index:D2}.package", Hash: (string?)$"content-{index}"))
            .ToArray();
        var session = Session(Enumerable.Range(0, resources).Select(index => Resource((ulong)(0xE337 + index), members)).ToArray());
        var card = session.Cards.Single();
        session.ToggleExpanded(card.Key);
        session.ToggleExpanded(Rows(session).Single(row => row.IsTechnicalToggle).Key);
        return session;
    }

    [TestMethod]
    [DataRow(5, 14, false)]
    [DataRow(5, 15, true)]
    [DataRow(6, 15, false)]
    [DataRow(1, 15, true)]
    public void LayoutUsesListOnlyForAtMostFiveResourcesAndAtLeastFifteenFiles(int resources, int files, bool list)
    {
        var session = TechnicalSession(resources, files);
        var rows = Rows(session);
        Assert.AreEqual(list ? resources : 0, rows.Count(row => row.Kind == ConflictRowKind.TechnicalListHeader));
        Assert.AreEqual(list ? resources * files : 0, rows.Count(row => row.Kind == ConflictRowKind.TechnicalListFile));
        Assert.AreEqual(list ? 0 : 1, rows.Count(row => row.Kind == ConflictRowKind.TechnicalHeader));
        Assert.AreEqual(list ? 0 : resources, rows.Count(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable));
        if (!list)
        {
            var table = rows.Single(row => row.Kind == ConflictRowKind.TechnicalScroll).TechnicalTable;
            Assert.IsTrue(rows.Where(row => row.TechnicalEntry is not null).All(row => ReferenceEquals(table, row.TechnicalTable)));
            Assert.IsTrue(rows.Where(row => row.TechnicalEntry is not null).All(row => row.TechnicalEntry!.Cells.Count == files));
        }
        Assert.IsTrue(rows.Single(row => row.Kind == ConflictRowKind.CopyDetails).CopyText.Contains("SHA256 content-0", StringComparison.Ordinal));
        var detail = rows.Single(row => row.IsTechnicalToggle);
        session.ToggleExpanded(detail.Key);
        Assert.IsFalse(Rows(session).Any(row => row.TechnicalEntry is not null || row.Kind == ConflictRowKind.TechnicalScroll));
        session.Reset();
        Assert.AreEqual(0, Rows(session).Count);
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(24)]
    public Task MouseWheelOverTableScrollsResultsVerticallyWithoutMovingFileColumns(int files) => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, TechnicalSession(60, files));
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var outer = Descendants<ScrollViewer>(list).Single(control => control.Name != "TechnicalColumnsScroll");
        var header = model.ConflictRows.Single(row => row.Kind == ConflictRowKind.TechnicalHeader);
        var body = model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable).ElementAt(30);
        foreach (var row in new[] { header, body })
        {
            list.ScrollIntoView(row);
            await WpfTestHost.LayoutAsync(window);
            foreach (var name in new[] { row == header ? "FileName" : "Version", "Identifier" })
            {
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
                var target = Descendants<TextBlock>(container).First(text => text.Name == name && text.IsVisible);
                var vertical = outer.VerticalOffset;
                var horizontal = row.TechnicalTable!.HorizontalOffset;
                Wheel(target, -120);
                await WpfTestHost.LayoutAsync(window);
                Assert.IsTrue(outer.VerticalOffset > vertical, "鼠标在表头、版本或固定编号上时，向下滚轮应滚动外层结果");
                vertical = outer.VerticalOffset;
                Wheel(target, 120);
                await WpfTestHost.LayoutAsync(window);
                Assert.IsTrue(outer.VerticalOffset < vertical, "向上滚轮应滚动外层结果");
                Assert.AreEqual(horizontal, row.TechnicalTable.HorizontalOffset, "上下滚轮不应移动横向文件列");
            }
        }
        Save(window, $"technical-table-wheel-{files}-files.png");

        static void Wheel(UIElement target, int delta)
        {
            var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
            {
                RoutedEvent = Mouse.PreviewMouseWheelEvent,
                Source = target
            };
            target.RaiseEvent(args);
            if (args.Handled) return;
            args.RoutedEvent = Mouse.MouseWheelEvent;
            target.RaiseEvent(args);
        }
    });

    [TestMethod]
    [DataRow(2)]
    [DataRow(24)]
    public Task NativeMouseWheelOverTableTextWhitespaceAndTooltipScrollsResults(int files) => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        window.Left = 30;
        window.Top = 30;
        window.Topmost = true;
        window.Activate();
        await SetSessionAsync(model, TechnicalSession(100, files));
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var outer = Descendants<ScrollViewer>(list).Single(control => control.Name != "TechnicalColumnsScroll");
        var row = model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable).ElementAt(40);
        list.ScrollIntoView(row);
        await WpfTestHost.LayoutAsync(window);
        outer.ScrollToVerticalOffset(outer.VerticalOffset + outer.ViewportHeight / 3);
        await WpfTestHost.LayoutAsync(window);
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
        var version = Descendants<TextBlock>(container).First(text => text.Name == "Version" && text.IsVisible);
        var identifier = Descendants<TextBlock>(container).Single(text => text.Name == "Identifier");
        Assert.IsTrue(GetCursorPos(out var previous));
        try
        {
            foreach (var (target, point, description) in new[]
            {
                (version, new Point(8, version.ActualHeight / 2), "版本文字"),
                (version, new Point(version.ActualWidth - 3, version.ActualHeight / 2), "文件单元格空白"),
                (identifier, new Point(8, identifier.ActualHeight / 2), "固定编号"),
            })
            {
                var screen = target.PointToScreen(point);
                Assert.IsTrue(SetCursorPos((int)screen.X, (int)screen.Y));
                Mouse.Synchronize();
                await Task.Delay(100);
                await CheckWheel(description);
            }
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            version.ToolTipOpening += (_, _) => opened.TrySetResult();
            ToolTipService.SetInitialShowDelay(version, 0);
            var away = identifier.PointToScreen(new Point(8, identifier.ActualHeight / 2));
            Assert.IsTrue(SetCursorPos((int)away.X, (int)away.Y));
            Mouse.Synchronize();
            await Task.Delay(100);
            var hover = version.PointToScreen(new Point(8, version.ActualHeight / 2));
            Assert.IsTrue(SetCursorPos((int)hover.X, (int)hover.Y));
            Mouse.Synchronize();
            Assert.AreSame(opened.Task, await Task.WhenAny(opened.Task, Task.Delay(3000)), "应实际悬停打开证据提示框");
            await WpfTestHost.LayoutAsync(window);
            var tooltip = PresentationSource.CurrentSources.Cast<PresentationSource>()
                .Select(source => source.RootVisual).OfType<DependencyObject>().SelectMany(Descendants<ToolTip>)
                .Single(tooltip => tooltip.IsOpen && Equals(tooltip.Content, version.ToolTip));
            Save(window, $"technical-table-native-wheel-tooltip-{files}.png");
            Save(window, $"technical-table-native-wheel-evidence-{files}.png", tooltip);
            await CheckWheel("证据提示框打开时");
        }
        finally { SetCursorPos(previous.X, previous.Y); }

        async Task CheckWheel(string description)
        {
            foreach (var delta in new[] { -120, 120 })
            {
                var before = outer.VerticalOffset;
                var horizontal = row.TechnicalTable!.HorizontalOffset;
                Console.WriteLine($"{description}: delta={delta}, before={before}, mouse={Mouse.DirectlyOver?.GetType().Name}");
                mouse_event(0x0800, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
                await Task.Delay(150);
                await WpfTestHost.LayoutAsync(window);
                Console.WriteLine($"after={outer.VerticalOffset}");
                Assert.IsTrue(delta < 0 ? outer.VerticalOffset > before : outer.VerticalOffset < before,
                    $"{description}上的系统鼠标滚轮应滚动结果列表（delta={delta}）");
                Assert.AreEqual(horizontal, row.TechnicalTable.HorizontalOffset, "上下滚轮不应移动文件列");
            }
        }
    });

    [TestMethod]
    public Task TableArrowCommandsAndThumbKeepIdentifiersFixedAndColumnsSynchronized() => WpfTestHost.RunAsync(async () =>
    {
        var session = TechnicalSession(6, 24);
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        window.Height = 1000;
        await SetSessionAsync(model, session);
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var scrollRow = model.ConflictRows.Single(row => row.Kind == ConflictRowKind.TechnicalScroll);
        list.ScrollIntoView(scrollRow);
        await WpfTestHost.LayoutAsync(window);
        var bar = Descendants<ScrollBar>(list).Single(control => control.Name == "TechnicalTableScrollBar");
        Assert.IsTrue(bar.IsVisible);
        Assert.IsTrue(bar.Maximum > 0);
        var identifiers = Descendants<TextBlock>(list).Where(text => text.Name == "Identifier").ToArray();
        Assert.IsTrue(identifiers.Length > 0);
        var positions = identifiers.Select(text => text.TranslatePoint(new Point(), window).X).ToArray();
        var left = (RepeatButton)bar.Template.FindName("LeftArrow", bar);
        var right = (RepeatButton)bar.Template.FindName("RightArrow", bar);
        foreach (var button in new[] { left, right })
            foreach (var fraction in new[] { 0.1, 0.5, 0.9 })
            {
                var hit = button.InputHitTest(new Point(button.ActualWidth * fraction, button.ActualHeight / 2)) as DependencyObject;
                Assert.IsNotNull(hit);
                Assert.IsTrue(ReferenceEquals(button, hit) || button.IsAncestorOf(hit));
            }
        Assert.IsTrue(((RoutedCommand)right.Command).CanExecute(null, bar));
        ((RoutedCommand)right.Command).Execute(null, bar);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(scrollRow.TechnicalTable!.HorizontalOffset > 0);
        foreach (var scroll in Descendants<ScrollViewer>(list).Where(control => control.Name == "TechnicalColumnsScroll"))
            Assert.AreEqual(scrollRow.TechnicalTable.HorizontalOffset, scroll.HorizontalOffset, 0.01);
        CollectionAssert.AreEqual(positions, identifiers.Select(text => text.TranslatePoint(new Point(), window).X).ToArray());
        ((RoutedCommand)left.Command).Execute(null, bar);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(0d, scrollRow.TechnicalTable.HorizontalOffset);
        var thumb = (Thumb)bar.Template.FindName("ScrollThumb", bar);
        thumb.RaiseEvent(new DragDeltaEventArgs(20, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(scrollRow.TechnicalTable.HorizontalOffset > 0, "拖动滑块应滚动文件列");
        bar.SetCurrentValue(RangeBase.ValueProperty, bar.Maximum);
        await WpfTestHost.LayoutAsync(window);
        var offset = scrollRow.TechnicalTable.HorizontalOffset;
        Assert.IsTrue(offset > 0);
        CheckColumnAlignment();
        foreach (var control in Descendants<ItemsControl>(list).Where(control => control.DataContext is ConflictResultRow { TechnicalTable: not null }))
            Assert.IsTrue(Descendants<TextBlock>(control).Count() < 24, "横向文件列应虚拟化");
        Save(window, "technical-table-horizontal-scroll.png");
        list.ScrollIntoView(model.ConflictRows[0]);
        await WpfTestHost.LayoutAsync(window);
        list.ScrollIntoView(scrollRow);
        await WpfTestHost.LayoutAsync(window);
        foreach (var scroll in Descendants<ScrollViewer>(list).Where(control => control.Name == "TechnicalColumnsScroll"))
            Assert.AreEqual(offset, scroll.HorizontalOffset, 0.01, "回收后应恢复本表格的滚动位置");
        CheckColumnAlignment();

        void CheckColumnAlignment()
        {
            var controls = Descendants<ItemsControl>(list).Where(control => control.DataContext is ConflictResultRow { TechnicalTable: not null }).ToArray();
            foreach (var control in controls)
            {
                var realized = Enumerable.Range(0, control.Items.Count).Select(index => (Index: index, Container: control.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement))
                    .Where(item => item.Container is not null).ToArray();
                Console.WriteLine($"{((ConflictResultRow)control.DataContext).Kind}: offset={Descendants<ScrollViewer>(control).Single().HorizontalOffset}, columns={string.Join(';', realized.Select(item => $"{item.Index}:{item.Container!.ActualWidth}:{item.Container.TranslatePoint(new Point(), window).X}"))}");
            }
            var headerControl = controls.Single(control => ((ConflictResultRow)control.DataContext).Kind == ConflictRowKind.TechnicalHeader);
            foreach (var control in controls.Where(control => !ReferenceEquals(control, headerControl)))
                for (var index = 0; index < control.Items.Count; index++)
                    if (control.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement cell
                        && headerControl.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement headerCell)
                        Assert.AreEqual(headerCell.TranslatePoint(new Point(), window).X, cell.TranslatePoint(new Point(), window).X, 0.01, "相同文件的表头和版本列应对齐");
        }
    });

    [TestMethod]
    public Task TableFittingViewportShowsAllColumnsWithoutHorizontalScrollBar() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, TechnicalSession(6, 2));
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var header = model.ConflictRows.Single(row => row.Kind == ConflictRowKind.TechnicalHeader);
        foreach (var width in new[] { 1180, 920 })
        {
            window.Width = width;
            list.ScrollIntoView(header);
            await WpfTestHost.LayoutAsync(window);
            Assert.AreEqual(0d, header.TechnicalTable!.ScrollableWidth);
            var names = Descendants<TextBlock>(list).Where(text => text.DataContext is ConflictTechnicalCell { FileName.Length: > 0 } cell && text.Text == cell.FileName).ToArray();
            Assert.AreEqual(2, names.Length);
            Assert.IsTrue(names.All(text => text.IsVisible));
            Assert.IsFalse(Descendants<ScrollBar>(list).Any(bar => bar.Name == "TechnicalTableScrollBar" && bar.IsVisible));
            Save(window, $"technical-table-fitting-{width}.png");
        }
    });

    [TestMethod]
    public Task ListHasPlainRowsWithoutExpandersAndRetainsFileEvidence() => WpfTestHost.RunAsync(async () =>
    {
        var session = TechnicalSession(2, 15);
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, session);
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var headers = model.ConflictRows.Where(row => row.Kind == ConflictRowKind.TechnicalListHeader).ToArray();
        foreach (var header in headers)
        {
            list.ScrollIntoView(header);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsFalse(Descendants<Expander>(list).Any());
            var file = model.ConflictRows.First(row => row.Kind == ConflictRowKind.TechnicalListFile && row.TechnicalEntry!.Identifier == header.Title);
            list.ScrollIntoView(file);
            await WpfTestHost.LayoutAsync(window);
            var name = Descendants<TextBlock>(list).Single(text => ReferenceEquals(text.DataContext, file) && text.Text == file.Title);
            Assert.IsTrue(name.ToolTip.ToString()!.Contains(@"C:\DemoMods\", StringComparison.Ordinal));
            Assert.IsTrue(name.ToolTip.ToString()!.Contains("SHA256", StringComparison.Ordinal));
        }
        Save(window, "technical-list-plain-rows.png");
    });
}
