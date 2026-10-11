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
    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly FileStamp Stamp = new(10, Now);
    private static readonly ResourceKey Key = new(1, 2, 3);

    [TestMethod]
    public void DifferentWithUncomparedKeepsBothStatesAndContentSubgroups()
    {
        var issue = new ResourceContentIssue("resource-test", ResourceContentStage.Read, "读取失败");
        var comparison = new ResourceContentComparison(Key,
            [new("HASH-A", 1, [Resource("C:\\Mods\\a.package", "HASH-A")]),
             new("HASH-B", 1, [Resource("C:\\Mods\\b.package", "HASH-B")])],
            [Resource("C:\\Mods\\c.package", null, issue)]);
        var session = new ConflictResultSession();
        session.Replace(Packages(), Contents(comparison), Scripts());

        Assert.AreEqual(1, session.Resources.Count);
        Assert.IsTrue(session.Resources[0].IsDifferent);
        Assert.IsTrue(session.Resources[0].IsPartial);
        Assert.AreEqual(3, session.Resources[0].Contents.Count);
        Assert.AreEqual("读取失败", session.Resources[0].Contents[2].Files[0].Reason);
        Assert.AreEqual(1, session.Problems.Count);
    }

    [TestMethod]
    public void ScriptModulesGroupOrdinalAndPreserveUncollidedIssueAndDistinctPaths()
    {
        var first = Module("pkg.a", "C:\\Mods\\A.ts4script", "HASH-A");
        var second = Module("pkg.a", "C:\\Mods\\B.ts4script", "HASH-B");
        var failure = new ScriptArchiveIssue("budget", ScriptArchiveIssueStage.Budget,
            "C:\\Mods\\A.ts4script", "预算不足", EntryName: "other.pyc");
        var report = Scripts(
            [new("pkg.a", [new("HASH-A", [first]), new("HASH-B", [second])], []),
             new("Pkg.b", [new("HASH-A", [Module("Pkg.b", "C:\\Mods\\A.ts4script", "HASH-A")])],
                [Module("Pkg.b", "C:\\Mods\\C.ts4script", null)])],
            [failure]);
        var session = new ConflictResultSession();
        session.Replace(Packages(), Contents(), report);

        Assert.AreEqual(2, session.Scripts.Count);
        Assert.AreEqual("Pkg", session.Scripts[0].Name);
        Assert.AreEqual("pkg", session.Scripts[1].Name);
        Assert.AreEqual(1, session.Problems.Count);
        Assert.IsFalse(session.Problems[0].IsWholeFile);
        Assert.AreEqual("other.pyc", session.Problems[0].Reasons[0].EntryName);
    }

    [TestMethod]
    public void IncompletePathsMergeReasonsAndEntryMetadataStaysRangeProblem()
    {
        var path = "C:\\Mods\\bad.package";
        var packageReport = Packages([path],
            [new("index-failed", PackageConflictScanIssueStage.IndexRead, path, "索引失败"),
             new("entry-metadata-failed", PackageConflictScanIssueStage.Metadata, "C:\\Mods\\unknown", "目录元数据失败")]);
        var scriptReport = Scripts(issues:
            [new("budget", ScriptArchiveIssueStage.Budget, path.ToUpperInvariant(), "模块超限", EntryName: "abc.pyc")]);
        var session = new ConflictResultSession();
        session.Replace(packageReport, Contents(), scriptReport);

        Assert.AreEqual(1, session.Problems.Count);
        Assert.IsTrue(session.Problems[0].IsWholeFile);
        Assert.AreEqual(3, session.Problems[0].Reasons.Count);
        Assert.AreEqual(1, session.RangeProblems.Count);
        Assert.AreEqual("C:\\Mods\\unknown", session.RangeProblems[0].Path);
    }

    private static ResourceContentOccurrence Resource(string path, string? hash, ResourceContentIssue? issue = null) =>
        new(Key, path, 0, Stamp, hash, hash is null ? null : 1, issue);

    private static ScriptModuleOccurrence Module(string name, string path, string? hash) =>
        new(name, path, Stamp, [], name.Replace('.', '/') + ".pyc", ScriptModuleKind.Module,
            ScriptModuleFormat.Compiled, hash, hash is null ? null : 1, null);

    private static PackageConflictScanReport Packages(IReadOnlyList<string>? paths = null, IReadOnlyList<PackageConflictScanIssue>? issues = null) =>
        new(Now, Now, [], 0, 0, paths ?? [], [], issues ?? []);

    private static ResourceContentComparisonReport Contents(params ResourceContentComparison[] comparisons) =>
        new(Now, Now, new ResourceContentLimits(), comparisons, [], 0);

    private static ScriptModuleScanReport Scripts(IReadOnlyList<ScriptModuleCollision>? collisions = null,
        IReadOnlyList<ScriptArchiveIssue>? issues = null) =>
        new(Now, Now, [], new ScriptModuleLimits(), 0, 0, [], 0, collisions ?? [], issues ?? [], 0);
}
