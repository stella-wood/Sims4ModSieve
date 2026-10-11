using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Desktop.ViewModels;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ConflictResultSessionTests
{
    internal static readonly FileStamp Stamp = new(10, DateTime.UnixEpoch);
    internal const string A = @"C:\Mods\a.package";
    internal const string B = @"C:\Mods\b.package";
    internal const string C = @"C:\Mods\c.package";
    internal static readonly IReadOnlySet<string> Closed = new HashSet<string>();
    internal static readonly Regex TechnicalText = new(@"[0-9A-Fa-f]{8}[:/-][0-9A-Fa-f]{8}|\b[0-9A-Fa-f]{32,}\b", RegexOptions.Compiled);

    [TestMethod]
    public void ThreeFilesKeepPairFactsAndOriginalContentGroups()
    {
        var comparison = Resource(1, (A, "same"), (B, "same"), (C, "other"));
        var session = Session([comparison], [(A, 1), (B, 1), (C, 1)]);
        Assert.AreEqual(3, session.Resources.Count);
        var pair = session.Resources.Single(pair => pair.FirstPath == A && pair.SecondPath == B);
        Assert.AreEqual(ConflictPairPriority.CanOrganize, pair.Priority);
        Assert.IsFalse(pair.HasConfirmedDifference || pair.HasIncomplete);
        Assert.AreSame(comparison, pair.Resources.Single().Comparison);
        Assert.AreSame(comparison.ContentGroups, pair.Resources.Single().Comparison.ContentGroups);
        var parent = Rows(session).Single(row => row.Tooltip == A && row.SecondTooltip == B);
        Assert.AreEqual(ConflictPairPriority.CanOrganize, parent.Priority);
        StringAssert.Contains(parent.Detail, "两份文件的资源完全一致");
        session.ToggleExpanded(parent.Key);
        var child = Rows(session).Single(row => row.Key.StartsWith(parent.Key + "/resource", StringComparison.Ordinal));
        Assert.AreEqual(ConflictPairPriority.CanOrganize, child.Priority);
        Assert.AreEqual("捏人部件", child.Title);
        session.ToggleExpanded(child.Key);
        var groups = Rows(session).Where(row => row.Key.StartsWith(child.Key) && row.Kind == ConflictRowKind.Content).ToArray();
        Assert.AreEqual(2, groups.Length);
        Assert.IsTrue(Rows(session).Any(row => row.Key == child.Key + "/technical"));
        Assert.IsFalse(Rows(session).Any(row => row.Kind == ConflictRowKind.Technical && row.Title.Contains("TGI", StringComparison.Ordinal)));
        session.ToggleExpanded(groups[0].Key);
        Assert.AreEqual(2, Rows(session).Count(row => row.Kind == ConflictRowKind.File));
        Assert.IsFalse(Rows(session).Any(row => row.Kind == ConflictRowKind.Technical && row.Title.Contains("SHA256", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ThirdFailureDoesNotChangeSuccessfulPairOrContainment()
    {
        var comparison = Resource(1, (A, "same"), (B, "same"), (C, null));
        var session = Session([comparison], [(A, 1), (B, 1), (C, 1)]);
        var pair = session.Resources.Single(pair => pair.FirstPath == A && pair.SecondPath == B);
        Assert.IsFalse(pair.HasIncomplete);
        Assert.IsTrue(pair.FirstContainedInSecond && pair.SecondContainedInFirst);
        Assert.AreEqual(1, session.Problems.Single().IncompleteCount);
    }

    [TestMethod]
    public void MixedPairsMatchBothFiltersAndKeepEveryDetailInCorePriorityOrder()
    {
        var session = Session([Resource(3, (A, "x"), (B, "x")), Resource(2, (A, "x"), (B, "y")), Resource(1, (A, "x"), (B, null))]);
        var parent = Rows(session).Single();
        Assert.AreEqual(ConflictPairPriority.NeedsReview, parent.Priority);
        StringAssert.Contains(parent.Detail, "未完成 1");
        StringAssert.Contains(parent.Detail, "疑似同一 mod 的两个版本");
        Assert.AreEqual(1, Rows(session, filter: "different").Count);
        Assert.AreEqual(1, Rows(session, filter: "same").Count);
        session.ToggleExpanded(parent.Key);
        var children = Rows(session, filter: "same").Where(row => row.Kind == ConflictRowKind.Resource).ToArray();
        Assert.AreEqual(3, children.Length);
        CollectionAssert.AreEqual(new[] { ConflictPairPriority.NeedsReview, ConflictPairPriority.CanOrganize, ConflictPairPriority.Incomplete }, children.Select(row => row.Priority!.Value).ToArray());
        Assert.AreEqual(3, Rows(session, filter: "different").Count(row => row.Kind == ConflictRowKind.Resource));
    }

    [TestMethod]
    public void ContainmentUsesInventoryAndStableVersionEvidenceInBothDirections()
    {
        foreach (var counts in new[] { (1, 2), (2, 1), (1, 1) })
        {
            var session = Session([Resource(1, (A, "x"), (B, "x"))], [(A, counts.Item1), (B, counts.Item2)]);
            Assert.IsNotNull(session.PackageReport);
            Assert.AreEqual(2, session.PackageReport.ResourceInventories.Count);
            Assert.IsTrue(session.ContentReport!.Packages.All(version => version.IsStable && version.ScannedStamp == Stamp && version.StampAfterRead == Stamp));
            var text = Rows(session).Single().Detail;
            StringAssert.Contains(text, counts == (1, 1) ? "两份文件的资源完全一致"
                : counts == (1, 2) ? "a.package 已被 b.package 完全包含，删除 a.package 不会丢失内容"
                : "b.package 已被 a.package 完全包含，删除 b.package 不会丢失内容");
        }
        Assert.AreEqual(0, Session([Resource(1, (A, "x"), (B, "x"))]).Resources.Count);
    }

    [TestMethod]
    public void ScriptPairsKeepModulesAndFullContextWithoutArchiveContainment()
    {
        var collisions = new[] { Collision("pkg.a", (A, "x"), (B, "y"), (C, "x")), Collision("pkg.b", (A, "x"), (B, "x")) };
        var session = Session([], scripts: Scripts(collisions));
        Assert.AreEqual(2, session.Scripts.Count);
        var parent = Rows(session, "scripts").Single(row => row.Tooltip == A && row.SecondTooltip == B);
        StringAssert.Contains(parent.Detail, "疑似同一 mod 的两个版本");
        Assert.IsFalse(parent.Detail.Contains("包含", StringComparison.Ordinal));
        session.ToggleExpanded(parent.Key);
        var modules = Rows(session, "scripts", "same").Where(row => row.Kind == ConflictRowKind.Module).ToArray();
        Assert.AreEqual(2, modules.Length);
        session.ToggleExpanded(modules[0].Key);
        var group = Rows(session, "scripts").First(row => row.Kind == ConflictRowKind.Content);
        session.ToggleExpanded(group.Key);
        Assert.AreEqual(2, Rows(session, "scripts").Count(row => row.Kind == ConflictRowKind.File));
        Assert.AreSame(collisions[0], session.Scripts.Single(pair => pair.FirstPath == A && pair.SecondPath == B).Modules[0].Collision);
    }

    [TestMethod]
    public void ProblemsDeduplicateTgiAndOrdinalEntryNamesAndMergeUncollidedFailures()
    {
        var first = new ScriptArchiveIssue(ScriptArchiveIssueCode.EntrySizeExceedsLimit, ScriptArchiveIssueStage.Entry, A, "misleading message", EntryName: "pkg/A.pyc");
        var other = first with { Path = A.ToUpperInvariant(), EntryName = "pkg/a.pyc" };
        var failure = Module("pkg.A", A, null) with { EntryName = first.EntryName!, Issue = first };
        var session = Session([Resource(1, (B, null)), Resource(1, (B.ToUpperInvariant(), null)), Resource(2, (B, null))],
            scripts: Scripts([new("pkg.A", [], [failure])], [first, other, other with { EntryName = "uncollided.pyc" }]));
        Assert.AreEqual(2, session.Problems.Count);
        Assert.AreEqual(3, session.Problems.Single(problem => problem.Path == A).IncompleteCount);
        Assert.AreEqual(2, session.Problems.Single(problem => problem.Path == B).IncompleteCount);
        Assert.AreEqual("单条资源过大", session.Problems.Single(problem => problem.Path == A).Summary);
        var problem = Rows(session, "problems").Single(row => row.Tooltip == A);
        Assert.IsFalse(problem.Detail.Contains(first.Code, StringComparison.Ordinal));
        Assert.IsFalse(problem.Detail.Contains("uncollided.pyc", StringComparison.Ordinal));
        session.ToggleExpanded(problem.Key);
        var detail = Rows(session, "problems").Single(row => row.Key == problem.Key + "/technical");
        Assert.AreEqual("技术详情", detail.Title);
        Assert.IsFalse(detail.IsExpanded);
        Assert.AreEqual("", detail.Tooltip);
        Assert.IsTrue(Rows(session, "problems").Any(row => row.Kind == ConflictRowKind.Explanation && row.Title.Contains("uncollided.pyc", StringComparison.Ordinal)));
        Assert.AreEqual(2, Rows(session, "problems").Count(row => row.Depth == 0));
        session.ToggleExpanded(detail.Key);
        Assert.IsTrue(Rows(session, "problems").Any(row => row.Kind == ConflictRowKind.Technical && row.Title.Contains(first.Code, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void WholeUnknownMultipleReasonsAndRangesStaySeparate()
    {
        var scripts = Scripts(issues: [
            new(ScriptArchiveIssueCode.EntryCorrupt, ScriptArchiveIssueStage.Entry, A, "ignored", EntryName: "one.pyc"),
            new(ScriptArchiveIssueCode.EntryCorrupt, ScriptArchiveIssueStage.Entry, A, "ignored", EntryName: "two.pyc"),
            new(ScriptArchiveIssueCode.BudgetExhausted, ScriptArchiveIssueStage.Budget, A, "ignored", EntryName: "three.pyc")]);
        var packages = Packages(paths: [A], issues: [new(PackageReadIssueCode.UnsupportedVersion, PackageConflictScanIssueStage.IndexRead, A, "ignored"),
            new("source-unavailable", PackageConflictScanIssueStage.SourceValidation, @"C:\Offline", "ignored")]);
        var session = new ConflictResultSession();
        session.Replace(packages, Contents(), scripts);
        var problem = session.Problems.Single();
        Assert.IsNull(problem.IncompleteCount);
        Assert.AreEqual("数量未知", problem.CountText);
        Assert.AreEqual("整个文件未分析 · one.pyc、two.pyc、three.pyc", problem.DetailText);
        Assert.AreEqual("文件损坏，建议重新下载", problem.Summary);
        Assert.AreEqual(5, problem.Reasons.Count);
        Assert.AreEqual(1, session.RangeProblems.Count);
        Assert.AreEqual(2, Rows(session, "problems").Count);
        var problemRow = Rows(session, "problems")[0];
        Assert.AreEqual("数量未知 · 文件损坏，建议重新下载", problemRow.Detail);
        Assert.IsFalse(problemRow.Detail.Contains("one.pyc", StringComparison.Ordinal));
        session.ToggleExpanded(problemRow.Key);
        Assert.AreEqual(8, Rows(session, "problems").Count);
        session.ToggleExpanded(Rows(session, "problems").Single(row => row.Title == "技术详情").Key);
        Assert.AreEqual(14, Rows(session, "problems").Count);
    }

    [TestMethod]
    public void StructuredReasonsIgnoreMessagesAndKeepFixedTieOrder()
    {
        var cases = new[] {
            (ScriptArchiveIssueCode.EntryCorrupt, ScriptArchiveIssueStage.Entry, "文件损坏，建议重新下载"),
            (ScriptArchiveIssueCode.Zip64Unsupported, ScriptArchiveIssueStage.Structure, "格式暂不支持"),
            (ScriptArchiveIssueCode.EntrySizeExceedsLimit, ScriptArchiveIssueStage.Entry, "单条资源过大"),
            ("custom", ScriptArchiveIssueStage.Stability, "扫描期间文件被修改"),
            ("custom", ScriptArchiveIssueStage.Access, "无法访问"),
            (ScriptArchiveIssueCode.TooLarge, ScriptArchiveIssueStage.Structure, "文件过大"),
            ("custom", ScriptArchiveIssueStage.Budget, "分析额度用尽"),
            ("custom", ScriptArchiveIssueStage.Unexpected, "未知读取失败") };
        foreach (var (code, stage, expected) in cases)
            Assert.AreEqual(expected, Session([], scripts: Scripts(issues: [new(code, stage, A, "损坏 不支持 超限", EntryName: "a.py")])).Problems.Single().Summary);
        var tie = Session([], scripts: Scripts(issues: [new(ScriptArchiveIssueCode.EntrySizeExceedsLimit, ScriptArchiveIssueStage.Entry, A, "", EntryName: "a.py"),
            new(ScriptArchiveIssueCode.EntryCorrupt, ScriptArchiveIssueStage.Entry, A, "", EntryName: "b.py")]));
        Assert.AreEqual("文件损坏，建议重新下载", tie.Problems.Single().Summary);
    }

    [TestMethod]
    public void CollapsedRowsAreParentsOnlyAndExpansionDoesNotCrossPairs()
    {
        var session = Session([Resource(1, (A, "a"), (B, "b"), (C, "c"))]);
        Assert.AreEqual(3, Rows(session).Count);
        Assert.IsTrue(Rows(session).All(row => row.Depth == 0));
        var parent = Rows(session)[0];
        session.ToggleExpanded(parent.Key);
        var resource = Rows(session).Single(row => row.Kind == ConflictRowKind.Resource);
        session.ToggleExpanded(resource.Key);
        Assert.AreEqual(3, Rows(session).Count(row => row.Kind == ConflictRowKind.Content));
        Assert.AreEqual(1, Rows(session).Count(row => row.Kind == ConflictRowKind.Resource));
        session.ToggleExpanded(parent.Key);
        Assert.AreEqual(3, Rows(session).Count);
        session.Reset();
        Assert.AreEqual(0, session.GetExpandedKeysSnapshot().Count);
        Assert.AreEqual(0, Rows(session).Count);
        Assert.IsNull(session.ContentReport);
    }

    [TestMethod]
    public void ResourceContentsHideTechnicalValuesUntilTheDetailIsExpanded()
    {
        var session = Session([Resource(1, (A, "alpha"), (B, "beta")), Resource(2, (A, "same"), (B, "same"))], [(A, 2), (B, 2)]);
        session.ToggleExpanded(Rows(session).Single().Key);
        var resources = Rows(session).Where(row => row.Kind == ConflictRowKind.Resource).ToArray();
        Assert.AreEqual(2, resources.Length);
        Assert.IsTrue(resources.All(row => row.Title == "捏人部件"));
        foreach (var resource in resources) session.ToggleExpanded(resource.Key);
        var rows = Rows(session);
        Assert.IsFalse(visibleTechnical(rows));
        var content = rows.Where(row => row.Kind == ConflictRowKind.Content).ToArray();
        Assert.IsTrue(content.All(row => !row.Detail.Contains("SHA256", StringComparison.Ordinal)));
        var technical = rows.Where(row => row.Kind == ConflictRowKind.Technical && row.Title == "技术详情").ToArray();
        Assert.IsTrue(technical.Length >= 1);
        Assert.IsTrue(technical.All(row => !row.IsExpanded));
        Assert.AreEqual("", technical[0].Tooltip);
        AssertOrdinaryRows(rows);
        session.ToggleExpanded(technical[0].Key);
        var opened = Rows(session);
        Assert.IsTrue(opened.Single(row => row.Key == technical[0].Key).IsExpanded);
        var value = opened.Single(row => row.Key == technical[0].Key + "0");
        Assert.IsTrue(value.Title.StartsWith("TGI", StringComparison.Ordinal));
        Assert.AreEqual(value.Title, value.Tooltip);
        session.ToggleExpanded(technical[0].Key);
        var closed = Rows(session);
        Assert.IsFalse(closed.Single(row => row.Key == technical[0].Key).IsExpanded);
        Assert.IsFalse(closed.Any(row => row.Kind == ConflictRowKind.Technical && row.Title != "技术详情"));
        Assert.IsFalse(closed.Any(row => row.Key.StartsWith(technical[0].Key, StringComparison.Ordinal) && row.Key != technical[0].Key));

        static bool visibleTechnical(IReadOnlyList<ConflictResultRow> rows) => rows.Where(row => row.Kind != ConflictRowKind.Technical)
            .Any(row => TechnicalText.IsMatch(row.Title) || TechnicalText.IsMatch(row.Detail) || TechnicalText.IsMatch(row.SecondTitle));
    }

    [TestMethod]
    public void ModuleContentsHideTechnicalValuesUntilTheDetailIsExpanded()
    {
        var session = Session([], scripts: Scripts([Collision("pkg.mod", (A + ".ts4script", "x"), (B + ".ts4script", "y"))]));
        var pair = Rows(session, "scripts").Single();
        session.ToggleExpanded(pair.Key);
        var module = Rows(session, "scripts").Single(row => row.Kind == ConflictRowKind.Module);
        Assert.AreEqual("pkg.mod", module.Title);
        session.ToggleExpanded(module.Key);
        var content = Rows(session, "scripts").Single(row => row.Kind == ConflictRowKind.Content && row.Title == "内容分组 1");
        Assert.AreEqual("1 个文件", content.Detail);
        Assert.IsFalse(content.Detail.Contains("SHA256", StringComparison.Ordinal));
        Assert.IsFalse(Rows(session, "scripts").Where(row => row.Kind != ConflictRowKind.Technical)
            .Any(row => TechnicalText.IsMatch(row.Title) || TechnicalText.IsMatch(row.Detail)));
        session.ToggleExpanded(content.Key);
        var technical = Rows(session, "scripts").Single(row => row.Kind == ConflictRowKind.Technical && row.Title == "技术详情");
        Assert.IsFalse(technical.IsExpanded);
        Assert.AreEqual("", technical.Tooltip);
        AssertOrdinaryRows(Rows(session, "scripts"));
        session.ToggleExpanded(technical.Key);
        var value = Rows(session, "scripts").Single(row => row.Key == technical.Key + "0");
        Assert.IsTrue(value.Title.StartsWith("SHA256", StringComparison.Ordinal));
        Assert.AreEqual(value.Title, value.Tooltip);
        session.ToggleExpanded(technical.Key);
        Assert.IsFalse(Rows(session, "scripts").Any(row => row.Kind == ConflictRowKind.Technical && row.Title != "技术详情"));
    }

    [TestMethod]
    public void UnfinishedDetailKeepsResourceIdentityOutOfTheOrdinaryExpansion()
    {
        var session = Session([Resource(1, (A, "alpha"), (B, "beta")), Resource(2, (A, null), (B, null))], [(A, 1), (B, 1)]);
        foreach (var pair in Rows(session)) session.ToggleExpanded(pair.Key);
        var resource = Rows(session).Single(row => row.Kind == ConflictRowKind.Resource && row.Key.Contains("00000002", StringComparison.Ordinal));
        session.ToggleExpanded(resource.Key);
        var unfinished = Rows(session).Single(row => row.Key == resource.Key + "/unfinished0");
        Assert.AreEqual("a.package", unfinished.Title);
        Assert.AreEqual(A, unfinished.Tooltip);
        var other = Rows(session).Single(row => row.Key == resource.Key + "/unfinished1");
        Assert.AreEqual("b.package", other.Title);
        Assert.AreEqual(B, other.Tooltip);
        StringAssert.Contains(unfinished.Detail, "捏人部件 · 未知读取失败");
        Assert.IsFalse(unfinished.Detail.Contains("034aeecb", StringComparison.OrdinalIgnoreCase));
        var resourceDetail = Rows(session).Single(row => row.Key == resource.Key + "/technical");
        Assert.IsFalse(resourceDetail.IsExpanded);
        Assert.AreEqual("", resourceDetail.Tooltip);
        session.ToggleExpanded(unfinished.Key);
        var detail = Rows(session).Single(row => row.Key == unfinished.Key + "/technical");
        Assert.IsFalse(detail.IsExpanded);
        Assert.AreEqual("", detail.Tooltip);
        AssertOrdinaryRows(Rows(session));
        Assert.IsFalse(Rows(session).Single(row => row.Key == other.Key).IsExpanded);
        Assert.IsFalse(visibleTechnical(Rows(session)));
        session.ToggleExpanded(detail.Key);
        Assert.IsTrue(Rows(session).Any(row => row.Title.Contains("未知读取失败", StringComparison.Ordinal)));
        Assert.IsTrue(Rows(session).Any(row => row.Title.Contains("034aeecb", StringComparison.OrdinalIgnoreCase)));

        static bool visibleTechnical(IReadOnlyList<ConflictResultRow> rows) => rows.Where(row => row.Kind != ConflictRowKind.Technical)
            .Any(row => TechnicalText.IsMatch(row.Title) || TechnicalText.IsMatch(row.Detail) || TechnicalText.IsMatch(row.SecondTitle));
    }

    [TestMethod]
    public void TechnicalDetailsTrackTheirOwningPairResourceAndContentGroup()
    {
        var session = Session([Resource(1, (A, "one"), (B, "xy")), Resource(2, (A, "three"), (B, "four"))], [(A, 2), (B, 2)]);
        var pair = Rows(session).Single();
        session.ToggleExpanded(pair.Key);
        var resources = Rows(session).Where(row => row.Kind == ConflictRowKind.Resource).ToArray();
        Assert.AreEqual(2, resources.Length);
        foreach (var item in resources) session.ToggleExpanded(item.Key);
        var groups = Rows(session).Where(row => row.Kind == ConflictRowKind.Content).ToArray();
        Assert.AreEqual(4, groups.Length);
        session.ToggleExpanded(groups[0].Key);
        var first = Rows(session).Single(row => row.Key == resources[0].Key + "/technical");
        var second = Rows(session).Single(row => row.Key == resources[1].Key + "/technical");
        var content = Rows(session).Single(row => row.Key == groups[0].Key + "/technical");
        Assert.IsTrue(new[] { first.Key, second.Key, content.Key }.Distinct(StringComparer.Ordinal).Count() == 3);
        session.ToggleExpanded(first.Key);
        Assert.IsTrue(Rows(session).Any(row => row.Title.StartsWith("TGI", StringComparison.Ordinal)));
        Assert.IsFalse(Rows(session).Any(row => row.Title.StartsWith("SHA256", StringComparison.Ordinal)));
        Assert.IsFalse(Rows(session).Single(row => row.Key == second.Key).IsExpanded);
        Assert.IsFalse(Rows(session).Single(row => row.Key == content.Key).IsExpanded);
        session.ToggleExpanded(content.Key);
        Assert.IsTrue(Rows(session).Any(row => row.Title.StartsWith("SHA256", StringComparison.Ordinal)));
        session.ToggleExpanded(first.Key);
        Assert.IsFalse(Rows(session).Any(row => row.Title.StartsWith("TGI", StringComparison.Ordinal)));
        Assert.IsTrue(Rows(session).Single(row => row.Key == content.Key).IsExpanded);
    }

    [TestMethod]
    public void WholeFileErrorsWithoutEntryNameKeepTechnicalDetails()
    {
        var session = new ConflictResultSession();
        session.Replace(Packages(paths: [A], issues: [new(PackageReadIssueCode.UnsupportedVersion,
            PackageConflictScanIssueStage.IndexRead, A, "ignored", Detail: "package raw detail 内容不同")]), Contents(),
            Scripts(issues: [new(ScriptArchiveIssueCode.NotZip, ScriptArchiveIssueStage.Structure,
                B + ".ts4script", "ignored", Detail: "script raw detail")]));
        foreach (var row in Rows(session, "problems")) session.ToggleExpanded(row.Key);
        AssertOrdinaryRows(Rows(session, "problems"));
        Assert.IsTrue(Rows(session, "problems").Any(row => row.Kind == ConflictRowKind.Explanation && row.Title == "格式暂不支持"));
        Assert.IsTrue(Rows(session, "problems").Any(row => row.Kind == ConflictRowKind.Explanation && row.Title == "文件损坏，建议重新下载"));
        foreach (var row in Rows(session, "problems").Where(row => row.Title == "技术详情")) session.ToggleExpanded(row.Key);
        foreach (var (code, raw) in new[] { (PackageReadIssueCode.UnsupportedVersion, "package raw detail 内容不同"), (ScriptArchiveIssueCode.NotZip, "script raw detail") })
        {
            var value = Rows(session, "problems").Single(row => row.Kind == ConflictRowKind.Technical && row.Title.Contains(raw, StringComparison.Ordinal));
            StringAssert.Contains(value.Title, code);
            Assert.AreEqual(value.Title, value.Tooltip);
        }
        Assert.IsTrue(session.Problems.All(problem => problem.IncompleteCount is null));
    }

    internal static void AssertOrdinaryRows(IReadOnlyList<ConflictResultRow> rows)
    {
        foreach (var row in rows)
        {
            foreach (var text in new[] { row.Title, row.Detail, row.Tooltip, row.SecondTitle, row.SecondTooltip, row.ExpandName })
            {
                Assert.IsFalse(TechnicalText.IsMatch(text), text);
                foreach (var forbidden in new[] { "SHA256", "TGI", ResourceContentIssueCode.UnexpectedError, "ignored", "raw detail" })
                    Assert.IsFalse(text.Contains(forbidden, StringComparison.Ordinal), text);
            }
            Assert.AreEqual((row.IsExpanded ? "收起 " : "展开 ") + row.Title, row.ExpandName);
        }
    }

    [TestMethod]
    public void KnownTypesFallbackAndCancellation()
    {
        Assert.AreEqual("捏人部件", ConflictResultSession.GetResourceTypeDisplayName(0x034AEECB));
        Assert.AreEqual("调校数据", ConflictResultSession.GetResourceTypeDisplayName(0x03B33DDF));
        Assert.AreEqual("模拟数据", ConflictResultSession.GetResourceTypeDisplayName(0x545AC67A));
        Assert.AreEqual("物件定义", ConflictResultSession.GetResourceTypeDisplayName(0xC0DB5AE7));
        Assert.AreEqual("物件目录", ConflictResultSession.GetResourceTypeDisplayName(0x319E4F1D));
        Assert.AreEqual("图像", ConflictResultSession.GetResourceTypeDisplayName(0x2BC04EDF));
        Assert.AreEqual("文本表", ConflictResultSession.GetResourceTypeDisplayName(0x220557DA));
        Assert.AreEqual("其他资源", ConflictResultSession.GetResourceTypeDisplayName(1));
        var session = Session([Resource(1, (A, "a"), (B, "b"))]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => session.BuildVisibleRows("resources", "all", Closed, cancelled.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => session.Replace(Packages(), Contents(), Scripts(), cancelled.Token));
        Assert.AreEqual(1, session.Resources.Count);
    }

    internal static IReadOnlyList<ConflictResultRow> Rows(ConflictResultSession session, string view = "resources", string filter = "all") =>
        session.BuildVisibleRows(view, filter, session.GetExpandedKeysSnapshot());

    internal static ConflictResultSession Session(ResourceContentComparison[] resources,
        (string Path, int Count)[]? inventories = null, ScriptModuleScanReport? scripts = null)
    {
        var session = new ConflictResultSession();
        session.Replace(Packages(inventories), Contents(resources), scripts ?? Scripts());
        return session;
    }
    internal static ResourceContentComparison Resource(ulong instance, params (string Path, string? Hash)[] files)
    {
        var key = new ResourceKey(0x034AEECB, 0, instance);
        ResourceContentOccurrence Occurrence((string Path, string? Hash) file) => new(key, file.Path, 0, Stamp, file.Hash,
            file.Hash is null ? null : 1, file.Hash is null ? new(ResourceContentIssueCode.UnexpectedError, ResourceContentStage.Unexpected, "ignored") : null);
        return new(key, files.Where(file => file.Hash is not null).GroupBy(file => file.Hash!).Select(group =>
            new ResourceContentSubgroup(group.Key, 1, group.Select(Occurrence).ToArray())).ToArray(), files.Where(file => file.Hash is null).Select(Occurrence).ToArray());
    }
    internal static ScriptModuleOccurrence Module(string name, string path, string? hash) =>
        new(name, path, Stamp, [], name.Replace('.', '/') + ".pyc", ScriptModuleKind.Module, ScriptModuleFormat.Compiled, hash, hash is null ? null : 1, null);
    internal static ScriptModuleCollision Collision(string name, params (string Path, string? Hash)[] files) =>
        new(name, files.Where(file => file.Hash is not null).GroupBy(file => file.Hash!).Select(group =>
            new ScriptModuleContentGroup(group.Key, group.Select(file => Module(name, file.Path, file.Hash)).ToArray())).ToArray(),
            files.Where(file => file.Hash is null).Select(file => Module(name, file.Path, null)).ToArray());
    internal static PackageConflictScanReport Packages((string Path, int Count)[]? inventories = null, IReadOnlyList<string>? paths = null, IReadOnlyList<PackageConflictScanIssue>? issues = null) =>
        new(DateTime.UnixEpoch, DateTime.UnixEpoch, [], 0, 0, paths ?? [], [], issues ?? [])
        { ResourceInventories = (inventories ?? []).Select(item => new PackageResourceInventory(item.Path, Stamp, item.Count)).ToArray() };
    internal static ResourceContentComparisonReport Contents(params ResourceContentComparison[] comparisons) =>
        new(DateTime.UnixEpoch, DateTime.UnixEpoch, new(), comparisons,
            comparisons.SelectMany(comparison => comparison.ContentGroups).SelectMany(group => group.Occurrences).Select(occurrence => occurrence.PackagePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => new ResourceContentPackageVersion(path, Stamp, Stamp, true)).ToArray(), 0);
    internal static ScriptModuleScanReport Scripts(IReadOnlyList<ScriptModuleCollision>? collisions = null, IReadOnlyList<ScriptArchiveIssue>? issues = null) =>
        new(DateTime.UnixEpoch, DateTime.UnixEpoch, [], new(), 0, 0, [], 0, collisions ?? [], issues ?? [], 0);
}
