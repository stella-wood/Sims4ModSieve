using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.Packages.Tests;

[TestClass]
public sealed class ConflictGroupingRealCorpusTests
{
    [TestMethod]
    [TestCategory("RealCorpus")]
    public async Task GenerateGroupingStatistics()
    {
        var directory = Environment.GetEnvironmentVariable("SIMS4_MOD_SIEVE_REAL_MODS");
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            Assert.Inconclusive("Set SIMS4_MOD_SIEVE_REAL_MODS to a real Mods directory to generate statistics.");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Sims4ModSieve.sln"))) repository = repository.Parent;
        Assert.IsNotNull(repository);
        var artifactDirectory = Path.Combine(repository.FullName, "artifacts");
        var baselinePath = Path.Combine(artifactDirectory, "conflict-grouping-v2-statistics.json");
        if (!File.Exists(baselinePath))
            Assert.Inconclusive("Restore artifacts/conflict-grouping-v2-statistics.json to compare against the local historical baseline.");
        using var baselineDocument = JsonDocument.Parse(await File.ReadAllTextAsync(baselinePath));
        var baseline = baselineDocument.RootElement;
        var baselineGeneratedAtUtc = baseline.GetProperty("GeneratedAtUtc").GetDateTime();
        var baselineScale = new[] { "DiscoveredPackageCount", "AnalyzedPackageCount", "TotalIndexResources", "CandidateTgiCount",
            "EligibleFiles", "EligibleResources", "PreparedPairCount", "ExcludedFileCount" }
            .ToDictionary(name => name, name => baseline.GetProperty(name).GetInt64());
        var baselineExits = baseline.GetProperty("ExcludedFiles").EnumerateArray()
            .ToDictionary(file => file.GetProperty("Path").GetString()!, BaselineExitSignature, StringComparer.OrdinalIgnoreCase);
        var timer = Stopwatch.StartNew();
        var scan = await new PackageConflictScanner(new PhysicalFileSystemAccess(), LlamaLogicPackageIndexReader.CreateDefault())
            .ScanAsync(new PackageConflictScanRequest([new ScanSource("mods", directory)]));
        var scanMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var contents = await new ResourceContentComparer(DbpfResourceContentHasher.CreateDefault())
            .CompareAsync(new ResourceContentComparisonRequest(scan.Candidates));
        var comparisonMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var builder = new PackageConflictGroupingBuilder();
        var input = builder.Prepare(scan, contents);
        var prepareMs = timer.Elapsed.TotalMilliseconds;
        var currentScale = new Dictionary<string, long>
        {
            ["DiscoveredPackageCount"] = scan.DiscoveredPackageCount, ["AnalyzedPackageCount"] = scan.AnalyzedPackageCount,
            ["TotalIndexResources"] = scan.ResourceInventories.Sum(file => (long)file.ResourceCount),
            ["CandidateTgiCount"] = scan.Candidates.Count, ["EligibleFiles"] = input.FileCount,
            ["EligibleResources"] = input.Files.Sum(file => (long)file.ResourceCount), ["PreparedPairCount"] = input.Pairs.Count,
            ["ExcludedFileCount"] = input.ExcludedFiles.Count,
        };
        var differences = baselineScale.Where(entry => entry.Value != currentScale[entry.Key])
            .Select(entry => $"{entry.Key}：基线 {entry.Value}，本次 {currentScale[entry.Key]}").ToList();
        var exits = input.ExcludedFiles.Select(file => new
        {
            Path = Relative(file.Path),
            Reasons = file.ScanIssues.Select(issue => issue.Code)
                .Concat(file.FailedResources.Select(item => item.Issue?.Code ?? "missing-resource-content"))
                .Concat(file.InvalidVersions.Select(_ => "unstable-package-version"))
                .Concat(file.InputIssues.Select(issue => issue.Code)).Distinct().Order(StringComparer.Ordinal).ToArray(),
            ScanIssues = file.ScanIssues.Select(issue => issue with { Path = Relative(issue.Path) }).ToArray(),
            FailedResources = file.FailedResources.Select(item => new { Tgi = item.Key.Tgi, item.Ordinal, item.Issue }).ToArray(),
            InvalidVersions = file.InvalidVersions.Select(version => version with { Path = Relative(version.Path) }).ToArray(),
            file.InputIssues,
        }).ToArray();
        var currentExits = exits.ToDictionary(file => file.Path, file => string.Join('|', file.Reasons), StringComparer.OrdinalIgnoreCase);
        if (baselineExits.Count != currentExits.Count || baselineExits.Any(entry => !currentExits.TryGetValue(entry.Key, out var signature) || signature != entry.Value))
            differences.Add("退出文件集合或原因分类与基线不同。");
        var formalOptions = new PackageConflictGroupingOptions();
        var configurations = new List<ConfigurationStatistics>();
        foreach (var options in new[] { formalOptions,
            new PackageConflictGroupingOptions(new(OverrideConflictCardRuleKind.BySharedKeyOverlap, 0.5)) })
        {
            var rule = options.OverrideCardRule;
            timer.Restart();
            var result = builder.Build(input, options);
            var buildMs = timer.Elapsed.TotalMilliseconds;
            var overrides = result.Groups.Where(group => group.Kind == PackageConflictRelationKind.OverrideConflict).ToArray();
            Assert.AreEqual(result.Relations.Count(edge => edge.Kind == PackageConflictRelationKind.OverrideConflict),
                overrides.SelectMany(group => group.Relations).Count(), "圈卡不得丢失覆盖文件对。");
            foreach (var group in result.Groups)
            {
                var paths = group.Members.Select(member => member.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var allPairs = input.Pairs.Where(pair => paths.Contains(pair.FirstPath) && paths.Contains(pair.SecondPath)).ToArray();
                var displayPairs = group.Kind == PackageConflictRelationKind.OverrideConflict ? group.Relations.Select(edge => edge.Pair).ToArray() : allPairs;
                var keys = displayPairs.SelectMany(pair => pair.Resources).Select(match => match.Key.Tgi).Distinct().Order(StringComparer.Ordinal).ToArray();
                CollectionAssert.AreEqual(keys, group.ResourceKeys.Select(key => key.Tgi).ToArray());
                foreach (var member in group.Members)
                {
                    int SharedKeys(IEnumerable<PackageConflictPairSummary> pairs) => pairs
                        .Where(pair => StringComparer.OrdinalIgnoreCase.Equals(pair.FirstPath, member.Path) || StringComparer.OrdinalIgnoreCase.Equals(pair.SecondPath, member.Path))
                        .SelectMany(pair => pair.Resources).Select(match => match.Key).Distinct().Count();
                    Assert.AreEqual(SharedKeys(displayPairs), member.SharedResourceCount);
                    Assert.AreEqual(member.ResourceCount - SharedKeys(allPairs), member.UniqueResourceCount);
                    var references = result.GroupsByFile[member.Path.ToUpperInvariant()];
                    Assert.AreEqual(1, references.Count(reference => ReferenceEquals(reference, group)));
                }
            }
            foreach (var (path, references) in result.GroupsByFile)
            {
                Assert.AreEqual(references.Count, references.Distinct(ReferenceEqualityComparer.Instance).Count());
                Assert.IsTrue(references.All(reference => result.Groups.Any(group => ReferenceEquals(group, reference))));
                Assert.AreEqual(result.Groups.Count(group => group.Members.Any(member => StringComparer.OrdinalIgnoreCase.Equals(member.Path, path))), references.Count);
                Assert.ThrowsExactly<NotSupportedException>(() => ((IList<PackageConflictGroup>)references).Clear());
            }
            Assert.ThrowsExactly<NotSupportedException>(() => ((IDictionary<string, IReadOnlyList<PackageConflictGroup>>)result.GroupsByFile).Clear());
            var previous = baseline.GetProperty("Configurations").EnumerateArray().Single(row =>
                row.GetProperty("MinimumContainedResourceCount").GetInt32() == options.MinimumContainedResourceCount
                && row.GetProperty("OverrideRule").GetString() == rule.Kind.ToString()
                && (rule.SharedKeyOverlapThreshold is null || row.GetProperty("SharedKeyOverlapThreshold").GetDouble() == rule.SharedKeyOverlapThreshold));
            var before = previous.GetProperty("Override");
            var largest = overrides.OrderByDescending(group => group.Members.Count)
                .ThenBy(group => group.Members[0].Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            configurations.Add(new(rule.Kind.ToString(), rule.SharedKeyOverlapThreshold,
                new(before.GetProperty("Count").GetInt32(), before.GetProperty("MaximumFiles").GetInt32()),
                new(overrides.Length, overrides.Select(group => group.Members.Count).DefaultIfEmpty().Max()), buildMs,
                result.Groups.Count(group => group.Kind == PackageConflictRelationKind.BundleContainment),
                result.Groups.Count(group => group.Kind == PackageConflictRelationKind.SuspectedSameMod),
                new(result.GroupsByFile.Count, result.GroupsByFile.Values.Sum(references => references.Count),
                    result.GroupsByFile.Count(entry => entry.Value.Count > 1), true),
                largest is null ? null : new(largest.ResourceKeys.Select(key => key.Tgi).ToArray(), largest.Relations.Count,
                    largest.Members.Select(member => new MemberExample(Relative(member.Path), member.ResourceCount,
                        member.SharedResourceCount, member.UniqueResourceCount)).ToArray(),
                    ConflictIgnoreKeyBuilder.Build(largest.Members.Select(member => Relative(member.Path)), largest.ResourceKeys))));
        }
        Assert.AreEqual(2, configurations.Count);
        var data = new
        {
            GeneratedAtUtc = DateTime.UtcNow, BaselineGeneratedAtUtc = baselineGeneratedAtUtc,
            BaselineArtifact = "conflict-grouping-v2-statistics.json", BaselineScale = baselineScale, CorpusScale = currentScale,
            Comparable = differences.Count == 0, ComparabilityLimitations = differences,
            MinimumContainedResourceCount = formalOptions.MinimumContainedResourceCount, SameModThreshold = formalOptions.SameModThreshold,
            ScanExecutions = 1, ContentComparisonExecutions = 1, ScanMilliseconds = scanMs,
            ComparisonMilliseconds = comparisonMs, PrepareMilliseconds = prepareMs, contents.ProcessedBytes,
            ExcludedFiles = exits, Configurations = configurations,
        };
        await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "conflict-grouping-v2-scoped-overrides.json"),
            JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var report = new StringBuilder();
        report.AppendLine("# 覆盖卡范围修正对照统计").AppendLine();
        report.AppendLine($"原统计：[conflict-grouping-v2-statistics.json](conflict-grouping-v2-statistics.json)，生成于 {baselineGeneratedAtUtc:yyyy-MM-dd HH:mm:ss} UTC；本次生成于 {data.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss} UTC。新机器结果：[conflict-grouping-v2-scoped-overrides.json](conflict-grouping-v2-scoped-overrides.json)。").AppendLine();
        report.AppendLine($"正式配置：合集门槛 {formalOptions.MinimumContainedResourceCount}、同 mod 线 {formalOptions.SameModThreshold}、`{formalOptions.OverrideCardRule.Kind}`；`BySharedKeyOverlap` 0.5 为实验对照。参数见[关系判定](../docs/design/冲突检测分组方案-v2.md#4-关系判定)，正式与实验规则、红卡计数及独有 M 口径见[覆盖卡](../docs/design/冲突检测分组方案-v2.md#43-覆盖冲突卡片)，忽略键见[忽略](../docs/design/冲突检测分组方案-v2.md#8-忽略)。本次范围修正未改变圈卡算法和成员资格，卡数不必下降。WPF 接入状态见[第一期 Core 实现状态](../docs/design/冲突检测分组方案-v2.md#第一期-core-实现状态)。").AppendLine();
        report.AppendLine("| 规模 | 基线 | 本次 |").AppendLine("|---|---:|---:|");
        foreach (var entry in currentScale) report.AppendLine($"| {entry.Key} | {baselineScale[entry.Key]} | {entry.Value} |");
        report.AppendLine().AppendLine(data.Comparable ? "上述语料规模、退出文件集合与原因分类、准备后的文件对数量均与基线一致。" : $"可比性限制：{string.Join("；", differences)}。不能把全部变化归因于本次代码。").AppendLine();
        report.AppendLine($"扫描 {scanMs / 1000:F3} 秒、内容比较 {comparisonMs / 1000:F3} 秒、Prepare {prepareMs:F3} 毫秒，各执行一次，两个配置共用输入。").AppendLine();
        report.AppendLine("| 规则 | 改前覆盖卡 | 改后覆盖卡 | 改前最大文件数 | 改后最大文件数 | Build ms |").AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var row in configurations) report.AppendLine($"| {Label(row)} | {row.Before.Count} | {row.After.Count} | {row.Before.MaximumFiles} | {row.After.MaximumFiles} | {row.BuildMilliseconds:F3} |");
        report.AppendLine().AppendLine("## 新最大覆盖卡").AppendLine();
        foreach (var row in configurations)
        {
            report.AppendLine($"### {Label(row)}").AppendLine();
            if (row.LargestOverride is not { } example) { report.AppendLine("无覆盖卡。").AppendLine(); continue; }
            report.AppendLine($"圈卡依据 TGI：{string.Join("、", example.ResourceKeys.Select(key => $"`{key}`"))}；收录 {example.RelationCount} 个覆盖文件对。忽略键：`{example.IgnoreKey}`。").AppendLine();
            report.AppendLine("| 成员相对路径 | 资源总数 | 撞 N | 独有 M |").AppendLine("|---|---:|---:|---:|");
            foreach (var member in example.Members) report.AppendLine($"| `{member.Path}` | {member.Resources} | {member.SharedResources} | {member.UniqueResources} |");
            report.AppendLine();
        }
        report.AppendLine("## 跨卡引用与退出文件").AppendLine();
        foreach (var row in configurations)
            report.AppendLine($"- {Label(row)}：索引 {row.CrossCardReferences.FilesIndexed} 个文件、{row.CrossCardReferences.TotalReferences} 条卡引用，{row.CrossCardReferences.FilesInMultipleCards} 个文件跨卡；引用身份、完整性、去重、大小写 lookup、外层字典与内层列表只读检查通过。合集卡 {row.BundleCards}，疑似卡 {row.SameModCards}；黄卡键与计数检查通过。");
        report.AppendLine();
        foreach (var file in exits) report.AppendLine($"- `{file.Path}`：{string.Join("、", file.Reasons)}；失败资源 {string.Join("、", file.FailedResources.Select(item => item.Tgi))}。");
        await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "conflict-grouping-v2-scoped-overrides.md"), report.ToString());

        string Relative(string path) => Path.GetRelativePath(directory, path);
        static string Label(ConfigurationStatistics row) => row.OverrideRule + (row.SharedKeyOverlapThreshold is null ? "" : " 0.5");
        static string BaselineExitSignature(JsonElement file) => string.Join('|',
            file.GetProperty("ScanIssues").EnumerateArray().Select(issue => issue.GetProperty("Code").GetString()!)
                .Concat(file.GetProperty("FailedResources").EnumerateArray().Select(item => item.GetProperty("Issue").GetProperty("Code").GetString()!))
                .Concat(file.GetProperty("InvalidVersions").EnumerateArray().Select(_ => "unstable-package-version"))
                .Concat(file.GetProperty("InputIssues").EnumerateArray().Select(issue => issue.GetProperty("Code").GetString()!))
                .Distinct().Order(StringComparer.Ordinal));
    }

    private sealed record CardStatistics(int Count, int MaximumFiles);
    private sealed record MemberExample(string Path, int Resources, int SharedResources, int UniqueResources);
    private sealed record GroupExample(string[] ResourceKeys, int RelationCount, MemberExample[] Members, string IgnoreKey);
    private sealed record CrossCardCheck(int FilesIndexed, int TotalReferences, int FilesInMultipleCards, bool Passed);
    private sealed record ConfigurationStatistics(string OverrideRule, double? SharedKeyOverlapThreshold, CardStatistics Before,
        CardStatistics After, double BuildMilliseconds, int BundleCards, int SameModCards, CrossCardCheck CrossCardReferences, GroupExample? LargestOverride);
}
