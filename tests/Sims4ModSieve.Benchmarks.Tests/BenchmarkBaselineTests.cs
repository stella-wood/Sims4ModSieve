using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Benchmarks;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Tests;

namespace Sims4ModSieve.Benchmarks.Tests;

[TestClass]
public sealed class BenchmarkBaselineTests
{
    [TestMethod]
    public async Task SyntheticPipelineIsRepeatableAndPreservesFailuresWithoutWritingInputs()
    {
        using var temp = new TempDirectory();
        var corpus = Path.Combine(temp.Path, "corpus");
        BenchmarkInputs.Generate(corpus);
        var before = FileDigests(corpus);
        var manifest = InputManifest.Capture([corpus]);
        ScanSource[] sources = [new("source-1", corpus)];
        var first = await BenchmarkRunner.MeasureAsync(token => BenchmarkRunner.CaptureAsync(sources, corpus, token));
        var second = await BenchmarkRunner.MeasureAsync(token => BenchmarkRunner.CaptureAsync(sources, corpus, token));

        Assert.AreEqual("completed", first.Outcome);
        Assert.IsNull(first.CancellationLatencyMs);
        Assert.IsNotNull(first.Snapshot);
        Assert.IsNotNull(second.Snapshot);
        Assert.IsNull(ResultSnapshot.FirstDifference(first.Snapshot, second.Snapshot));
        Assert.AreEqual(10, first.Snapshot["duplicates"]!["discoveredFileCount"]!.GetValue<int>());
        Assert.AreEqual(1, first.Snapshot["duplicates"]!["groups"]!.AsArray().Count);
        Assert.IsTrue(first.Snapshot["packages"]!["issues"]!.AsArray().Count > 0);
        Assert.IsTrue(first.Snapshot["scripts"]!["issues"]!.AsArray().Count > 0);
        var comparisons = first.Snapshot["contents"]!["comparisons"]!.AsArray();
        Assert.AreEqual(1, comparisons.Count);
        Assert.AreEqual(2, comparisons[0]!["contentGroups"]!.AsArray().Count);
        var issues = comparisons[0]!["uncompared"]!.AsArray()
            .Select(node => node!["issue"]!["code"]!.GetValue<string>()).ToArray();
        CollectionAssert.Contains(issues, ResourceContentIssueCode.UnsupportedCompression);
        Assert.IsTrue(issues.Any(code => code != ResourceContentIssueCode.UnsupportedCompression));
        Assert.IsTrue(first.Snapshot["grouping"]!["excludedFiles"]!.AsArray().Count > 0);
        Assert.IsTrue(first.Stages.Any(stage => stage.Name == "Package · 排序和读取索引"));
        Assert.IsTrue(first.Stages.Any(stage => stage.DecompressionAttempts > 0));
        Assert.IsTrue(first.Stages.Any(stage => stage.Name == "基准 · 生成一致性快照"));
        Assert.AreEqual(manifest.Fingerprint, InputManifest.Capture([corpus]).Fingerprint);
        CollectionAssert.AreEqual(before, FileDigests(corpus));
        Assert.IsTrue(!first.Snapshot.ContainsKey("startedAtUtc"));
        Assert.IsTrue(!first.Snapshot["contents"]!.AsObject().ContainsKey("processedBytes"));
    }

    [TestMethod]
    public void SnapshotComparisonPreservesOrderIssuesAndRelationshipFacts()
    {
        var baseline = JsonNode.Parse("""
            {"groups":[{"hash":"a","members":["keep","delete"],"keep":"keep"}],
             "issues":[{"code":"failed","path":"bad"}],
             "pair":{"identical":2,"different":1,"contained":true}}
            """)!;
        Assert.IsNull(ResultSnapshot.FirstDifference(baseline, JsonNode.Parse("""
            {"pair":{"contained":true,"different":1,"identical":2},
             "issues":[{"path":"bad","code":"failed"}],
             "groups":[{"keep":"keep","members":["keep","delete"],"hash":"a"}]}
            """)));
        var changed = baseline.DeepClone();
        changed["groups"]![0]!["members"] = new JsonArray("delete", "keep");
        Assert.AreEqual("$.groups[0].members[0]: value differs", ResultSnapshot.FirstDifference(baseline, changed));
        changed = baseline.DeepClone();
        changed["issues"]![0]!["code"] = "silently-passed";
        Assert.AreEqual("$.issues[0].code: value differs", ResultSnapshot.FirstDifference(baseline, changed));
        changed = baseline.DeepClone();
        changed["pair"]!["contained"] = false;
        Assert.AreEqual("$.pair.contained: value differs", ResultSnapshot.FirstDifference(baseline, changed));
        changed = baseline.DeepClone();
        changed["groups"]![0]!["keep"] = "delete";
        Assert.IsNotNull(ResultSnapshot.FirstDifference(baseline, changed));
        changed = baseline.DeepClone();
        changed["groups"]![0]!["hash"] = "b";
        Assert.IsNotNull(ResultSnapshot.FirstDifference(baseline, changed));
    }

    [TestMethod]
    public async Task CancellationWaitsForWorkerCleanupAndPublishesNoSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var measurement = BenchmarkRunner.MeasureAsync(async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { enteredCleanup.SetResult(); await releaseCleanup.Task; }
            return new JsonObject();
        }, cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await enteredCleanup.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(measurement.IsCompleted);
        releaseCleanup.SetResult();
        var result = await measurement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("cancelled", result.Outcome);
        Assert.IsNull(result.Snapshot);
        Assert.IsNotNull(result.CancellationLatencyMs);
        Assert.IsTrue(result.CancellationLatencyMs >= 0);
    }

    [TestMethod]
    public async Task TimedCancellationRecordsLatencyOnlyWhenItActuallyCancels()
    {
        var result = await BenchmarkRunner.MeasureAsync(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new JsonObject();
        }, cancelAfterMs: 10);
        Assert.AreEqual("cancelled", result.Outcome);
        Assert.IsNotNull(result.CancellationLatencyMs);
        var completed = await BenchmarkRunner.MeasureAsync(_ => Task.FromResult(new JsonObject()), cancelAfterMs: 60000);
        Assert.AreEqual("completed", completed.Outcome);
        Assert.IsNull(completed.CancellationLatencyMs);
    }

    [TestMethod]
    public async Task UnexpectedFailureIsRecordedWithoutPublishingSuccessfulResults()
    {
        var result = await BenchmarkRunner.MeasureAsync(_ =>
            Task.FromException<JsonObject>(new IOException("injected failure")));
        Assert.AreEqual("failed", result.Outcome);
        Assert.IsNull(result.Snapshot);
        Assert.IsNull(result.CancellationLatencyMs);
        StringAssert.Contains(result.Error!, "IOException: injected failure");
        StringAssert.Contains(result.TimingLog, "failed");
    }

    [TestMethod]
    public void ManifestDetectsChangesAndPreservesUnavailableSources()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("a.package", "a");
        var before = InputManifest.Capture([temp.Path]);
        File.AppendAllText(path, "changed");
        Assert.AreNotEqual(before.Fingerprint, InputManifest.Capture([temp.Path]).Fingerprint);
        var missing = InputManifest.Capture([Path.Combine(temp.Path, "missing")]);
        Assert.AreEqual(1, missing.Issues.Count);
        Assert.AreEqual(0, missing.Files.Count);
    }

    [TestMethod]
    public void OutputGuardRejectsNestedAndExistingPathsWithoutRejectingSiblings()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        Assert.ThrowsExactly<ArgumentException>(() =>
            BenchmarkInputs.ValidateOutput(Path.Combine(source, "results"), [source]));
        Assert.ThrowsExactly<ArgumentException>(() => BenchmarkInputs.ValidateOutput(source, [source]));
        BenchmarkInputs.ValidateOutput(Path.Combine(temp.Path, "Mods2"), [source]);
        Assert.ThrowsExactly<ArgumentException>(() => BenchmarkInputs.Generate(source));
    }

    [TestMethod]
    public void MedianUsesAllMeasurementsAndHandlesEvenCounts()
    {
        Assert.AreEqual(20d, BenchmarkRunner.Median([300, 10, 20]));
        Assert.AreEqual(25d, BenchmarkRunner.Median([40, 10, 30, 20]));
        Assert.ThrowsExactly<ArgumentException>(() => BenchmarkRunner.Median([]));
    }

    [TestMethod]
    public async Task CommandComparesBaselineAndRejectsChangedResultsAndInputs()
    {
        using var temp = new TempDirectory();
        var corpus = Path.Combine(temp.Path, "corpus");
        Assert.AreEqual(0, await Program.ExecuteAsync(["generate", "--output", corpus]));
        var baseline = Path.Combine(temp.Path, "baseline");
        var repeat = Path.Combine(temp.Path, "repeat");
        Assert.AreEqual(0, await Program.ExecuteAsync(
            ["run", "--source", corpus, "--output", baseline, "--sample-id", "test-v1", "--runs", "3"]));
        Assert.AreEqual(0, await Program.ExecuteAsync(
            ["run", "--source", corpus, "--output", repeat, "--sample-id", "test-v1", "--runs", "1", "--baseline", baseline]));
        var snapshotPath = Path.Combine(baseline, "snapshot.json");
        var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!;
        snapshot["duplicates"]!["groups"]![0]!["suggestedKeepPath"] = "wrong-file";
        await File.WriteAllTextAsync(snapshotPath, snapshot.ToJsonString());
        var mismatch = Path.Combine(temp.Path, "mismatch");
        Assert.AreEqual(1, await Program.ExecuteAsync(
            ["run", "--source", corpus, "--output", mismatch, "--sample-id", "test-v1", "--runs", "1", "--baseline", baseline]));
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(mismatch, "summary.json")))!;
        Assert.IsFalse(summary["valid"]!.GetValue<bool>());
        StringAssert.Contains(summary["difference"]!.GetValue<string>(), "suggestedKeepPath");
        Assert.IsFalse(File.Exists(Path.Combine(mismatch, "snapshot.json")));
        File.AppendAllText(Path.Combine(corpus, "a.package"), "changed");
        var changedInput = Path.Combine(temp.Path, "changed-input");
        Assert.AreEqual(2, await Program.ExecuteAsync(
            ["run", "--source", corpus, "--output", changedInput, "--sample-id", "test-v1", "--baseline", repeat]));
        Assert.IsFalse(Directory.Exists(changedInput));
        await File.WriteAllTextAsync(Path.Combine(baseline, "summary.json"), """{"valid":"invalid-type"}""");
        Assert.AreEqual(2, await Program.ExecuteAsync(
            ["run", "--source", corpus, "--output", changedInput, "--sample-id", "test-v1", "--baseline", baseline]));
    }

    [TestMethod]
    public async Task CommandDoesNotAcceptCompletedScanAsCancellationEvidence()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("empty");
        var output = Path.Combine(temp.Path, "cancel");
        Assert.AreEqual(1, await Program.ExecuteAsync(
            ["run", "--source", source, "--output", output, "--sample-id", "empty",
                "--runs", "1", "--cancel-after-ms", "60000"]));
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "summary.json")))!;
        Assert.IsFalse(summary["valid"]!.GetValue<bool>());
        Assert.AreEqual(0, summary["cancelledRuns"]!.GetValue<int>());
        Assert.IsNull(summary["cancellationMedianMs"]);
        Assert.IsFalse(File.Exists(Path.Combine(output, "snapshot.json")));
        Assert.AreEqual(2, await Program.ExecuteAsync(
            ["run", "--source", source, "--output", Path.Combine(temp.Path, "invalid"), "--sample-id", "empty", "--runs", "0"]));
    }

    private static string[] FileDigests(string directory) => Directory.EnumerateFiles(directory).Order()
        .Select(path => $"{Path.GetFileName(path)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}").ToArray();
}
