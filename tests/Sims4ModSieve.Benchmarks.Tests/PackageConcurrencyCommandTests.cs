using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Benchmarks;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Tests;

namespace Sims4ModSieve.Benchmarks.Tests;

[TestClass]
public sealed class PackageConcurrencyCommandTests
{
    [TestMethod]
    [DataRow(null, null, "legacy", "legacy")]
    [DataRow("auto", "auto", "auto", "auto")]
    [DataRow("1", "8", "1", "8")]
    [DataRow("2", "4", "2", "4")]
    [DataRow("4", "2", "4", "2")]
    [DataRow("8", "1", "8", "1")]
    [DataRow(null, "4", "legacy", "4")]
    [DataRow("4", null, "4", "legacy")]
    public async Task PackageConcurrencyOptionsReachStagesAndPreserveLegacySnapshot(string? index, string? content,
        string expectedIndexLabel, string expectedContentLabel)
    {
        using var temp = new TempDirectory();
        var corpus = Path.Combine(temp.Path, "corpus");
        BenchmarkInputs.Generate(corpus);
        var baseline = Path.Combine(temp.Path, "baseline");
        var output = Path.Combine(temp.Path, "configured");
        string[] Arguments(string destination) => ["run", "--source", corpus, "--output", destination,
            "--sample-id", "phase2", "--runs", "1"];
        Assert.AreEqual(0, await Program.ExecuteAsync(Arguments(baseline)));
        var arguments = Arguments(output).Concat(new[] { "--baseline", baseline }).ToList();
        if (index is not null) arguments.AddRange(["--index-concurrency", index]);
        if (content is not null) arguments.AddRange(["--content-concurrency", content]);
        Assert.AreEqual(0, await Program.ExecuteAsync(arguments.ToArray()));
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "summary.json")))!;
        Assert.AreEqual(expectedIndexLabel, summary["indexConcurrency"]!.GetValue<string>());
        Assert.AreEqual(expectedContentLabel, summary["contentConcurrency"]!.GetValue<string>());
        var run = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "run-01.json")))!;
        var stages = run["stages"]!.AsArray();
        int Concurrency(string name) => stages.Single(stage => stage!["name"]!.GetValue<string>() == name)!["parallelism"]!.GetValue<int>();
        var auto = OperatingSystem.IsWindows() ? (int?)null : 2;
        var expectedIndex = index == "auto" ? auto : index is null ? PackageConflictScanner.DefaultMaxDegreeOfParallelism : int.Parse(index);
        var expectedContent = content == "auto" ? auto : content is null ? ResourceContentLimits.DefaultMaxDegreeOfParallelism : int.Parse(content);
        if (expectedIndex is { } i) Assert.AreEqual(i, Concurrency("Package · 排序和读取索引"));
        if (expectedContent is { } c) Assert.AreEqual(c, Concurrency("资源内容 · 复核索引、读取、解压和哈希"));
        Assert.AreEqual(2, Concurrency("脚本 · ZIP 目录、模块读取、解压和哈希"));
        Assert.IsNull(ResultSnapshot.FirstDifference(Path.Combine(baseline, "snapshot.json"), Path.Combine(output, "snapshot.json")));
        var log = await File.ReadAllTextAsync(Path.Combine(output, "run-01.log"));
        Assert.AreEqual(index is not null, log.Contains("阶段=package 索引；", StringComparison.Ordinal));
        Assert.AreEqual(content is not null, log.Contains("阶段=资源内容；", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("--index-concurrency", "3")]
    [DataRow("--content-concurrency", "16")]
    [DataRow("--index-concurrency", "unknown")]
    public async Task InvalidPackageConcurrencyIsRejectedBeforeOutput(string option, string value)
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("empty");
        var output = Path.Combine(temp.Path, "rejected");
        Assert.AreEqual(2, await Program.ExecuteAsync(["run", "--source", source, "--output", output,
            "--sample-id", "invalid", option, value]));
        Assert.IsFalse(Directory.Exists(output));
    }
}
