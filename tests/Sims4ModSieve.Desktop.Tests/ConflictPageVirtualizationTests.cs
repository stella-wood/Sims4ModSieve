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
    public Task StandaloneProblemsRangesAndSourcesStayBoundedAndSurviveRecycling() => WpfTestHost.RunAsync(async () =>
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
        for (var index = 0; index < 500; index++) model.ConflictSources.Add(new ScanSourceViewModel($@"C:\Sources\source-{index:D4}"));
        var window = OpenWindow(model);
        window.Width = 920;
        window.Height = 640;
        await SetSessionAsync(model, session, "problems");
        var list = Descendants<ListBox>(window).Single(list => list.Name == "ConflictRowsList");
        Assert.AreEqual(1000, model.ConflictRows.Count(row => row.IsCardStart));
        Assert.AreEqual(12000, model.ConflictRows.Count);
        Assert.IsFalse(model.ConflictRows[0].IsExpandable);
        AssertOrdinaryRows(model.ConflictRows);
        await CheckScrollAsync(window, list, "未完成文件与原因");
        var details = model.ConflictRows.First(row => row.IsTechnicalToggle);
        model.ToggleConflictExpandedCommand.Execute(details);
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(22002, model.ConflictRows.Count);
        await CheckScrollAsync(window, list, "未完成技术详情展开");
        Assert.IsTrue(model.ConflictRows.Single(row => row.Key == details.Key).IsExpanded);
        Assert.AreEqual("file-0000.ts4script", ((ConflictResultRow)((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)).DataContext).Title);
        var range = model.ConflictRows.First(row => row.Kind == ConflictRowKind.RangeProblem);
        model.ToggleConflictExpandedCommand.Execute(range);
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(22004, model.ConflictRows.Count);
        await CheckScrollAsync(window, list, "范围问题");
        var sources = Descendants<ListBox>(window).Single(list => list.Name == "ConflictSourcesList");
        Assert.AreEqual(501, sources.Items.Count);
        await CheckScrollAsync(window, sources, "来源");
        model.ToggleConflictExpandedCommand.Execute(details);
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(12002, model.ConflictRows.Count);
        await CheckScrollAsync(window, list, "收起后");
    });

    [TestMethod]
    public Task ResourceAndScriptCardsStayVirtualizedAcrossThousandsOfGroups() => WpfTestHost.RunAsync(async () =>
    {
        var resources = Enumerable.Range(0, 10500).Select(index => Resource((ulong)index, ($@"C:\Mods\first-{index:D5}.package", "a"), ($@"C:\Mods\second-{index:D5}.package", "b"))).ToArray();
        var modules = Enumerable.Range(0, 10500).Select(index => Collision($"library.module{index:D5}", (A + ".ts4script", "a"), (B + ".ts4script", "b"))).ToArray();
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
            Assert.AreEqual(10500, model.ConflictRows.Count);
            await CheckScrollAsync(window, list, view + "收起");
            var card = model.ConflictRows[0];
            model.ToggleConflictExpandedCommand.Execute(card);
            await model.ConflictRowsRefreshTask;
            Assert.AreEqual(10504, model.ConflictRows.Count);
            Assert.AreEqual(10500, model.VisibleConflictParentCount);
            Assert.IsTrue(model.ConflictRows.All(row => row.Depth <= 1));
            await ExpandAsync(model, ConflictRowKind.Technical);
            Assert.IsTrue(model.ConflictRows.Any(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable));
            await CheckScrollAsync(window, list, view + "展开技术详情");
            Assert.IsTrue(model.ConflictRows[0].IsExpanded);
            model.ToggleConflictExpandedCommand.Execute(card);
            await model.ConflictRowsRefreshTask;
            Assert.AreEqual(10500, model.ConflictRows.Count);
        }
    });

    [TestMethod]
    public Task FullContentContextWithThousandsOfFilesRemainsVirtualized() => WpfTestHost.RunAsync(async () =>
    {
        var files = Enumerable.Range(0, 10000).Select(index => $@"C:\Mods\copy-{index:D5}.package").ToArray();
        var session = ConflictV2SessionTests.V2([], [], ConflictV2SessionTests.Duplicates(files));
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, session, "all");
        Assert.AreEqual(1, model.ConflictRows.Count);
        model.ToggleConflictExpandedCommand.Execute(model.ConflictRows[0]);
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
        await ExpandAsync(model, ConflictRowKind.Resource);
        Assert.IsTrue(model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Resource).All(row => row.IsExpanded));
        var details = model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && row.Title == "技术详情").ToArray();
        Assert.AreEqual(200, details.Length);
        Assert.IsTrue(details.All(row => !row.IsExpanded));
        AssertOrdinaryRows(model.ConflictRows);
        Assert.IsTrue(details.All(row => row.Tooltip.Length == 0));
        await ExpandAsync(model, ConflictRowKind.Technical);
        Assert.IsTrue(model.ConflictRows.Where(row => row.Kind == ConflictRowKind.Technical && row.Title == "技术详情").All(row => row.IsExpanded));
        await CheckScrollAsync(window, list, "技术详情展开");
        Assert.IsTrue(model.ConflictRows.Any(row => row.Kind == ConflictRowKind.File));
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
        var session = Session([], scripts: Scripts([Collision("pkg.shared", files.Append((B, (string?)null)).ToArray())],
            Enumerable.Range(0, 1000).Select(index => new ScriptArchiveIssue(ScriptArchiveIssueCode.EntryCorrupt,
                ScriptArchiveIssueStage.Entry, A, "raw detail", EntryName: $"pkg/{index}.pyc")).ToArray()));
        var pair = Rows(session, "scripts").First(row => row.Kind == ConflictRowKind.Module);
        session.ToggleExpanded(pair.Key);
        AssertOrdinaryRows(Rows(session, "scripts"));
        Assert.AreEqual(1001, Rows(session, "scripts").Count(row => row.Kind == ConflictRowKind.File));
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, session, "scripts");
        var list = Descendants<ListBox>(window).Single(element => element.Name == "ConflictRowsList");
        await CheckScrollAsync(window, list, "千份文件技术详情收起");
        await ExpandAsync(model, ConflictRowKind.Technical);
        await CheckScrollAsync(window, list, "千份文件技术详情展开");
        model.SelectConflictViewCommand.Execute("problems");
        await model.ConflictRowsRefreshTask;
        var problem = model.ConflictRows.Single(row => row.IsCardStart && row.Tooltip == A);
        model.ToggleConflictExpandedCommand.Execute(problem);
        await model.ConflictRowsRefreshTask;
        AssertOrdinaryRows(model.ConflictRows);
        Assert.AreEqual(1000, model.ConflictRows.Count(row => row.Kind == ConflictRowKind.Explanation || row.Kind == ConflictRowKind.Problem && row.HasAnnotation));
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
        Assert.IsTrue(Descendants<ItemsControl>(list).Count(control => control is not ListBox) <= containers * 2);
        Console.WriteLine($"{label}: logical={list.Items.Count}, containers={containers}, text-elements={texts}, all-elements={controls}");
    }
}
