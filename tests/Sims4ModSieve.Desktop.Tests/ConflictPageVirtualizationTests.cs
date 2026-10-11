using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictResultSessionTests;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ConflictPageVirtualizationTests
{
    [TestMethod]
    public Task ExpandedProblemsRangesAndSourcesStayBoundedAndSurviveRecycling() => WpfTestHost.RunAsync(async () =>
    {
        var issues = Enumerable.Range(0, 500).Select(index => new ScriptArchiveIssue(ScriptArchiveIssueCode.EntryCorrupt,
            ScriptArchiveIssueStage.Entry, $@"C:\Mods\file-{index:D4}.ts4script", "ignored", EntryName: "initial.pyc"))
            .Concat(Enumerable.Range(0, 10000).Select(index => new ScriptArchiveIssue(ScriptArchiveIssueCode.EntrySizeExceedsLimit,
                ScriptArchiveIssueStage.Entry, @"C:\Mods\file-0000.ts4script", "ignored", EntryName: $"module-{index:D5}.pyc"))).ToArray();
        var ranges = Enumerable.Range(0, 500).Select(index => new PackageConflictScanIssue("source-unavailable",
            PackageConflictScanIssueStage.SourceValidation, $@"C:\Offline\folder-{index:D4}", "ignored")).ToArray();
        var session = new ConflictResultSession();
        session.Replace(ConflictResultSessionTests.Packages(issues: ranges), Contents(), Scripts(issues: issues));
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        for (var index = 0; index < 500; index++) model.Sources.Add(new ScanSourceViewModel($@"C:\Sources\source-{index:D4}"));
        var window = OpenWindow(model);
        window.Width = 920;
        window.Height = 640;
        await SetSessionAsync(model, session, "problems");
        var list = Descendants<ListBox>(window).Single(list => list.Name == "ConflictRowsList");
        Assert.AreEqual(1000, model.ConflictRows.Count);
        await CheckScrollAsync(window, list, "未完成父行");
        model.ToggleConflictExpandedCommand.Execute(model.ConflictRows[0]);
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(11002, model.ConflictRows.Count);
        AssertOrdinaryRows(model.ConflictRows);
        await CheckScrollAsync(window, list, "未完成展开");
        Assert.IsTrue(model.ConflictRows[0].IsExpanded);
        Assert.AreEqual("file-0000.ts4script", ((ConflictResultRow)((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)).DataContext).Title);
        var range = model.ConflictRows.First(row => row.Kind == ConflictRowKind.RangeProblem);
        model.ToggleConflictExpandedCommand.Execute(range);
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(11003, model.ConflictRows.Count);
        await CheckScrollAsync(window, list, "范围问题");
        var sources = Descendants<ListBox>(window).Single(list => list.Name == "ConflictSourcesList");
        Assert.AreEqual(501, sources.Items.Count);
        await CheckScrollAsync(window, sources, "来源");
        model.ToggleConflictExpandedCommand.Execute(model.ConflictRows[0]);
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(1001, model.ConflictRows.Count);
        await CheckScrollAsync(window, list, "收起后");
    });

    [TestMethod]
    public Task ExpandedResourceAndScriptDetailsUseTheSameVirtualizedContainers() => WpfTestHost.RunAsync(async () =>
    {
        var resources = Enumerable.Range(0, 10000).Select(index => Resource((ulong)index, (A, "a"), (B, "b")))
            .Concat(Enumerable.Range(0, 500).Select(index => Resource((ulong)index + 20000,
                ($@"C:\Mods\first-{index:D4}.package", "a"), ($@"C:\Mods\second-{index:D4}.package", "b")))).ToArray();
        var modules = Enumerable.Range(0, 10000).Select(index => Collision($"library.module{index:D5}", (A + ".ts4script", "a"), (B + ".ts4script", "b")))
            .Concat(Enumerable.Range(0, 500).Select(index => Collision($"other.module{index:D4}",
                ($@"C:\Mods\first-{index:D4}.ts4script", "a"), ($@"C:\Mods\second-{index:D4}.ts4script", "b")))).ToArray();
        var session = Session(resources, scripts: Scripts(modules));
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, session);
        var list = Descendants<ListBox>(window).Single(list => list.Name == "ConflictRowsList");
        foreach (var view in new[] { "resources", "scripts" })
        {
            model.SelectConflictViewCommand.Execute(view);
            await model.ConflictRowsRefreshTask;
            Assert.AreEqual(501, model.ConflictRows.Count);
            await CheckScrollAsync(window, list, view + "父行");
            var parent = model.ConflictRows[0];
            model.ToggleConflictExpandedCommand.Execute(parent);
            await model.ConflictRowsRefreshTask;
            Assert.AreEqual(10501, model.ConflictRows.Count);
            Assert.AreEqual(501, model.VisibleConflictParentCount);
            var child = model.ConflictRows[1];
            model.ToggleConflictExpandedCommand.Execute(child);
            await model.ConflictRowsRefreshTask;
            await ExpandAsync(model, ConflictRowKind.Content);
            await ExpandAsync(model, ConflictRowKind.Technical);
            var file = model.ConflictRows.First(row => row.Kind == ConflictRowKind.File);
            model.ToggleConflictExpandedCommand.Execute(file);
            await model.ConflictRowsRefreshTask;
            Assert.IsTrue(model.ConflictRows.Any(row => row.Kind == ConflictRowKind.Technical && row.Depth >= 4));
            await CheckScrollAsync(window, list, view + "展开");
            Assert.IsTrue(model.ConflictRows[0].IsExpanded);
            Assert.IsTrue(model.ConflictRows[1].IsExpanded);
            model.ToggleConflictExpandedCommand.Execute(model.ConflictRows[0]);
            await model.ConflictRowsRefreshTask;
            Assert.AreEqual(501, model.ConflictRows.Count);
        }
    });

    [TestMethod]
    public Task FullContentContextWithThousandsOfFilesRemainsVirtualized() => WpfTestHost.RunAsync(async () =>
    {
        var files = Enumerable.Range(0, 10000).Select(index => ($@"C:\Mods\copy-{index:D5}.package", (string?)"same"))
            .Append((@"C:\Mods\unfinished.package", (string?)null)).ToArray();
        var session = Session([Resource(1, files)]);
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, session);
        Assert.AreEqual(10000, model.ConflictRows.Count);
        model.ToggleConflictExpandedCommand.Execute(model.ConflictRows[0]);
        await model.ConflictRowsRefreshTask;
        model.ToggleConflictExpandedCommand.Execute(model.ConflictRows[1]);
        await model.ConflictRowsRefreshTask;
        var content = model.ConflictRows.First(row => row.Kind == ConflictRowKind.Content);
        model.ToggleConflictExpandedCommand.Execute(content);
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(10000, model.ConflictRows.Count(row => row.Kind == ConflictRowKind.File));
        var list = Descendants<ListBox>(window).Single(list => list.Name == "ConflictRowsList");
        await CheckScrollAsync(window, list, "完整内容组文件");
    });

    [TestMethod]
    public Task ExpandedTechnicalDetailsStayVirtualizedAndSurviveRecycling() => WpfTestHost.RunAsync(async () =>
    {
        var resources = Enumerable.Range(0, 200).Select(index => Resource((ulong)index + 40000,
            ($@"C:\Mods\技术样本-{index:D4}.package", "a"), ($@"C:\Mods\技术样本副本-{index:D4}.package", "b"))).ToArray();
        var session = Session(resources);
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        window.Width = 920;
        window.Height = 640;
        await SetSessionAsync(model, session);
        var list = Descendants<ListBox>(window).Single(list => list.Name == "ConflictRowsList");
        Assert.AreEqual(200, model.ConflictRows.Count);
        model.ToggleConflictExpandedCommand.Execute(model.ConflictRows[0]);
        await model.ConflictRowsRefreshTask;
        foreach (var pair in model.ConflictRows.Where(row => row.Kind == ConflictRowKind.PackagePair).ToArray())
        {
            if (!pair.IsExpanded)
            {
                model.ToggleConflictExpandedCommand.Execute(pair);
                await model.ConflictRowsRefreshTask;
            }
            foreach (var resource in model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Resource && !row.IsExpanded).ToArray())
                model.ToggleConflictExpandedCommand.Execute(resource);
            await model.ConflictRowsRefreshTask;
        }
        Assert.IsTrue(model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Resource).All(row => row.IsExpanded));
        var details = model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && row.Title == "技术详情").ToArray();
        Assert.AreEqual(200, details.Length);
        Assert.IsTrue(details.All(row => !row.IsExpanded));
        AssertOrdinaryRows(model.ConflictRows);
        Assert.IsTrue(details.All(row => row.Tooltip.Length == 0));
        await ExpandAsync(model, ConflictRowKind.Technical);
        Assert.IsTrue(model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && row.Title == "技术详情").All(row => row.IsExpanded));
        await CheckScrollAsync(window, list, "技术详情展开");
        await ExpandAsync(model, ConflictRowKind.Content);
        await ExpandAsync(model, ConflictRowKind.Technical);
        await CheckScrollAsync(window, list, "内容组技术详情展开");
        Assert.IsTrue(model.ConflictRows.Any(row => row.Kind == ConflictRowKind.File));
        await ExpandAsync(model, ConflictRowKind.File);
        await CheckScrollAsync(window, list, "文件技术详情展开");
        foreach (var row in model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && row.Title == "技术详情").ToArray())
            model.ToggleConflictExpandedCommand.Execute(row);
        await model.ConflictRowsRefreshTask;
        Assert.IsTrue(model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && row.Title == "技术详情").All(row => !row.IsExpanded));
        await CheckScrollAsync(window, list, "技术详情收起");
    });

    [TestMethod]
    public Task ManyFilesAndReasonsStaySeparateWithoutCollapsedDetailTooltips() => WpfTestHost.RunAsync(async () =>
    {
        var files = Enumerable.Range(0, 1000).Select(index => ($@"C:\Mods\文件-{index:D4}.package", (string?)"same")).ToArray();
        var session = Session([Resource(1, files.Append((B, (string?)null)).ToArray())], scripts: Scripts(issues:
            Enumerable.Range(0, 1000).Select(index => new Sims4ModSieve.Core.Scripts.ScriptArchiveIssue(
                Sims4ModSieve.Core.Scripts.ScriptArchiveIssueCode.EntryCorrupt,
                Sims4ModSieve.Core.Scripts.ScriptArchiveIssueStage.Entry, A, "raw detail", EntryName: $"pkg/{index}.pyc")).ToArray()));
        var pair = Rows(session).First();
        session.ToggleExpanded(pair.Key);
        session.ToggleExpanded(Rows(session).Single(row => row.Kind == ConflictRowKind.Resource).Key);
        session.ToggleExpanded(Rows(session).First(row => row.Kind == ConflictRowKind.Content).Key);
        AssertOrdinaryRows(Rows(session));
        Assert.AreEqual(1000, Rows(session).Count(row => row.Kind == ConflictRowKind.File));
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, session);
        var list = Descendants<ListBox>(window).Single(element => element.Name == "ConflictRowsList");
        await CheckScrollAsync(window, list, "千份文件技术详情收起");
        await ExpandAsync(model, ConflictRowKind.Technical);
        await CheckScrollAsync(window, list, "千份文件技术详情展开");
        model.SelectConflictViewCommand.Execute("problems");
        await model.ConflictRowsRefreshTask;
        var problem = model.ConflictRows.Single(row => row.Tooltip == A);
        model.ToggleConflictExpandedCommand.Execute(problem);
        await model.ConflictRowsRefreshTask;
        AssertOrdinaryRows(model.ConflictRows);
        Assert.AreEqual(1000, model.ConflictRows.Count(row => row.Kind == ConflictRowKind.Explanation));
        await CheckScrollAsync(window, list, "千条普通原因");
        await ExpandAsync(model, ConflictRowKind.Technical);
        await CheckScrollAsync(window, list, "千条技术原因");
    });

    private static async Task CheckScrollAsync(Window window, ListBox list, string label)
    {
        await WpfTestHost.LayoutAsync(window);
        AssertBounded(list, label + "起点");
        var last = list.Items[list.Items.Count - 1];
        list.ScrollIntoView(last);
        await WpfTestHost.LayoutAsync(window);
        AssertBounded(list, label + "远处");
        Assert.IsNotNull(list.ItemContainerGenerator.ContainerFromItem(last));
        Assert.AreSame(last, ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(last)).DataContext);
        var first = list.Items[0];
        list.ScrollIntoView(first);
        await WpfTestHost.LayoutAsync(window);
        AssertBounded(list, label + "返回");
        Assert.AreSame(first, ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(first)).DataContext);
    }

    private static void AssertBounded(ListBox list, string label)
    {
        var containers = Descendants<ListBoxItem>(list).Count();
        var texts = Descendants<TextBlock>(list).Count();
        var controls = Descendants<FrameworkElement>(list).Count();
        Assert.IsTrue(containers is > 0 and < 150, $"{label}: containers={containers}");
        Assert.IsTrue(texts < 750, $"{label}: detail text elements={texts}");
        Assert.IsTrue(controls < 3000, $"{label}: elements={controls}");
        Assert.AreEqual(0, Descendants<ItemsControl>(list).Count(control => control is not ListBox));
        Console.WriteLine($"{label}: logical={list.Items.Count}, containers={containers}, text-elements={texts}, all-elements={controls}");
    }
}
