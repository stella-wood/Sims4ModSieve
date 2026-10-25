using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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

    internal static ConflictResultSession VersionSession(bool tiedLatest = false)
    {
        var stamps = new Dictionary<string, FileStamp>
        {
            [A] = Stamp,
            [B] = Stamp with { LastWriteTimeUtc = Stamp.LastWriteTimeUtc.AddDays(tiedLatest ? 2 : 1) },
            [C] = Stamp with { LastWriteTimeUtc = Stamp.LastWriteTimeUtc.AddDays(2) },
        };
        var comparison = Resource(1, (C, "new"), (A, "old"), (B, "middle"));
        comparison = comparison with { ContentGroups = comparison.ContentGroups.Select(group => group with
        {
            Occurrences = group.Occurrences.Select(item => item with { PackageStamp = stamps[item.PackagePath] }).ToArray(),
        }).ToArray() };
        var packages = ConflictResultSessionTests.Packages([(A, 2), (B, 2), (C, 2)]);
        packages = packages with { ResourceInventories = packages.ResourceInventories.Select(item =>
            item with { ScannedStamp = stamps[item.Path] }).ToArray() };
        var contents = Contents(comparison);
        contents = contents with { Packages = stamps.Select(item => new ResourceContentPackageVersion(item.Key, item.Value, item.Value, true)).ToArray() };
        var session = new ConflictResultSession();
        session.Replace(packages, contents, Scripts());
        return session;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SuspectedFilesSortNewestFirstWithStablePathTies(bool tiedLatest)
    {
        var session = VersionSession(tiedLatest);
        var card = session.Cards.Single();
        Assert.AreEqual("suspected", card.Type);
        session.ToggleExpanded(card.Key);
        var files = Rows(session).Where(row => row.Kind == ConflictRowKind.File).ToArray();
        CollectionAssert.AreEqual(tiedLatest ? new[] { B, C, A } : new[] { C, B, A }, files.Select(row => row.File!.Path).ToArray());
        CollectionAssert.AreEquivalent(tiedLatest ? new[] { B, C } : new[] { C }, files.Where(row => row.Tag == "最新").Select(row => row.File!.Path).ToArray());
        session.ApplyDeletedPaths([A]);
        session.ApplyRestoredPaths([A]);
        var restored = session.Cards.Single();
        Assert.AreEqual(card.Key, restored.Key);
        Assert.AreEqual(card.GroupIndex, restored.GroupIndex);
        CollectionAssert.AreEqual(files.Select(row => row.File!.Path).ToArray(), Rows(session).Where(row => row.Kind == ConflictRowKind.File).Select(row => row.File!.Path).ToArray());
    }

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
    [DataRow(4, 5, false, "suspected")]
    [DataRow(4, 5, true, "suspected")]
    [DataRow(10, 10, true, "override")]
    public void ResourceAnnotationsSeparateIdenticalDifferentAndUniqueTgis(int firstCount, int secondCount, bool different, string type)
    {
        var session = V2([Resource(1, (A, "one"), (B, "one")), Resource(2, (A, "two"), (B, "two")),
            Resource(3, (A, "three"), (B, different ? "changed" : "three"))], [(A, firstCount), (B, secondCount)]);
        var card = session.Cards.Single();
        Assert.AreEqual(type, card.Type);
        session.ToggleExpanded(card.Key);
        foreach (var (path, count) in new[] { (A, firstCount), (B, secondCount) })
        {
            var row = Rows(session).Single(row => row.File?.Path == path);
            Assert.AreEqual($"撞 {(different ? 1 : 0)} 样 · 独有 {count - 3} 样", row.Annotation);
            Assert.AreEqual($"相同TGI编号3个，其中{(different ? 2 : 3)}个内容相同，独有编号{count - 3}个", row.AnnotationTooltip);
        }
    }

    [TestMethod]
    public void ResourceAnnotationsCountEachTgiOnceWhenThreeFilesHaveMixedContents()
    {
        var session = V2([Resource(1, (A, "one"), (B, "one")),
            Resource(2, (A, "two"), (B, "two"), (C, "changed")),
            Resource(3, (A, "three"), (B, "changed"), (C, "three"))], [(A, 4), (B, 4), (C, 4)]);
        var card = session.Cards.Single();
        Assert.AreEqual("suspected", card.Type);
        Assert.AreEqual(3, card.Files.Count);
        session.ToggleExpanded(card.Key);
        foreach (var path in new[] { A, B })
        {
            var row = Rows(session).Single(row => row.File?.Path == path);
            Assert.AreEqual("撞 2 样 · 独有 1 样", row.Annotation);
            Assert.AreEqual("相同TGI编号3个，其中1个内容相同，独有编号1个", row.AnnotationTooltip);
        }
        var third = Rows(session).Single(row => row.File?.Path == C);
        Assert.AreEqual("撞 2 样 · 独有 2 样", third.Annotation);
        Assert.AreEqual("相同TGI编号2个，其中0个内容相同，独有编号2个", third.AnnotationTooltip);
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
        Assert.IsTrue(duplicateFiles.All(file => !file.HasTag));
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
        CollectionAssert.AreEquivalent(new[] { A }, session.Cards.Single().Files.Select(file => file.Path).ToArray());
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
        CollectionAssert.AreEquivalent(new[] { A }, session.Cards.Single().Files.Select(file => file.Path).ToArray());
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
        Assert.AreEqual(2, session.Cards.Count);
        Assert.AreEqual(2, session.Relationships(A)!.CardCount);
        Assert.AreEqual(mixed.Key, session.Cards.Single(card => card.Type == "suspected").Key);
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
    [DataRow(false)]
    [DataRow(true)]
    public void CopiedTechnicalDetailsKeepVersionsAndScannedFileMetadata(bool scripts)
    {
        const string old = @"C:\Mods\Old\a.package";
        var session = scripts
            ? V2([], [], scripts: Scripts([Collision("pkg.mod", (A, "same"), (old, "same"), (C, "other"))]))
            : V2([Resource(1, (A, "same"), (old, "same"), (C, "other"))], [(A, 1), (old, 1), (C, 1)]);
        var card = session.Cards.Single();
        var entry = session.TechnicalEntries(card).Single();
        foreach (var file in card.Files)
        {
            var cell = entry.Cells.Single(cell => cell.Evidence.StartsWith(file.Path + "\n", StringComparison.Ordinal));
            Assert.AreEqual(file.Path == C ? "版本 2" : "版本 1", cell.Version);
            StringAssert.Contains(cell.Evidence, $"修改时间：{Stamp.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            StringAssert.Contains(cell.Evidence, "文件大小：10 字节");
            StringAssert.Contains(cell.Evidence, "SHA256 " + (file.Path == C ? "other" : "same"));
            StringAssert.Contains(cell.Evidence, (scripts ? "模块" : "资源") + "解压大小 1 字节");
        }
        session.ToggleExpanded(card.Key);
        session.ToggleExpanded(Rows(session, "all").Single(row => row.IsTechnicalToggle).Key);
        var copy = Rows(session, "all").Single(row => row.Kind == ConflictRowKind.CopyDetails);
        StringAssert.Contains(copy.CopyText, entry.Identifier + "\t" + entry.TypeName);
        foreach (var cell in entry.Cells) StringAssert.Contains(copy.CopyText, cell.FileName + "\t" + cell.Version + "\t" + cell.Evidence);
        Assert.IsFalse(copy.CopyText.Contains("结论：", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ResourceTechnicalDetailsDistinguishMissingResourcesFromFailedComparisons()
    {
        const string failedPath = @"C:\Mods\failed.package";
        var first = Resource(1, (A, "same"), (B, "same"), (failedPath, null));
        var issue = new ResourceContentIssue(ResourceContentIssueCode.Corrupt, ResourceContentStage.Decompress,
            "资源解压失败", "invalid zlib data");
        first = first with { Uncompared = [first.Uncompared.Single() with { Issue = issue }] };
        var session = V2([first, Resource(2, (A, "same"), (B, "same"), (C, "same"))], [(A, 2), (B, 2), (C, 1), (failedPath, 1)]);
        var card = session.Cards.Single();
        var entries = session.TechnicalEntries(card);
        var firstEntry = entries.Single(entry => entry.Identifier == first.Key.Tgi);
        Assert.AreEqual("—", firstEntry.Cells.Single(cell => cell.FileName == "c.package").Version);
        Assert.IsFalse(firstEntry.Cells.Single(cell => cell.FileName == "c.package").Evidence.Contains("SHA256", StringComparison.Ordinal));
        Assert.IsFalse(card.Files.Any(file => file.Path == failedPath));
        Assert.IsTrue(session.Problems.Single(problem => problem.Path == failedPath).Reasons.Any(reason => reason.Code == issue.Code));
        // 原始失败证据仍可被技术投影解释，不将缺席资源误标为失败。
        var withFailedFile = card with { Files = card.Files.Append(new ConflictFile(failedPath, Stamp)).ToArray() };
        var failed = session.TechnicalEntries(withFailedFile).Single(entry => entry.Identifier == first.Key.Tgi).Cells.Last();
        Assert.AreEqual("未完成：资源解压失败", failed.Version);
        StringAssert.Contains(failed.Evidence, issue.Code);
        StringAssert.Contains(failed.Evidence, "Decompress");
        StringAssert.Contains(failed.Evidence, issue.Detail!);
        Assert.IsFalse(failed.Evidence.Contains("SHA256", StringComparison.Ordinal));
        Assert.IsFalse(failed.Evidence.Contains("资源解压大小", StringComparison.Ordinal));
    }

    [TestMethod]
    public Task ScriptFailureReasonRendersAndCopyButtonUsesCompleteDetails() => WpfTestHost.RunAsync(async () =>
    {
        var issue = new ScriptArchiveIssue(ScriptArchiveIssueCode.EntryCorrupt, ScriptArchiveIssueStage.Entry,
            C, "脚本条目损坏", EntryName: "pkg/mod.pyc", Detail: "CRC mismatch");
        var collision = Collision("pkg.mod", (A, "same"), (B, "same"), (C, null));
        collision = collision with { Uncompared = [collision.Uncompared.Single() with { Issue = issue }] };
        var session = V2([], [], scripts: Scripts([collision], [issue]));
        var card = session.Cards.Single();
        session.ToggleExpanded(card.Key);
        session.ToggleExpanded(Rows(session, "scripts").Single(row => row.IsTechnicalToggle).Key);
        string? copied = null;
        var model = CreateModel(text => copied = text);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, session, "scripts");
        var list = Descendants<ListBox>(window).Single(item => item.Name == "ConflictRowsList");
        var technical = model.ConflictRows.Single(row => row.TechnicalEntry?.Identifier == "pkg.mod");
        list.ScrollIntoView(technical);
        await WpfTestHost.LayoutAsync(window);
        var reason = Descendants<TextBlock>(list).Single(text => text.Text == "未完成：脚本条目损坏");
        Assert.IsTrue(reason.IsVisible);
        var evidence = reason.ToolTip!.ToString()!;
        StringAssert.Contains(evidence, issue.Code);
        StringAssert.Contains(evidence, issue.Detail!);
        StringAssert.Contains(evidence, "修改时间：");
        StringAssert.Contains(evidence, "文件大小：10 字节");
        Assert.IsFalse(evidence.Contains("SHA256", StringComparison.Ordinal));
        Assert.IsFalse(evidence.Contains("模块解压大小", StringComparison.Ordinal));
        Save(window, "issue9-script-failure.png");
        var copy = model.ConflictRows.Single(row => row.Kind == ConflictRowKind.CopyDetails);
        list.ScrollIntoView(copy);
        await WpfTestHost.LayoutAsync(window);
        var button = Descendants<Button>(list).Single(item => ReferenceEquals(item.DataContext, copy));
        var center = new Point(button.ActualWidth / 2, button.ActualHeight / 2);
        var hit = window.InputHitTest(button.TranslatePoint(center, window)) as DependencyObject;
        while (hit is not null && !ReferenceEquals(hit, button)) hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        Assert.AreSame(button, hit);
        var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!;
        invoke.Invoke();
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(copy.CopyText, copied);
        StringAssert.Contains(copied!, "未完成：脚本条目损坏");
        StringAssert.Contains(copied!, "SHA256 same");
        StringAssert.Contains(copied!, C);
        var output = Environment.GetEnvironmentVariable("SIMS4_CONFLICT_REVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(output)) System.IO.File.WriteAllText(System.IO.Path.Combine(output, "issue9-copied-details.txt"), copied);
    });

    [TestMethod]
    public void CachedDeletionAndPartialRestoreRebuildGroupsWithoutReadingFiles()
    {
        var session = ContainmentSession();
        var original = session.Cards.Single(card => card.Type == "collection");
        session.ToggleExpanded(original.Key);
        session.ApplyDeletedPaths([A]);
        Assert.IsFalse(session.Cards.Any(card => card.Files.Any(file => file.Path == A)));
        Assert.AreEqual(2, session.Cards.Count);
        session.ApplyRestoredPaths([C]);
        Assert.AreEqual(2, session.Cards.Count);
        session.ApplyRestoredPaths([A]);
        Assert.AreEqual(original.GroupIndex, session.Cards.Single(card => card.Key == original.Key).GroupIndex);
        Assert.IsTrue(Rows(session).Single(row => row.Key == original.Key).IsExpanded);
    }

    [TestMethod]
    [DataRow("resources")]
    [DataRow("scripts")]
    [DataRow("duplicates")]
    public void DeletedPairKeepsRemainingFileAndRestoresOriginalCard(string kind)
    {
        var session = kind switch
        {
            "scripts" => V2([], [], scripts: Scripts([Collision("pkg.main", (A, "one"), (B, "two"))])),
            "duplicates" => V2([], [], Duplicates(A, B)),
            _ => V2([Resource(1, (A, "one"), (B, "two"))], [(A, 10), (B, 10)]),
        };
        var original = session.Cards.Single();
        session.ToggleExpanded(original.Key);
        session.ToggleExpanded(original.Key + "/technical");
        var deletion = session.ProjectionSnapshot();
        deletion.ApplyDeletedPaths([A]);
        session.AcceptFileChanges(deletion);
        var remaining = session.Cards.Single();
        Assert.AreEqual(original.Key, remaining.Key);
        Assert.AreEqual(original.GroupIndex, remaining.GroupIndex);
        Assert.AreEqual(B, remaining.Files.Single().Path);
        Assert.AreEqual("已移除 1 个文件，剩余 1 个文件", remaining.Explanation);
        var rows = Rows(session, "all");
        Assert.IsTrue(rows.Single(row => row.IsCardStart).IsExpanded);
        Assert.AreEqual(B, rows.Single(row => row.File is not null).File!.Path);
        Assert.IsFalse(rows.Any(row => row.IsTechnicalToggle));
        if (kind == "resources")
            Assert.AreEqual("撞 0 样 · 独有 10 样", rows.Single(row => row.File is not null).Annotation);
        else Assert.IsFalse(rows.Any(row => row.HasAnnotation));
        Assert.IsNull(session.Relationships(A));
        Assert.AreEqual(0, session.Relationships(B)!.Related.Count);
        Assert.AreEqual(0, session.Relationships(B)!.Copies.Count);
        session.ApplyDeletedPaths([B]);
        Assert.AreEqual(0, session.Cards.Count);
        var restore = session.ProjectionSnapshot();
        restore.ApplyRestoredPaths([A, B]);
        session.AcceptFileChanges(restore);
        Assert.AreEqual(original, session.Cards.Single());
        Assert.IsTrue(Rows(session, "all").Single(row => row.IsCardStart).IsExpanded);
        if (kind != "duplicates")
            Assert.IsTrue(Rows(session, "all").Single(row => row.IsTechnicalToggle).IsExpanded);
    }

    [TestMethod]
    public void DeletedMemberKeepsRemainingDirectRelationsAndTechnicalEvidence()
    {
        var session = V2([Resource(1, (A, "one"), (B, "two"), (C, "three")),
            Resource(2, (A, "one"), (B, "two"))], [(A, 10), (B, 10), (C, 10)]);
        var original = session.Cards.Single(card => card.Files.Count == 3);
        session.ToggleExpanded(original.Key);
        session.ToggleExpanded(original.Key + "/technical");
        session.ApplyDeletedPaths([A]);
        var remaining = session.Cards.Single(card => card.Key == original.Key);
        Assert.AreEqual(original.GroupIndex, remaining.GroupIndex);
        CollectionAssert.AreEquivalent(new[] { B, C }, remaining.Files.Select(file => file.Path).ToArray());
        var entries = session.TechnicalEntries(remaining);
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(2, entries.Single().Cells.Count);
        Assert.IsFalse(entries.Single().CopyText.Contains(A, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(entries.Single().HasDifferentVersions);
        Assert.AreEqual(C, session.Relationships(B)!.Related.Single().File.Path);
        Assert.IsNull(session.Relationships(A));
        Assert.IsTrue(Rows(session).Single(row => row.OwnerKey == original.Key && row.IsTechnicalToggle).IsExpanded);
    }

    [TestMethod]
    public void DeletionRecomputesOverrideUniqueCountsUsingAllRemainingMemberPairs()
    {
        var session = V2([Resource(1, (A, "different"), (B, "same"), (C, "same")),
            Resource(2, (B, "same"), (C, "same"))], [(A, 10), (B, 10), (C, 10)]);
        var card = session.Cards.Single();
        session.ToggleExpanded(card.Key);
        Assert.AreEqual(8, card.Package!.Members.Single(member => member.Path == B).UniqueResourceCount);
        session.ApplyDeletedPaths([C]);
        var row = Rows(session).Single(row => row.File?.Path == B);
        Assert.AreEqual("撞 1 样 · 独有 9 样", row.Annotation);
        Assert.AreEqual("相同TGI编号1个，其中0个内容相同，独有编号9个", row.AnnotationTooltip);
        session.ApplyRestoredPaths([C]);
        Assert.AreEqual("撞 1 样 · 独有 8 样", Rows(session).Single(row => row.File?.Path == B).Annotation);
        session.ApplyDeletedPaths([A]);
        row = Rows(session).Single(row => row.File?.Path == B);
        Assert.AreEqual("撞 0 样 · 独有 8 样", row.Annotation);
        Assert.AreEqual("相同TGI编号0个，其中0个内容相同，独有编号8个", row.AnnotationTooltip);
        Assert.IsNull(session.Relationships(A));
    }

    [TestMethod]
    public void DeletionRecomputesContainmentAndClearsLabelsWhenBundleIsRemoved()
    {
        var session = V2([Resource(1, (A, "same"), (B, "same")), Resource(2, (A, "same"), (B, "same")),
            Resource(3, (A, "same"), (B, "same")), Resource(4, (B, "same"), (C, "same")),
            Resource(5, (B, "same"), (C, "same")), Resource(6, (B, "same"), (C, "same"))], [(A, 3), (B, 10), (C, 3)]);
        var card = session.Cards.Single();
        session.ToggleExpanded(card.Key);
        Assert.AreEqual(4, card.Package!.BundleUniqueResourceCount);
        session.ApplyDeletedPaths([A]);
        var remaining = session.Cards.Single();
        Assert.AreEqual(7, remaining.Package!.BundleUniqueResourceCount);
        Assert.AreEqual("独有 7 样", Rows(session).Single(row => row.File?.Path == B).Annotation);
        Assert.AreEqual("已被包含", Rows(session).Single(row => row.File?.Path == C).Annotation);
        session.ApplyRestoredPaths([A]);
        session.ApplyDeletedPaths([B]);
        Assert.IsNull(session.Cards.Single().Package!.BundlePath);
        foreach (var row in Rows(session).Where(row => row.File is not null))
        {
            Assert.AreEqual("撞 0 样 · 独有 3 样", row.Annotation);
            Assert.AreEqual("相同TGI编号0个，其中0个内容相同，独有编号3个", row.AnnotationTooltip);
            Assert.IsFalse(row.HasTag || row.IsBundle);
        }
        Assert.IsFalse(Rows(session).Any(row => row.IsTechnicalToggle));
    }

    [TestMethod]
    public void DeletionKeepsLatestTagOnNewestRemainingFileIncludingSingleton()
    {
        var session = VersionSession();
        var card = session.Cards.Single();
        session.ToggleExpanded(card.Key);
        session.ApplyDeletedPaths([C]);
        Assert.AreEqual("最新", Rows(session).Single(row => row.File?.Path == B).Tag);
        Assert.AreEqual("", Rows(session).Single(row => row.File?.Path == A).Tag);
        session.ApplyDeletedPaths([B]);
        var only = Rows(session).Single(row => row.File is not null);
        Assert.AreEqual(A, only.File!.Path);
        Assert.AreEqual("最新", only.Tag);
        Assert.AreEqual("撞 0 样 · 独有 2 样", only.Annotation);
        Assert.IsFalse(Rows(session).Any(row => row.IsTechnicalToggle));
        session.ApplyRestoredPaths([B, C]);
        Assert.AreEqual("最新", Rows(session).Single(row => row.File?.Path == C).Tag);
        Assert.AreEqual(card.GroupIndex, Rows(session).Single(row => row.IsCardStart).GroupIndex);
    }

    [TestMethod]
    public void DeletedScriptMemberLeavesOnlyActiveModuleEvidence()
    {
        var session = V2([], [], scripts: Scripts([Collision("pkg.main", (A, "one"), (B, "two"), (C, "three"))]));
        var original = session.Cards.Single();
        session.ToggleExpanded(original.Key);
        session.ToggleExpanded(original.Key + "/technical");
        session.ApplyDeletedPaths([A]);
        var card = session.Cards.Single();
        Assert.AreEqual(original.Key, card.Key);
        CollectionAssert.AreEquivalent(new[] { B, C }, card.Module!.Occurrences.Select(item => item.ArchivePath).ToArray());
        var entry = session.TechnicalEntries(card).Single();
        Assert.AreEqual(2, entry.Cells.Count);
        Assert.IsFalse(entry.CopyText.Contains(A, StringComparison.OrdinalIgnoreCase));
        var copy = Rows(session, "all").Single(row => row.Kind == ConflictRowKind.CopyDetails);
        Assert.IsFalse(copy.CopyText.Contains(A, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(C, session.Relationships(B)!.Related.Single().File.Path);
        Assert.IsNull(session.Relationships(A));
    }

    [TestMethod]
    public void BulkExpansionOnlyChangesVisibleCardKeysAndPreservesTechnicalState()
    {
        var session = ContainmentSession();
        var red = session.Cards.Single(card => card.Type == "override");
        var yellow = session.Cards.Single(card => card.Type == "collection");
        session.ToggleExpanded(red.Key + "/technical");
        session.ToggleAllGroups(Rows(session, filter: "different"));
        Assert.IsTrue(Rows(session).Single(row => row.Key == red.Key).IsExpanded);
        Assert.IsFalse(Rows(session).Single(row => row.Key == yellow.Key).IsExpanded);
        Assert.AreEqual("收起", Rows(session).Single(row => row.IsTechnicalToggle).Title);
        session.ToggleAllGroups(Rows(session, filter: "same"));
        Assert.IsTrue(Rows(session).Where(row => row.IsCardStart).All(row => row.IsExpanded));
        session.ToggleAllGroups(Rows(session));
        Assert.IsTrue(Rows(session).All(row => !row.IsExpanded));
        Assert.IsTrue(session.GetExpandedKeysSnapshot().Contains(red.Key + "/technical"));
        session.ToggleAllGroups(Rows(session));
        Assert.IsTrue(Rows(session).Single(row => row.OwnerKey == red.Key && row.IsTechnicalToggle).IsExpanded);
        Assert.IsFalse(Rows(session).Single(row => row.OwnerKey == yellow.Key && row.IsTechnicalToggle).IsExpanded);
        session.Reset();
        Assert.AreEqual(0, session.GetExpandedKeysSnapshot().Count);
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
