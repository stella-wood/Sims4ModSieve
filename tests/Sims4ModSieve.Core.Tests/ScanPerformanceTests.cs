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
