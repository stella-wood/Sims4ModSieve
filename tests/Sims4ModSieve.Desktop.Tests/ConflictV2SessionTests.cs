using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;
using System.Windows.Controls;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictResultSessionTests;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ConflictV2SessionTests
{
    internal static ConflictResultSession V2(ResourceContentComparison[] comparisons,
        (string Path, int Count)[] inventories, DuplicateReport? duplicates = null, ScriptModuleScanReport? scripts = null)
    {
        var session = new ConflictResultSession();
        var report = new PackageConflictScanReport(DateTime.UnixEpoch, DateTime.UnixEpoch, [], inventories.Length,
            inventories.Length, [], [], [])
        {
            ResourceInventories = inventories.Select(item => new PackageResourceInventory(item.Path, Stamp, item.Count)
            { ResourceTypeCounts = new Dictionary<uint, int> { [0x034AEECB] = item.Count } }).ToArray(),
        };
        session.Replace(report, Contents(comparisons), scripts ?? Scripts(), duplicates: duplicates);
        return session;
    }

    internal static DuplicateReport Duplicates(params string[] paths)
    {
        var files = paths.Select(path => new DuplicateFile(path, Stamp.Length, Stamp.LastWriteTimeUtc, [], [], true,
            path.Count(character => character == '\\'))).ToArray();
        var selection = new DuplicateSelectionService().Select(files);
        return new(DateTime.UnixEpoch, DateTime.UnixEpoch, [], paths.Length, paths.Length,
            [new("WHOLE-FILE-HASH", Stamp.Length, DuplicateSection.Ordinary, files, selection.KeepPath, selection.DeletePaths)], []);
    }

    internal static ConflictResultSession ContainmentSession() => V2([
        Resource(1, (A, "a"), (B, "a")), Resource(2, (A, "b"), (B, "b")), Resource(3, (A, "c"), (B, "c")),
        Resource(4, (B, "d"), (C, "e"))], [(A, 3), (B, 10), (C, 4)]);

    [TestMethod]
    public void V2CountsAndStableNumbersUseFormalCoreCards()
    {
        var session = ContainmentSession();
        Assert.AreEqual(2, session.Cards.Count);
        var collection = session.Cards.Single(card => card.Type == "collection");
        var core = collection.Package!;
        Assert.AreEqual(B, core.BundlePath);
        Assert.AreEqual(7, core.BundleUniqueResourceCount);
        Assert.IsTrue(core.Members.Single(member => member.Path == A).ContainedContentIdentical);
        var red = session.Cards.Single(card => card.Type == "override");
        Assert.AreEqual(1, red.ResourceKeys.Count);
        Assert.AreEqual(1, red.Package!.Members.Single(member => member.Path == B).SharedResourceCount);
        Assert.AreEqual(9, red.Package.Members.Single(member => member.Path == B).UniqueResourceCount);
        var before = Rows(session).ToDictionary(row => row.Key, row => row.GroupIndex);
        session.ToggleExpanded(collection.Key);
        Assert.AreEqual(before[collection.Key], Rows(session, filter: "same").Single(row => row.Depth == 0).GroupIndex);
        Assert.AreEqual(before[red.Key], Rows(session, filter: "different").Single().GroupIndex);
    }

    [TestMethod]
    public void IgnoreIsLocalAndAllIgnoredFilesStillLeaveTheirCard()
    {
        var session = ContainmentSession();
        foreach (var card in session.Cards) session.ToggleExpanded(card.Key);
        var rows = Rows(session);
        var collection = session.Cards.Single(card => card.Type == "collection");
        foreach (var row in rows.Where(row => row.Kind == ConflictRowKind.File && row.OwnerKey == collection.Key)) session.ToggleIgnored(row);
        var hidden = Rows(session);
        Assert.IsTrue(hidden.Any(row => row.Key == collection.Key));
        Assert.IsFalse(hidden.Any(row => row.Kind == ConflictRowKind.File && row.OwnerKey == collection.Key));
        Assert.IsTrue(hidden.Any(row => row.File?.Path == B));
        var shown = session.BuildVisibleRows("all", "all", session.GetExpandedKeysSnapshot(), showIgnored: true);
        Assert.AreEqual(2, shown.Count(row => row.OwnerKey == collection.Key && row.File is not null && row.IsIgnored));
        session.ToggleIgnored(shown.Single(row => row.Key == collection.Key));
        Assert.IsFalse(Rows(session).Any(row => row.Key == collection.Key));
        Assert.AreEqual(2, session.Cards.Count);
        Assert.IsTrue(session.BuildVisibleRows("all", "all", Closed, showIgnored: true).Single(row => row.Key == collection.Key).IsIgnored);
    }

    [TestMethod]
    public void RelationshipsUseDirectPairsAndRealContainmentDirection()
    {
        var session = ContainmentSession();
        var contained = session.Relationships(A)!;
        Assert.IsTrue(contained.HasContainment);
        Assert.AreEqual(1, contained.Related.Count);
        Assert.AreEqual(B, contained.Related[0].File.Path);
        Assert.IsTrue(contained.Related[0].Relationships.Single().ContainsMain);
        Assert.IsTrue(session.Relationships(B)!.Related.Single(row => row.File.Path == A).Relationships.Single().ContainedByMain);
        Assert.IsFalse(session.Relationships(C)!.HasContainment);
        Assert.IsFalse(session.Relationships(C)!.Related.Any(row => row.File.Path == A));
    }

    [TestMethod]
    public void ResourceEqualityDoesNotBecomeWholeFileDuplicates()
    {
        var comparisons = new[] { Resource(1, (A, "same"), (B, "same")) };
        var resourceOnly = V2(comparisons, [(A, 1), (B, 1)]);
        Assert.AreEqual(0, resourceOnly.DuplicateGroupCount);
        Assert.AreEqual("suspected", resourceOnly.Cards.Single().Type);
        var exact = V2(comparisons, [(A, 1), (B, 1)], Duplicates(A, B));
        Assert.AreEqual(1, exact.DuplicateGroupCount);
        Assert.AreEqual(1, exact.Cards.Count);
        Assert.AreEqual("duplicates", exact.Cards.Single().Type);
        var relations = exact.Relationships(A)!;
        Assert.AreEqual(1, relations.Copies.Count);
        Assert.AreEqual(B, relations.Copies[0].Path);
        Assert.AreEqual(0, relations.Related.Count);
        Assert.AreEqual(1, relations.CardCount);
        var duplicate = exact.Cards.Single(card => card.Duplicate is not null);
        exact.ToggleExpanded(duplicate.Key);
        var rows = exact.BuildVisibleRows("all", "all", exact.GetExpandedKeysSnapshot());
        var duplicateFiles = rows.Where(item => item.OwnerKey == duplicate.Key && item.File is not null).ToArray();
        Assert.AreEqual("", duplicateFiles[0].Annotation);
        Assert.AreEqual("内容完全相同", duplicateFiles[1].Annotation);
        Assert.IsFalse(rows.Any(item => item.OwnerKey == duplicate.Key && item.IsTechnicalToggle));
        exact.ToggleIgnored(duplicateFiles[0]);
        Assert.AreEqual("", exact.BuildVisibleRows("all", "all", exact.GetExpandedKeysSnapshot())
            .Single(item => item.OwnerKey == duplicate.Key && item.File is not null).Annotation);
    }

    [TestMethod]
    public void HashChangeRemovesFileButKeepsStablePair()
    {
        var report = Duplicates(A, B) with { Groups = [], Issues = [
            new("file-changed-during-hash", ScanIssueStage.Hashing, C.ToUpperInvariant(), "changed")] };
        var session = V2([Resource(1, (A, "same"), (B, "same"), (C, "other"))], [(A, 1), (B, 1), (C, 1)], report);
        AssertChangedFileExcluded(session, "file-changed-during-hash");
        CollectionAssert.AreEquivalent(new[] { A, B }, session.Cards.Single().Files.Select(file => file.Path).ToArray());
        Assert.AreEqual(PackageConflictRelationKind.SuspectedSameMod, session.Grouping!.Relations.Single().Kind);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void HashStampMismatchRemovesFileWithStructuredReason(bool changeLength)
    {
        var report = Duplicates(A, B, C);
        report = report with { Groups = report.Groups.Select(group => group with
        {
            Files = group.Files.Select(file => file.Path != C ? file : changeLength
                ? file with { Size = file.Size + 1 }
                : file with { LastWriteTimeUtc = file.LastWriteTimeUtc.AddSeconds(1) }).ToArray(),
        }).ToArray() };
        var session = V2([Resource(1, (A, "same"), (B, "same"), (C, "same"))], [(A, 1), (B, 1), (C, 1)], report);
        AssertChangedFileExcluded(session, "hash-scan-stamp-mismatch");
        Assert.AreEqual("duplicates", session.Cards.Single().Type);
        Assert.AreEqual(2, session.Cards.Single().Files.Count);
        var deleted = session.ProjectionSnapshot();
        deleted.ApplyDeletedPaths([B]);
        session.AcceptFileChanges(deleted);
        Assert.AreEqual(0, session.Cards.Count);
        var restored = session.ProjectionSnapshot();
        restored.ApplyRestoredPaths([B, C]);
        session.AcceptFileChanges(restored);
        Assert.AreEqual("duplicates", session.Cards.Single().Type);
        Assert.AreEqual(2, session.Cards.Single().Files.Count);
        AssertChangedFileExcluded(session, "hash-scan-stamp-mismatch");
    }

    [TestMethod]
    public void ChangedFileCannotReturnThroughCachedDeleteRestore()
    {
        var report = Duplicates(A, B) with { Groups = [], Issues = [
            new("file-changed-during-hash", ScanIssueStage.Hashing, C, "changed")] };
        var session = V2([Resource(1, (A, "same"), (B, "same"), (C, "other"))], [(A, 1), (B, 1), (C, 1)], report);
        var change = session.ProjectionSnapshot();
        change.ApplyDeletedPaths([B]);
        session.AcceptFileChanges(change);
        Assert.AreEqual(0, session.Cards.Count);
        AssertChangedFileExcluded(session, "file-changed-during-hash");
        var restore = session.ProjectionSnapshot();
        restore.ApplyRestoredPaths([B, C]);
        session.AcceptFileChanges(restore);
        CollectionAssert.AreEquivalent(new[] { A, B }, session.Cards.Single().Files.Select(file => file.Path).ToArray());
        AssertChangedFileExcluded(session, "file-changed-during-hash");
    }

    [TestMethod]
    public void PureDuplicateCardsHaveNoSuppressedLinks()
    {
        var session = V2([Resource(1, (A, "same"), (B, "same"))], [(A, 1), (B, 1)], Duplicates(A, B));
        Assert.AreEqual(PackageConflictRelationKind.SuspectedSameMod, session.Grouping!.Groups.Single().Kind);
        var card = session.Cards.Single();
        Assert.AreEqual("duplicates", card.Type);
        Assert.AreEqual(1, card.GroupIndex);
        Assert.AreEqual(1, session.DuplicateGroupCount);
        Assert.AreEqual(1, Rows(session, "all").Count(row => row.Depth == 0));
        foreach (var path in new[] { A, B })
        {
            var relationships = session.Relationships(path)!;
            Assert.AreEqual(1, relationships.CardCount);
            Assert.AreEqual(card.Key, relationships.DuplicateLink!.Key);
            Assert.AreEqual(0, relationships.Related.Count);
            Assert.AreEqual(1, relationships.Copies.Count);
        }
    }

    [TestMethod]
    public void MixedDuplicateCardsKeepRealRelationsAndFoldCopies()
    {
        var session = V2([Resource(1, (A, "same"), (B, "same"), (C, "other"))], [(A, 1), (B, 1), (C, 1)], Duplicates(A, B));
        var mixed = session.Cards.Single(card => card.Type == "suspected");
        session.ToggleExpanded(mixed.Key);
        var files = Rows(session, "all").Where(row => row.OwnerKey == mixed.Key && row.File is not null).ToArray();
        Assert.AreEqual(2, files.Length);
        Assert.AreEqual(1, files.Single(row => row.File!.Path == A).CopyCount);
        Assert.IsTrue(files.Any(row => row.File!.Path == C));
        Assert.AreEqual(3, mixed.Package!.Relations.Count);
        var relationships = session.Relationships(C)!;
        Assert.AreEqual(1, relationships.CardCount);
        var partner = relationships.Related.Single();
        Assert.AreEqual(A, partner.File.Path);
        Assert.AreEqual(B, partner.Copies.Single().Path);
        Assert.IsTrue(partner.Relationships.All(relation => relation.Link.Key == mixed.Key));
        Assert.AreEqual(2, session.Relationships(A)!.CardCount);
        session.ApplyDeletedPaths([C]);
        Assert.AreEqual("duplicates", session.Cards.Single().Type);
        Assert.AreEqual(1, session.Relationships(A)!.CardCount);
        session.ApplyRestoredPaths([C]);
        Assert.AreEqual(2, session.Cards.Count);
        Assert.AreEqual(mixed.GroupIndex, session.Cards.Single(card => card.Key == mixed.Key).GroupIndex);
    }

    [TestMethod]
    public void HashReadFailureKeepsStableResourcesAndScriptEntryFailures()
    {
        var first = A + ".ts4script";
        var second = B + ".ts4script";
        var report = Duplicates(A, B) with { Groups = [], Issues = [new("hash-failed", ScanIssueStage.Hashing, C, "changed")] };
        var session = V2([Resource(1, (A, "same"), (B, "same"), (C, "other"))], [(A, 1), (B, 1), (C, 1)], report,
            Scripts([Collision("good", (first, "a"), (second, "b")), Collision("bad", (first, null), (second, "b"))]));
        Assert.IsTrue(session.Cards.Single(card => card.Package is not null).Files.Any(file => file.Path == C));
        Assert.AreEqual(ConflictProblemCategory.Access, session.Problems.Single(problem => problem.Path == C).Reasons.Single().Category);
        Assert.IsTrue(session.Cards.Any(card => card.Module?.ModuleName == "good" && card.Files.Any(file => file.Path == first)));
        Assert.IsTrue(session.Cards.Any(card => card.Module?.ModuleName == "bad"));
        var changed = report with { Issues = [new("file-changed-during-hash", ScanIssueStage.Hashing, first, "changed")] };
        var changedSession = V2([], [], changed, Scripts([Collision("good", (first, "a"), (second, "b"), (C + ".ts4script", "c"))]));
        Assert.IsFalse(changedSession.Cards.SelectMany(card => card.Files).Any(file => file.Path == first));
        Assert.AreEqual(0, changedSession.Relationships(first)!.Related.Count);
    }

    [TestMethod]
    public void ResourceIdenticalFilesInSeparateWholeFileGroupsRemainSuspected()
    {
        var report = Duplicates(A, A + ".copy");
        report = report with { Groups = report.Groups.Concat(Duplicates(B, B + ".copy").Groups
            .Select(group => group with { Sha256 = "OTHER-WHOLE-FILE-HASH" })).ToArray() };
        var session = V2([Resource(1, (A, "same"), (B, "same"))], [(A, 1), (B, 1)], report);
        var suspected = session.Cards.Single(card => card.Type == "suspected");
        session.ToggleExpanded(suspected.Key);
        Assert.AreEqual(3, session.Cards.Count);
        Assert.AreEqual(2, session.DuplicateGroupCount);
        Assert.AreEqual(2, Rows(session, "all").Count(row => row.OwnerKey == suspected.Key && row.File is not null));
    }

    [TestMethod]
    public Task PureAndMixedDuplicateCardsRenderWithConsistentRelationships() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        window.Left = 70;
        window.Top = 50;
        foreach (var mixed in new[] { false, true })
        {
            var comparisons = mixed ? new[] { Resource(1, (A, "same"), (B, "same"), (C, "other")) }
                : [Resource(1, (A, "same"), (B, "same"))];
            var session = V2(comparisons, mixed ? [(A, 1), (B, 1), (C, 1)] : [(A, 1), (B, 1)], Duplicates(A, B));
            foreach (var card in session.Cards) session.ToggleExpanded(card.Key);
            await SetSessionAsync(model, session, "all");
            foreach (var (width, height) in new[] { (1180, 780), (920, 640) })
            {
                window.Width = width;
                window.Height = height;
                await WpfTestHost.LayoutAsync(window);
                Assert.AreEqual(mixed ? 2 : 1, model.VisibleConflictParentCount);
                Assert.AreEqual(mixed, Descendants<TextBlock>(window).Any(text => text.IsVisible && text.Text == "疑似同一 mod"));
                Save(window, $"fix-{(mixed ? "mixed" : "pure")}-{width}x{height}.png");
            }
            var file = model.ConflictRows.First(row => row.File?.Path == (mixed ? C : A));
            model.OpenConflictRelationshipsCommand.Execute(file);
            await Task.Delay(280);
            await WpfTestHost.LayoutAsync(window);
            Assert.AreEqual(1, model.ConflictRelationships!.CardCount);
            Assert.AreEqual(mixed ? 1 : 0, model.ConflictRelationships.Related.Count);
            Save(window, $"fix-{(mixed ? "mixed" : "pure")}-relations.png");
            model.CloseConflictRelationshipsCommand.Execute(null);
            await Task.Delay(230);
        }
    });

    private static void AssertChangedFileExcluded(ConflictResultSession session, string code)
    {
        Assert.IsTrue(session.Problems.Single(problem => StringComparer.OrdinalIgnoreCase.Equals(problem.Path, C)).Reasons
            .Any(reason => reason.Code == code && reason.Category == ConflictProblemCategory.Changed));
        Assert.IsFalse(session.Cards.SelectMany(card => card.Files).Any(file => StringComparer.OrdinalIgnoreCase.Equals(file.Path, C)));
        Assert.IsFalse(session.Grouping!.GroupsByFile.ContainsKey(C));
        Assert.IsFalse(session.Resources.Any(pair => pair.FirstPath == C || pair.SecondPath == C));
        Assert.AreEqual(0, session.Relationships(C)!.Related.Count);
        Assert.AreEqual(0, session.Relationships(C)!.Copies.Count);
        Assert.AreEqual(0, session.Relationships(C)!.CardCount);
        Assert.IsFalse(session.Relationships(A)?.Related.Any(row => row.File.Path == C) == true);
        foreach (var card in session.Cards)
            Assert.IsFalse(session.TechnicalEntries(card).Any(entry => entry.CopyText.Contains(C, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void TechnicalVersionsUseReportEvidenceAndOnlyCardKeys()
    {
        var session = ContainmentSession();
        var card = session.Cards.Single(card => card.Type == "override");
        var entries = session.TechnicalEntries(card);
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(Resource(4).Key.Tgi, entries[0].Identifier);
        CollectionAssert.AreEquivalent(new[] { "版本 1", "版本 2" }, entries[0].Cells.Select(cell => cell.Version).ToArray());
        Assert.IsTrue(entries[0].CopyText.Contains("SHA256 d", StringComparison.Ordinal));
        Assert.IsTrue(entries[0].CopyText.Contains(B, StringComparison.Ordinal));
        session.ToggleExpanded(card.Key);
        var detail = Rows(session).Single(row => row.IsTechnicalToggle);
        session.ToggleExpanded(detail.Key);
        var copy = Rows(session).Single(row => row.Kind == ConflictRowKind.CopyDetails);
        Assert.IsTrue(copy.CopyText.Contains(entries[0].Identifier, StringComparison.Ordinal));
        Assert.IsFalse(copy.CopyText.Contains(Resource(1).Key.Tgi, StringComparison.Ordinal));
    }

    [TestMethod]
    public void CachedDeletionAndPartialRestoreRebuildGroupsWithoutReadingFiles()
    {
        var session = ContainmentSession();
        var original = session.Cards.Single(card => card.Type == "collection");
        session.ToggleExpanded(original.Key);
        session.ApplyDeletedPaths([A]);
        Assert.IsFalse(session.Cards.Any(card => card.Files.Any(file => file.Path == A)));
        Assert.AreEqual(1, session.Cards.Count);
        session.ApplyRestoredPaths([C]);
        Assert.AreEqual(1, session.Cards.Count);
        session.ApplyRestoredPaths([A]);
        Assert.AreEqual(original.GroupIndex, session.Cards.Single(card => card.Key == original.Key).GroupIndex);
        Assert.IsTrue(Rows(session).Single(row => row.Key == original.Key).IsExpanded);
    }

    [TestMethod]
    public void ExcludedFilesKeepStructuredReasonsAndNeverReenterRelationshipCards()
    {
        var session = V2([Resource(1, (A, "same"), (B, "same")), Resource(2, (A, "x"), (C, null))], [(A, 2), (B, 2), (C, 1)]);
        Assert.AreEqual(1, session.Grouping!.ExcludedFiles.Count);
        Assert.IsFalse(session.Cards.Any(card => card.Files.Any(file => file.Path == C)));
        Assert.IsTrue(session.Problems.Single(problem => problem.Path == C).Reasons.Any(reason => reason.Code == ResourceContentIssueCode.UnexpectedError));
        Assert.AreEqual(0, session.Relationships(C)!.Related.Count);
        Assert.IsFalse(session.Relationships(C)!.HasContainment);
        var hashing = V2([], [], Duplicates(A, B) with { Issues = [
            new("hash-failed", ScanIssueStage.Hashing, A, "扫描期间变化"),
            new("file-changed-during-hash", ScanIssueStage.Hashing, B, "无法访问"),
            new("source-unavailable", ScanIssueStage.SourceValidation, @"C:\Offline", "offline")] });
        Assert.AreEqual(ConflictProblemCategory.Access, hashing.Problems.Single(problem => problem.Path == A).Reasons.Single().Category);
        Assert.AreEqual(ConflictProblemCategory.Changed, hashing.Problems.Single(problem => problem.Path == B).Reasons.Single().Category);
        Assert.AreEqual(1, hashing.RangeProblems.Count);
        Assert.AreEqual(0, hashing.DuplicateGroupCount);
    }
}
