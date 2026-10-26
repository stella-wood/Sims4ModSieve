using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Desktop.Infrastructure;
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

    private static ConflictResultSession RelatedSession(int pairCount = 8)
    {
        var pairs = Enumerable.Range(0, pairCount).Select(index => (First: $@"C:\DemoMods\pair-{index}-a.package", Second: $@"C:\DemoMods\pair-{index}-b.package")).ToArray();
        return Session([Resource(1, pairs.SelectMany((pair, index) => new[] { (pair.First, (string?)$"shared-{index}"), (pair.Second, (string?)$"shared-{index}") }).ToArray()),
            .. pairs.SelectMany((pair, index) => new[] { Resource((ulong)(100 + index * 2), (pair.First, "same"), (pair.Second, "same")),
                Resource((ulong)(101 + index * 2), (pair.First, "same"), (pair.Second, "same")) })],
            pairs.SelectMany(pair => new[] { (pair.First, 4), (pair.Second, 4) }).ToArray());
    }

    [TestMethod]
    public void TechnicalVersionClassificationUsesCardFilesAndKeepsIncompleteRowsUndimmed()
    {
        const string other = @"C:\DemoMods\other.package";
        var resources = new[] { Resource(1, (A, "same"), (B, "same"), (other, "different")),
            Resource(2, (A, "first"), (B, "second")), Resource(3, (A, "same"), (B, null)) };
        var session = Session(resources);
        var source = session.Cards.First(card => card.Package is not null);
        var card = source with { Files = [new(A, null), new(B, null)],
            Package = source.Package! with { ResourceKeys = resources.Select(resource => resource.Key).ToArray() } };
        var entries = session.TechnicalEntries(card);
        Assert.IsTrue(entries[0].HasIdenticalVersions, "其他组的不同版本不影响本组内相同判定");
        Assert.IsTrue(entries[0].Cells.All(cell => cell.IsIdenticalVersion));
        Assert.IsTrue(entries[1].HasDifferentVersions);
        Assert.IsTrue(entries[1].Cells.All(cell => !cell.IsIdenticalVersion));
        Assert.IsFalse(entries[2].HasIdenticalVersions || entries[2].HasDifferentVersions);
        Assert.IsTrue(entries[2].Cells.All(cell => !cell.IsIdenticalVersion), "未完成比较不能视作版本相同");
    }

    [TestMethod]
    [DataRow(6, 4)]
    [DataRow(4, 15)]
    public Task DifferentVersionsSortFirstAndOnlyIdenticalFileCellsAreDimmed(int resources, int files) => WpfTestHost.RunAsync(async () =>
    {
        var session = Session(Enumerable.Range(0, resources).Select(resource => Resource((ulong)(100 + resource),
            Enumerable.Range(0, files).Select(file => ($@"C:\DemoMods\mixed-{file}.package", (string?)(resource % 2 == 0 ? "same" : $"version-{file}"))).ToArray())).ToArray());
        var card = session.Cards.Single();
        session.ToggleExpanded(card.Key);
        session.ToggleExpanded(card.Key + "/technical");
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, session);
        var rows = model.ConflictRows.Where(row => row.Kind == ConflictRowKind.TechnicalListHeader
            || row.Kind == ConflictRowKind.Technical && !row.IsExpandable).ToArray();
        Assert.AreEqual(resources, rows.Length);
        Assert.IsTrue(rows.Take(resources / 2).All(row => row.TechnicalEntry!.HasDifferentVersions));
        Assert.IsTrue(rows.Skip(resources / 2).All(row => row.TechnicalEntry!.HasIdenticalVersions));
        CollectionAssert.AreEqual(Enumerable.Range(0, resources).Where(index => index % 2 == 1).Concat(
            Enumerable.Range(0, resources).Where(index => index % 2 == 0)).Select(index => $"034AEECB:00000000:{100 + index:X16}").ToArray(),
            rows.Select(row => row.TechnicalEntry!.Identifier).ToArray(), "同类判定内保持原顺序");
        foreach (var row in rows)
        {
            Assert.IsTrue(row.TechnicalEntry!.Cells.Where(cell => cell.IsFile).All(cell => cell.IsIdenticalVersion == row.TechnicalEntry.HasIdenticalVersions));
            Assert.IsTrue(row.TechnicalEntry.Cells.Where(cell => cell.IsType || cell.IsRelated).All(cell => !cell.IsIdenticalVersion));
        }
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        list.ScrollIntoView(rows[0]);
        await WpfTestHost.LayoutAsync(window);
        Save(window, $"technical-version-emphasis-{files}.png");
        list.ScrollIntoView(rows[^1]);
        await WpfTestHost.LayoutAsync(window);
        Save(window, $"technical-version-emphasis-comparison-{files}.png");
    });

    [TestMethod]
    public void RelatedGroupsUseEachTgiAndReplaceStandaloneReferences()
    {
        var session = RelatedSession();
        Assert.IsTrue(session.Cards.Count > 2);
        foreach (var card in session.Cards)
        {
            session.ToggleExpanded(card.Key);
            session.ToggleExpanded(card.Key + "/technical");
        }
        var rows = Rows(session);
        Assert.IsFalse(rows.Any(row => row.Kind == ConflictRowKind.GroupReference));
        Assert.IsTrue(rows.Any(row => row.TechnicalEntry?.RelatedGroups.Count > 2));
        foreach (var header in rows.Where(row => row.Kind == ConflictRowKind.TechnicalHeader))
        {
            var body = rows.Where(row => row.OwnerKey == header.OwnerKey && row.Kind == ConflictRowKind.Technical && !row.IsExpandable).ToArray();
            var hasRelated = body.Any(row => row.TechnicalEntry!.RelatedGroups.Count > 0);
            Assert.AreEqual(hasRelated, header.TechnicalTable!.HasRelatedGroups);
            Assert.AreEqual(hasRelated, header.TechnicalEntry!.Cells.Any(cell => cell.IsRelated));
            Assert.IsTrue(body.All(row => row.TechnicalEntry!.Cells.Any(cell => cell.IsRelated) == hasRelated), "有部分 TGI 关联时应保留整列并保持对齐");
        }
        foreach (var row in rows.Where(row => row.TechnicalEntry is not null && row.Kind is ConflictRowKind.Technical or ConflictRowKind.TechnicalListHeader))
        {
            var entry = row.TechnicalEntry!;
            var expected = session.Cards.Where(card => card.Key != row.OwnerKey && card.ResourceKeys.Any(key => key.Tgi == entry.Identifier)).Select(card => card.Key).ToArray();
            CollectionAssert.AreEquivalent(expected, entry.RelatedGroups.Select(link => link.Key).ToArray());
        }
        Assert.IsTrue(rows.Where(row => row.Kind == ConflictRowKind.CopyDetails).All(row => !row.CopyText.Contains("相关依据", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(12)]
    public Task TableWithoutRelatedGroupsOmitsColumnAndReservedSpace(int files) => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, TechnicalSession(6, files));
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var header = model.ConflictRows.Single(row => row.Kind == ConflictRowKind.TechnicalHeader);
        list.ScrollIntoView(header);
        await WpfTestHost.LayoutAsync(window);
        var table = header.TechnicalTable!;
        Assert.IsFalse(table.HasRelatedGroups);
        foreach (var row in model.ConflictRows.Where(row => row.TechnicalTable == table && row.TechnicalEntry is not null))
        {
            Assert.IsFalse(row.TechnicalEntry!.Cells.Any(cell => cell.IsRelated));
            Assert.AreEqual(files + 1, row.TechnicalEntry.Cells.Count);
        }
        table.HorizontalOffset = table.ScrollableWidth;
        await WpfTestHost.LayoutAsync(window);
        var sticky = Descendants<ContentControl>(window).Single(control => control.Name == "StickyTechnicalContent");
        var scroll = Descendants<ScrollViewer>(sticky).Single();
        Assert.AreEqual(table.ScrollableWidth, scroll.ScrollableWidth, 0.1, "滚动范围不应保留被隐藏的相关组列");
        var cells = Descendants<ItemsControl>(sticky).Single().Items;
        Assert.IsFalse(cells.Cast<ConflictTechnicalCell>().Any(cell => cell.IsRelated));
        Save(window, $"technical-no-related-{files}.png");
    });

    [TestMethod]
    public Task ContinuousNativeWheelMovesContentByRequestedDistanceWithoutRebound() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        WpfTestHost.PlaceOnScreen(window);
        await SetSessionAsync(model, TechnicalSession(100, 12));
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var outer = Descendants<ScrollViewer>(list).First();
        var anchor = model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable).ElementAt(60);
        list.ScrollIntoView(anchor);
        await WpfTestHost.LayoutAsync(window);
        var identifier = Descendants<TextBlock>((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(anchor)).Single(text => text.Name == "Identifier");
        var samples = new List<(TimeSpan Frame, double Offset, double Y)>();
        TimeSpan? frame = null;
        void Frame(object? sender, EventArgs args) => frame = ((RenderingEventArgs)args).RenderingTime;
        void Changed(object sender, ScrollChangedEventArgs args)
        {
            if (!ReferenceEquals(args.OriginalSource, outer) || Math.Abs(args.VerticalChange) < 0.01 || frame is not { } timestamp) return;
            if (list.ItemContainerGenerator.ContainerFromItem(anchor) is ListBoxItem item)
                samples.Add((timestamp, outer.VerticalOffset, item.TranslatePoint(new Point(), window).Y));
        }
        Assert.IsTrue(GetCursorPos(out var previous));
        try
        {
            var point = identifier.PointToScreen(new Point(8, identifier.ActualHeight / 2));
            Assert.IsTrue(SetCursorPos((int)point.X, (int)point.Y));
            Mouse.Synchronize();
            await Task.Delay(100);
            CompositionTarget.Rendering += Frame;
            outer.ScrollChanged += Changed;
            var origin = ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(anchor)).TranslatePoint(new Point(), window).Y;
            for (int count = 0; count < 6; count++)
            {
                mouse_event(0x0800, 0, 0, unchecked((uint)-120), UIntPtr.Zero);
                await Task.Delay(70);
            }
            await Task.Delay(300);
            await WpfTestHost.LayoutAsync(window);
            Console.WriteLine(string.Join("\n", samples.Select(sample => $"frame={sample.Frame.TotalMilliseconds:F2}, offset={sample.Offset:F3}, anchor={sample.Y:F3}")));
            Assert.IsTrue(samples.Count > 5, "连续系统滚轮应呈现多帧移动");
            Assert.IsTrue(samples.Select(sample => sample.Frame).Distinct().Count() > 5, "连续滚动应跨越多个实际渲染帧");
            Assert.IsTrue(samples.Zip(samples.Skip(1)).All(pair => pair.First.Y >= pair.Second.Y), "同向滚动时实际文字不能来回抖动");
            var requested = 6 * (SystemParameters.WheelScrollLines < 0 ? outer.ViewportHeight : SystemParameters.WheelScrollLines * 16d);
            var final = ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(anchor)).TranslatePoint(new Point(), window).Y;
            Assert.AreEqual(requested, origin - final, 0.1, "实际文字只能移动滚轮请求的距离，不能额外追赶虚拟化重估前的坐标");
            Save(window, "technical-continuous-wheel.png");
        }
        finally
        {
            CompositionTarget.Rendering -= Frame;
            outer.ScrollChanged -= Changed;
            SetCursorPos(previous.X, previous.Y);
        }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task TechnicalColumnsKeepExactRangeAndReachEdgesAfterRecycling(bool related) => WpfTestHost.RunAsync(async () =>
    {
        var session = related ? RelatedSession(6) : TechnicalSession(24, 80);
        string? ownerKey = null;
        if (related)
        {
            var card = session.Cards.MaxBy(card => card.Files.Count)!;
            ownerKey = card.Key;
            session.ToggleExpanded(card.Key);
            session.ToggleExpanded(card.Key + "/technical");
        }
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, session);
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var row = model.ConflictRows.First(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable
            && (ownerKey is null || row.OwnerKey == ownerKey));
        var table = row.TechnicalTable!;
        Assert.AreEqual(related, table.HasRelatedGroups);
        foreach (var width in new[] { 920d, 1180d })
        {
            window.Width = width;
            list.ScrollIntoView(row);
            await WpfTestHost.LayoutAsync(window);
            foreach (var fraction in new[] { 0d, 0.5, 1d, 0d, 1d })
            {
                table.HorizontalOffset = table.ScrollableWidth * fraction;
                await WpfTestHost.LayoutAsync(window);
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
                var columns = Descendants<ItemsControl>(container).First(control => control.Items.Count > 0 && control.Items[0] is ConflictTechnicalCell);
                var scroll = Descendants<ScrollViewer>(columns).Single();
                Console.WriteLine($"related={related}, fraction={fraction}, extent={scroll.ExtentWidth}, viewport={scroll.ViewportWidth}, range={scroll.ScrollableWidth}, modelRange={table.ScrollableWidth}");
                Assert.AreEqual(table.ScrollableWidth, scroll.ScrollableWidth, 0.01, "已知列宽的滚动范围不能因容器回收而改变");
                Assert.AreEqual(table.HorizontalOffset, scroll.HorizontalOffset, 0.01, "正文应到达共享滚动位置");
                var edgeIndex = fraction == 1 ? columns.Items.Count - 1 : 0;
                if (fraction is 0 or 1)
                {
                    var edge = columns.ItemContainerGenerator.ContainerFromIndex(edgeIndex) as FrameworkElement;
                    Assert.IsNotNull(edge, "左右端的真实列应被实现");
                    var bounds = new Rect(edge.TranslatePoint(new Point(), scroll), edge.RenderSize);
                    Assert.IsTrue(bounds.Left >= -0.01 && bounds.Right <= scroll.ViewportWidth + 0.01, "端点列应完整进入视口");
                }
                if (fraction == 0.5)
                {
                    var partial = Enumerable.Range(0, columns.Items.Count)
                        .Select(index => columns.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement)
                        .First(cell => cell is not null);
                    partial!.BringIntoView();
                    await WpfTestHost.LayoutAsync(window);
                    Assert.IsTrue(partial.TranslatePoint(new Point(), scroll).X >= -0.01, "BringIntoView应将部分可见列完整带入视口");
                }
                var realized = Enumerable.Range(0, columns.Items.Count).Where(index => columns.ItemContainerGenerator.ContainerFromIndex(index) is not null).ToArray();
                Assert.IsTrue(realized.Length < columns.Items.Count, $"横向列必须继续虚拟化，不能全量实现来掩盖范围估算：{string.Join(",", realized)}");
                var outer = Descendants<ScrollViewer>(list).First();
                Assert.AreEqual(0d, outer.ScrollableWidth, $"内层不能让外层横滚：extent={outer.ExtentWidth}, viewport={outer.ViewportWidth}");
            }
            var realizedRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
            var columnsPanel = Descendants<TechnicalColumnsPanel>(realizedRow).Single();
            columnsPanel.BringIndexIntoViewPublic(0);
            await WpfTestHost.LayoutAsync(window);
            Assert.AreEqual(0d, table.HorizontalOffset);
            var rowColumns = Descendants<ItemsControl>(realizedRow).First(control => control.Items.Count > 0 && control.Items[0] is ConflictTechnicalCell);
            columnsPanel.BringIndexIntoViewPublic(rowColumns.Items.Count - 1);
            await WpfTestHost.LayoutAsync(window);
            Assert.AreEqual(table.ScrollableWidth, table.HorizontalOffset, 0.01, "未实现的末列也应能按索引带入视口");
            list.ScrollIntoView(model.ConflictRows[0]);
            await WpfTestHost.LayoutAsync(window);
            list.ScrollIntoView(row);
            await WpfTestHost.LayoutAsync(window);
            Save(window, $"technical-exact-range-{related}-{width}.png");
        }
    });

    [TestMethod]
    public Task TechnicalColumnsClampOffsetWhenViewportGrows() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        window.MinWidth = 1;
        window.Width = 760;
        await SetSessionAsync(model, TechnicalSession(24, 2));
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var row = model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable).ElementAt(12);
        list.ScrollIntoView(row);
        await WpfTestHost.LayoutAsync(window);
        var table = row.TechnicalTable!;
        Assert.IsTrue(table.ScrollableWidth > 0);
        table.HorizontalOffset = table.ScrollableWidth;
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(table.HorizontalOffset > 0);
        window.Width = 1180;
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(0d, table.ScrollableWidth);
        Assert.AreEqual(0d, table.HorizontalOffset, "视口放大到容纳全部列时应清除旧偏移");
        var header = Descendants<ContentControl>(window).Single(control => control.Name == "StickyTechnicalContent");
        var headerScroll = Descendants<ScrollViewer>(header).Single();
        Assert.AreEqual(0d, headerScroll.HorizontalOffset);
        Assert.IsNull(headerScroll.OpacityMask, "无需横滚时不能残留左侧渐隐");
        Assert.IsFalse(Descendants<ScrollBar>(window).Single(control => control.Name == "TechnicalTableScrollBar").IsVisible);
        window.Width = 760;
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(table.ScrollableWidth > 0);
        Assert.AreEqual(0d, table.HorizontalOffset);
        Save(window, "technical-offset-after-resize.png");
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task SmoothWheelUsesIntermediatePixelsAccumulatesAndReverses(bool insideTable) => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, TechnicalSession(100, 12));
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var outer = Descendants<ScrollViewer>(list).First();
        var row = insideTable ? model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable).ElementAt(40)
            : model.ConflictRows.First(row => row.Kind == ConflictRowKind.File);
        list.ScrollIntoView(row);
        await WpfTestHost.LayoutAsync(window);
        var table = model.ConflictRows.Single(row => row.Kind == ConflictRowKind.TechnicalHeader).TechnicalTable!;
        table.HorizontalOffset = table.ScrollableWidth / 3;
        await WpfTestHost.LayoutAsync(window);
        var horizontal = table.HorizontalOffset;
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
        UIElement target = insideTable ? Descendants<TextBlock>(container).First(text => text.Name == "Version" && text.DataContext is ConflictTechnicalCell { IsFile: true }) : container;
        var positions = new List<double>();
        var frames = new List<(double Offset, double Y)>();
        void Track(object? sender, EventArgs args) => frames.Add((outer.VerticalOffset, container.TranslatePoint(new Point(), window).Y));
        outer.ScrollChanged += (_, args) => { if (ReferenceEquals(args.OriginalSource, outer) && args.VerticalChange != 0) positions.Add(outer.VerticalOffset); };
        var before = outer.VerticalOffset;
        var origin = container.TranslatePoint(new Point(), window).Y;
        CompositionTarget.Rendering += Track;
        try
        {
            SendWheel(target, -120);
            Assert.AreEqual(before, outer.VerticalOffset, "滚轮不应立即跳到目标位置");
            await Task.Delay(250);
            await WpfTestHost.LayoutAsync(window);
        }
        finally { CompositionTarget.Rendering -= Track; }
        Console.WriteLine($"{(insideTable ? "表内" : "外层")}: {string.Join(", ", positions.Select(position => position.ToString("F2")))}");
        Console.WriteLine(string.Join("; ", frames.Select(frame => $"offset={frame.Offset:F2}, Y={frame.Y:F2}")));
        Assert.IsTrue(positions.Distinct().Count() > 2, "实际窗口应呈现多个中间滚动位置");
        Assert.AreSame(row, container.DataContext, "用于追踪实际位置的资源行不能被回收复用");
        Assert.IsTrue(frames.Select(frame => frame.Y).Distinct().Count() > 2, "实际内容应在多个渲染帧中移动");
        Assert.IsTrue(frames.Zip(frames.Skip(1)).All(pair => pair.First.Y >= pair.Second.Y), "单次向下缓动时实际内容不能反向跳动");
        var requested = SystemParameters.WheelScrollLines < 0 ? outer.ViewportHeight : SystemParameters.WheelScrollLines * 16d;
        Assert.AreEqual(requested, origin - container.TranslatePoint(new Point(), window).Y, 0.1, "实际内容应只移动本次滚轮请求的距离");
        var single = outer.VerticalOffset - before;
        Assert.IsTrue(single > 0);
        outer.ScrollToVerticalOffset(before);
        await WpfTestHost.LayoutAsync(window);
        for (int count = 0; count < 3; count++) SendWheel(list, -120);
        await Task.Delay(250);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(outer.VerticalOffset - before > single, "连续同向滚轮应累加距离");
        for (int count = 0; count < 3; count++) SendWheel(list, -120);
        await Task.Delay(40);
        await WpfTestHost.LayoutAsync(window);
        var turningPoint = outer.VerticalOffset;
        SendWheel(list, 120);
        await Task.Delay(250);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(outer.VerticalOffset < turningPoint, "反向滚轮应从当前位置响应，不应继续追赶旧目标");
        Assert.AreEqual(horizontal, table.HorizontalOffset);
        if (insideTable)
        {
            var sticky = Descendants<ContentControl>(window).Single(control => control.Name == "StickyTechnicalContent");
            var header = Descendants<ItemsControl>(sticky).Single();
            foreach (var body in Descendants<ItemsControl>(list).Where(control => control.DataContext is ConflictResultRow { Kind: ConflictRowKind.Technical, IsExpandable: false }))
                for (int index = 0; index < body.Items.Count; index++)
                    if (body.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement cell
                        && header.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement headerCell)
                        Assert.AreEqual(headerCell.TranslatePoint(new Point(), window).X, cell.TranslatePoint(new Point(), window).X, 0.1, "像素滚动回收后，正文版本仍应与对应文件表头对齐");
        }
        Save(window, $"technical-smooth-wheel-{insideTable}.png");
    });

    [TestMethod]
    [DataRow("collapse")]
    [DataRow("filter")]
    [DataRow("home")]
    public Task SmoothWheelStopsWhenResultsChangeOrPageIsHidden(string action) => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, TechnicalSession(100, 12));
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        list.ScrollIntoView(model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable).ElementAt(40));
        await WpfTestHost.LayoutAsync(window);
        var outer = Descendants<ScrollViewer>(list).First();
        for (int count = 0; count < 3; count++) SendWheel(list, -120);
        if (action == "collapse") model.ToggleConflictExpandedCommand.Execute(model.ConflictRows.Single(row => row.IsTechnicalToggle));
        else if (action == "filter") model.SelectConflictFilterCommand.Execute("same");
        else model.GoHomeCommand.Execute(null);
        await model.ConflictRowsRefreshTask;
        await WpfTestHost.LayoutAsync(window);
        var stopped = outer.VerticalOffset;
        await Task.Delay(250);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(stopped, outer.VerticalOffset, "内容或页面改变后不能继续执行旧滚动");
    });

    [TestMethod]
    public Task NativeVerticalThumbDragCancelsWheelEasingWithoutRebound() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        WpfTestHost.PlaceOnScreen(window);
        await SetSessionAsync(model, TechnicalSession(100, 12));
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        list.ScrollIntoView(model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable).ElementAt(40));
        await WpfTestHost.LayoutAsync(window);
        var outer = Descendants<ScrollViewer>(list).First();
        var bar = Descendants<ScrollBar>(outer).First(control => control.Orientation == Orientation.Vertical && control.IsVisible);
        var thumb = (Thumb)bar.Template.FindName("ScrollThumb", bar);
        Assert.IsTrue(GetCursorPos(out var previous));
        try
        {
            var point = thumb.PointToScreen(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2));
            Assert.IsTrue(SetCursorPos((int)point.X, (int)point.Y));
            Mouse.Synchronize();
            await Task.Delay(100);
            for (int count = 0; count < 3; count++) SendWheel(list, -120);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            for (int attempt = 0; attempt < 40 && !thumb.IsMouseCaptured; attempt++)
            {
                await Task.Delay(50);
                await WpfTestHost.LayoutAsync(window);
            }
            Assert.IsTrue(thumb.IsMouseCaptured);
            var before = outer.VerticalOffset;
            Assert.IsTrue(SetCursorPos((int)point.X, (int)point.Y + 50));
            Mouse.Synchronize();
            await Task.Delay(100);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(outer.VerticalOffset > before, "拖动纵向滑块应直接改变位置");
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            await WpfTestHost.LayoutAsync(window);
            var dragged = outer.VerticalOffset;
            await Task.Delay(250);
            await WpfTestHost.LayoutAsync(window);
            Assert.AreEqual(dragged, outer.VerticalOffset, "拖动后不能被未完成的滚轮缓动拉回");
            Save(window, "technical-native-vertical-drag.png");
        }
        finally { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); SetCursorPos(previous.X, previous.Y); }
    });

    private static void SendWheel(UIElement target, int delta) => target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = target });

    [TestMethod]
    public Task HeaderEdgeFadesBodyRowsAndClearsMasksWhenRecycledOrCollapsed() => WpfTestHost.RunAsync(async () =>
    {
        var session = TechnicalSession(80, 12);
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        window.Left = -10000;
        window.Top = -10000;
        await SetSessionAsync(model, session);
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        var row = model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable).ElementAt(40);
        list.ScrollIntoView(row);
        await WpfTestHost.LayoutAsync(window);
        var outer = Descendants<ScrollViewer>(list).First();
        var sticky = Descendants<Border>(window).Single(control => control.Name == "StickyTechnicalHeader");
        var viewport = Descendants<Grid>(window).Single(control => control.Name == "TechnicalViewport");
        var boundary = sticky.TranslatePoint(new Point(0, sticky.ActualHeight), viewport).Y;
        var candidate = Descendants<ListBoxItem>(list).First(item => item.DataContext is ConflictResultRow { Kind: ConflictRowKind.Technical, IsExpandable: false }
            && item.TranslatePoint(new Point(), viewport).Y > boundary);
        var candidateRow = (ConflictResultRow)candidate.DataContext;
        var text = Descendants<TextBlock>(candidate).Single(text => text.Name == "Identifier");
        var offset = outer.VerticalOffset;
        var textTop = text.TranslatePoint(new Point(), viewport).Y;
        outer.ScrollToVerticalOffset(offset + textTop - boundary);
        await WpfTestHost.LayoutAsync(window);
        candidate = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(candidateRow);
        Assert.IsInstanceOfType<LinearGradientBrush>(candidate.OpacityMask);
        var mask = (LinearGradientBrush)candidate.OpacityMask;
        Assert.IsTrue(mask.EndPoint.Y > mask.StartPoint.Y);
        var start = candidate.TranslatePoint(mask.StartPoint, viewport).Y;
        var end = candidate.TranslatePoint(mask.EndPoint, viewport).Y;
        boundary = sticky.TranslatePoint(new Point(0, sticky.ActualHeight), viewport).Y;
        Assert.AreEqual(boundary, start, 0.1, "正文透明边界应接在表头下沿");
        text = Descendants<TextBlock>(candidate).Single(text => text.Name == "Identifier");
        var textCenter = text.TranslatePoint(new Point(0, text.ActualHeight / 2), viewport).Y;
        Assert.IsTrue(textCenter > start && textCenter < end, "实际文字应位于透明过渡区内");
        Assert.IsNotNull(candidate.InputHitTest(new Point(candidate.ActualWidth / 2, candidate.ActualHeight / 2)), "透明遮罩不得阻断正文命中");
        row.TechnicalTable!.HorizontalOffset = row.TechnicalTable.ScrollableWidth / 3;
        await WpfTestHost.LayoutAsync(window);
        Assert.IsNotNull(Descendants<ScrollViewer>(candidate).Single().OpacityMask, "顶部和左侧渐隐应同时保留");
        Save(window, "technical-top-fade.png", viewport);
        Save(window, "technical-top-fade-window.png");
        var masked = Descendants<ListBoxItem>(list).Where(item => item.OpacityMask is not null).ToArray();
        Assert.IsTrue(masked.Length > 0);
        list.ScrollIntoView(model.ConflictRows[0]);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(masked.All(item => item.OpacityMask is null), "行回收后应清除渐隐，不能带到其他内容");
        list.ScrollIntoView(row);
        await WpfTestHost.LayoutAsync(window);
        masked = Descendants<ListBoxItem>(list).Where(item => item.OpacityMask is not null).ToArray();
        Assert.IsTrue(masked.Length > 0);
        model.ToggleConflictExpandedCommand.Execute(model.ConflictRows.Single(row => row.IsTechnicalToggle));
        await model.ConflictRowsRefreshTask;
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(masked.All(item => item.OpacityMask is null));
        Assert.IsFalse(sticky.IsVisible);
    });

    [TestMethod]
    public Task FloatingBarKeepsScrollTransparencyAndTracksHoverAndDrag() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        WpfTestHost.PlaceOnScreen(window);
        await SetSessionAsync(model, TechnicalSession(100, 12));
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var row = model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable).ElementAt(40);
        list.ScrollIntoView(row);
        await WpfTestHost.LayoutAsync(window);
        var outer = Descendants<ScrollViewer>(list).First();
        var floating = Descendants<Border>(window).Single(control => control.Name == "FloatingTechnicalScroll");
        var bar = Descendants<ScrollBar>(floating).Single();
        var thumb = (Thumb)bar.Template.FindName("ScrollThumb", bar);
        Assert.IsTrue(GetCursorPos(out var previous));
        try
        {
            await Move(window, new Point(8, 8));
            var resting = floating.Opacity;
            Assert.IsTrue(resting > 0 && resting < 1);
            outer.ScrollToVerticalOffset(outer.VerticalOffset + 1);
            await WpfTestHost.LayoutAsync(window);
            await Task.Delay(200);
            Assert.AreEqual(resting, floating.Opacity, "上下滚动不应将滚动条唤成不透明");
            var viewport = Descendants<Grid>(window).Single(control => control.Name == "TechnicalViewport");
            var viewportBounds = new Rect(new Point(), viewport.RenderSize);
            var barBounds = new Rect(bar.TranslatePoint(new Point(), viewport), bar.RenderSize);
            Assert.IsTrue(barBounds.Left > viewportBounds.Left && barBounds.Right < viewportBounds.Right, "横向条两端应留出空隙");
            Assert.IsTrue(Descendants<ListBoxItem>(list).Any(item => item.OpacityMask is not null), "表头下沿应让正文内容本身渐隐");
            Save(window, "technical-header-fade-resting.png");
            await Move(thumb, new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2));
            var hovered = floating.Opacity;
            Assert.IsTrue(hovered > resting && hovered < 1, "悬停时应更清晰，同时保留半透明效果");
            Save(window, "technical-scrollbar-hover.png");
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            for (int attempt = 0; attempt < 40 && !bar.IsMouseCaptureWithin; attempt++)
            {
                await Task.Delay(50);
                await WpfTestHost.LayoutAsync(window);
            }
            Assert.IsTrue(bar.IsMouseCaptureWithin, "真实按下滑块后应捕获鼠标");
            var offset = row.TechnicalTable!.HorizontalOffset;
            var point = thumb.PointToScreen(new Point(thumb.ActualWidth / 2 + 50, thumb.ActualHeight + 50));
            Assert.IsTrue(SetCursorPos((int)point.X, (int)point.Y));
            Mouse.Synchronize();
            await Task.Delay(200);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(row.TechnicalTable.HorizontalOffset > offset, "实际拖动应移动文件列");
            Assert.AreEqual(hovered, floating.Opacity, "拖出滚动条后仍应保持拖动透明度");
            outer.ScrollToVerticalOffset(outer.VerticalOffset + 1);
            await WpfTestHost.LayoutAsync(window);
            await Task.Delay(200);
            Assert.AreEqual(hovered, floating.Opacity, "拖动时上下滚动不应覆盖交互透明度");
            Save(window, "technical-scrollbar-drag.png");
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            await Move(window, new Point(8, 8));
            Assert.IsFalse(bar.IsMouseCaptureWithin);
            Assert.AreEqual(resting, floating.Opacity, "松开并移出后应恢复默认半透明");
        }
        finally
        {
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos(previous.X, previous.Y);
        }

        async Task Move(FrameworkElement target, Point point)
        {
            var screen = target.PointToScreen(point);
            Assert.IsTrue(SetCursorPos((int)screen.X, (int)screen.Y));
            Mouse.Synchronize();
            await Task.Delay(250);
            await WpfTestHost.LayoutAsync(window);
        }
    });

    [TestMethod]
    public Task HomeNavigationLoadsTechnicalScrollingOnlyWhenResultsBecomeVisible() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        var window = OpenWindow(model);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsFalse(Descendants<ScrollBar>(window).Any(control => control.Name == "TechnicalTableScrollBar" && control.IsVisible));
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        await SetSessionAsync(model, TechnicalSession(24, 12));
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var body = model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable).ElementAt(12);
        list.ScrollIntoView(body);
        await WpfTestHost.LayoutAsync(window);
        var bar = Descendants<ScrollBar>(window).Single(control => control.Name == "TechnicalTableScrollBar");
        Assert.IsTrue(bar.IsVisible);
        Assert.AreSame(body.TechnicalTable, bar.DataContext);
        bar.SetCurrentValue(RangeBase.ValueProperty, bar.Maximum);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(body.TechnicalTable!.HorizontalOffset > 0);
    });

    [TestMethod]
    public Task StickyHeaderAndFloatingBarFollowVisibleTableAndReleaseAfterCollapse() => WpfTestHost.RunAsync(async () =>
    {
        var session = Session(Enumerable.Range(0, 2).SelectMany(group => Enumerable.Range(0, 24).Select(resource =>
            Resource((ulong)(10000 + group * 100 + resource), Enumerable.Range(0, 12).Select(file =>
                ($@"C:\DemoMods\group-{group}\file-{file}.package", (string?)$"content-{file}")).ToArray()))).ToArray());
        Assert.AreEqual(2, session.Cards.Count);
        foreach (var card in session.Cards) { session.ToggleExpanded(card.Key); session.ToggleExpanded(card.Key + "/technical"); }
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        window.Left = -10000;
        window.Top = -10000;
        await SetSessionAsync(model, session);
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var sticky = Descendants<ContentControl>(window).Single(control => control.Name == "StickyTechnicalContent");
        var bar = Descendants<ScrollBar>(window).Single(control => control.Name == "TechnicalTableScrollBar");
        foreach (var card in session.Cards)
        {
            var body = model.ConflictRows.Where(row => row.OwnerKey == card.Key && row.Kind == ConflictRowKind.Technical && !row.IsExpandable).ElementAt(12);
            list.ScrollIntoView(body);
            await WpfTestHost.LayoutAsync(window);
            Assert.AreSame(body.TechnicalTable!.HeaderRow, sticky.Content);
            Assert.IsTrue(sticky.IsVisible && bar.IsVisible);
            Assert.AreSame(body.TechnicalTable, bar.DataContext);
            bar.SetCurrentValue(RangeBase.ValueProperty, bar.Maximum);
            await WpfTestHost.LayoutAsync(window);
            var headerScroll = Descendants<ScrollViewer>(sticky).Single();
            Assert.AreEqual(body.TechnicalTable.HorizontalOffset, headerScroll.HorizontalOffset, 0.01);
            Assert.IsNotNull(headerScroll.OpacityMask, "滚过的文件列边缘应渐隐");
            Save(window, $"technical-sticky-{card.GroupIndex}.png");
            var floating = Descendants<Border>(window).Single(control => control.Name == "FloatingTechnicalScroll");
            await Task.Delay(200);
            var resting = floating.Opacity;
            var outer = Descendants<ScrollViewer>(list).First();
            outer.ScrollToVerticalOffset(outer.VerticalOffset + 1);
            await WpfTestHost.LayoutAsync(window);
            await Task.Delay(200);
            Assert.AreEqual(resting, floating.Opacity, "上下滚动应保持半透明状态");
            var footer = model.ConflictRows.Single(row => row.OwnerKey == card.Key && row.Kind == ConflictRowKind.TechnicalScroll);
            list.ScrollIntoView(footer);
            await WpfTestHost.LayoutAsync(window);
            var footerContainer = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(footer);
            var floatingBottom = floating.TranslatePoint(new Point(0, floating.ActualHeight), list).Y;
            var footerBottom = footerContainer.TranslatePoint(new Point(0, footerContainer.ActualHeight), list).Y;
            var viewport = Descendants<Grid>(window).Single(control => control.Name == "TechnicalViewport");
            var dpi = VisualTreeHelper.GetDpi(window);
            var geometry = $"group={card.GroupIndex}, dpi={dpi.DpiScaleX:R}x{dpi.DpiScaleY:R}, floatingBottom={floatingBottom:R}, footerBottom={footerBottom:R}, delta={floatingBottom - footerBottom:R}, "
                + $"floatingViewportBottom={floating.TranslatePoint(new Point(0, floating.ActualHeight), viewport).Y:R}, footerViewportBottom={footerContainer.TranslatePoint(new Point(0, footerContainer.ActualHeight), viewport).Y:R}, "
                + $"viewport={viewport.ActualWidth:R}x{viewport.ActualHeight:R}, outerViewport={outer.ViewportWidth:R}x{outer.ViewportHeight:R}, offset={outer.VerticalOffset:R}";
            Console.WriteLine(geometry);
            // 两条视觉祖先路径的坐标变换会留下约 1e-13 DIP 的浮点残差。
            Assert.IsTrue(floatingBottom <= footerBottom + 1e-10, "悬浮滚动条不得进入表格后的正文；" + geometry);
            list.ScrollIntoView(model.ConflictRows[0]);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsNull(sticky.Content, "没有可见表格时应解除吸附");
            list.ScrollIntoView(body);
            await WpfTestHost.LayoutAsync(window);
            Assert.AreSame(body.TechnicalTable.HeaderRow, sticky.Content, "行回收后仍应跟随正确表格");
        }
        foreach (var toggle in model.ConflictRows.Where(row => row.IsTechnicalToggle).ToArray())
        {
            model.ToggleConflictExpandedCommand.Execute(toggle);
            await model.ConflictRowsRefreshTask;
        }
        await WpfTestHost.LayoutAsync(window);
        Assert.IsNull(sticky.Content);
        Assert.IsFalse(bar.IsVisible);
        Assert.IsNull(bar.DataContext);
    });

    [TestMethod]
    public Task RelatedGroupLinksWrapWithoutOverlappingRowsAndNavigate() => WpfTestHost.RunAsync(async () =>
    {
        var session = RelatedSession();
        var card = session.Cards.First(card => card.Type == "suspected");
        session.ToggleExpanded(card.Key);
        session.ToggleExpanded(card.Key + "/technical");
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, session);
        var row = model.ConflictRows.First(row => row.OwnerKey == card.Key && row.Kind == ConflictRowKind.Technical && row.TechnicalEntry?.RelatedGroups.Count > 2);
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        list.ScrollIntoView(row);
        await WpfTestHost.LayoutAsync(window);
        row.TechnicalTable!.HorizontalOffset = row.TechnicalTable.ScrollableWidth;
        await WpfTestHost.LayoutAsync(window);
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
        var links = Descendants<Button>(container).Where(button => button.DataContext is ConflictGroupLink).ToArray();
        Assert.AreEqual(row.TechnicalEntry!.RelatedGroups.Count, links.Length);
        foreach (var link in links)
        {
            var bounds = new Rect(link.TranslatePoint(new Point(), container), link.RenderSize);
            Assert.IsTrue(bounds.Top >= 0 && bounds.Bottom <= container.ActualHeight, "换行后的相关组不得超出当前资源行");
            Assert.IsNotNull(link.InputHitTest(new Point(link.ActualWidth / 2, link.ActualHeight / 2)));
        }
        Assert.IsTrue(links.Select(link => link.TranslatePoint(new Point(), container).Y).Distinct().Count() > 1);
        Save(window, "technical-related-groups-wrapped.png");
        var target = (ConflictGroupLink)links[0].DataContext;
        ((IInvokeProvider)new ButtonAutomationPeer(links[0]).GetPattern(PatternInterface.Invoke)!).Invoke();
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await model.ConflictRowsRefreshTask;
        await WpfTestHost.LayoutAsync(window);
        var destination = model.ConflictRows.Single(row => row.Key == target.Key);
        Assert.AreEqual(target.GroupIndex, destination.GroupIndex);
        Assert.IsTrue(destination.IsExpanded);
    });

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
            Assert.IsTrue(rows.Where(row => row.TechnicalEntry is not null).All(row => row.TechnicalEntry!.Cells.Count(cell => cell.IsFile) == files));
            Assert.IsTrue(rows.Where(row => row.TechnicalEntry is not null).All(row => row.TechnicalEntry!.Cells.First().IsType && !row.TechnicalEntry.Cells.Any(cell => cell.IsRelated)));
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
                var target = Descendants<TextBlock>(container).First(text => text.Name == name && text.IsVisible
                    && (name == "Identifier" || text.DataContext is ConflictTechnicalCell { IsFile: true }));
                var vertical = outer.VerticalOffset;
                var horizontal = row.TechnicalTable!.HorizontalOffset;
                Wheel(target, -120);
                await Task.Delay(200);
                await WpfTestHost.LayoutAsync(window);
                Assert.IsTrue(outer.VerticalOffset > vertical, "鼠标在表头、版本或固定编号上时，向下滚轮应滚动外层结果");
                vertical = outer.VerticalOffset;
                Wheel(target, 120);
                await Task.Delay(200);
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
        WpfTestHost.PlaceOnScreen(window);
        await SetSessionAsync(model, TechnicalSession(100, files));
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var outer = Descendants<ScrollViewer>(list).Single(control => control.Name != "TechnicalColumnsScroll");
        var row = model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable).ElementAt(40);
        list.ScrollIntoView(row);
        await WpfTestHost.LayoutAsync(window);
        outer.ScrollToVerticalOffset(outer.VerticalOffset + outer.ViewportHeight / 3);
        await WpfTestHost.LayoutAsync(window);
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
        var version = Descendants<TextBlock>(container).First(text => text.Name == "Version" && text.IsVisible && text.DataContext is ConflictTechnicalCell { IsFile: true });
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
            ToolTip? tooltip = null;
            for (int attempt = 0; attempt < 40 && tooltip is null; attempt++)
            {
                await Task.Delay(50);
                await WpfTestHost.LayoutAsync(window);
                tooltip = PresentationSource.CurrentSources.Cast<PresentationSource>()
                    .Select(source => source.RootVisual).OfType<DependencyObject>().SelectMany(Descendants<ToolTip>)
                    .FirstOrDefault(candidate => candidate.IsOpen && Equals(candidate.Content, version.ToolTip));
            }
            Assert.IsNotNull(tooltip, "证据提示框应实际打开后再验证滚轮");
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
        var bar = Descendants<ScrollBar>(window).Single(control => control.Name == "TechnicalTableScrollBar");
        Assert.IsTrue(bar.IsVisible);
        Assert.IsTrue(bar.Maximum > 0);
        var identifiers = Descendants<TextBlock>(list).Where(text => text.Name == "Identifier").ToArray();
        Assert.IsTrue(identifiers.Length > 0);
        var positions = identifiers.Select(text => text.TranslatePoint(new Point(), window).X).ToArray();
        var types = Descendants<TextBlock>(list).Where(text => text.Name == "Version" && text.DataContext is ConflictTechnicalCell { IsType: true }).ToArray();
        var typePositions = types.Select(text => text.TranslatePoint(new Point(), window).X).ToArray();
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
        Assert.IsTrue(types.Where((text, index) => text.TranslatePoint(new Point(), window).X < typePositions[index]).Any(), "种类应随文件区域滚动，只有 TGI 固定");
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
        var columnPositions = HeaderPositions();
        list.ScrollIntoView(model.ConflictRows[0]);
        await WpfTestHost.LayoutAsync(window);
        list.ScrollIntoView(scrollRow);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(offset, scrollRow.TechnicalTable.HorizontalOffset, "回收后应保留本表格的滚动目标");
        var restoredPositions = HeaderPositions();
        CollectionAssert.AreEqual(columnPositions.Select(cell => cell.Index).ToArray(), restoredPositions.Select(cell => cell.Index).ToArray());
        for (int index = 0; index < columnPositions.Length; index++)
            Assert.AreEqual(columnPositions[index].X, restoredPositions[index].X, 0.01, "回收后文件列的屏幕位置不应改变");
        CheckColumnAlignment();

        (int Index, double X)[] HeaderPositions()
        {
            var headerControl = Descendants<ItemsControl>(list).Single(control => control.DataContext is ConflictResultRow { Kind: ConflictRowKind.TechnicalHeader });
            var scroll = Descendants<ScrollViewer>(headerControl).Single();
            var left = scroll.TranslatePoint(new Point(), window).X;
            var right = left + scroll.ActualWidth;
            return Enumerable.Range(0, headerControl.Items.Count)
                .Where(index => headerControl.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement)
                .Select(index => (Index: index, Cell: (FrameworkElement)headerControl.ItemContainerGenerator.ContainerFromIndex(index)))
                .Where(item => item.Cell.TranslatePoint(new Point(), window).X + item.Cell.ActualWidth > left + 0.01
                    && item.Cell.TranslatePoint(new Point(), window).X < right - 0.01)
                .Select(item => (item.Index, item.Cell.TranslatePoint(new Point(), window).X)).ToArray();
        }

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
        foreach (var width in new[] { 1180, 1380 })
        {
            window.Width = width;
            list.ScrollIntoView(header);
            await WpfTestHost.LayoutAsync(window);
            Assert.AreEqual(0d, header.TechnicalTable!.ScrollableWidth);
            var names = Descendants<TextBlock>(list).Where(text => text.DataContext is ConflictTechnicalCell { IsFile: true, FileName.Length: > 0 } cell && text.Text == cell.FileName).ToArray();
            Assert.AreEqual(2, names.Length);
            Assert.IsTrue(names.All(text => text.IsVisible));
            Assert.IsFalse(Descendants<ScrollBar>(window).Any(bar => bar.Name == "TechnicalTableScrollBar" && bar.IsVisible));
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
    [TestMethod]
    [DataRow(2)]
    [DataRow(15)]
    public Task FloatingCollapseRemainsClickableAndClosesItsTechnicalDetails(int files) => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, TechnicalSession(files == 15 ? 3 : 100, files));
        var list = Descendants<ListBox>(window).Single(control => control.Name == "ConflictRowsList");
        var toggle = model.ConflictRows.Single(row => row.IsTechnicalToggle);
        list.ScrollIntoView(toggle);
        await WpfTestHost.LayoutAsync(window);
        var inline = Descendants<Button>(list).Single(button => button.Name == "TechnicalToggleButton");
        Assert.AreEqual("收起", inline.Content);
        var ownerKey = toggle.OwnerKey;
        var details = model.ConflictRows.Where(row => row.OwnerKey == ownerKey &&
            (row.Kind == ConflictRowKind.Technical && !row.IsExpandable || row.Kind == ConflictRowKind.TechnicalListFile)).ToArray();
        list.ScrollIntoView(details[details.Length / 2]);
        await WpfTestHost.LayoutAsync(window);
        var floating = Descendants<Button>(window).Single(button => button.Name == "FloatingTechnicalCollapse");
        Assert.IsTrue(floating.IsVisible);
        Assert.AreEqual(ownerKey, ((ConflictResultRow)floating.DataContext).OwnerKey);
        var center = floating.TranslatePoint(new Point(floating.ActualWidth / 2, floating.ActualHeight / 2), window);
        DependencyObject? hit = window.InputHitTest(center) as DependencyObject;
        while (hit is not null && !ReferenceEquals(hit, floating)) hit = VisualTreeHelper.GetParent(hit);
        Assert.AreSame(floating, hit, "浮动收起必须在表头上方接收点击");
        Save(window, $"technical-floating-collapse-{files}.png");
        var output = Environment.GetEnvironmentVariable("SIMS4_CONFLICT_REVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(output))
        {
            var root = (FrameworkElement)window.Content;
            var region = new Rect(floating.TranslatePoint(new Point(-8, -8), root),
                new Size(floating.ActualWidth + 16, floating.ActualHeight + 16));
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
                drawing.DrawRectangle(new VisualBrush(root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = region, Stretch = Stretch.Fill },
                    null, new Rect(region.Size));
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(region.Width), (int)Math.Ceiling(region.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, $"technical-floating-collapse-button-{files}.png"));
            encoder.Save(stream);
        }
        floating.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await model.ConflictRowsRefreshTask;
        await WpfTestHost.LayoutAsync(window);
        Assert.IsFalse(model.ConflictRows.Single(row => row.IsTechnicalToggle && row.OwnerKey == ownerKey).IsExpanded);
        Assert.IsTrue(model.ConflictRows.Any(row => row.Key == ownerKey));
        Assert.IsFalse(model.ConflictRows.Any(row => row.OwnerKey == ownerKey && row.TechnicalEntry is not null));
        Assert.IsFalse(floating.IsVisible);
        list.ScrollIntoView(model.ConflictRows.Single(row => row.IsTechnicalToggle && row.OwnerKey == ownerKey));
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual("技术详情", Descendants<Button>(list).Single(button => button.Name == "TechnicalToggleButton").Content);
    });

}
