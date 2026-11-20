using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Packages;

namespace Sims4ModSieve.Benchmarks;

internal sealed class RawReuseReport
{
    public int SchemaVersion => 1;
    public IReadOnlyList<string> Scope { get; init; } = [];
    public bool StableInput { get; set; } = true;
    public int DiscoveredPackages { get; init; }
    public int IndexedPackages { get; init; }
    public long CandidateResources { get; init; }
    public long NoneResources { get; init; }
    public long NeedsDecompressionResources { get; init; }
    public long StoredBytes { get; init; }
    public long DeclaredOutputBytes { get; init; }
    public long ValidatedDecompressionResources { get; init; }
    public long MetadataGroups { get; init; }
    public long DigestPotentialSavings { get; init; }
    public long InvalidContentSavingsExcluded => DigestPotentialSavings - SavedDecompressions;
    public long EquivalentSets { get; init; }
    public long EquivalentResources { get; init; }
    public long SavedDecompressions { get; init; }
    public double? SavedDecompressionRatio => Ratio(SavedDecompressions, NeedsDecompressionResources);
    public long SavedOutputBytes { get; init; }
    public double? SavedOutputRatio => Ratio(SavedOutputBytes, DeclaredOutputBytes);
    public long FingerprintPreReadBytes { get; init; }
    public long ConfirmationReadBytes { get; init; }
    public long GrossAdditionalReadBytes => FingerprintPreReadBytes + ConfirmationReadBytes;
    public long AvoidedOriginalReadBytes { get; init; }
    public long NetAdditionalReadBytes => GrossAdditionalReadBytes - AvoidedOriginalReadBytes;
    public long SingletonMetadataResources { get; init; }
    public WholeFileCopies WholeFileCopies { get; init; } = new(0, 0, 0, 0, 0, 0);
    public IReadOnlyList<SetSizeCount> SetSizeDistribution { get; init; } = [];
    public IReadOnlyList<TypeSavings> TopTypes { get; init; } = [];
    public IReadOnlyDictionary<string, long> ScanIssueCounts { get; init; } = new Dictionary<string, long>();
    public IReadOnlyDictionary<string, long> ResourceSkipCounts { get; init; } = new Dictionary<string, long>();
    public long SkippedResources => ResourceSkipCounts.Values.Sum();
    public IReadOnlyDictionary<string, long> WholeFileHashSkipCounts { get; init; } = new Dictionary<string, long>();
    public double ElapsedMilliseconds { get; set; }

    private static double? Ratio(long numerator, long denominator) => denominator == 0 ? null : (double)numerator / denominator;
}

internal sealed record WholeFileCopies(long Sets, long RedundantPackages, long CandidateResourcesInRedundantCopies,
    long NeedsDecompressionResourcesInRedundantCopies, long SavedDecompressions, long SavedOutputBytes);
internal sealed record SetSizeCount(int Size, long Sets, long Resources);
internal sealed record TypeSavings(string Type, long EquivalentSets, long SavedDecompressions, long SavedOutputBytes);

internal static class RawReuseStats
{
    private const int ChunkSize = 64 * 1024;
    private static readonly string[] Scope =
    [
        "调用生产 PackageConflictScanner：仅 .package（.off 不入选），不跟随链接；重叠来源按路径去重，排除应用目录；索引失败的包不进入候选。",
        "仅同 TGI 出现在至少两个不同 package 的资源；按各包的 ToolingMetadataTypes 排除工具元数据；按路径、文件戳、Ordinal/TGI 合批去重。",
        "桌面端在资源内容比较之后才做整文件重复核对与分组；当前生产内容阶段不合并整文件副本。本报告保留这些副本，并单列其贡献。忽略是结果展示状态，不在读取前排除。",
        "候选总数包含 None、坏资源及不支持的压缩。需解压分母仅包含实际编号 0x5A42（zlib）、范围及默认单条大小上限合格的资源，包含随后解压失败者；None、RefPack、Unknown、Deleted 及提前拒绝者不进入该分母。",
        "逐包使用生产 DbpfResourceContentHasher 完整验证所有候选；仅验证成功且本次读取稳定的 zlib 计收益。独立只读定位器读取实际压缩编号、存储长度和声明输出长度。",
        "同 TGI + 实际压缩编号 + 存储长度 L + 声明输出长度 D 成元数据桶；只对多成员桶完整读取原始存储字节并计算 SHA-256，再按摘要分集合。集合大小分布只列大小≥2、验证成功的集合；本统计未实际执行逐字节确认，摘要相同是统计依据，不是生产复用授权。",
        "成本估算：单成员桶 0；多成员桶指纹预读 ΣL（含坏 zlib）；每个原始摘要命中集合确认读取 2(k−1)L（含坏 zlib）。净额外读取=指纹预读+确认读取−可省原路径读取 Σ(k−1)L；成功集合为 (2k−1)L，未命中者仍多读 L。未读成摘要者无法估确认量。",
        "不估算新增打开、文件戳、header/索引复核、故障回退及 OS 缓存成本；字节为逻辑输入量，不是物理磁盘 IO 或耗时收益。统计本身包含原路径解压验证、整文件哈希、指纹和最终戳检查，不是优化后性能基准。",
        "整文件副本单列范围为索引成功的 package，按完整 SHA-256 分组；稳定路径首项仅用于统计归因，不改变候选。整文件哈希失败单列，相关资源仍可统计，但副本贡献可能少计。",
        "文件在任一复核时变动或无法确认时排除该包资源，stableInput=false、退出 1；文件戳不能发现同大小同时间戳改写。统计期间应保持来源不变。耗时不含报告写入；失败/不支持计数不表示扫描已完整分析所有文件。",
    ];

    private sealed class Item(string path, FileStamp stamp, ResourceContentTarget target)
    {
        internal string Path { get; } = path;
        internal FileStamp Stamp { get; } = stamp;
        internal ResourceContentTarget Target { get; } = target;
        internal RawResourceDescriptor? Descriptor { get; set; }
        internal string? Skip { get; set; }
        internal string? RawSha256 { get; set; }
        internal bool NeedsDecompression => Descriptor is { } entry && entry.NeedsDecompression(Stamp.Length);
    }

    internal static async Task<int> RunAsync(IReadOnlyList<string> paths, string output)
    {
        var sources = paths.Select((path, index) => new ScanSource($"source-{index + 1}", Path.GetFullPath(path), Order: index)).ToArray();
        output = Path.GetFullPath(output);
        BenchmarkInputs.ValidateOutput(output, sources.Select(source => source.Path));
        using var lifetime = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; lifetime.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var report = await CollectAsync(sources, lifetime.Token);
            BenchmarkInputs.ValidateOutput(output, sources.Select(source => source.Path));
            Directory.CreateDirectory(output);
            await using (var stream = new FileStream(Path.Combine(output, "stats.json"), FileMode.CreateNew, FileAccess.Write))
                await JsonSerializer.SerializeAsync(stream, report, ResultSnapshot.JsonOptions, lifetime.Token);
            await File.WriteAllTextAsync(Path.Combine(output, "stats.md"), Markdown(report), lifetime.Token);
            Console.Error.WriteLine($"候选 {report.CandidateResources:N0} 条，需解压 {report.NeedsDecompressionResources:N0} 条；"
                + $"等价集合 {report.EquivalentSets:N0} 个，可省 {report.SavedDecompressions:N0} 次（{Percent(report.SavedDecompressionRatio)}）、"
                + $"输出 {report.SavedOutputBytes:N0} 字节（{Percent(report.SavedOutputRatio)}）；"
                + $"净额外读取估算 {report.NetAdditionalReadBytes:N0} 字节；跳过资源 {report.SkippedResources:N0}，"
                + $"扫描问题 {report.ScanIssueCounts.Values.Sum():N0}；耗时 {report.ElapsedMilliseconds:F0} ms；输入稳定={report.StableInput}。");
            return report.StableInput ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("统计已取消，未完成的输出不能作为统计结果。");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static async Task<RawReuseReport> CollectAsync(IReadOnlyList<ScanSource> sources, CancellationToken token = default)
    {
        var watch = Stopwatch.StartNew();
        var fileSystem = new PhysicalFileSystemAccess();
        var scan = await new PackageConflictScanner(fileSystem, LlamaLogicPackageIndexReader.CreateDefault())
            .ScanAsync(new(sources), token);
        var limits = ResourceContentLimits.Default;
        var budget = new ResourceContentBudget(limits.MaxTotalProcessedBytes);
        var hasher = new DbpfResourceContentHasher(fileSystem);
        var items = new List<Item>();
        var changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stable = true;
        // 扫描器已按路径去重，且每个路径只有一个索引版本；与内容比较器相同地按 ordinal/TGI 去重。
        foreach (var package in scan.Candidates.SelectMany(candidate => candidate.Occurrences)
                     .GroupBy(occurrence => occurrence.PackagePath, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var stamp = package.First().PackageStamp;
            var targets = package.Select(occurrence => new ResourceContentTarget(occurrence.Key, occurrence.Ordinal))
                .Distinct().OrderBy(target => target.Ordinal).ThenBy(target => target.Key.Type)
                .ThenBy(target => target.Key.Group).ThenBy(target => target.Key.Instance).ToArray();
            var batch = targets.Select(target => new Item(package.Key, stamp, target)).ToArray();
            items.AddRange(batch);
            var result = await hasher.HashAsync(new(package.Key, stamp, targets, limits), budget, token);
            for (var index = 0; index < batch.Length; index++) batch[index].Skip = result.Results[index].Issue?.Code;
            if (result.Results.Any(value => value.Issue?.Stage == ResourceContentStage.Stability))
            {
                stable = false;
                changedPaths.Add(package.Key);
            }
            try
            {
                EnsureStamp(package.Key, stamp, fileSystem);
                using var stream = fileSystem.OpenRead(package.Key);
                var entries = RawResourceIndex.Read(stream, stamp.Length, targets);
                foreach (var item in batch) item.Descriptor = entries[item.Target.Ordinal];
                EnsureStamp(package.Key, stamp, fileSystem);
            }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                if (exception is FileChangedDuringHashException) { stable = false; changedPaths.Add(package.Key); }
                foreach (var item in batch) item.Skip ??= "raw-index-" + exception.GetType().Name;
            }
        }

        var fileHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hashSkips = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var fullHasher = new StableFileHasher(fileSystem);
        // 长度独特的整文件不可能有副本，无须额外读取完整包。
        foreach (var sameLength in scan.ResourceInventories.GroupBy(inventory => inventory.ScannedStamp.Length).Where(group => group.Count() > 1))
        foreach (var inventory in sameLength)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var hash = await fullHasher.HashAsync(inventory.Path, token);
                if (hash.Stamp != inventory.ScannedStamp) throw new FileChangedDuringHashException(inventory.Path);
                fileHashes.Add(inventory.Path, hash.Sha256);
            }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                Increment(hashSkips, exception.GetType().Name);
                if (exception is FileChangedDuringHashException) { stable = false; changedPaths.Add(inventory.Path); }
            }
        }

        long preRead = 0;
        long singleton = 0;
        var buffer = new byte[ChunkSize];
        var fingerprintItems = new List<Item>();
        foreach (var metadata in items.Where(item => item.NeedsDecompression)
                     .GroupBy(item => (item.Target.Key, item.Descriptor!.Value.Compression,
                         item.Descriptor.Value.StoredSize, item.Descriptor.Value.DeclaredSize)))
        {
            if (metadata.Count() == 1) { singleton++; continue; }
            fingerprintItems.AddRange(metadata);
        }
        preRead = fingerprintItems.Sum(item => (long)item.Descriptor!.Value.StoredSize);
        foreach (var package in fingerprintItems.GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var stamp = package.First().Stamp;
            try
            {
                EnsureStamp(package.Key, stamp, fileSystem);
                using var stream = fileSystem.OpenRead(package.Key);
                foreach (var item in package.OrderBy(item => item.Descriptor!.Value.Position))
                {
                    token.ThrowIfCancellationRequested();
                    var entry = item.Descriptor!.Value;
                    try
                    {
                        stream.Position = entry.Position;
                        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        long remaining = entry.StoredSize;
                        while (remaining > 0)
                        {
                            var length = (int)Math.Min(remaining, buffer.Length);
                            await stream.ReadExactlyAsync(buffer.AsMemory(0, length), token);
                            digest.AppendData(buffer, 0, length);
                            remaining -= length;
                        }
                        item.RawSha256 = Convert.ToHexString(digest.GetHashAndReset());
                    }
                    catch (Exception exception) when (IsReadFailure(exception))
                    {
                        item.Skip ??= "raw-payload-" + exception.GetType().Name;
                    }
                }
                EnsureStamp(package.Key, stamp, fileSystem);
            }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                if (exception is FileChangedDuringHashException) { stable = false; changedPaths.Add(package.Key); }
                foreach (var item in package)
                {
                    item.RawSha256 = null;
                    item.Skip ??= "raw-payload-" + exception.GetType().Name;
                }
            }
        }

        // 全部跨包读取结束后统一核对，避免把先读包的过时证据计成可复用收益。
        foreach (var inventory in scan.ResourceInventories)
        {
            token.ThrowIfCancellationRequested();
            try { EnsureStamp(inventory.Path, inventory.ScannedStamp, fileSystem); }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                stable = false;
                changedPaths.Add(inventory.Path);
            }
        }
        foreach (var path in changedPaths) fileHashes.Remove(path);
        foreach (var item in items.Where(item => changedPaths.Contains(item.Path)))
        {
            item.Skip = "raw-final-stability-failed";
            item.RawSha256 = null;
        }

        var rawSets = items.Where(item => item.NeedsDecompression && item.RawSha256 is not null)
            .GroupBy(item => (item.Target.Key, item.Descriptor!.Value.Compression, item.Descriptor.Value.StoredSize,
                item.Descriptor.Value.DeclaredSize, item.RawSha256)).ToArray();
        var successfulSets = rawSets.Select(group => group.Where(item => item.Skip is null).ToArray())
            .Where(group => group.Length >= 2).ToArray();
        var duplicateSets = fileHashes.GroupBy(pair => pair.Value).Where(group => group.Count() >= 2).ToArray();
        var redundantPaths = duplicateSets.SelectMany(group => group.Select(pair => pair.Key)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.Ordinal).Skip(1))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        long duplicateSavings = 0;
        long duplicateOutput = 0;
        foreach (var group in successfulSets)
        {
            var saved = group.Where(item => fileHashes.ContainsKey(item.Path)).GroupBy(item => fileHashes[item.Path])
                .Sum(copies => (long)copies.Count() - 1);
            duplicateSavings += saved;
            duplicateOutput += saved * group[0].Descriptor!.Value.DeclaredSize;
        }
        var compressed = items.Where(item => item.NeedsDecompression).ToArray();
        var report = new RawReuseReport
        {
            Scope = Scope, StableInput = stable,
            DiscoveredPackages = scan.DiscoveredPackageCount, IndexedPackages = scan.AnalyzedPackageCount,
            CandidateResources = items.Count,
            NoneResources = items.LongCount(item => item.Descriptor?.Compression == 0),
            NeedsDecompressionResources = compressed.Length,
            StoredBytes = compressed.Sum(item => (long)item.Descriptor!.Value.StoredSize),
            DeclaredOutputBytes = compressed.Sum(item => (long)item.Descriptor!.Value.DeclaredSize),
            ValidatedDecompressionResources = compressed.LongCount(item => item.Skip is null),
            MetadataGroups = compressed.GroupBy(item => (item.Target.Key, item.Descriptor!.Value.Compression,
                item.Descriptor.Value.StoredSize, item.Descriptor.Value.DeclaredSize)).LongCount(),
            DigestPotentialSavings = rawSets.Sum(group => (long)group.Count() - 1),
            EquivalentSets = successfulSets.Length,
            EquivalentResources = successfulSets.Sum(group => (long)group.Length),
            SavedDecompressions = successfulSets.Sum(group => (long)group.Length - 1),
            SavedOutputBytes = successfulSets.Sum(group => (group.LongLength - 1) * group[0].Descriptor!.Value.DeclaredSize),
            FingerprintPreReadBytes = preRead,
            ConfirmationReadBytes = rawSets.Sum(group => 2L * (group.Count() - 1) * group.Key.StoredSize),
            AvoidedOriginalReadBytes = successfulSets.Sum(group => (group.LongLength - 1) * group[0].Descriptor!.Value.StoredSize),
            SingletonMetadataResources = singleton,
            WholeFileCopies = new(duplicateSets.Length, redundantPaths.Count,
                items.LongCount(item => redundantPaths.Contains(item.Path)),
                compressed.LongCount(item => redundantPaths.Contains(item.Path)), duplicateSavings, duplicateOutput),
            SetSizeDistribution = successfulSets.GroupBy(group => group.Length).OrderBy(group => group.Key)
                .Select(group => new SetSizeCount(group.Key, group.Count(), (long)group.Key * group.Count())).ToArray(),
            TopTypes = successfulSets.GroupBy(group => group[0].Target.Key.Type)
                .Select(group => new TypeSavings($"0x{group.Key:X8}", group.Count(), group.Sum(set => set.LongLength - 1),
                    group.Sum(set => (set.LongLength - 1) * set[0].Descriptor!.Value.DeclaredSize)))
                .OrderByDescending(type => type.SavedOutputBytes).ThenBy(type => type.Type, StringComparer.Ordinal).Take(20).ToArray(),
            ScanIssueCounts = Counts(scan.Issues.Select(issue => $"{issue.Stage}/{issue.Code}")),
            ResourceSkipCounts = Counts(items.Where(item => item.Skip is not null).Select(item => SkipCategory(item))),
            WholeFileHashSkipCounts = hashSkips,
        };
        watch.Stop();
        report.ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds;
        return report;
    }

    private static string SkipCategory(Item item) => item.Skip == ResourceContentIssueCode.UnsupportedCompression
        ? item.Descriptor?.Compression is 0xFFFF or 0xFFFE ? "RefPack" : "UnknownCompression"
        : item.Skip!;

    private static bool IsReadFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or NotSupportedException or InvalidDataException;

    private static void EnsureStamp(string path, FileStamp expected, IFileSystemAccess fileSystem)
    {
        if (fileSystem.GetFileStamp(path) != expected) throw new FileChangedDuringHashException(path);
    }

    private static void Increment(IDictionary<string, long> counts, string key) => counts[key] = counts.TryGetValue(key, out var value) ? value + 1 : 1;

    private static SortedDictionary<string, long> Counts(IEnumerable<string> keys)
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var key in keys) Increment(counts, key);
        return counts;
    }

    private static string Percent(double? ratio) => ratio?.ToString("P2", CultureInfo.InvariantCulture) ?? "不适用（分母为 0）";

    internal static string Markdown(RawReuseReport stats)
    {
        var text = new StringBuilder("# 原始字节复用只读统计\n\n## 统计口径\n\n");
        foreach (var line in stats.Scope) text.AppendLine($"- {line}");
        text.AppendLine($"\n## 结果\n\n输入稳定：{stats.StableInput}；统计耗时：{stats.ElapsedMilliseconds:F3} ms（不含报告写入）。\n");
        text.AppendLine("| 指标 | 数值 |\n| --- | ---: |");
        void Row(string name, object value) => text.AppendLine($"| {name} | {value} |");
        Row("发现 / 索引成功 package", $"{stats.DiscoveredPackages} / {stats.IndexedPackages}");
        Row("候选资源总数", stats.CandidateResources);
        Row("None 资源（不计需解压）", stats.NoneResources);
        Row("需解压条数 / 验证成功条数", $"{stats.NeedsDecompressionResources} / {stats.ValidatedDecompressionResources}");
        Row("需解压存储字节", stats.StoredBytes);
        Row("需解压声明输出字节", stats.DeclaredOutputBytes);
        Row("等价集合数 / 涉及条数", $"{stats.EquivalentSets} / {stats.EquivalentResources}");
        Row("可省解压次数 / 比例", $"{stats.SavedDecompressions} / {Percent(stats.SavedDecompressionRatio)}");
        Row("可省输出字节 / 比例", $"{stats.SavedOutputBytes} / {Percent(stats.SavedOutputRatio)}");
        Row("单成员元数据桶资源（无预读）", stats.SingletonMetadataResources);
        Row("估算：指纹预读字节", stats.FingerprintPreReadBytes);
        Row("估算：逐字节确认读取字节", stats.ConfirmationReadBytes);
        Row("元数据桶 / 未排除坏内容的摘要潜在节省", $"{stats.MetadataGroups} / {stats.DigestPotentialSavings}");
        Row("坏内容排除的潜在节省", stats.InvalidContentSavingsExcluded);
        Row("估算：额外读取毛额", stats.GrossAdditionalReadBytes);
        Row("估算：可省原路径读取字节", stats.AvoidedOriginalReadBytes);
        Row("估算：净额外读取字节", stats.NetAdditionalReadBytes);
        text.AppendLine("\n## 整文件完全相同的副本\n\n当前生产内容阶段仍逐份读取，以下贡献已包含在上表，不能再次相加。若以后先合并整文件副本，应从阶段 3 机会中扣除此处贡献。\n");
        var copies = stats.WholeFileCopies;
        text.AppendLine("| 指标 | 数值 |\n| --- | ---: |");
        Row("重复集合 / 多余副本 package", $"{copies.Sets} / {copies.RedundantPackages}");
        Row("多余副本内候选资源 / 需解压资源", $"{copies.CandidateResourcesInRedundantCopies} / {copies.NeedsDecompressionResourcesInRedundantCopies}");
        Row("其中可省解压次数 / 输出字节", $"{copies.SavedDecompressions} / {copies.SavedOutputBytes}");
        Row("扣除整文件副本贡献后可省解压次数 / 输出字节", $"{stats.SavedDecompressions - copies.SavedDecompressions} / {stats.SavedOutputBytes - copies.SavedOutputBytes}");
        text.AppendLine("\n## 等价集合大小分布\n\n| 大小 | 集合数 | 涉及条数 |\n| ---: | ---: | ---: |");
        foreach (var size in stats.SetSizeDistribution) text.AppendLine($"| {size.Size} | {size.Sets} | {size.Resources} |");
        text.AppendLine("\n## 可省输出字节最多的前 20 个 Type\n\n| Type | 等价集合数 | 可省解压次数 | 可省输出字节 |\n| --- | ---: | ---: | ---: |");
        foreach (var type in stats.TopTypes) text.AppendLine($"| {type.Type} | {type.EquivalentSets} | {type.SavedDecompressions} | {type.SavedOutputBytes} |");
        text.AppendLine($"\n## 跳过与问题\n\n跳过候选资源：{stats.SkippedResources}。扫描问题按事件计数，资源跳过按条数计数，整文件哈希失败按文件计数，三者不能相加。\n");
        void CountsTable(string title, IReadOnlyDictionary<string, long> counts)
        {
            text.AppendLine($"### {title}\n\n| 分类 | 数量 |\n| --- | ---: |");
            if (counts.Count == 0) text.AppendLine("| 无 | 0 |");
            foreach (var pair in counts) Row(pair.Key, pair.Value);
            text.AppendLine();
        }
        CountsTable("来源、枚举、索引问题", stats.ScanIssueCounts);
        CountsTable("资源跳过", stats.ResourceSkipCounts);
        CountsTable("整文件哈希失败（仅影响副本归因）", stats.WholeFileHashSkipCounts);
        return text.ToString();
    }
}
