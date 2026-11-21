using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Benchmarks;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Tests;
using Sims4ModSieve.Core.Tests.Packages;

namespace Sims4ModSieve.Benchmarks.Tests;

[TestClass]
public sealed class BenchmarkBaselineTests
{
    [TestMethod]
    public async Task CachedRunReportsColdMissesThenWarmHitsForIdenticalSnapshots()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("Mods/a.package", "same");
        temp.WriteFile("Mods/b.package", "same");
        var output = Path.Combine(temp.Path, "benchmark");

        Assert.AreEqual(0, await Program.ExecuteAsync(["run", "--source", Path.Combine(temp.Path, "Mods"),
            "--output", output, "--sample-id", "cache", "--scope", "duplicates", "--runs", "2", "--cache", "enabled"]));

        var first = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "run-01.json")))!;
        var second = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "run-02.json")))!;
        Assert.AreEqual(0, first["cacheHits"]!.GetValue<int>());
        Assert.AreEqual(2, first["cacheMisses"]!.GetValue<int>());
        Assert.AreEqual(2, second["cacheHits"]!.GetValue<int>());
        Assert.AreEqual(0, second["cacheMisses"]!.GetValue<int>());
        Assert.IsTrue(JsonNode.DeepEquals(first["snapshot"], second["snapshot"]));
    }

    [TestMethod]
    public async Task CompletedRunCachesSuccessfulHashesDespiteIncompleteAnalysis()
    {
        using var temp = new TempDirectory();
        var corpus = Path.Combine(temp.Path, "corpus");
        BenchmarkInputs.Generate(corpus);
        var output = Path.Combine(temp.Path, "benchmark");

        Assert.AreEqual(0, await Program.ExecuteAsync(["run", "--source", corpus, "--output", output,
            "--sample-id", "cache-with-issues", "--runs", "2", "--cache", "enabled"]));

        var first = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "run-01.json")))!;
        var second = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "run-02.json")))!;
        var snapshot = first["snapshot"]!;
        Assert.IsTrue(snapshot["packages"]!["issues"]!.AsArray().Count > 0);
        Assert.IsTrue(snapshot["scripts"]!["issues"]!.AsArray().Count > 0);
        Assert.IsTrue(snapshot["contents"]!["comparisons"]!.AsArray()
            .Any(comparison => comparison!["uncompared"]!.AsArray().Count > 0));
        Assert.IsTrue(JsonNode.DeepEquals(snapshot, second["snapshot"]));
        var misses = first["cacheMisses"]!.GetValue<int>();
        Assert.IsTrue(misses >= 2);
        Assert.AreEqual(0, first["cacheHits"]!.GetValue<int>());
        Assert.AreEqual(misses, second["cacheHits"]!.GetValue<int>());
        Assert.AreEqual(0, second["cacheMisses"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task SyntheticPipelineIsRepeatableAndPreservesFailuresWithoutWritingInputs()
    {
        using var temp = new TempDirectory();
        var corpus = Path.Combine(temp.Path, "corpus");
        BenchmarkInputs.Generate(corpus);
        var before = FileDigests(corpus);
        var manifest = InputManifest.Capture([corpus]);
        ScanSource[] sources = [new("source-1", corpus)];
        var first = await BenchmarkRunner.MeasureAsync(token => BenchmarkRunner.CaptureAsync(sources, corpus, token,
            Path.Combine(temp.Path, "first.json")));
        var second = await BenchmarkRunner.MeasureAsync(token => BenchmarkRunner.CaptureAsync(sources, corpus, token,
            Path.Combine(temp.Path, "second.json")));

        Assert.AreEqual("completed", first.Outcome);
        Assert.IsNull(first.CancellationLatencyMs);
        Assert.IsNotNull(first.Snapshot);
        Assert.IsNotNull(second.Snapshot);
        Assert.IsNull(ResultSnapshot.FirstDifference(first.Snapshot, second.Snapshot));
        var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(first.Snapshot))!;
        Assert.AreEqual(10, snapshot["duplicates"]!["discoveredFileCount"]!.GetValue<int>());
        Assert.AreEqual(1, snapshot["duplicates"]!["groups"]!.AsArray().Count);
        Assert.IsTrue(snapshot["packages"]!["issues"]!.AsArray().Count > 0);
        Assert.IsTrue(snapshot["scripts"]!["issues"]!.AsArray().Count > 0);
        var comparisons = snapshot["contents"]!["comparisons"]!.AsArray();
        Assert.AreEqual(1, comparisons.Count);
        Assert.AreEqual(2, comparisons[0]!["contentGroups"]!.AsArray().Count);
        var issues = comparisons[0]!["uncompared"]!.AsArray()
            .Select(node => node!["issue"]!["code"]!.GetValue<string>()).ToArray();
        CollectionAssert.Contains(issues, ResourceContentIssueCode.UnsupportedCompression);
        Assert.IsTrue(issues.Any(code => code != ResourceContentIssueCode.UnsupportedCompression));
        Assert.IsTrue(snapshot["grouping"]!["excludedFiles"]!.AsArray().Count > 0);
        Assert.IsTrue(first.Stages.Any(stage => stage.Name == "Package · 排序和读取索引"));
        Assert.IsTrue(first.Stages.Any(stage => stage.DecompressionAttempts > 0));
        Assert.IsTrue(first.Stages.Any(stage => stage.Name == "基准 · 生成一致性快照"));
        Assert.AreEqual(manifest.Fingerprint, InputManifest.Capture([corpus]).Fingerprint);
        CollectionAssert.AreEqual(before, FileDigests(corpus));
        Assert.IsTrue(!snapshot.AsObject().ContainsKey("startedAtUtc"));
        Assert.IsTrue(!snapshot["contents"]!.AsObject().ContainsKey("processedBytes"));
    }

    [TestMethod]
    public void SnapshotComparisonPreservesOrderIssuesAndRelationshipFacts()
    {
        var baseline = JsonNode.Parse("""
            {"groups":[{"hash":"a","members":["keep","delete"],"keep":"keep"}],
             "issues":[{"code":"failed","path":"bad"}],
             "pair":{"identical":2,"different":1,"contained":true}}
            """)!;
        Assert.IsNull(CompareSnapshots(baseline, JsonNode.Parse("""
            {"pair":{"contained":true,"different":1,"identical":2},
             "issues":[{"path":"bad","code":"failed"}],
             "groups":[{"keep":"keep","members":["keep","delete"],"hash":"a"}]}
            """)));
        var changed = baseline.DeepClone();
        changed["groups"]![0]!["members"] = new JsonArray("delete", "keep");
        Assert.AreEqual("$.groups[0].members[0]: value differs", CompareSnapshots(baseline, changed));
        changed = baseline.DeepClone();
        changed["issues"]![0]!["code"] = "silently-passed";
        Assert.AreEqual("$.issues[0].code: value differs", CompareSnapshots(baseline, changed));
        changed = baseline.DeepClone();
        changed["pair"]!["contained"] = false;
        Assert.AreEqual("$.pair.contained: value differs", CompareSnapshots(baseline, changed));
        changed = baseline.DeepClone();
        changed["groups"]![0]!["keep"] = "delete";
        Assert.IsNotNull(CompareSnapshots(baseline, changed));
        changed = baseline.DeepClone();
        changed["groups"]![0]!["hash"] = "b";
        Assert.IsNotNull(CompareSnapshots(baseline, changed));
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
            return "unused.json";
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
            return "unused.json";
        }, cancelAfterMs: 10);
        Assert.AreEqual("cancelled", result.Outcome);
        Assert.IsNotNull(result.CancellationLatencyMs);
        var completed = await BenchmarkRunner.MeasureAsync(_ => Task.FromResult("unused.json"), cancelAfterMs: 60000);
        Assert.AreEqual("completed", completed.Outcome);
        Assert.IsNull(completed.CancellationLatencyMs);
    }

    [TestMethod]
    public async Task UnexpectedFailureIsRecordedWithoutPublishingSuccessfulResults()
    {
        var result = await BenchmarkRunner.MeasureAsync(_ =>
            Task.FromException<string>(new IOException("injected failure")));
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

    [TestMethod]
    public async Task DuplicatesScopeOnlyHashesFilesAndComparesItsOwnBaselines()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var backup = temp.CreateDirectory("Backup");
        foreach (var directory in new[] { mods, backup })
        {
            File.WriteAllText(Path.Combine(directory, "broken.package"), "invalid DBPF, valid duplicate");
            File.WriteAllText(Path.Combine(directory, "broken.ts4script"), "invalid ZIP, valid duplicate");
            File.WriteAllText(Path.Combine(directory, "ignored.txt"), "not a mod");
        }
        var beforeMods = FileDigests(mods);
        var beforeBackup = FileDigests(backup);
        var baseline = Path.Combine(temp.Path, "duplicates");
        string[] Arguments(string output) => ["run", "--source", mods, "--source", backup, "--mods-root", mods,
            "--output", output, "--sample-id", "paired", "--scope", "duplicates", "--runs", "3"];
        Assert.AreEqual(0, await Program.ExecuteAsync(Arguments(baseline)));
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(baseline, "summary.json")))!;
        Assert.AreEqual("duplicates", summary["scope"]!.GetValue<string>());
        Assert.AreEqual(3, summary["completedRuns"]!.GetValue<int>());
        Assert.IsTrue(summary["stableInput"]!.GetValue<bool>());
        Assert.IsTrue(summary["valid"]!.GetValue<bool>());
        var stageNames = summary["stageMedians"]!.AsArray().Select(stage => stage!["name"]!.GetValue<string>()).ToArray();
        Assert.AreEqual(5, stageNames.Length);
        Assert.IsTrue(stageNames.All(name => name.StartsWith("整文件核对", StringComparison.Ordinal)
            || name == "基准 · 生成一致性快照"));
        var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(baseline, "snapshot.json")))!;
        CollectionAssert.AreEquivalent(new[] { "schemaVersion", "duplicates" }, snapshot.AsObject().Select(pair => pair.Key).ToArray());
        Assert.AreEqual(4, snapshot["duplicates"]!["discoveredFileCount"]!.GetValue<int>());
        var groups = snapshot["duplicates"]!["groups"]!.AsArray();
        Assert.AreEqual(2, groups.Count);
        Assert.IsTrue(groups.All(group => Path.GetDirectoryName(group!["suggestedKeepPath"]!.GetValue<string>()) == mods));
        var run = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(baseline, "run-01.json")))!;
        Assert.IsTrue(JsonNode.DeepEquals(snapshot, run["snapshot"]));
        Assert.AreEqual(0, await Program.ExecuteAsync([.. Arguments(Path.Combine(temp.Path, "repeat")), "--baseline", baseline]));

        var all = Arguments(Path.Combine(temp.Path, "wrong-scope"));
        all[Array.IndexOf(all, "--scope") + 1] = "all";
        Assert.AreEqual(2, await Program.ExecuteAsync([.. all, "--baseline", baseline]));
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "wrong-scope")));
        all[Array.IndexOf(all, "--scope") + 1] = "unknown";
        Assert.AreEqual(2, await Program.ExecuteAsync(all));
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "wrong-scope")));
        CollectionAssert.AreEqual(beforeMods, FileDigests(mods));
        CollectionAssert.AreEqual(beforeBackup, FileDigests(backup));
    }

    [TestMethod]
    public async Task DefaultScopeAcceptsLegacyAllBaselineAndRejectsMalformedSnapshots()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("empty");
        var baseline = Path.Combine(temp.Path, "baseline");
        string[] Arguments(string output) => ["run", "--source", source, "--output", output,
            "--sample-id", "legacy", "--runs", "1"];
        Assert.AreEqual(0, await Program.ExecuteAsync(Arguments(baseline)));
        var summaryPath = Path.Combine(baseline, "summary.json");
        var header = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!.AsObject();
        header.Remove("scope");
        await File.WriteAllTextAsync(summaryPath, header.ToJsonString());
        Assert.AreEqual(0, await Program.ExecuteAsync([.. Arguments(Path.Combine(temp.Path, "repeat")), "--baseline", baseline]));
        var duplicates = Arguments(Path.Combine(temp.Path, "wrong-scope"));
        Assert.AreEqual(2, await Program.ExecuteAsync([.. duplicates, "--scope", "duplicates", "--baseline", baseline]));
        await File.WriteAllTextAsync(Path.Combine(baseline, "snapshot.json"), "{\"unfinished\":[");
        var bad = Path.Combine(temp.Path, "bad");
        Assert.AreEqual(2, await Program.ExecuteAsync([.. Arguments(bad), "--baseline", baseline]));
        Assert.IsFalse(Directory.Exists(bad));
    }

    [TestMethod]
    public async Task PairedCorpusCompletesThreeRunsUnderBoundedHeap()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var backup = temp.CreateDirectory("Backup");
        for (var package = 0; package < 32; package++)
        {
            var builder = new DbpfFixtureBuilder();
            for (var resource = 0; resource < 64; resource++)
                builder.AddContent(BitConverter.GetBytes(resource), instanceLo: (uint)(resource < 48
                    ? resource + 1 : 100000 + package * 64 + resource));
            var bytes = builder.Build();
            File.WriteAllBytes(Path.Combine(mods, $"{package:D3}.package"), bytes);
            File.WriteAllBytes(Path.Combine(backup, $"{package:D3}.package"), bytes);
        }
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var output = Path.Combine(temp.Path, "results");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        string[] arguments = [typeof(Program).Assembly.Location, "run", "--source", mods, "--source", backup,
            "--mods-root", mods, "--output", output, "--sample-id", "paired-memory", "--runs", "3"];
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_GCHeapHardLimit"] = "0x10000000";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await stdout + await stderr);
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "summary.json")))!;
        Assert.IsTrue(summary["valid"]!.GetValue<bool>());
        Assert.AreEqual(3, summary["completedRuns"]!.GetValue<int>());
        Assert.IsTrue(summary["stableInput"]!.GetValue<bool>());
        Assert.IsNull(summary["difference"]);
    }

    private static string[] FileDigests(string directory) => Directory.EnumerateFiles(directory).Order()
        .Select(path => $"{Path.GetFileName(path)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}").ToArray();

    private static string? CompareSnapshots(JsonNode? expected, JsonNode? actual)
    {
        using var temp = new TempDirectory();
        var left = temp.WriteFile("left.json", expected?.ToJsonString() ?? "null");
        var right = temp.WriteFile("right.json", actual?.ToJsonString() ?? "null");
        return ResultSnapshot.FirstDifference(left, right);
    }
}
