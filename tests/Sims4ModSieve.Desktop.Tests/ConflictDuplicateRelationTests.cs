using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictResultSessionTests;
using static Sims4ModSieve.Desktop.Tests.ConflictV2SessionTests;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ConflictDuplicateRelationTests
{
    [TestMethod]
    [DataRow("collection")]
    [DataRow("override")]
    [DataRow("suspected")]
    public Task CopiesShareOneRelationGroupAndKeepPathsAndUndo(string kind) => WpfTestHost.RunAsync(async () =>
    {
        var singles = Enumerable.Range(0, 4).Select(index => $@"C:\Mods\single{index}.package").ToArray();
        var bundles = Enumerable.Range(0, 4).Select(index => $@"C:\Mods\bundle{index}.package").ToArray();
        var first = Duplicates(singles);
        var second = Duplicates(bundles);
        var report = first with { Groups = [first.Groups.Single(), second.Groups.Single() with { Sha256 = "OTHER-HASH" }] };
        var sameResources = kind != "override";
        var comparisons = Enumerable.Range(1, 3).Select(key => Resource((ulong)key,
            singles.Select(path => (path, (string?)"a")).Concat(bundles.Select(path => (path, (string?)(sameResources ? "a" : "b")))).ToArray())).ToArray();
        var inventories = singles.Select(path => (path, kind == "collection" ? 3 : kind == "suspected" ? 4 : 10))
            .Concat(bundles.Select(path => (path, kind == "suspected" ? 4 : 10))).ToArray();
        if (kind == "collection") Assert.AreEqual(4, V2(comparisons, inventories).Cards.Count(card => card.Type == "collection"),
            "没有整文件重复证据时，各个合集仍须分别保留");
        var session = V2(comparisons, inventories, report);
        var card = session.Cards.Single(card => card.Type == kind);
        Assert.AreEqual(8, card.Files.Count);
        session.ToggleExpanded(card.Key);
        var rows = Rows(session, "all");
        var files = rows.Where(row => row.OwnerKey == card.Key && row.File is not null).ToArray();
        CollectionAssert.AreEquivalent(new[] { first.Groups.Single().SuggestedKeepPath, second.Groups.Single().SuggestedKeepPath }, files.Select(row => row.File!.Path).ToArray());
        Assert.IsTrue(files.All(row => row.CopyCount == 3));
        var relationships = session.Relationships(singles[0])!;
        Assert.AreEqual(2, relationships.CardCount);
        var partner = relationships.Related.Single();
        Assert.AreEqual(1, partner.Annotations.Count);
        Assert.AreEqual(1, partner.Relationships.Select(relation => relation.Link.Key).Distinct().Count());
        foreach (var path in singles.Concat(bundles)) Assert.IsTrue(session.Relationships(path)!.Related.Any());
        session.SetIgnoredFiles([bundles[0]]);
        Assert.AreEqual(card.Key, session.Cards.Single(item => item.Type == kind).Key);
        session.ApplyDeletedPaths([bundles[0]]);
        Assert.AreEqual(1, session.Cards.Count(item => item.Type == kind));
        Assert.AreEqual(card.GroupIndex, session.Cards.Single(item => item.Type == kind).GroupIndex);
        Assert.AreEqual(2, session.Relationships(singles[0])!.CardCount);
        session.ApplyRestoredPaths([bundles[0]]);
        Assert.AreEqual(8, session.Cards.Single(item => item.Type == kind).Files.Count);
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, session, "all");
        model.OpenConflictRelationshipsCommand.Execute(model.ConflictRows.First(row => row.File?.Path == singles[0]));
        await WpfTestHost.LayoutAsync(window);
        await Task.Delay(280);
        var dialog = Descendants<ConflictRelationshipsDialog>(window).Single();
        Assert.AreEqual(1, Descendants<Border>(dialog).Count(border => border.Name == "Badge" && border.IsVisible));
        Save(window, $"duplicate-relation-{kind}.png");
    });

    [TestMethod]
    public void SeparateGroupLinksDoNotDuplicateSameRelationBadge()
    {
        var row = new ConflictRelationshipRow(new(A, Stamp), Sims4ModSieve.Core.Conflicts.ConflictPairPriority.CanOrganize,
            [new("合集包含", "依据一", new("one", 2), ContainsMain: true), new("合集包含", "依据二", new("two", 1), ContainsMain: true)], [], null);
        Assert.AreEqual(1, row.Annotations.Count);
        StringAssert.Contains(row.Annotations.Single().Detail, "依据一");
        StringAssert.Contains(row.Annotations.Single().Detail, "依据二");
        var display = new ConflictRelationshipDisplayRow(Partner: row);
        CollectionAssert.AreEqual(new[] { 1, 2 }, display.Links.Select(link => link.GroupIndex).ToArray());
    }
}
