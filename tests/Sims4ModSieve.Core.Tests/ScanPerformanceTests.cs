using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Diagnostics;
using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.Core.Tests;

[TestClass]
public sealed class ScanPerformanceTests
{
    [TestMethod]
    public async Task DuplicateTimingPreservesResultsAndCountsFilteredFiles()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a.package", "same");
        temp.WriteFile("b.package", "same");
        temp.WriteFile("unique.package", "unique-size");
        var request = new DuplicateScanRequest([new("source", temp.Path)]);
        var scanner = DuplicateScanner.CreateDefault();
        var baseline = await scanner.ScanAsync(request);
        using var performance = ScanPerformance.Start("重复检测");
        var measured = await scanner.ScanAsync(request);
        Assert.AreEqual(JsonSerializer.Serialize(baseline.Groups), JsonSerializer.Serialize(measured.Groups));
        Assert.AreEqual(JsonSerializer.Serialize(baseline.Issues), JsonSerializer.Serialize(measured.Issues));
        Assert.AreEqual(4, performance.Stages.Count);
        var discovery = performance.Stages[0];
        Assert.AreEqual(3L, discovery.Files);
        Assert.AreEqual(19L, discovery.Bytes);
        var hashing = performance.Stages.Single(stage => stage.Name.EndsWith("整文件 SHA-256", StringComparison.Ordinal));
        Assert.AreEqual(2L, hashing.Files);
        Assert.AreEqual(8L, hashing.ProcessedBytes);
        Assert.AreEqual(1L, hashing.SkippedFiles);
        var summary = performance.Finish("完成", "未知");
        StringAssert.Contains(summary, "总耗时");
        StringAssert.Contains(summary, "逻辑 CPU");
        StringAssert.Contains(summary, "文件=3；总字节=19");
        Assert.IsTrue(performance.Stages.All(stage => stage.Elapsed > TimeSpan.Zero));
    }

    [TestMethod]
    [DataRow(new[] { 3, 5, 7 }, 3, 0)]
    [DataRow(new[] { 3, 3, 5 }, 1, 2)]
    [DataRow(new[] { 3, 3, 5, 5 }, 0, 4)]
    public async Task DuplicateTimingCountsUniqueLengthsAndHashesSameSizeDifferentContent(
        int[] lengths, int expectedSkipped, int expectedHashed)
    {
        using var temp = new TempDirectory();
        for (var index = 0; index < lengths.Length; index++)
            temp.WriteBytes($"{index}.package", Enumerable.Repeat((byte)index, lengths[index]).ToArray());

        using var performance = ScanPerformance.Start("重复检测");
        var report = await DuplicateScanner.CreateDefault().ScanAsync(new([new("source", temp.Path)]));

        Assert.AreEqual(lengths.Length, report.DiscoveredFileCount);
        Assert.AreEqual(expectedHashed, report.HashedFileCount);
        Assert.AreEqual(0, report.Groups.Count);
        Assert.AreEqual(0, report.Issues.Count);
        var preparation = performance.Stages.Single(stage => stage.Name.EndsWith("按长度筛选哈希候选", StringComparison.Ordinal));
        Assert.AreEqual((long)lengths.Length, preparation.Files);
        Assert.AreEqual((long)expectedSkipped, preparation.SkippedFiles);
        Assert.AreEqual((long)expectedHashed, preparation.ResultItems);
        var hashing = performance.Stages.Single(stage => stage.Name.EndsWith("整文件 SHA-256", StringComparison.Ordinal));
        Assert.AreEqual((long)expectedHashed, hashing.Files);
        Assert.AreEqual((long)expectedSkipped, hashing.SkippedFiles);
        var summary = performance.Finish("完成", "未知");
        var preparationLine = summary.Split('\n').Single(line => line.StartsWith(preparation.Name + "：", StringComparison.Ordinal));
        StringAssert.Contains(preparationLine, $"跳过文件={expectedSkipped}；");
        StringAssert.Contains(preparationLine, $"结果项={expectedHashed}");
    }

    [TestMethod]
    public async Task DuplicateTimingDistinguishesOverlappingSourcesFromSeparateCopies()
    {
        using var temp = new TempDirectory();
        var original = temp.CreateDirectory("original");
        var child = temp.CreateDirectory("original", "child");
        var copy = temp.CreateDirectory("copy");
        foreach (var length in new[] { 3, 5, 7 })
        {
            temp.WriteBytes(Path.Combine("original", "child", $"{length}.package"), new byte[length]);
            temp.WriteBytes(Path.Combine("copy", $"{length}.package"), new byte[length]);
        }

        var scanner = DuplicateScanner.CreateDefault();
        using (var performance = ScanPerformance.Start("重复检测"))
        {
            var report = await scanner.ScanAsync(new([new("original", original), new("child", child)]));
            Assert.AreEqual(3, report.DiscoveredFileCount);
            Assert.AreEqual(0, report.HashedFileCount);
            Assert.AreEqual(0, report.Groups.Count);
            Assert.AreEqual(0, report.Issues.Count);
            var preparation = performance.Stages.Single(stage => stage.Name.EndsWith("按长度筛选哈希候选", StringComparison.Ordinal));
            Assert.AreEqual(3L, preparation.SkippedFiles);
        }

        using (var performance = ScanPerformance.Start("重复检测"))
        {
            var report = await scanner.ScanAsync(new([new("original", original), new("child", child), new("copy", copy)]));
            Assert.AreEqual(6, report.DiscoveredFileCount);
            Assert.AreEqual(6, report.HashedFileCount);
            Assert.AreEqual(3, report.Groups.Count);
            Assert.IsTrue(report.Groups.All(group => group.Files.Count == 2));
            Assert.AreEqual(0, report.Issues.Count);
            var preparation = performance.Stages.Single(stage => stage.Name.EndsWith("按长度筛选哈希候选", StringComparison.Ordinal));
            Assert.AreEqual(0L, preparation.SkippedFiles);
            Assert.AreEqual(6L, preparation.ResultItems);
        }
    }

    [TestMethod]
    public async Task TimingScopesFlowToWorkersAndKeepIndependentScansIsolated()
    {
        async Task<string> Run(string kind, int count)
        {
            using var performance = ScanPerformance.Start(kind);
            using (var stage = ScanPerformance.Measure("content", 4))
            {
                stage!.Entries = count;
                stage.CountsContent = true;
                await Parallel.ForEachAsync(Enumerable.Range(0, count), async (_, _) =>
                {
                    await Task.Yield();
                    ScanPerformance.CurrentStage!.BeginContentRead(true);
                    ScanPerformance.CurrentStage.CompleteDecompression();
                });
            }
            Assert.AreEqual((long)count, performance.Stages.Single().DecompressionCompleted);
            return performance.Finish("完成", "未知");
        }
        var results = await Task.WhenAll(Task.Run(() => Run("A", 13)), Task.Run(() => Run("B", 29)));
        StringAssert.Contains(results[0], "尝试=13；成功=13");
        StringAssert.Contains(results[1], "尝试=29；成功=29");
        Assert.IsNull(ScanPerformance.CurrentStage);
        Assert.IsNull(ScanPerformance.Measure("outside"));
    }

    [TestMethod]
    public async Task CancelledHashStillRecordsTheActiveStage()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a.package", "same");
        temp.WriteFile("b.package", "same");
        using var performance = ScanPerformance.Start("重复检测");
        var scanner = new DuplicateScanner(new PhysicalFileSystemAccess(), new CancelledHasher(), new());
        await Assert.ThrowsAsync<OperationCanceledException>(() => scanner.ScanAsync(new([new("source", temp.Path)])));
        var stage = performance.Stages.Last();
        StringAssert.Contains(stage.Name, "SHA-256");
        Assert.AreEqual(2L, stage.Files);
        Assert.IsTrue(stage.Elapsed > TimeSpan.Zero);
        StringAssert.Contains(performance.Finish("已取消", "未知"), "已取消");
    }

    private sealed class CancelledHasher : IStableFileHasher
    {
        public Task<StableFileHash> HashAsync(string path, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException();
    }
}
