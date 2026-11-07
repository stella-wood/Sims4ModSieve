using System.IO;
using System.IO.Compression;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictResultSessionTests;
using static Sims4ModSieve.Desktop.Tests.ConflictV2SessionTests;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ConflictScriptGroupTests
{
    [TestMethod]
    [DataRow(false, false, "重复文件（脚本）", ConflictPairPriority.CanOrganize)]
    [DataRow(true, false, "脚本冲突", ConflictPairPriority.NeedsReview)]
    [DataRow(false, true, "脚本冲突", ConflictPairPriority.Incomplete)]
    [DataRow(true, true, "脚本冲突", ConflictPairPriority.NeedsReview)]
    public void SameLibraryAndFilesMergeWithWholeGroupVerdict(bool different, bool incomplete, string title, ConflictPairPriority priority)
    {
        var session = V2([], [], scripts: Scripts([
            Collision("kit_diff.common", (A, "x"), (B, different ? "y" : "x")),
            Collision("kit_diff.extra", (A, "x"), (B, incomplete ? null : "x")),
            Collision("kit_diff.third", (A, "x"), (B, "x"))]));
        var card = session.Cards.Single();
        Assert.AreEqual(3, card.Modules!.Count);
        Assert.AreEqual(title, card.Title);
        Assert.AreEqual(priority, card.Priority);
        session.ToggleExpanded(card.Key);
        var rows = Rows(session, "all");
        Assert.AreEqual(2, rows.Count(row => row.File is not null));
        Assert.AreEqual(0, rows.Count(row => row.Kind == ConflictRowKind.Content));
        var explanation = rows.Single(row => row.Kind == ConflictRowKind.Explanation).Title;
        Assert.IsFalse(explanation.Contains('；'));
        if (incomplete) StringAssert.Contains(explanation, "1 个" + (different ? "模块没比完" : "没比完"));
        else Assert.AreEqual(different
            ? "这 2 份文件都带了 kit_diff，共 3 个模块，其中 1 个内容不同。游戏对同名模块只会加载一份"
            : "这 2 份文件都带了 kit_diff，共 3 个模块，内容都相同", explanation);
        var details = session.TechnicalEntries(card);
        Assert.AreEqual(3, details.Count);
        Assert.AreEqual(incomplete ? "未分析" : "内容相同", details.Single(entry => entry.Identifier.EndsWith("extra", StringComparison.Ordinal)).Conclusion);
    }

    [TestMethod]
    public void DifferentFileSetsAndTopLevelNamesStaySeparate()
    {
        var session = V2([], [], scripts: Scripts([
            Collision("kit.one", (A, "x"), (B, "y")),
            Collision("kit.two", (A, "x"), (C, "y")),
            Collision("other.one", (A, "x"), (B, "y"))]));
        Assert.AreEqual(3, session.Cards.Count);
        Assert.AreEqual(3, session.Cards.Select(card => card.Key).Distinct().Count());
    }

    [TestMethod]
    public void DeletionMergesConvergingFileSetsAndUndoRestoresOriginalGroups()
    {
        var session = V2([], [], scripts: Scripts([
            Collision("kit.one", (A, "x"), (B, "y")),
            Collision("kit.two", (A, "x"), (B, "x"), (C, "z"))]));
        var original = session.Cards.ToDictionary(card => card.Key, card => card.GroupIndex);
        Assert.AreEqual(2, original.Count);
        session.ToggleIgnored(Rows(session, "all").First());
        session.ApplyDeletedPaths([C]);
        var merged = session.Cards.Single();
        Assert.AreEqual(2, merged.Modules!.Count);
        Assert.AreEqual("脚本冲突", merged.Title);
        Assert.IsFalse(session.IsGroupIgnored(merged.Key));
        Assert.AreEqual(1, Rows(session, "all").Count);
        session.ApplyRestoredPaths([C]);
        Assert.AreEqual(2, session.Cards.Count);
        foreach (var card in session.Cards) Assert.AreEqual(original[card.Key], card.GroupIndex);
        Assert.AreEqual(1, session.Cards.Count(card => session.IsGroupIgnored(card.Key)));
    }

    [TestMethod]
    public void IgnoreDeleteRestoreAndFiltersKeepGroupedModulesConsistent()
    {
        var session = V2([], [], scripts: Scripts([
            Collision("kit.one", (A, "x"), (B, "y"), (C, "x")),
            Collision("kit.two", (A, "x"), (B, "x"), (C, "x"))]));
        var original = session.Cards.Single();
        session.ToggleExpanded(original.Key);
        session.SetIgnoredFiles([B]);
        Assert.AreEqual(2, Rows(session, "all").Count(row => row.File is not null));
        Assert.AreEqual(3, session.BuildVisibleRows("all", "all", session.GetExpandedKeysSnapshot(), showIgnored: true).Count(row => row.File is not null));
        Assert.AreEqual("脚本冲突", session.Cards.Single().Title);
        session.ApplyDeletedPaths([B]);
        var current = session.Cards.Single();
        Assert.AreEqual(original.Key, current.Key);
        Assert.AreEqual(original.GroupIndex, current.GroupIndex);
        Assert.AreEqual("重复文件（脚本）", current.Title);
        Assert.AreEqual(ConflictPairPriority.CanOrganize, current.Priority);
        Assert.IsTrue(session.TechnicalEntries(current).All(entry => entry.Conclusion == "内容相同"));
        Assert.AreEqual(0, session.BuildVisibleRows("all", "all", Closed, types: new HashSet<string> { "script-conflict" }).Count);
        Assert.AreEqual(1, session.BuildVisibleRows("all", "same", Closed, types: new HashSet<string> { "script-duplicate" }).Count);
        Assert.AreEqual(1, session.Relationships(A)!.CardCount);
        session.ApplyRestoredPaths([B]);
        Assert.AreEqual("脚本冲突", session.Cards.Single().Title);
        Assert.AreEqual(2, session.Cards.Single().Modules!.Count);
        var heading = Rows(session, "all").Single(row => row.IsCardStart);
        session.ToggleIgnored(heading);
        Assert.AreEqual(0, Rows(session, "all").Count);
        Assert.AreEqual(1, session.BuildVisibleRows("all", "all", Closed, showIgnored: true).Count);
    }

    [TestMethod]
    public Task ScriptGroupFiltersAndCountsFollowWholeGroupVerdicts() => WpfTestHost.RunAsync(async () =>
    {
        var session = V2([], [], scripts: Scripts([
            Collision("kit.common", (A, "x"), (B, "y")), Collision("kit.extra", (A, "x"), (B, "x")),
            Collision("same.common", (A, "x"), (B, "x")), Collision("partial.common", (A, "x"), (B, null))]));
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, session, "all");
        Assert.AreEqual(3, model.VisibleConflictParentCount);
        var popup = Descendants<Popup>(window).Single(element => element.Name == "ConflictFilterPopup");
        popup.IsOpen = true;
        await WpfTestHost.LayoutAsync(window);
        var labels = Descendants<CheckBox>(popup.Child).Select(box => box.Content.ToString()).ToArray();
        Assert.IsTrue(labels.Contains("脚本冲突") && labels.Contains("重复文件（脚本）") && labels.Contains("重复文件"));
        Assert.IsFalse(labels.Contains("文件重复") || labels.Contains("脚本重复") || labels.Contains("同名脚本"));
        Save(window, "script-group-filters.png", popup.Child as System.Windows.FrameworkElement);
        popup.IsOpen = false;
        model.IncludeAllConflictTypes = false;
        model.IncludeConflictScriptDuplicates = true;
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(1, model.VisibleConflictParentCount);
        Assert.AreEqual("重复文件（脚本）", model.ConflictRows.Single().Title);
        model.IncludeAllConflictStates = false;
        model.IncludeConflictIncomplete = true;
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(0, model.VisibleConflictParentCount);
        model.IncludeConflictScriptDuplicates = false;
        model.IncludeConflictScriptConflicts = true;
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(1, model.VisibleConflictParentCount);
        Assert.AreEqual(ConflictPairPriority.Incomplete, model.ConflictRows.Single().Priority);
        model.IncludeConflictNeedsReview = true;
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(2, model.VisibleConflictParentCount);
        model.IgnoreConflictCommand.Execute(model.ConflictRows.First());
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(1, model.VisibleConflictParentCount);
        model.ShowIgnoredConflicts = true;
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(2, model.VisibleConflictParentCount);
    });

    [TestMethod]
    [DataRow(1, true)]
    [DataRow(3, true)]
    [DataRow(3, false)]
    public Task RealScriptSamplesRenderWholeSentencesAndModuleDetails(int moduleCount, bool different) => WpfTestHost.RunAsync(async () =>
    {
        var output = Environment.GetEnvironmentVariable("SIMS4_CONFLICT_REVIEW_DIR") ?? Path.GetTempPath();
        var root = Path.Combine(output, $"samples-{moduleCount}-{different}");
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "a", "b" })
        {
            using var file = File.Create(Path.Combine(root, name + ".ts4script"));
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            foreach (var module in new[] { "common", "extra", "third" }.Take(moduleCount))
            {
                var entry = zip.CreateEntry("kit_diff/" + module + ".py");
                entry.LastWriteTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("value = " + (different && name == "b" && module == "common" ? "2" : "1"));
            }
            if (moduleCount > 1)
            {
                using var writer = new StreamWriter(zip.CreateEntry("README.txt").Open());
                writer.Write(name);
            }
        }
        if (moduleCount == 1)
            foreach (var name in new[] { "a", "b" })
                for (var index = 1; index <= 3; index++) File.Copy(Path.Combine(root, name + ".ts4script"), Path.Combine(root, name + index + ".ts4script"), true);
        var scripts = await new ScriptModuleScanner(new PhysicalFileSystemAccess()).ScanAsync(new([new("samples", root)]));
        Assert.IsTrue(scripts.Collisions.Count > 0, string.Join("\n", scripts.Issues.Select(issue => issue.Message)));
        var duplicates = await new DuplicateRunService(DuplicateScanner.CreateDefault()).RunAsync(new([new(root, "样本")], root));
        var session = V2([], [], duplicates, scripts);
        var card = session.Cards.Single(card => card.Modules is not null);
        session.ToggleExpanded(card.Key);
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, session, "all");
        await WpfTestHost.LayoutAsync(window);
        var expected = moduleCount == 1
            ? "这 8 份文件 (含 6 份重复) 都带了 kit_diff.common，内容有 2 种。游戏只会加载其中一份"
            : different ? "这 2 份文件都带了 kit_diff，共 3 个模块，其中 1 个内容不同。游戏对同名模块只会加载一份"
            : "这 2 份文件都带了 kit_diff，共 3 个模块，内容都相同";
        Assert.AreEqual(expected, model.ConflictRows.Single(row => row.Key == card.Key + "/explanation").Title);
        Assert.IsTrue(Descendants<TextBlock>(window).Any(text => text.IsVisible && text.Text == expected));
        File.WriteAllText(Path.Combine(output, $"sentence-{moduleCount}-{different}.txt"), expected);
        Save(window, $"script-group-{moduleCount}-{different}.png");
        model.ToggleConflictExpandedCommand.Execute(model.ConflictRows.Single(row => row.OwnerKey == card.Key && row.IsTechnicalToggle));
        await model.ConflictRowsRefreshTask;
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(moduleCount, session.TechnicalEntries(card).Count);
        Save(window, $"script-details-{moduleCount}-{different}.png");
    });
}
