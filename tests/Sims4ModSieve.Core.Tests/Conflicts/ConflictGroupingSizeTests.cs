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
    [DataRow(8L, 2_147_483_648L)]
    [DataRow(16L, 4_294_967_296L)]
    [DataRow(32L, 8_589_934_592L)]
    public void MemoryBudgetScalesWithAvailableMemory(long availableGiB, long expectedLimit)
    {
        Assert.AreEqual(expectedLimit, ConflictGroupingSize.CalculateMemoryBudgetBytes(availableGiB * 1024 * 1024 * 1024));
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(64L * 1024 * 1024)]
    [DataRow(512L * 1024 * 1024)]
    [DataRow(1024L * 1024 * 1024)]
    public void SmallMemoryBudgetUsesMinimumLimit(long availableBytes)
    {
        Assert.AreEqual(ConflictGroupingSize.MinimumMemoryBudgetBytes,
            ConflictGroupingSize.CalculateMemoryBudgetBytes(availableBytes));
    }

    [TestMethod]
    public void MemoryBudgetRejectsNegativeMemoryAndDoesNotOverflow()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ConflictGroupingSize.CalculateMemoryBudgetBytes(-1));
        Assert.AreEqual(long.MaxValue / 4, ConflictGroupingSize.CalculateMemoryBudgetBytes(long.MaxValue));
    }

    [TestMethod]
    [DataRow(16_061L, null)]
    [DataRow(16_060L, "duplicate-copies")]
    [DataRow(3_108L, "duplicate-copies")]
    [DataRow(3_107L, "too-many-shared-resources")]
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
        Assert.AreEqual(limit, size.MemoryBudgetBytes);
        Assert.IsTrue(size.IsLimitOverridden);
        StringAssert.Contains(size.DiagnosticText, "上限来源=注入");
        Assert.AreEqual(16_061L, size.EstimatedMemoryBytes);
        Assert.AreEqual(3_108L, size.CollapsedEstimatedMemoryBytes);
        StringAssert.Contains(size.DiagnosticText, "文件对上界=15；合并副本后文件对上界=3；系数字节每文件对=492；安全余量=1.4；估算内存字节=16061；合并副本后估算内存字节=3108");
    }

    [TestMethod]
    public void DefaultLimitUsesAvailableMemorySnapshotInDiagnostics()
    {
        var (scan, contents, duplicates) = Fanout(3, 2);
        ConflictGroupingSize? size = null;
        new PackageConflictGroupingBuilder().Prepare(scan, contents, duplicates: duplicates, sizeObserved: value => size = value);
        Assert.AreEqual(ConflictGroupingSize.CalculateMemoryBudgetBytes(size!.AvailableMemoryBytes), size.MemoryBudgetBytes);
        Assert.IsFalse(size.IsLimitOverridden);
        StringAssert.Contains(size.DiagnosticText, $"可用内存字节={size.AvailableMemoryBytes}");
        StringAssert.Contains(size.DiagnosticText, "份额=1/4；系数字节每匹配=124；上限来源=内存预算");
    }

    [TestMethod]
    [DataRow(0L, 0, 0L)]
    [DataRow(1L, 1, 0L)]
    [DataRow(2L, 4, 2L)]
    [DataRow(100L, 4, 6L)]
    [DataRow(long.MaxValue, int.MaxValue, 2_305_843_005_992_468_481L)]
    public void FilePairUpperBoundUsesSmallerOfMatchesAndFileCombinations(long matches, int files, long expected)
    {
        Assert.AreEqual(expected, ConflictGroupingSize.CalculateFilePairUpperBound(matches, files));
    }

    [TestMethod]
    public void FilePairUpperBoundCountsOnlyEligibleParticipantsWithoutMaterializingPairs()
    {
        var contents = Contents(Resource(1, ("a", "same"), ("b", "same")),
            Resource(2, ("c", "same"), ("d", "same")), Resource(3, ("singleton", "same")),
            Resource(4, ("a", "same"), ("b", "same")));
        var size = new ConflictPairSummaryBuilder().EstimateGroupingPackagePairs(contents,
            new HashSet<uint>(), new HashSet<string>(), new Dictionary<string, string> { ["b"] = "a" }, 1);
        Assert.AreEqual(3L, size.ResourceMatchCount);
        Assert.AreEqual(3L, size.FilePairCount); // M 小于 C(n,2)，无需构造实际两对文件的集合。
        Assert.AreEqual(1L, size.CollapsedResourceMatchCount);
        Assert.AreEqual(1L, size.CollapsedFilePairCount);
    }

    [TestMethod]
    public void SingletonFilesDoNotInflatePairUpperBound()
    {
        var resources = Enumerable.Range(1, 10).Select(index => Resource((ulong)index, ("a", "same"), ("b", "same")))
            .Append(Resource(11, ("singleton", "same"))).ToArray();
        var size = new ConflictPairSummaryBuilder().EstimateGroupingPackagePairs(Contents(resources),
            new HashSet<uint>(), new HashSet<string>(), new Dictionary<string, string>(), 0);
        Assert.AreEqual(10L, size.ResourceMatchCount);
        Assert.AreEqual(1L, size.FilePairCount);
        Assert.AreEqual(1L, size.CollapsedFilePairCount);
    }

    [TestMethod]
    public void TwoTermReasonsUseCollapsedPairsAsWellAsMatches()
    {
        var size = new ConflictGroupingSize(200, 90, 100, 1, 40_000, 1);
        Assert.IsTrue(size.ExceedsLimit);
        Assert.AreEqual("duplicate-copies", size.Reason);
        Assert.AreEqual("too-many-shared-resources", (size with { CollapsedFilePairCount = 90 }).Reason);
        Assert.IsFalse((size with { FilePairCount = 1 }).ExceedsLimit);
    }

    [TestMethod]
    public void TwoTermMemoryEstimateChargesPairsAndMatchesAndRoundsUp()
    {
        Assert.AreEqual(0L, ConflictGroupingSize.EstimateMemoryBytes(0, 0));
        Assert.AreEqual(863L, ConflictGroupingSize.EstimateMemoryBytes(1, 1));
        Assert.AreEqual(1_725L, ConflictGroupingSize.EstimateMemoryBytes(2, 2));
        Assert.AreEqual(1_036L, ConflictGroupingSize.EstimateMemoryBytes(1, 2));
        Assert.AreEqual(long.MaxValue, ConflictGroupingSize.EstimateMemoryBytes(long.MaxValue, long.MaxValue));
    }

    [TestMethod]
    public void FittedMemoryCoefficientsSnapshotMatchesMeasuredModel()
    {
        Assert.AreEqual(492L, typeof(ConflictGroupingSize).GetField(nameof(ConflictGroupingSize.BytesPerFilePair))!.GetRawConstantValue());
        Assert.AreEqual(124L, typeof(ConflictGroupingSize).GetField(nameof(ConflictGroupingSize.BytesPerResourceMatch))!.GetRawConstantValue());
        Assert.AreEqual(1.4m, typeof(ConflictGroupingSize).GetField(nameof(ConflictGroupingSize.MemorySafetyMargin))!.GetValue(null));
        Assert.AreEqual(1_180_880_692L, ConflictGroupingSize.EstimateMemoryBytes(130_816, 6_283_264));
        Assert.AreEqual(1_818_900_071L, ConflictGroupingSize.EstimateMemoryBytes(2_096_128, 2_160_640));
    }

    [TestMethod]
    [DataRow(8, 48, true, 345)]
    [DataRow(16, 48, true, 488)]
    [DataRow(32, 48, true, 690)]
    [DataRow(8, 48, false, 690)]
    [DataRow(16, 48, false, 976)]
    [DataRow(32, 48, false, 1380)]
    [DataRow(8, 1, false, 2232)]
    [DataRow(16, 1, false, 3156)]
    [DataRow(32, 1, false, 4463)]
    public void MemoryBudgetAllowsReportedShapesOnlyThroughMaximum(int availableGiB, int shared, bool copies, int maximum)
    {
        var budget = ConflictGroupingSize.CalculateMemoryBudgetBytes(availableGiB * 1024L * 1024 * 1024);
        long Estimate(int count)
        {
            var files = copies ? count * 2 : count;
            var pairs = files * (files - 1L) / 2;
            var matches = shared * pairs + (copies ? (64 - shared) * (long)count : 0);
            return ConflictGroupingSize.EstimateMemoryBytes(pairs, matches);
        }
        Assert.IsTrue(Estimate(maximum) <= budget);
        Assert.IsTrue(Estimate(maximum + 1) > budget);
    }

    [TestMethod]
    public void CompletedGroupingRecordsEndMemoryAfterDuplicateMerge()
    {
        var (scan, contents, duplicates) = Fanout(3, 2);
        using var performance = ScanPerformance.Start("冲突检测");
        var (input, result, changedPaths, _) = ConflictResultGrouping.BuildPackages(scan, contents,
            new(DateTime.UnixEpoch, DateTime.UnixEpoch, [], ScriptModuleLimits.Default, 0, 0, [], 0, [], [], 0),
            duplicates, memoryBudgetBytes: 16_061);
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
        Assert.AreEqual(3L, size.FilePairCount);
        Assert.AreEqual(pairs.Count, size.FilePairCount);
        Assert.AreEqual(size.FilePairCount, size.CollapsedFilePairCount);
        Assert.AreEqual((long)pairs.Sum(pair => pair.Resources.Count), size.ResourceMatchCount);
        Assert.AreEqual(size.ResourceMatchCount, size.CollapsedResourceMatchCount);
    }

    [TestMethod]
    public void EstimateUses64BitCountsWithoutMaterializingFilePairs()
    {
        var values = Enumerable.Range(0, 70_000).Select(index => ($"{index}.package", (string?)"same")).ToArray();
        var size = new ConflictPairSummaryBuilder().EstimateGroupingPackagePairs(Contents(Resource(1, values)),
            new HashSet<uint>(), new HashSet<string>(), new Dictionary<string, string>(), 0, memoryBudgetBytes: 100);
        Assert.AreEqual(2_449_965_000L, size.ResourceMatchCount);
        Assert.AreEqual(2_449_965_000L, size.FilePairCount);
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
        Assert.AreEqual(1L, size.FilePairCount);
        Assert.AreEqual(1L, size.CollapsedFilePairCount);
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    public void SharedTgiFanoutStopsBeforeExpansionAndClassifiesDuplicateCopies(int copies)
    {
        var (scan, contents, duplicates) = Fanout(3, copies);
        ConflictGroupingSize? observed = null;
        var error = Assert.ThrowsExactly<ConflictGroupingTooLargeException>(() =>
            new PackageConflictGroupingBuilder(4_000).Prepare(scan, contents, duplicates: duplicates, sizeObserved: size => observed = size));
        var files = 3 * copies;
        Assert.AreSame(observed, error.Size);
        Assert.AreEqual(2L * files * (files - 1) / 2 + 3L * copies * (copies - 1) / 2, error.Size.ResourceMatchCount);
        Assert.AreEqual(6L, error.Size.CollapsedResourceMatchCount);
        Assert.AreEqual(files * (files - 1L) / 2, error.Size.FilePairCount);
        Assert.AreEqual(3L, error.Size.CollapsedFilePairCount);
        Assert.AreEqual(3 * (copies - 1), error.Size.RedundantCopyCount);
        Assert.AreEqual("duplicate-copies", error.Size.Reason);
        Assert.AreEqual($"⚠️文件夹内存在大量相同副本（{3 * (copies - 1)}份），建议优先进行重复检测", error.Message);
    }

    [TestMethod]
    public void ManyIndependentContentsRemainTooLargeAfterCollapsingCopies()
    {
        var (scan, contents, duplicates) = Fanout(4, 2);
        var error = Assert.ThrowsExactly<ConflictGroupingTooLargeException>(() =>
            new PackageConflictGroupingBuilder(4_000).Prepare(scan, contents, duplicates: duplicates));
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
            new PackageConflictGroupingBuilder(4_000).Prepare(scan, contents,
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
        var builder = new PackageConflictGroupingBuilder(16_061);
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
    [DataRow(100_000)]
    public void ScanDiagnosticsRecordBothEstimatesAndLimitEvenWhenStopped(int limit)
    {
        var (scan, contents, duplicates) = Fanout(3, 2);
        using var performance = ScanPerformance.Start("冲突检测");
        try { new PackageConflictGroupingBuilder(limit).Prepare(scan, contents, duplicates: duplicates); }
        catch (ConflictGroupingTooLargeException) { }
        var diagnostics = performance.Finish("未完成", "test");
        StringAssert.Contains(diagnostics,
            $"冲突分组规模：资源匹配估算=33；合并副本后估算=6；上限={Math.Max(0, decimal.Floor((limit / 1.4m - 492 * 15) / 124))}；多余副本=3");
        StringAssert.Contains(diagnostics, "可用内存字节=");
        StringAssert.Contains(diagnostics, "份额=1/4；系数字节每匹配=124；上限来源=注入");
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
        var builder = new PackageConflictGroupingBuilder(100_000);
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
