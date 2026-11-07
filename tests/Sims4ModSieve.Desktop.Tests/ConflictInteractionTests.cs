using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;
using static Sims4ModSieve.Desktop.Tests.ConflictResultSessionTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ConflictInteractionTests
{
    [TestMethod]
    public Task StateAndTypeCheckboxesFilterTheirIntersectionAndUseResultNames() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        var session = new ConflictResultSession();
        session.Replace(ConflictResultSessionTests.Packages([(A, 3), (B, 10), (C, 4)], issues: [
            new("unsupported-version", Sims4ModSieve.Core.Conflicts.PackageConflictScanIssueStage.IndexRead, A + ".failed", "unsupported"),
            new("source-unavailable", Sims4ModSieve.Core.Conflicts.PackageConflictScanIssueStage.SourceValidation, @"C:\Offline", "unavailable")]),
            Contents(Resource(1, (A, "a"), (B, "a")), Resource(2, (A, "b"), (B, "b")),
                Resource(3, (A, "c"), (B, "c")), Resource(4, (B, "d"), (C, "e"))), Scripts());
        await SetSessionAsync(model, session, "all");
        var popup = Descendants<Popup>(window).Single(element => element.Name == "ConflictFilterPopup");
        popup.IsOpen = true;
        await WpfTestHost.LayoutAsync(window);
        var typeChoices = Descendants<Grid>(popup.Child).Single(grid => grid.Name == "ConflictTypeChoices");
        Assert.IsFalse(Descendants<CheckBox>(typeChoices).Any(box => Equals(box.Content, "未分析")));
        CollectionAssert.AreEqual(new[] { "全部", "异常", "有隐患", "未分析" },
            Descendants<CheckBox>(Descendants<Grid>(popup.Child).Single(grid => grid.Name == "ConflictStatusChoices")).Select(box => box.Content.ToString()).ToArray());
        Assert.IsTrue(Descendants<CheckBox>(typeChoices).Any(box => Equals(box.Content, "脚本冲突")));
        Assert.IsTrue(model.IncludeAllConflictTypes);
        Assert.IsFalse(model.HasActiveConflictFilter);
        Assert.AreEqual(4, model.VisibleConflictParentCount);
        model.IncludeAllConflictTypes = false;
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(0, model.ConflictRows.Count);
        var collectionChoice = Descendants<CheckBox>(typeChoices).Single(box => Equals(box.Content, "合集包含"));
        collectionChoice.IsChecked = true;
        await model.ConflictRowsRefreshTask;
        var collection = model.ConflictRows.Single();
        Assert.AreEqual("合集包含", collection.Title);
        Assert.AreEqual("有隐患", collection.StateText);
        Assert.IsTrue(model.HasActiveConflictFilter);
        model.IncludeAllConflictStates = false;
        model.IncludeConflictIncomplete = true;
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(0, model.ConflictRows.Count);
        model.IncludeConflictCanOrganize = true;
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(collection.Key, model.ConflictRows.Single().Key);
        model.IncludeAllConflictTypes = true;
        model.IncludeConflictCanOrganize = false;
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(2, model.VisibleConflictParentCount);
        Assert.IsTrue(model.ConflictRows.Where(row => row.IsCardStart).All(row => row.StateText == "未分析"));
        model.IncludeAllConflictStates = true;
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(4, model.VisibleConflictParentCount);
        Assert.IsFalse(model.HasActiveConflictFilter);
        Save(window, "conflict-unanalyzed-filter.png", popup.Child as FrameworkElement);
        popup.IsOpen = false;
        Save(window, "conflict-unanalyzed-results.png");
    });

    [TestMethod]
    [DataRow("row")]
    [DataRow("relation")]
    [DataRow("copy")]
    public Task GroupLinksExpandTargetAndCenterTitle(string route) => WpfTestHost.RunAsync(async () =>
    {
        var paths = Enumerable.Range(0, 40).Select(index => new[] { $@"C:\Mods\jump{index:D2}a.package", $@"C:\Mods\jump{index:D2}b.package", $@"C:\Mods\jump{index:D2}c.package" }).ToArray();
        var duplicates = ConflictV2SessionTests.Duplicates(paths[0][0], paths[0][1]) with
        {
            Groups = paths.Select((pair, index) => ConflictV2SessionTests.Duplicates(pair[0], pair[1]).Groups.Single() with { Sha256 = $"HASH{index:D2}" }).ToArray(),
        };
        var session = ConflictV2SessionTests.V2(paths.Select((pair, index) => Resource((ulong)index + 1,
            (pair[0], "same"), (pair[1], "same"), (pair[2], "other"))).ToArray(),
            paths.SelectMany(pair => pair.Select(path => (path, 1))).ToArray(), duplicates);
        var source = session.Cards.Single(card => card.Type == "suspected" && card.Files.Any(file => file.Path == paths[20][0]));
        session.ToggleExpanded(source.Key);
        session.SetIgnoredFiles([paths[20][1]]);
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, session, "all");
        var list = Descendants<ListBox>(window).Single(element => element.Name == "ConflictRowsList");
        var sourceRow = model.ConflictRows.Single(row => row.OwnerKey == source.Key && row.HasCopies);
        list.ScrollIntoView(sourceRow);
        await WpfTestHost.LayoutAsync(window);
        var link = Descendants<Button>(list).Single(button => ReferenceEquals(button.DataContext, sourceRow) && button.Command == model.JumpToConflictGroupCommand);
        Assert.AreEqual(sourceRow.DuplicateLink, link.CommandParameter);
        model.IncludeConflictCollections = false;
        await model.ConflictRowsRefreshTask;
        list.ScrollIntoView(model.ConflictRows.Single(row => row.Key == sourceRow.Key));
        await WpfTestHost.LayoutAsync(window);
        sourceRow = model.ConflictRows.Single(row => row.Key == sourceRow.Key);
        link = Descendants<Button>(list).Single(button => ReferenceEquals(button.DataContext, sourceRow) && button.Command == model.JumpToConflictGroupCommand);
        var targetKey = sourceRow.DuplicateLink!.Key;
        if (route == "row") Invoke(link);
        else
        {
            var details = Descendants<Button>(list).Single(button => ReferenceEquals(button.DataContext, sourceRow) && button.Name == "FileDetailsButton");
            Assert.IsTrue(details.Focus());
            model.OpenConflictRelationshipsCommand.Execute(sourceRow);
            await Task.Delay(280);
            await WpfTestHost.LayoutAsync(window);
            var dialog = Descendants<ConflictRelationshipsDialog>(window).Single();
            Assert.IsTrue(dialog.IsVisible);
            if (route == "copy")
            {
                Descendants<Button>(dialog).Single(button => button.Name == "OwnCopiesToggle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WpfTestHost.LayoutAsync(window);
                link = Descendants<Button>(dialog).Single(button => button.IsVisible && button.DataContext is ConflictRelationshipDisplayRow { HasCopyLink: true });
            }
            else
            {
                targetKey = source.Key;
                link = Descendants<Button>(dialog).First(button => button.IsVisible && button.DataContext is ConflictGroupLink group && group.Key == targetKey);
            }
            link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(230);
            Assert.IsFalse(dialog.IsVisible);
            Assert.IsNull(model.ConflictRelationships);
        }
        await WpfTestHost.LayoutAsync(window);
        await model.ConflictRowsRefreshTask;
        await WpfTestHost.LayoutAsync(window);
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        var target = model.ConflictRows.Single(row => row.Key == targetKey);
        Assert.IsTrue(target.IsExpanded);
        Assert.IsTrue(model.IncludeAllConflictTypes);
        Assert.IsTrue(model.IncludeConflictCollections);
        var viewer = Descendants<ScrollViewer>(list).First();
        var presenter = Descendants<ScrollContentPresenter>(viewer).First();
        var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(target);
        Assert.IsNotNull(item);
        Assert.AreEqual(presenter.ActualHeight / 2, item.TranslatePoint(new Point(0, item.ActualHeight / 2), presenter).Y, 2d);
        Assert.AreEqual(route == "relation" ? 2 : 1, model.ConflictRows.Count(row => row.OwnerKey == target.Key && row.File is not null));
        Save(window, $"conflict-{route}-jump-centered.png");
        model.ShowIgnoredConflicts = true;
        await model.ConflictRowsRefreshTask;
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(2, model.ConflictRows.Count(row => row.OwnerKey == target.Key && row.File is not null));
        if (route != "relation") Assert.IsTrue(Descendants<TextBlock>(window).Any(text => text.IsVisible && text.Text == "已忽略"));
        Save(window, $"conflict-{route}-jump-ignored.png");
    });

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);

    [TestMethod]
    public Task OpenFilterPopupAllowsNativeWheelScrollingAndOutsideClick() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        PlaceOnScreen(window);
        await SetSessionAsync(model, ManyCards(), "all");
        await WpfTestHost.LayoutAsync(window);
        var list = Descendants<ListBox>(window).Single(element => element.Name == "ConflictRowsList");
        var viewer = Descendants<ScrollViewer>(list).First();
        var toggle = Descendants<ToggleButton>(window).Single(element => element.Name == "ConflictFilterButton");
        var popup = Descendants<Popup>(window).Single();
        Assert.IsTrue(GetCursorPos(out var previous));
        try
        {
            toggle.IsChecked = true;
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(popup.IsOpen);
            var choice = Descendants<CheckBox>(popup.Child).First();
            var listWheel = new List<string>();
            var popupWheel = new List<string>();
            list.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, e) => listWheel.Add($"{e.Delta}:{e.OriginalSource.GetType().Name}:handled={e.Handled}")), true);
            popup.Child.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, e) => popupWheel.Add($"{e.Delta}:{e.OriginalSource.GetType().Name}:handled={e.Handled}")), true);
            await ClickAsync(choice);
            Assert.AreEqual(false, choice.IsChecked, "第一次真实点击应取消全部状态");
            Assert.IsTrue(popup.IsOpen, "筛选选择后弹层应保持打开");
            await ClickAsync(choice);
            Assert.AreEqual(true, choice.IsChecked, "第二次真实点击应恢复全部状态");
            await model.ConflictRowsRefreshTask;
            await WpfTestHost.LayoutAsync(window);
            viewer.ScrollToTop();
            await WpfTestHost.LayoutAsync(window);
            await WpfTestHost.MoveMouseAsync(list, new Point(list.ActualWidth / 3, list.ActualHeight / 2));
            await Task.Delay(100);
            Assert.IsTrue(IsInside(Mouse.DirectlyOver as DependencyObject, list),
                $"真实鼠标应命中结果列表；over={Mouse.DirectlyOver}, active={window.IsActive}, foreground={GetForegroundWindow()}");
            var before = viewer.VerticalOffset;
            // Native input follows Popup capture and HWND routing, unlike RaiseEvent on the list.
            mouse_event(0x0800, 0, 0, unchecked((uint)-360), UIntPtr.Zero);
            await Task.Delay(250);
            await WpfTestHost.LayoutAsync(window);
            Save(window, "filter-native-wheel-page.png");
            Assert.IsTrue(viewer.VerticalOffset > before,
                $"弹层打开时真实滚轮应滚动下方结果；before={before}, after={viewer.VerticalOffset}, scrollable={viewer.ScrollableHeight}, active={window.IsActive}, foreground={GetForegroundWindow()}, owner={new System.Windows.Interop.WindowInteropHelper(window).Handle}, capture={GetCapture()}, managedCapture={Mouse.Captured}, focus={Keyboard.FocusedElement}, over={Mouse.DirectlyOver}, pointer={Mouse.GetPosition(list)}, listWheel=[{string.Join(";", listWheel)}], popupWheel=[{string.Join(";", popupWheel)}]");
            Assert.IsTrue(popup.IsOpen);
            Save(window, "filter-open-native-wheel.png", (FrameworkElement)popup.Child);
            var expand = Descendants<Button>(window).Single(element => element.Name == "ToggleAllConflictGroupsButton");
            Assert.IsTrue(model.ConflictRows.Where(row => row.IsCardStart && row.IsExpandable).All(row => !row.IsExpanded));
            await ClickAsync(expand);
            await model.ConflictRowsRefreshTask;
            Assert.IsFalse(popup.IsOpen, "首次点击外部应关闭弹层");
            Assert.IsTrue(model.ConflictRows.Where(row => row.IsCardStart && row.IsExpandable).All(row => row.IsExpanded),
                "关闭弹层的首次点击应同时执行底栏展开操作");
            toggle.IsChecked = true;
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(popup.IsOpen);
            choice.Focus();
            Assert.IsTrue(choice.IsKeyboardFocused);
            keybd_event(0x1B, 0, 0, UIntPtr.Zero);
            keybd_event(0x1B, 0, 0x0002, UIntPtr.Zero);
            await Task.Delay(150);
            Assert.IsFalse(popup.IsOpen, "焦点在弹层内部时 Escape 应关闭弹层");
        }
        finally { toggle.IsChecked = false; SetCursorPos(previous.X, previous.Y); }
    });

    [TestMethod]
    public Task ConflictFooterTogglesOnlyVisibleCardsAndKeepsDuplicateExpansionIndependent() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        var duplicateSession = (DuplicateResultSession)typeof(MainWindowViewModel)
            .GetField("resultSession", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model)!;
        duplicateSession.Replace(duplicateSession.PrepareGroups(ConflictV2SessionTests.Duplicates(A, B).Groups));
        var duplicate = model.Groups.Single();
        duplicate.IsExpanded = true;
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, ConflictV2SessionTests.ContainmentSession(), "all");
        model.SelectConflictFilterCommand.Execute("same");
        await model.ConflictRowsRefreshTask;
        await WpfTestHost.LayoutAsync(window);
        var button = Descendants<Button>(window).Single(element => element.Name == "ToggleAllConflictGroupsButton");
        Assert.IsTrue(button.IsEnabled);
        Invoke(button);
        await WpfTestHost.LayoutAsync(window);
        await model.ConflictRowsRefreshTask;
        Assert.IsTrue(model.ConflictRows.Where(row => row.IsCardStart && row.IsExpandable).All(row => row.IsExpanded));
        Assert.IsTrue(model.ShowCollapseAllConflictIcon);
        Assert.IsTrue(duplicate.IsExpanded);
        model.SelectConflictFilterCommand.Execute("different");
        await model.ConflictRowsRefreshTask;
        Assert.IsTrue(model.ConflictRows.Where(row => row.IsCardStart && row.IsExpandable).All(row => !row.IsExpanded));
        model.SelectConflictFilterCommand.Execute("same");
        await model.ConflictRowsRefreshTask;
        Invoke(button);
        await WpfTestHost.LayoutAsync(window);
        await model.ConflictRowsRefreshTask;
        Assert.IsTrue(model.ShowExpandAllConflictIcon);
        Assert.IsTrue(model.ConflictRows.Where(row => row.IsCardStart && row.IsExpandable).All(row => !row.IsExpanded));
        model.OpenDuplicateScannerCommand.Execute(null);
        model.ToggleAllGroupsCommand.Execute(null);
        Assert.IsFalse(duplicate.IsExpanded);
        model.OpenConflictCommand.Execute(null);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(model.ShowExpandAllConflictIcon);
        Save(window, "conflict-footer.png");
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ConflictBulkExpansionKeepsMiddleGroupAndCollapseReturnsToChildOwner(bool fromTechnicalRows) => WpfTestHost.RunAsync(async () =>
    {
        var paths = Enumerable.Range(0, 80).Select(index => ($@"C:\Mods\bulk{index:D2}a.package", $@"C:\Mods\bulk{index:D2}b.package")).ToArray();
        var session = ConflictV2SessionTests.V2(paths.SelectMany((pair, index) => Enumerable.Range(0, 24)
            .Select(resource => Resource((ulong)(index * 100 + resource + 1), (pair.Item1, "one"), (pair.Item2, "two")))).ToArray(),
            paths.SelectMany(pair => new[] { (pair.Item1, 100), (pair.Item2, 100) }).ToArray());
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, session, "all");
        await SettleAsync();
        var list = Descendants<ListBox>(window).Single(element => element.Name == "ConflictRowsList");
        var viewer = Descendants<ScrollViewer>(list).First();
        var presenter = Descendants<ScrollContentPresenter>(viewer).First();
        var button = Descendants<Button>(window).Single(element => element.Name == "ToggleAllConflictGroupsButton");
        var card = session.Cards.Single(card => card.GroupIndex == 53);
        var header = model.ConflictRows.Single(row => row.Key == card.Key);
        list.ScrollIntoView(header);
        await SettleAsync();
        var headerItem = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(header);
        viewer.ScrollToVerticalOffset(viewer.VerticalOffset + headerItem.TranslatePoint(new Point(), presenter).Y + headerItem.ActualHeight / 3);
        await SettleAsync();
        var before = Position(card.Key);
        Assert.IsTrue(before < 0 && before + headerItem.ActualHeight > 0, "以第53组部分可见标题构造视口锚点");
        Save(window, $"conflict-bulk-expand-before-{fromTechnicalRows}.png");
        Invoke(button);
        await WpfTestHost.LayoutAsync(window);
        await model.ConflictRowsRefreshTask;
        await SettleAsync();
        Assert.AreEqual(before, Position(card.Key), 1d, "全部展开后第53组标题应留在原相对位置");
        Assert.IsTrue(model.ShowCollapseAllConflictIcon);
        Assert.IsTrue(model.ConflictRows.Where(row => row.IsCardStart && row.IsExpandable).All(row => row.IsExpanded));
        Assert.IsFalse(model.ConflictRows.Any(row => row.IsTechnicalToggle && row.IsExpanded));
        Assert.IsTrue(Descendants<ListBoxItem>(list).Count() < model.ConflictRows.Count, "批量展开仍保留行虚拟化");
        Save(window, $"conflict-bulk-expand-after-{fromTechnicalRows}.png");

        ConflictResultRow child;
        if (fromTechnicalRows)
        {
            var toggle = model.ConflictRows.Single(row => row.OwnerKey == card.Key && row.IsTechnicalToggle);
            model.ToggleConflictExpandedCommand.Execute(toggle);
            await model.ConflictRowsRefreshTask;
            child = model.ConflictRows.Where(row => row.OwnerKey == card.Key && row.Kind == ConflictRowKind.Technical && !row.IsExpandable).Skip(12).First();
        }
        else child = model.ConflictRows.Where(row => row.OwnerKey == card.Key && row.File is not null).Last();
        list.ScrollIntoView(child);
        await SettleAsync();
        viewer.ScrollToVerticalOffset(viewer.VerticalOffset + Position(child.Key));
        await SettleAsync();
        Assert.IsTrue(Math.Abs(Position(child.Key)) < 1, "把组内子行置于视口顶部");
        Save(window, $"conflict-bulk-collapse-before-{fromTechnicalRows}.png");
        Invoke(button);
        await WpfTestHost.LayoutAsync(window);
        await model.ConflictRowsRefreshTask;
        await SettleAsync();
        Assert.AreEqual(0, Position(card.Key), 1d, "全部收起后应显示原子行所属第53组标题");
        Assert.IsTrue(model.ShowExpandAllConflictIcon);
        Assert.IsTrue(model.ConflictRows.All(row => row.IsCardStart));
        Assert.AreEqual(fromTechnicalRows, session.GetExpandedKeysSnapshot().Contains(card.Key + "/technical"));
        Save(window, $"conflict-bulk-collapse-after-{fromTechnicalRows}.png");
        model.SelectConflictFilterCommand.Execute("same");
        await model.ConflictRowsRefreshTask;
        await SettleAsync();
        Assert.AreEqual(0, model.ConflictRows.Count, "后续筛选不受批量位置恢复回调影响");

        double Position(string key)
        {
            var row = model.ConflictRows.Single(row => row.Key == key);
            var item = list.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
            Assert.IsNotNull(item, "当前锚点应保持实现");
            return item.TranslatePoint(new Point(), presenter).Y;
        }
        async Task SettleAsync()
        {
            await WpfTestHost.LayoutAsync(window);
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
            await Task.Delay(60);
            await WpfTestHost.LayoutAsync(window);
        }
    });

    [TestMethod]
    public Task ResultScrollbarsSupportNativeThumbDraggingAndTrackPaging() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        PlaceOnScreen(window);
        await SetSessionAsync(model, ManyCards(), "all");
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(GetCursorPos(out var previous));
        try
        {
            await AssertScrollbarAsync(window, "ConflictRowsList");
            var duplicateSession = (DuplicateResultSession)typeof(MainWindowViewModel)
                .GetField("resultSession", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model)!;
            duplicateSession.Replace(duplicateSession.PrepareGroups(Enumerable.Range(0, 60)
                .Select(index => ConflictV2SessionTests.Duplicates($@"C:\Mods\dup{index}a.package", $@"C:\Mods\dup{index}b.package").Groups.Single()).ToArray()));
            model.OpenDuplicateScannerCommand.Execute(null);
            await WpfTestHost.LayoutAsync(window);
            await AssertScrollbarAsync(window, "DuplicateGroupsList");
            Save(window, "duplicate-result-scrollbar.png");
            model.OpenConflictCommand.Execute(null);
            var paths = Enumerable.Range(0, 60).Select(index => $@"C:\Mods\relation{index}.package").ToArray();
            var session = ConflictV2SessionTests.V2([Resource(1, paths.Select((path, index) => (path, (string?)$"version{index}")).ToArray())],
                paths.Select(path => (path, 10)).ToArray());
            session.ToggleExpanded(session.Cards.First().Key);
            await SetSessionAsync(model, session, "all");
            model.OpenConflictRelationshipsCommand.Execute(model.ConflictRows.First(row => row.Kind == ConflictRowKind.File));
            await Task.Delay(250);
            await WpfTestHost.LayoutAsync(window);
            await AssertScrollbarAsync(window, "RelationRowsList");
            Save(window, "relationship-scrollbar.png");
        }
        finally { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); SetCursorPos(previous.X, previous.Y); }
    });

    private static ConflictResultSession ManyCards()
    {
        var paths = Enumerable.Range(0, 80).Select(index => ($@"C:\Mods\pair{index}a.package", $@"C:\Mods\pair{index}b.package")).ToArray();
        return ConflictV2SessionTests.V2(paths.Select((pair, index) => Resource((uint)(index + 1), (pair.Item1, "a"), (pair.Item2, "b"))).ToArray(),
            paths.SelectMany(pair => new[] { (pair.Item1, 10), (pair.Item2, 10) }).ToArray());
    }

    private static async Task AssertScrollbarAsync(Window window, string listName)
    {
        var list = Descendants<ListBox>(window).Single(element => element.Name == listName);
        var viewer = Descendants<ScrollViewer>(list).First();
        viewer.ScrollToTop();
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(viewer.ScrollableHeight > 0, listName);
        var bar = Descendants<ScrollBar>(list).Single(element => element.Orientation == Orientation.Vertical && element.IsVisible);
        var track = Descendants<Track>(bar).Single();
        await ClickAsync(track.IncreaseRepeatButton!);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(viewer.VerticalOffset > 0, $"{listName}轨道翻页应滚动");
        viewer.ScrollToTop();
        await WpfTestHost.LayoutAsync(window);
        var thumb = track.Thumb;
        await WpfTestHost.BeginDragAsync(thumb);
        await WpfTestHost.MoveMouseAsync(bar, new Point(bar.ActualWidth / 2, bar.ActualHeight * 0.7));
        await Task.Delay(100);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(100);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(viewer.VerticalOffset > 0, $"{listName}拖动滑块应滚动");
    }

    private static void PlaceOnScreen(Window window)
    {
        WpfTestHost.PlaceOnScreen(window);
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
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static Task ClickAsync(FrameworkElement element, Point? point = null) => WpfTestHost.ClickAsync(element, point);
}
