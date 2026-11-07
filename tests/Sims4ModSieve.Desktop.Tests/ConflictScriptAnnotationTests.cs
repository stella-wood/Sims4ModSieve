using System.IO;
using System.IO.Compression;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictResultSessionTests;
using static Sims4ModSieve.Desktop.Tests.ConflictV2SessionTests;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ConflictScriptAnnotationTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public Task ScriptRowsCountPrivateModulesAndFollowSessionChanges(bool different, bool incomplete) => WpfTestHost.RunAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "s4ms-script-counts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "a.ts4script");
            var second = Path.Combine(root, "b.ts4script");
            var firstEntries = new Dictionary<string, string> { ["kit/common.py"] = "value=1", ["kit/same.py"] = "value=1", ["private_a.py"] = "value=1" };
            var secondEntries = new Dictionary<string, string> { ["kit/common.py"] = different ? "value=2" : "value=1", ["kit/same.py"] = "value=1", ["private_b.py"] = "value=1", ["another_b.py"] = "value=1" };
            if (incomplete)
            {
                firstEntries["kit/unfinished.py"] = "value=1";
                secondEntries["kit/unfinished.pyc"] = "invalid bytecode";
            }
            WriteArchive(first, firstEntries);
            WriteArchive(second, secondEntries);
            var scripts = await new ScriptModuleScanner(new PhysicalFileSystemAccess()).ScanAsync(new([new("samples", root)]));
            var session = V2([], [], scripts: scripts);
            var card = session.Cards.Single();
            session.ToggleExpanded(card.Key);
            var files = Rows(session, "all").Where(row => row.File is not null).ToArray();
            Assert.AreEqual($"撞 {(different ? 1 : 0)} 样 · 独有 1 样", files.Single(row => row.File!.Path == first).Annotation);
            Assert.AreEqual($"撞 {(different ? 1 : 0)} 样 · 独有 2 样", files.Single(row => row.File!.Path == second).Annotation);
            Assert.AreEqual($"本组同名模块 {(incomplete ? 3 : 2)} 个，其中 {(different ? 1 : 2)} 个内容相同、{(incomplete ? 1 : 0)} 个未分析；整份文件独有模块 1 个",
                files.Single(row => row.File!.Path == first).AnnotationTooltip);
            Assert.AreEqual(files[0].Annotation, session.ProjectionSnapshot().BuildVisibleRows("all", "all", session.GetExpandedKeysSnapshot())
                .Single(row => row.File?.Path == files[0].File!.Path).Annotation);
            session.SetIgnoredFiles([second]);
            Assert.AreEqual(files.Single(row => row.File!.Path == first).Annotation, Rows(session, "all").Single(row => row.File is not null).Annotation);
            var shown = session.BuildVisibleRows("all", "all", session.GetExpandedKeysSnapshot(), showIgnored: true);
            Assert.IsTrue(shown.Single(row => row.File?.Path == second).IsIgnored);
            Assert.AreEqual(files.Single(row => row.File!.Path == second).Annotation, shown.Single(row => row.File?.Path == second).Annotation);
            session.SetIgnoredFiles([]);
            var model = CreateModel();
            model.OpenConflictCommand.Execute(null);
            model.OpenConflictSectionCommand.Execute("basic");
            await SetSessionAsync(model, session, "all");
            var window = OpenWindow(model);
            await WpfTestHost.LayoutAsync(window);
            foreach (var row in model.ConflictRows.Where(row => row.File is not null))
                Assert.IsTrue(Descendants<TextBlock>(window).Any(text => text.IsVisible && ReferenceEquals(text.DataContext, row) && text.Text == row.Annotation));
            Save(window, $"script-annotation-{different}-{incomplete}.png");
            session.ApplyDeletedPaths([second]);
            Assert.AreEqual($"撞 0 样 · 独有 {(incomplete ? 4 : 3)} 样", Rows(session, "all").Single(row => row.File is not null).Annotation);
            session.ApplyRestoredPaths([second]);
            Assert.AreEqual(card.GroupIndex, session.Cards.Single().GroupIndex);
            CollectionAssert.AreEquivalent(files.Select(row => row.Annotation).ToArray(), Rows(session, "all").Where(row => row.File is not null).Select(row => row.Annotation).ToArray());
        }
        finally { Directory.Delete(root, recursive: true); }
    });

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void MissingOrStaleModuleInventoryDoesNotBecomeZeroUnique(int evidence)
    {
        var scripts = Scripts([Collision("kit.common", (A, "one"), (B, "two"))]) with
        {
            ArchiveInventories = evidence == 0 ? [] : evidence == 1
                ? [new(A, Stamp, ["kit.common", "private"])]
                : [new(A, Stamp, ["kit.common", "private"]), new(B, Stamp with { Length = Stamp.Length + 1 }, ["kit.common"])],
        };
        var session = V2([], [], scripts: scripts);
        session.ToggleExpanded(session.Cards.Single().Key);
        Assert.IsTrue(Rows(session, "all").Where(row => row.File is not null).All(row => row.Annotation == "撞 1 样 · 独有数量未知"));
    }

    [TestMethod]
    public void UncomparedFileDoesNotInheritOtherFilesConfirmedCollisionCount()
    {
        var scripts = Scripts([Collision("kit.common", (A, null), (B, "one"), (C, "two"))]) with
        {
            ArchiveInventories = new[] { A, B, C }.Select(path => new ScriptArchiveInventory(path, Stamp, ["kit.common"])).ToArray(),
        };
        var session = V2([], [], scripts: scripts);
        session.ToggleExpanded(session.Cards.Single().Key);
        var files = Rows(session, "all").Where(row => row.File is not null).ToArray();
        Assert.AreEqual("撞 0 样 · 独有 0 样", files.Single(row => row.File!.Path == A).Annotation);
        Assert.AreEqual("撞 1 样 · 独有 0 样", files.Single(row => row.File!.Path == B).Annotation);
        Assert.AreEqual("撞 1 样 · 独有 0 样", files.Single(row => row.File!.Path == C).Annotation);
    }

    private static void WriteArchive(string path, IReadOnlyDictionary<string, string> entries)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }
    }
}
