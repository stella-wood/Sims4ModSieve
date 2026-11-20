using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Benchmarks;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Tests;
using Sims4ModSieve.Core.Tests.Packages;

namespace Sims4ModSieve.Benchmarks.Tests;

[TestClass]
public sealed class RawReuseMatrixTests
{
    [TestMethod]
    public async Task CompleteBusinessResultsMatchOffOnAtOneTwoFourEight()
    {
        using var temp = new TempDirectory();
        var corpus = Path.Combine(temp.Path, "corpus");
        BenchmarkInputs.Generate(corpus);
        var content = Enumerable.Range(0, 10000).Select(i => (byte)(i % 251)).ToArray();
        var raw = DbpfFixtureBuilder.Zlib(content);
        for (var index = 0; index < 4; index++)
            File.WriteAllBytes(Path.Combine(corpus, $"reuse-{index}.package"), new DbpfFixtureBuilder()
                .AddContent(raw, DbpfFixtureBuilder.CompressionZlib, (uint)content.Length, instanceLo: 7)
                .AddContent([(byte)index], instanceLo: (uint)(100 + index)).Build());
        foreach (var (name, encoded, compression) in new[] {
            ("fast", DbpfFixtureBuilder.Zlib(content, CompressionLevel.Fastest), DbpfFixtureBuilder.CompressionZlib),
            ("none", content, DbpfFixtureBuilder.CompressionNone) })
            File.WriteAllBytes(Path.Combine(corpus, name + ".package"), new DbpfFixtureBuilder()
                .AddContent(encoded, compression, (uint)content.Length, instanceLo: 7).Build());
        string? baseline = null;
        long? offAttempts = null;
        foreach (var mode in new[] { ResourceContentReuseMode.Off, ResourceContentReuseMode.On })
        foreach (var concurrency in new[] { 1, 2, 4, 8 })
        {
            var result = await BenchmarkRunner.MeasureAsync(token => BenchmarkRunner.CaptureAsync([new("source-1", corpus)], corpus, token,
                Path.Combine(temp.Path, $"{mode}-{concurrency}.json"), hashConcurrency: DuplicateHashConcurrency.Two,
                indexConcurrency: (DuplicateHashConcurrency)concurrency, contentConcurrency: (DuplicateHashConcurrency)concurrency, contentReuse: mode));
            Assert.AreEqual("completed", result.Outcome, result.Error);
            Assert.IsNotNull(result.Snapshot);
            baseline ??= result.Snapshot;
            Assert.IsNull(ResultSnapshot.FirstDifference(baseline, result.Snapshot), $"{mode}/{concurrency}");
            var stage = result.Stages.Single(stage => stage.ContentReuse is not null);
            offAttempts ??= stage.DecompressionAttempts;
            if (mode == ResourceContentReuseMode.On)
            {
                Assert.AreEqual(3L, stage.ContentReuse!.SavedDecompressions);
                Assert.AreEqual(offAttempts - 3, stage.DecompressionAttempts);
                Assert.IsTrue(stage.ContentReuse.GrossAdditionalReadBytes > 0);
            }
        }
    }

    [TestMethod]
    public async Task CommandAcceptsOffOnAndAutoAndRecordsCounters()
    {
        using var temp = new TempDirectory();
        var corpus = temp.CreateDirectory("corpus");
        var content = new byte[2000];
        var raw = DbpfFixtureBuilder.Zlib(content);
        for (var i = 0; i < 2; i++) temp.WriteBytes($"corpus/{i}.package", new DbpfFixtureBuilder()
            .AddContent(raw, DbpfFixtureBuilder.CompressionZlib, (uint)content.Length, instanceLo: 1).Build());
        string? baseline = null;
        foreach (var mode in new[] { "off", "on", "auto" })
        {
            var output = Path.Combine(temp.Path, mode);
            var args = new List<string> { "run", "--source", corpus, "--mods-root", corpus, "--output", output, "--sample-id", "reuse",
                "--runs", "1", "--index-concurrency", "8", "--content-concurrency", "8", "--content-reuse", mode };
            if (baseline is not null) args.AddRange(["--baseline", baseline]);
            Assert.AreEqual(0, await Program.ExecuteAsync(args.ToArray()));
            var run = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "run-01.json")))!;
            var counters = run["stages"]!.AsArray().Single(stage => stage!["contentReuse"] is not null)!["contentReuse"]!;
            Assert.AreEqual(mode == "off" || counters["bypassReason"] is not null ? 0 : 1, counters["savedDecompressions"]!.GetValue<int>());
            Assert.AreEqual(0, counters["sizeSkipped"]!.GetValue<int>());
            Assert.IsTrue(run["processLifetimePeakWorkingSetBytes"]!.GetValue<long>() > 0);
            baseline ??= output;
        }
        Assert.AreEqual(2, await Program.ExecuteAsync(["run", "--source", corpus, "--output", Path.Combine(temp.Path, "bad"),
            "--sample-id", "reuse", "--content-reuse", "invalid"]));
    }
}
