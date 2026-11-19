using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Diagnostics;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Scripts;
using static Sims4ModSieve.Core.Tests.Conflicts.PackageConflictGroupingBuilderTests;

namespace Sims4ModSieve.Core.Tests.Conflicts;

[TestClass]
public sealed class ConflictGroupingSizeTests
{
    [TestMethod]
    [DataRow(8L, 2_097_152L)]
    [DataRow(16L, 4_194_304L)]
    [DataRow(32L, 8_388_608L)]
    public void MemoryBudgetScalesWithAvailableMemory(long availableGiB, long expectedLimit)
    {
        Assert.AreEqual(expectedLimit, ConflictGroupingSize.CalculateResourceMatchLimit(availableGiB * 1024 * 1024 * 1024));
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(64L * 1024 * 1024)]
    [DataRow(512L * 1024 * 1024)]
    [DataRow(1024L * 1024 * 1024)]
    public void SmallMemoryBudgetUsesMinimumLimit(long availableBytes)
    {
        Assert.AreEqual(ConflictGroupingSize.MinimumResourceMatchLimit,
            ConflictGroupingSize.CalculateResourceMatchLimit(availableBytes));
    }

    [TestMethod]
    public void MemoryBudgetRejectsNegativeMemoryAndDoesNotOverflow()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ConflictGroupingSize.CalculateResourceMatchLimit(-1));
        Assert.AreEqual(long.MaxValue / 4 / 1024, ConflictGroupingSize.CalculateResourceMatchLimit(long.MaxValue));
    }

    [TestMethod]
    [DataRow(33L, null)]
    [DataRow(32L, "duplicate-copies")]
    [DataRow(6L, "duplicate-copies")]
    [DataRow(5L, "too-many-shared-resources")]
    public void InjectedLimitPreservesBoundaryAndBothReasons(long limit, string? expectedReason)
    {
        var (scan, contents, duplicates) = Fanout(3, 2);
        ConflictGroupingSize? size = null;
        var builder = new PackageConflictGroupingBuilder(limit);
        if (expectedReason is null)
        {
            var input = builder.Prepare(scan, contents, duplicates: duplicates, sizeObserved: value => size = value);
            Assert.AreEqual(33L, input.Pairs.Sum(pair => (long)pair.Resources.Count));
        }
        else
            Assert.ThrowsExactly<ConflictGroupingTooLargeException>(() =>
                builder.Prepare(scan, contents, duplicates: duplicates, sizeObserved: value => size = value));
        Assert.AreEqual(expectedReason, size!.Reason);
        Assert.AreEqual(limit, size.ResourceMatchLimit);
        Assert.IsTrue(size.IsLimitOverridden);
        StringAssert.Contains(size.DiagnosticText, "上限来源=注入");
    }

    [TestMethod]
    public void DefaultLimitUsesAvailableMemorySnapshotInDiagnostics()
    {
        var (scan, contents, duplicates) = Fanout(3, 2);
        ConflictGroupingSize? size = null;
        new PackageConflictGroupingBuilder().Prepare(scan, contents, duplicates: duplicates, sizeObserved: value => size = value);
        Assert.AreEqual(ConflictGroupingSize.CalculateResourceMatchLimit(size!.AvailableMemoryBytes), size.ResourceMatchLimit);
        Assert.IsFalse(size.IsLimitOverridden);
        StringAssert.Contains(size.DiagnosticText, $"可用内存字节={size.AvailableMemoryBytes}");
        StringAssert.Contains(size.DiagnosticText, "份额=1/4；系数字节每匹配=1024；上限来源=内存预算");
    }

    [TestMethod]
    public void CompletedGroupingRecordsEndMemoryAfterDuplicateMerge()
    {
        var (scan, contents, duplicates) = Fanout(3, 2);
        using var performance = ScanPerformance.Start("冲突检测");
        var (input, result, changedPaths, _) = ConflictResultGrouping.BuildPackages(scan, contents,
            new(DateTime.UnixEpoch, DateTime.UnixEpoch, [], ScriptModuleLimits.Default, 0, 0, [], 0, [], [], 0),
            duplicates, resourceMatchLimit: 33);
        var beforeMerge = performance.Finish("完成", "test");
        Assert.IsFalse(beforeMerge.Contains("结束托管内存字节=", StringComparison.Ordinal));
        string? observed = null;
        ConflictResultGrouping.MergeDuplicatePackageGroups(input, result, duplicates, changedPaths, scan, null,
            memoryObserved: value => observed = value);
        StringAssert.Contains(observed!, "冲突分组内存：阶段=合并副本后；结束托管内存字节=");
        StringAssert.Contains(observed!, "强制GC=false");
        StringAssert.Contains(performance.Finish("完成", "test"), observed!);
    }

    [TestMethod]
    public void EstimateMatchesMaterializedResourcesWithRepeatedOccurrencesAndExclusions()
    {
        var resource = Resource(1, ("a", "same"), ("A", "same"), ("b", "other"), ("c", null), ("exit", "same"));
        var excluded = Resource(2, ("a", "same"), ("b", "other")) with { Key = new(99, 0, 2) };
        var contents = Contents(resource, excluded, Resource(3, ("a", "same"), ("b", "same")));
        var types = new HashSet<uint> { 99 };
        var paths = new HashSet<string> { "EXIT" };
        var builder = new ConflictPairSummaryBuilder();
        var size = builder.EstimateGroupingPackagePairs(contents, types, paths,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), 0);
        var pairs = builder.BuildGroupingPackagePairs(contents, types, paths);
        Assert.AreEqual(4L, size.ResourceMatchCount);
        Assert.AreEqual((long)pairs.Sum(pair => pair.Resources.Count), size.ResourceMatchCount);
        Assert.AreEqual(size.ResourceMatchCount, size.CollapsedResourceMatchCount);
    }

    [TestMethod]
    public void EstimateUses64BitCountsWithoutMaterializingFilePairs()
    {
        var values = Enumerable.Range(0, 70_000).Select(index => ($"{index}.package", (string?)"same")).ToArray();
        var size = new ConflictPairSummaryBuilder().EstimateGroupingPackagePairs(Contents(Resource(1, values)),
            new HashSet<uint>(), new HashSet<string>(), new Dictionary<string, string>(), 0, resourceMatchLimit: 100);
        Assert.AreEqual(2_449_965_000L, size.ResourceMatchCount);
        Assert.IsTrue(size.ExceedsLimit);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void PrepareEstimatesAfterItsExistingTypeAndFileExclusions(bool excludeNameMap)
    {
        var shared = Resource(1, ("a", "same"), ("b", "same"), ("bad", null));
        var name = Resource(2, ("a", "same"), ("b", "same")) with { Key = new(0x0166038C, 0, 2) };
        var tooling = Resource(3, ("a", "same"), ("b", "same")) with { Key = new(99, 0, 3) };
        var scan = Scan(("a", 3), ("b", 3), ("bad", 3)) with
        {
            ToolingMetadataTypes = new HashSet<uint> { 99 },
            ResourceInventories = new[] { "a", "b", "bad" }.Select(path => new PackageResourceInventory(path, Stamp, 3)
            {
                ResourceTypeCounts = new Dictionary<uint, int> { [1] = 1, [0x0166038C] = 1, [99] = 1 },
            }).ToArray(),
        };
        ConflictGroupingSize? size = null;
        var input = new PackageConflictGroupingBuilder().Prepare(scan, Contents(shared, name, tooling), excludeNameMap,
            sizeObserved: observed => size = observed);
        Assert.AreEqual(excludeNameMap ? 1L : 2L, size!.ResourceMatchCount);
        Assert.AreEqual((long)input.Pairs.Sum(pair => pair.Resources.Count), size.ResourceMatchCount);
        Assert.AreEqual("bad", input.ExcludedFiles.Single().Path);
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    public void SharedTgiFanoutStopsBeforeExpansionAndClassifiesDuplicateCopies(int copies)
    {
        var (scan, contents, duplicates) = Fanout(3, copies);
        ConflictGroupingSize? observed = null;
        var error = Assert.ThrowsExactly<ConflictGroupingTooLargeException>(() =>
            new PackageConflictGroupingBuilder(10).Prepare(scan, contents, duplicates: duplicates, sizeObserved: size => observed = size));
        var files = 3 * copies;
        Assert.AreSame(observed, error.Size);
        Assert.AreEqual(2L * files * (files - 1) / 2 + 3L * copies * (copies - 1) / 2, error.Size.ResourceMatchCount);
        Assert.AreEqual(6L, error.Size.CollapsedResourceMatchCount);
        Assert.AreEqual(3 * (copies - 1), error.Size.RedundantCopyCount);
        Assert.AreEqual("duplicate-copies", error.Size.Reason);
        Assert.AreEqual($"⚠️文件夹内存在大量相同副本（{3 * (copies - 1)}份），建议优先进行重复检测", error.Message);
    }

    [TestMethod]
    public void ManyIndependentContentsRemainTooLargeAfterCollapsingCopies()
    {
        var (scan, contents, duplicates) = Fanout(4, 2);
        var error = Assert.ThrowsExactly<ConflictGroupingTooLargeException>(() =>
            new PackageConflictGroupingBuilder(10).Prepare(scan, contents, duplicates: duplicates));
        Assert.AreEqual(60L, error.Size.ResourceMatchCount);
        Assert.AreEqual(12L, error.Size.CollapsedResourceMatchCount);
        Assert.AreEqual("too-many-shared-resources", error.Size.Reason);
        Assert.AreEqual("⚠️所选文件冲突资源数过多，无法完整处理，请分批检测", error.Message);
    }

    [TestMethod]
    [DataRow("missing-report")]
    [DataRow("missing-hash")]
    [DataRow("length")]
    [DataRow("time")]
    [DataRow("identity")]
    [DataRow("changed")]
    public void CopiesWithoutCompleteWholeFileEvidenceAreNotCollapsed(string missing)
    {
        var (scan, contents, duplicates) = Fanout(3, 2);
        var invalid = duplicates.Groups.Select(group => group with
        {
            Sha256 = missing == "missing-hash" ? "" : group.Sha256,
            Files = group.Files.Select(file => file with
            {
                Size = missing == "length" ? file.Size + 1 : file.Size,
                LastWriteTimeUtc = missing == "time" ? file.LastWriteTimeUtc.AddSeconds(1) : file.LastWriteTimeUtc,
                Identity = missing == "identity" ? new(1, 2, 3) : file.Identity,
            }).ToArray(),
        }).ToArray();
        duplicates = duplicates with
        {
            Groups = invalid,
            Issues = missing == "changed" ? invalid.SelectMany(group => group.Files)
                .Select(file => new ScanIssue("file-changed-during-hash", ScanIssueStage.Hashing, file.Path, "changed")).ToArray() : [],
        };
        var error = Assert.ThrowsExactly<ConflictGroupingTooLargeException>(() =>
            new PackageConflictGroupingBuilder(10).Prepare(scan, contents,
                duplicates: missing == "missing-report" ? null : duplicates));
        Assert.AreEqual(error.Size.ResourceMatchCount, error.Size.CollapsedResourceMatchCount);
        Assert.AreEqual(0, error.Size.RedundantCopyCount);
        Assert.AreEqual("too-many-shared-resources", error.Size.Reason);
    }

    [TestMethod]
    public void PartialEvidenceCollapsesOnlyEligibleStableMembersWithTheirOwnStamps()
    {
        var (scan, contents, duplicates) = Fanout(3, 3);
        duplicates = duplicates with { Groups = duplicates.Groups.Select(group => group with
        {
            Files = group.Files.Select((file, index) => index == 2 ? file with { Size = 101 } : file).ToArray(),
        }).ToArray() };
        ConflictGroupingSize? size = null;
        new PackageConflictGroupingBuilder().Prepare(scan, contents, duplicates: duplicates, sizeObserved: observed => size = observed);
        Assert.AreEqual(3, size!.RedundantCopyCount);
        Assert.AreEqual(33L, size.CollapsedResourceMatchCount);

        var stamps = scan.ResourceInventories.Select((inventory, index) => (inventory.Path,
            Stamp: Stamp with { LastWriteTimeUtc = Stamp.LastWriteTimeUtc.AddSeconds(index), Identity = new(1, (ulong)index, 0) }))
            .ToDictionary(item => item.Path, item => item.Stamp);
        scan = scan with { ResourceInventories = scan.ResourceInventories.Select(inventory =>
            inventory with { ScannedStamp = stamps[inventory.Path] }).ToArray() };
        contents = contents with
        {
            Packages = contents.Packages.Select(version => version with
                { ScannedStamp = stamps[version.Path], StampAfterRead = stamps[version.Path] }).ToArray(),
            Comparisons = contents.Comparisons.Select(comparison => comparison with
            {
                ContentGroups = comparison.ContentGroups.Select(group => group with
                    { Occurrences = group.Occurrences.Select(occurrence => occurrence with { PackageStamp = stamps[occurrence.PackagePath] }).ToArray() }).ToArray(),
            }).ToArray(),
        };
        duplicates = duplicates with { Groups = duplicates.Groups.Select(group => group with
        {
            Files = group.Files.Select(file => file with { Size = 100, LastWriteTimeUtc = stamps[file.Path].LastWriteTimeUtc,
                Identity = stamps[file.Path].Identity }).ToArray(),
        }).ToArray() };
        new PackageConflictGroupingBuilder().Prepare(scan, contents, duplicates: duplicates, sizeObserved: observed => size = observed);
        Assert.AreEqual(6, size!.RedundantCopyCount);
        Assert.AreEqual(6L, size.CollapsedResourceMatchCount);
    }

    [TestMethod]
    [DataRow("unstable-version")]
    [DataRow("missing-version")]
    [DataRow("occurrence-stamp")]
    [DataRow("resource-failure")]
    public void ExitedCopiesDoNotContributeToCollapsedEstimateOrRedundantCount(string failure)
    {
        var (scan, contents, duplicates) = Fanout(3, 2);
        bool Invalid(string path) => path.EndsWith("-1.package", StringComparison.Ordinal);
        contents = contents with
        {
            Packages = contents.Packages.Where(version => failure != "missing-version" || !Invalid(version.Path))
                .Select(version => failure == "unstable-version" && Invalid(version.Path)
                    ? version with { IsStable = false, StampAfterRead = null } : version).ToArray(),
            Comparisons = contents.Comparisons.Select(comparison => comparison with
            {
                ContentGroups = comparison.ContentGroups.Select(group => group with
                {
                    Occurrences = group.Occurrences.Where(item => failure != "resource-failure" || !Invalid(item.PackagePath))
                        .Select(item => failure == "occurrence-stamp" && Invalid(item.PackagePath)
                            ? item with { PackageStamp = Stamp with { Length = 101 } } : item).ToArray(),
                }).Where(group => group.Occurrences.Count > 0).ToArray(),
                Uncompared = failure == "resource-failure" ? comparison.ContentGroups.SelectMany(group => group.Occurrences)
                    .Where(item => Invalid(item.PackagePath)).Select(item => item with
                    { Sha256 = null, DecompressedLength = null, Issue = new("test-failure", ResourceContentStage.Read, "test") }).ToArray() : [],
            }).ToArray(),
        };
        ConflictGroupingSize? size = null;
        var input = new PackageConflictGroupingBuilder().Prepare(scan, contents, duplicates: duplicates, sizeObserved: observed => size = observed);
        Assert.AreEqual(3, input.ExcludedFiles.Count);
        Assert.AreEqual(0, size!.RedundantCopyCount);
        Assert.AreEqual(6L, size.ResourceMatchCount);
        Assert.AreEqual(6L, size.CollapsedResourceMatchCount);
    }

    [TestMethod]
    public void ExactLimitAllowsOriginalPrepareAndBuildResultsIncludingDuplicateEdges()
    {
        var (scan, contents, duplicates) = Fanout(3, 2);
        var builder = new PackageConflictGroupingBuilder(33);
        var input = builder.Prepare(scan, contents, duplicates: duplicates);
        Assert.AreEqual(33, input.Pairs.Sum(pair => pair.Resources.Count));
        Assert.AreEqual(15, input.Pairs.Count);
        Assert.AreEqual(15, builder.Build(input, Options()).Relations.Count);
        var originalPairs = new ConflictPairSummaryBuilder().BuildGroupingPackagePairs(contents, new HashSet<uint> { 0x0166038C },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.AreEqual(JsonSerializer.Serialize(originalPairs), JsonSerializer.Serialize(input.Pairs));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(100)]
    public void ScanDiagnosticsRecordBothEstimatesAndLimitEvenWhenStopped(int limit)
    {
        var (scan, contents, duplicates) = Fanout(3, 2);
        using var performance = ScanPerformance.Start("冲突检测");
        try { new PackageConflictGroupingBuilder(limit).Prepare(scan, contents, duplicates: duplicates); }
        catch (ConflictGroupingTooLargeException) { }
        var diagnostics = performance.Finish("未完成", "test");
        StringAssert.Contains(diagnostics,
            $"冲突分组规模：资源匹配估算=33；合并副本后估算=6；上限={limit}；多余副本=3");
        StringAssert.Contains(diagnostics, "可用内存字节=");
        StringAssert.Contains(diagnostics, "份额=1/4；系数字节每匹配=1024；上限来源=注入");
        Assert.IsFalse(diagnostics.Contains("结束托管内存字节=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EstimateCancellationPropagatesBeforeAndDuringCounting()
    {
        var (_, contents, _) = Fanout(3, 2);
        var builder = new ConflictPairSummaryBuilder();
        var types = new HashSet<uint>();
        var paths = new HashSet<string>();
        var representatives = new Dictionary<string, string>();
        Assert.ThrowsExactly<OperationCanceledException>(() => builder.EstimateGroupingPackagePairs(contents,
            types, paths, representatives, 0, cancellationToken: new(true)));
        using var cancellation = new CancellationTokenSource();
        var first = contents.Comparisons[0];
        contents = contents with { Comparisons = [first with
        {
            ContentGroups = [first.ContentGroups[0] with
                { Occurrences = new CancelingOccurrences(first.ContentGroups[0].Occurrences, cancellation) }],
        }] };
        Assert.ThrowsExactly<OperationCanceledException>(() => builder.EstimateGroupingPackagePairs(contents,
            types, paths, representatives, 0, cancellationToken: cancellation.Token));
        using var afterEstimate = new CancellationTokenSource();
        var (scan, report, duplicates) = Fanout(3, 2);
        Assert.ThrowsExactly<OperationCanceledException>(() => new PackageConflictGroupingBuilder(1).Prepare(scan, report,
            cancellationToken: afterEstimate.Token, duplicates: duplicates, sizeObserved: _ => afterEstimate.Cancel()));
    }

    [TestMethod]
    public void UnderLimitPrepareAndBuildMatchPreGuardSnapshotFieldForField()
    {
        // 快照由修改前的 ccccf93792ad0daf98f0254ca1c10a548130fa9e 在临时目录运行同一样本生成。
        var scan = Scan(("a", 3), ("b", 7), ("broken", 2));
        var contents = Contents(Resource(1, ("b", "y"), ("a", "x"), ("broken", null)),
            Resource(2, ("b", "y"), ("a", "x")), Resource(3, ("b", "y"), ("a", "x")));
        var builder = new PackageConflictGroupingBuilder(100);
        var input = builder.Prepare(scan, contents);
        var actual = JsonSerializer.SerializeToNode(new { Input = input, Result = builder.Build(input, Options()) });
        using var stream = typeof(ConflictGroupingSizeTests).Assembly.GetManifestResourceStream("GroupingBeforeGuard.json")!;
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(stream), actual), "Prepare / Build 的字段或顺序偏离门卫加入前的快照。");
    }

    private static (PackageConflictScanReport, ResourceContentComparisonReport, DuplicateReport) Fanout(int originals, int copies)
    {
        var paths = Enumerable.Range(0, originals).SelectMany(index => Enumerable.Range(0, copies)
            .Select(copy => $"{index}-{copy}.package")).ToArray();
        var shared = paths.Select(path => (path, (string?)"same")).ToArray();
        var resources = new List<ResourceContentComparison> { Resource(1, shared), Resource(2, shared) };
        for (var index = 0; index < originals; index++)
            resources.Add(Resource((ulong)(100 + index), paths.Skip(index * copies).Take(copies).Select(path => (path, (string?)"same")).ToArray()));
        var groups = Enumerable.Range(0, originals).Select(index =>
        {
            var files = paths.Skip(index * copies).Take(copies).Select(path => new DuplicateFile(path, Stamp.Length,
                Stamp.LastWriteTimeUtc, [], [], true, 1)).ToArray();
            return new DuplicateGroup($"whole-file-{index}", Stamp.Length, DuplicateSection.Ordinary, files, files[0].Path, files.Skip(1).Select(file => file.Path).ToArray());
        }).ToArray();
        return (Scan(paths.Select(path => (path, 3)).ToArray()), Contents(resources.ToArray()),
            new(DateTime.UnixEpoch, DateTime.UnixEpoch, [], paths.Length, paths.Length, groups, []));
    }

    private sealed class CancelingOccurrences(IReadOnlyList<ResourceContentOccurrence> values, CancellationTokenSource cancellation)
        : IReadOnlyList<ResourceContentOccurrence>
    {
        public int Count => values.Count;
        public ResourceContentOccurrence this[int index] => values[index];
        public IEnumerator<ResourceContentOccurrence> GetEnumerator()
        {
            foreach (var value in values) { cancellation.Cancel(); yield return value; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
