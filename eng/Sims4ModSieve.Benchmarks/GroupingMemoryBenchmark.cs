using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Scripts;

namespace Sims4ModSieve.Benchmarks;

// 每个样本单独启动进程；只构造报告，不读写 Mods，不做报告序列化或 WPF 测量。
internal static class GroupingMemoryBenchmark
{
    internal static int Run(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[0], out var originals) || originals < 2
            || !int.TryParse(args[1], out var shared) || shared is < 1 or > 63)
            throw new ArgumentException("grouping-memory requires <originals >= 2> <shared-resources: 1..63>.");
        var stamp = new FileStamp(100, DateTime.UnixEpoch);
        var paths = Enumerable.Range(0, originals).SelectMany(index => Enumerable.Range(0, 2)
            .Select(copy => $"{index:D5}-{copy}.package")).ToArray();
        var resources = new List<ResourceContentComparison>();
        void AddResource(ulong instance, IEnumerable<string> participants, int ordinal)
        {
            var key = new ResourceKey(1, 0, instance);
            var occurrences = participants.Select(path => new ResourceContentOccurrence(key, path, ordinal, stamp,
                "same", 1, null)).ToArray();
            resources.Add(new(key, [new("same", 1, occurrences)], []));
        }
        for (var ordinal = 0; ordinal < shared; ordinal++) AddResource((ulong)ordinal, paths, ordinal);
        for (var index = 0; index < originals; index++)
            for (var ordinal = shared; ordinal < 64; ordinal++)
                AddResource((ulong)(64 + index * 64 + ordinal), paths.Skip(index * 2).Take(2), ordinal);
        var scan = new PackageConflictScanReport(DateTime.UnixEpoch, DateTime.UnixEpoch, [], paths.Length, paths.Length, [], [], [])
        {
            ResourceInventories = paths.Select(path => new PackageResourceInventory(path, stamp, 64)
                { ResourceTypeCounts = new Dictionary<uint, int> { [1] = 64 } }).ToArray(),
        };
        var contents = new ResourceContentComparisonReport(DateTime.UnixEpoch, DateTime.UnixEpoch,
            ResourceContentLimits.Default, resources.ToArray(), paths.Select(path =>
                new ResourceContentPackageVersion(path, stamp, stamp, true)).ToArray(), 0);
        var groups = Enumerable.Range(0, originals).Select(index =>
        {
            var files = paths.Skip(index * 2).Take(2).Select(path =>
                new DuplicateFile(path, stamp.Length, stamp.LastWriteTimeUtc, [], [], true, 1)).ToArray();
            return new DuplicateGroup($"whole-file-{index}", stamp.Length, DuplicateSection.Ordinary, files,
                files[0].Path, [files[1].Path]);
        }).ToArray();
        var duplicates = new DuplicateReport(DateTime.UnixEpoch, DateTime.UnixEpoch, [], paths.Length, paths.Length, groups, []);
        var scripts = new ScriptModuleScanReport(DateTime.UnixEpoch, DateTime.UnixEpoch, [], ScriptModuleLimits.Default,
            0, 0, [], 0, [], [], 0);
        using var process = Process.GetCurrentProcess();
        var baseline = GC.GetTotalMemory(true);
        var baselineWorkingSet = process.WorkingSet64;
        long sampledPeak = baseline;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                sampledPeak = Math.Max(sampledPeak, GC.GetTotalMemory(false));
                await Task.Delay(5);
            }
        });
        // 显式放行，实验容量不受被校准的门卫影响。
        var builder = new PackageConflictGroupingBuilder(long.MaxValue);
        ConflictGroupingSize? size = null;
        var started = Stopwatch.StartNew();
        var input = builder.Prepare(scan, contents, duplicates: duplicates, scripts: scripts, sizeObserved: value => size = value);
        var prepareEnd = GC.GetTotalMemory(false);
        var result = builder.Build(input, new());
        var buildEnd = GC.GetTotalMemory(false);
        var merged = ConflictResultGrouping.MergeDuplicatePackageGroups(input, result, duplicates,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), scan, scripts);
        var mergeEnd = GC.GetTotalMemory(false);
        var elapsed = started.ElapsedMilliseconds;
        sampling.Cancel();
        sampler.GetAwaiter().GetResult();
        sampledPeak = new[] { sampledPeak, prepareEnd, buildEnd, mergeEnd }.Max();
        var liveEnd = GC.GetTotalMemory(true);
        process.Refresh();
        var peakWorkingSet = process.PeakWorkingSet64;
        var matches = input.Pairs.Sum(pair => (long)pair.Resources.Count);
        if (size!.ResourceMatchCount != matches) throw new InvalidOperationException("Estimated/materialized match counts differ.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), serverGC = System.Runtime.GCSettings.IsServerGC,
            availableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            originals, copies = 2, shared, resourcesPerFile = 64, matches, collapsedMatches = size.CollapsedResourceMatchCount,
            pairs = input.Pairs.Count, mergedGroups = merged.Result.Groups.Count, baseline, baselineWorkingSet,
            prepareEnd, buildEnd, mergeEnd, liveEnd, sampledPeak, peakWorkingSet, elapsed,
            liveBytesPerMatch = (liveEnd - baseline) / (double)matches,
            sampledPeakBytesPerMatch = (sampledPeak - baseline) / (double)matches,
            processPeakBytesPerMatch = peakWorkingSet / (double)matches,
        }));
        GC.KeepAlive(input);
        GC.KeepAlive(result);
        GC.KeepAlive(merged);
        GC.KeepAlive(scan);
        GC.KeepAlive(contents);
        GC.KeepAlive(duplicates);
        return 0;
    }
}
