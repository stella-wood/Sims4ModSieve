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
    public void ThreeFilesUseOneCardAndInlineIdenticalFilesWithoutChangingPairFacts()
    {
        var comparison = Resource(1, (A, "same"), (B, "same"), (C, "other"));
        var session = Session([comparison], [(A, 1), (B, 1), (C, 1)]);
        Assert.AreEqual(3, session.Resources.Count);
        var pair = session.Resources.Single(pair => pair.FirstPath == A && pair.SecondPath == B);
        Assert.AreEqual(ConflictPairPriority.CanOrganize, pair.Priority);
        Assert.IsFalse(pair.HasConfirmedDifference || pair.HasIncomplete);
        Assert.AreSame(comparison, pair.Resources.Single().Comparison);
        var card = Rows(session).Single();
        Assert.AreEqual(ConflictPairPriority.CanOrganize, card.Priority);
        Assert.AreEqual("疑似同一 mod", card.Title);
        Assert.IsFalse(card.IsExpanded);
        Assert.IsTrue(card.IsCardEnd);
        session.ToggleExpanded(card.Key);
        var rows = Rows(session);
        Assert.AreEqual(3, rows.Count(row => row.Kind == ConflictRowKind.File));
        Assert.AreEqual(0, rows.Count(row => row.Kind == ConflictRowKind.Content));
        Assert.IsTrue(rows.Where(row => row.File is not null).All(row => row.HasAnnotation));
        CollectionAssert.AreEqual(new[] { A, B, C }, rows.Where(row => row.Kind == ConflictRowKind.File).Select(row => row.Tooltip).ToArray());
        Assert.IsTrue(rows.All(row => row.Depth <= 1));
        Assert.AreEqual(1, rows.Count(row => row.HasState));
        Assert.AreEqual(1, rows.Count(row => row.IsCardEnd));
        AssertOrdinaryRows(rows);
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
    public void ResourceCardsSortByPriorityAndCheckboxFiltersSelectWholeCards()
    {
        var session = Session([Resource(3, (C, "x"), (C + "copy", "x")), Resource(2, (A, "x"), (B, "y")), Resource(1, (C + "failed", null))], [(A, 10), (B, 10), (C, 1), (C + "copy", 1), (C + "failed", 1)]);
        var rows = Rows(session).Where(row => row.IsCardStart).ToArray();
        CollectionAssert.AreEqual(new[] { ConflictPairPriority.NeedsReview, ConflictPairPriority.CanOrganize, ConflictPairPriority.Incomplete }, rows.Select(row => row.Priority!.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "第 1 组", "第 2 组", "第 3 组" }, rows.Select(row => row.GroupLabel).ToArray());
        Assert.AreEqual(1, Rows(session, filter: "different").Count);
        Assert.AreEqual(1, Rows(session, filter: "same").Count);
        Assert.AreEqual("第 2 组", Rows(session, filter: "same").Single().GroupLabel);
        Assert.AreEqual("第 3 组", Rows(session, filter: "incomplete").Single(row => row.IsCardStart).GroupLabel);
        CollectionAssert.AreEqual(new[] { "第 1 组", "第 3 组" }, Rows(session, filter: "different,incomplete").Where(row => row.IsCardStart).Select(row => row.GroupLabel).ToArray());
        Assert.AreEqual(1, Rows(session, filter: "incomplete").Count(row => row.IsCardStart));
        Assert.AreEqual(2, Rows(session, filter: "different,incomplete").Count(row => row.IsCardStart));
        Assert.AreEqual(0, Rows(session, filter: "").Count);
        session.ToggleExpanded(rows[0].Key);
        Assert.AreEqual(2, Rows(session, filter: "different").Count(row => row.Kind == ConflictRowKind.File));
        Assert.IsTrue(Rows(session).Where(row => row.Depth > 0).All(row => row.GroupLabel.Length == 0));
        Assert.IsTrue(Rows(session, filter: "same").All(row => row.Depth == 0));
    }

    [TestMethod]
    public void GroupNumbersRemainStableAcrossStatusAndTypeFilters()
    {
        var session = Session([Resource(1, (C, "x"), (C + "copy", "x")), Resource(2, (A, "x"), (B, "y")), Resource(3, (C + "failed", null))],
            [(A, 10), (B, 10), (C, 1), (C + "copy", 1), (C + "failed", 1)], scripts: Scripts([Collision("pkg.a", (A, "x"), (B, "y")), Collision("pkg.b", (A, "x"), (B, "x"))]));
        var all = Rows(session, "all").Where(row => row.IsCardStart).ToArray();
        var numbers = all.ToDictionary(row => row.Key, row => row.GroupIndex);
        Assert.AreEqual(5, numbers.Count);
        session.ToggleExpanded(all[0].Key);
        foreach (var view in new[] { "all", "resources", "scripts", "problems", "none" })
            foreach (var filter in new[] { "all", "different", "same", "incomplete", "different,incomplete" })
                foreach (var row in Rows(session, view, filter).Where(row => row.Depth == 0))
                    Assert.AreEqual(numbers[row.Key], row.GroupIndex);
        CollectionAssert.AreEqual(all.Select(row => row.GroupLabel).ToArray(),
            Rows(session, "all").Where(row => row.Depth == 0).Select(row => row.GroupLabel).ToArray());
    }

    [TestMethod]
    public void ContainmentEligibilityStillUsesCoreInventoryAndStableVersions()
    {
        foreach (var counts in new[] { (1, 2), (2, 1), (1, 1) })
        {
            var session = Session([Resource(1, (A, "x"), (B, "x"))], [(A, counts.Item1), (B, counts.Item2)]);
            var pair = session.Resources.Single();
            Assert.AreEqual(counts.Item1 == 1, pair.FirstContainedInSecond);
            Assert.AreEqual(counts.Item2 == 1, pair.SecondContainedInFirst);
            Assert.AreEqual(ConflictPairPriority.CanOrganize, Rows(session).Single().Priority);
        }
        var missing = new ConflictResultSession();
        missing.Replace(Packages(), Contents(Resource(1, (A, "x"), (B, "x"))), Scripts());
        Assert.IsFalse(Rows(missing).Any(row => row.Kind == ConflictRowKind.Resource));
        Assert.AreEqual(2, missing.Problems.Count);
    }

    [TestMethod]
    public void ScriptsUseModuleCardsAndKeepFullContentContext()
    {
        var collisions = new[] { Collision("pkg.a", (A, "x"), (B, "y"), (C, "x")), Collision("pkg.b", (A, "x"), (B, "x")) };
        var session = Session([], scripts: Scripts(collisions));
        Assert.AreEqual(2, session.Scripts.Count);
        var cards = Rows(session, "scripts");
        Assert.AreEqual(2, cards.Count);
        Assert.AreEqual("pkg.a", cards[0].Title);
        Assert.AreEqual(ConflictPairPriority.NeedsReview, cards[0].Priority);
        Assert.AreEqual(ConflictPairPriority.CanOrganize, cards[1].Priority);
        session.ToggleExpanded(cards[0].Key);
        Assert.AreEqual(3, Rows(session, "scripts").Count(row => row.Kind == ConflictRowKind.File));
        Assert.AreEqual("以下 2 个内容相同", Rows(session, "scripts").Single(row => row.Kind == ConflictRowKind.Content).Title);
        Assert.AreEqual(1, Rows(session, "scripts", "same").Count);
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
        var problem = Rows(session, "problems").Single(row => row.IsCardStart && row.Tooltip == A);
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
        Assert.AreEqual(2, Rows(session, "problems").Count(row => row.IsCardStart));
        var problemRow = Rows(session, "problems")[0];
        Assert.AreEqual(A, Rows(session, "problems").Single(row => row.File is not null).File!.Path);
        Assert.IsFalse(problemRow.Detail.Contains("one.pyc", StringComparison.Ordinal));
        Assert.IsFalse(problemRow.IsExpandable);
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
    public void CardsStartClosedAndExpansionIsIndependentAcrossResources()
    {
        var session = Session([Resource(1, (A, "a"), (B, "b"), (C, "c")), Resource(2, (C + "other", "a"), (C + "last", "b"))]);
        var cards = Rows(session);
        Assert.AreEqual(2, cards.Count);
        Assert.IsTrue(cards.All(row => row.Depth == 0 && !row.IsExpanded));
        session.ToggleExpanded(cards[0].Key);
        Assert.AreEqual(3, Rows(session).Count(row => row.Kind == ConflictRowKind.File));
        Assert.IsFalse(Rows(session).Single(row => row.Key == cards[1].Key).IsExpanded);
        session.ToggleExpanded(cards[0].Key);
        Assert.AreEqual(2, Rows(session).Count);
        session.Reset();
        Assert.AreEqual(0, session.GetExpandedKeysSnapshot().Count);
        Assert.AreEqual(0, Rows(session).Count);
        Assert.IsNull(session.ContentReport);
    }

    [TestMethod]
    public void TwoFilesHaveMetadataAndOneTechnicalEntryWithoutContentGrouping()
    {
        var session = Session([Resource(1, (A, "alpha"), (B, "beta"))]);
        var card = Rows(session).Single();
        session.ToggleExpanded(card.Key);
        var rows = Rows(session);
        Assert.AreEqual(0, rows.Count(row => row.Kind == ConflictRowKind.Content));
        var files = rows.Where(row => row.Kind == ConflictRowKind.File).ToArray();
        Assert.AreEqual(2, files.Length);
        Assert.IsTrue(files.All(row => !row.IsExpandable && row.Detail.Contains(@"C:\Mods", StringComparison.Ordinal)));
        Assert.IsTrue(files.All(row => row.Detail.Contains(Stamp.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), StringComparison.Ordinal) && row.Detail.Contains("10 B", StringComparison.Ordinal)));
        AssertOrdinaryRows(rows);
        var detail = rows.Single(row => row.IsTechnicalToggle);
        Assert.AreEqual("", detail.Tooltip);
        Assert.IsFalse(detail.IsExpanded);
        Assert.IsTrue(detail.IsCardEnd);
        session.ToggleExpanded(detail.Key);
        var technical = Rows(session).Where(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable).ToArray();
        Assert.AreEqual(1, technical.Length);
        Assert.AreEqual(Resource(1).Key.Tgi, technical[0].TechnicalEntry!.Identifier);
        Assert.IsTrue(technical[0].TechnicalEntry!.Cells.Any(cell => cell.Evidence.Contains(A, StringComparison.Ordinal)
            && cell.Evidence.Contains("SHA256 alpha", StringComparison.Ordinal) && cell.Evidence.Contains("解压大小 1 字节", StringComparison.Ordinal)));
        Assert.IsTrue(technical.All(row => row.Depth == 1));
        session.ToggleExpanded(detail.Key);
        Assert.IsFalse(Rows(session).Any(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable));
    }

    [TestMethod]
    public void ScriptTechnicalValuesRequireAnExplicitClick()
    {
        var session = Session([], scripts: Scripts([Collision("pkg.mod", (A + ".ts4script", "x"), (B + ".ts4script", "y"))]));
        var card = Rows(session, "scripts").Single();
        session.ToggleExpanded(card.Key);
        AssertOrdinaryRows(Rows(session, "scripts"));
        Assert.AreEqual(0, Rows(session, "scripts").Count(row => row.Kind == ConflictRowKind.Content));
        Assert.AreEqual(2, Rows(session, "scripts").Count(row => row.Kind == ConflictRowKind.File));
        var detail = Rows(session, "scripts").Single(row => row.IsTechnicalToggle);
        session.ToggleExpanded(detail.Key);
        Assert.IsTrue(Rows(session, "scripts").Any(row => row.TechnicalEntry?.Cells.Any(cell => cell.Evidence.Contains("SHA256 x", StringComparison.Ordinal) && cell.Evidence.Contains("pkg/mod.pyc", StringComparison.Ordinal)) == true));
        session.ToggleExpanded(detail.Key);
        Assert.IsFalse(Rows(session, "scripts").Any(row => row.Kind == ConflictRowKind.Technical && !row.IsExpandable));
    }

    [TestMethod]
    public void IncompleteResourcesExitCardsAndShowStandaloneFilesWithExplicitTechnicalDetails()
    {
        var session = Session([Resource(1, (A, "alpha"), (B, "beta")), Resource(2, (A, null), (B, null))]);
        Assert.AreEqual(0, session.Cards.Count);
        var files = Rows(session, "problems").Where(row => row.IsCardStart).ToArray();
        Assert.AreEqual(2, files.Length);
        Assert.IsTrue(files.All(row => row.File is null && row.HasAnnotation && !row.IsExpandable && row.GroupIndex > 0));
        var rows = Rows(session, "problems").ToList();
        Assert.AreEqual(2, rows.Count(row => row.Kind == ConflictRowKind.File));
        foreach (var header in files)
        {
            var file = rows.Single(row => row.OwnerKey == header.Key && row.Kind == ConflictRowKind.File);
            Assert.IsTrue(rows.IndexOf(header) < rows.IndexOf(file));
            Assert.IsFalse(rows.Any(row => row.OwnerKey == header.Key && row.Kind == ConflictRowKind.Explanation && row.Title == header.Annotation));
        }
        AssertOrdinaryRows(Rows(session, "problems"));
        session.ToggleExpanded(Rows(session, "problems").First(row => row.IsTechnicalToggle).Key);
        Assert.IsTrue(Rows(session, "problems").Any(row => row.Kind == ConflictRowKind.Technical && row.Title.Contains(ResourceContentIssueCode.UnexpectedError, StringComparison.Ordinal)));
        session.ToggleIgnored(rows.Single(row => row.OwnerKey == files[0].Key && row.Kind == ConflictRowKind.File));
        Assert.IsFalse(Rows(session, "problems").Any(row => row.OwnerKey == files[0].Key));
        var ignored = session.BuildVisibleRows("problems", "all", session.GetExpandedKeysSnapshot(), showIgnored: true)
            .Where(row => row.OwnerKey == files[0].Key).ToArray();
        Assert.IsTrue(ignored.Length > 1 && ignored.All(row => row.IsIgnored));
        Assert.AreEqual(files[0].GroupIndex, ignored.Single(row => row.IsCardStart).GroupIndex);
        session.ToggleIgnored(ignored.Single(row => row.IsCardStart));
        Assert.IsTrue(Rows(session, "problems").Any(row => row.Key == files[0].Key));
    }

    [TestMethod]
    public void TechnicalDetailsRemainIndependentAndAllViewsUseTheSameStableKeys()
    {
        var session = Session([Resource(1, (A, "one"), (B, "xy")), Resource(2, (C, "three"), (C + "copy", "four"))]);
        var cards = Rows(session);
        foreach (var card in cards) session.ToggleExpanded(card.Key);
        var details = Rows(session).Where(row => row.IsTechnicalToggle).ToArray();
        Assert.AreEqual(2, details.Length);
        session.ToggleExpanded(details[0].Key);
        Assert.IsTrue(Rows(session).Single(row => row.Key == details[0].Key).IsExpanded);
        Assert.IsFalse(Rows(session).Single(row => row.Key == details[1].Key).IsExpanded);
        Assert.AreEqual(Rows(session).Count, Rows(session, "all").Count);
        session.ToggleExpanded(cards[0].Key);
        Assert.IsFalse(Rows(session).Any(row => row.Key.StartsWith(cards[0].Key + "/", StringComparison.Ordinal)));
        session.ToggleExpanded(cards[0].Key);
        Assert.IsTrue(Rows(session).Single(row => row.Key == details[0].Key).IsExpanded);
    }

    [TestMethod]
    public void WholeFileErrorsWithoutEntryNameKeepTechnicalDetails()
    {
        var session = new ConflictResultSession();
        session.Replace(Packages(paths: [A], issues: [new(PackageReadIssueCode.UnsupportedVersion,
            PackageConflictScanIssueStage.IndexRead, A, "ignored", Detail: "package raw detail 内容不同")]), Contents(),
            Scripts(issues: [new(ScriptArchiveIssueCode.NotZip, ScriptArchiveIssueStage.Structure,
                B + ".ts4script", "ignored", Detail: "script raw detail")]));
        AssertOrdinaryRows(Rows(session, "problems"));
        Assert.IsTrue(Rows(session, "problems").Any(row => row.Annotation == "格式暂不支持"));
        Assert.IsTrue(Rows(session, "problems").Any(row => row.Annotation == "文件损坏，建议重新下载"));
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
        Assert.AreEqual("调参", ConflictResultSession.GetResourceTypeDisplayName(0x03B33DDF));
        Assert.AreEqual("模拟数据", ConflictResultSession.GetResourceTypeDisplayName(0x545AC67A));
        Assert.AreEqual("物件定义", ConflictResultSession.GetResourceTypeDisplayName(0xC0DB5AE7));
        Assert.AreEqual("物件目录", ConflictResultSession.GetResourceTypeDisplayName(0x319E4F1D));
        Assert.AreEqual("图像", ConflictResultSession.GetResourceTypeDisplayName(0x2BC04EDF));
        Assert.AreEqual("文本表", ConflictResultSession.GetResourceTypeDisplayName(0x220557DA));
        Assert.AreEqual("未识别资源", ConflictResultSession.GetResourceTypeDisplayName(1));
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
        var observed = resources.SelectMany(comparison => comparison.ContentGroups.SelectMany(group => group.Occurrences).Concat(comparison.Uncompared))
            .GroupBy(item => item.PackagePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Path: group.Key, Count: group.Select(item => item.Key).Distinct().Count())).ToArray();
        var supplied = (inventories ?? []).ToDictionary(item => item.Path, item => item.Count, StringComparer.OrdinalIgnoreCase);
        var complete = observed.Select(item => (item.Path, Count: supplied.GetValueOrDefault(item.Path, item.Count)))
            .Concat((inventories ?? []).Where(item => !observed.Any(other => StringComparer.OrdinalIgnoreCase.Equals(item.Path, other.Path)))).ToArray();
        session.Replace(Packages(complete), Contents(resources), scripts ?? Scripts());
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
        new(DateTime.UnixEpoch, DateTime.UnixEpoch, [], (inventories ?? []).Length, (inventories ?? []).Length, paths ?? [], [], issues ?? [])
        { ResourceInventories = (inventories ?? []).Select(item => new PackageResourceInventory(item.Path, Stamp, item.Count) { ResourceTypeCounts = new Dictionary<uint, int> { [0x034AEECB] = item.Count } }).ToArray() };
    internal static ResourceContentComparisonReport Contents(params ResourceContentComparison[] comparisons) =>
        new(DateTime.UnixEpoch, DateTime.UnixEpoch, new(), comparisons,
            comparisons.SelectMany(comparison => comparison.ContentGroups).SelectMany(group => group.Occurrences).Select(occurrence => occurrence.PackagePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => new ResourceContentPackageVersion(path, Stamp, Stamp, true)).ToArray(), 0);
    internal static ScriptModuleScanReport Scripts(IReadOnlyList<ScriptModuleCollision>? collisions = null, IReadOnlyList<ScriptArchiveIssue>? issues = null) =>
        new(DateTime.UnixEpoch, DateTime.UnixEpoch, [], new(), 0, 0, [], 0, collisions ?? [], issues ?? [], 0);
}
