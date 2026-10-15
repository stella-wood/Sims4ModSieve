using System.Collections.ObjectModel;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;

namespace Sims4ModSieve.Core.Conflicts;

/// <summary>Prepare 和 Build 均只处理报告中的内存数据，不持有读取器。</summary>
public sealed class PackageConflictGroupingBuilder
{
    public PackageConflictGroupingInput Prepare(PackageConflictScanReport scanReport,
        ResourceContentComparisonReport contentReport, bool excludeNameMap = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scanReport.ResourceInventories.Count != scanReport.AnalyzedPackageCount)
            throw new ArgumentException("分组输入缺数据：成功索引的完整资源清单不齐。", nameof(scanReport));
        var excludedTypes = new HashSet<uint> { 0x7FB6AD8A, 0x6BF15BBE, 0x01357924 };
        if (excludeNameMap) excludedTypes.Add(0x0166038C);
        var exits = new Dictionary<string, Exclusion>(PathRules.Comparer);
        Exclusion Exit(string path)
        {
            if (!exits.TryGetValue(path, out var exit)) exits.Add(path, exit = new Exclusion());
            return exit;
        }
        var inventories = new Dictionary<string, PackageResourceInventory>(PathRules.Comparer);
        foreach (var inventory in scanReport.ResourceInventories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inventories.Add(inventory.Path, inventory);
            if (inventory.ResourceTypeCounts is null
                || inventory.ResourceTypeCounts.Values.Any(count => count < 0)
                || inventory.ResourceTypeCounts.Values.Sum(count => (long)count) != inventory.ResourceCount)
                Exit(inventory.Path).Input.Add(new("missing-resource-type-counts", "缺少完整索引类型数量。"));
        }
        foreach (var path in scanReport.IncompletePackagePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exit(path);
        }
        foreach (var issue in scanReport.Issues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (exits.ContainsKey(issue.Path)) Exit(issue.Path).Scan.Add(issue);
        }
        var versions = new HashSet<string>(PathRules.Comparer);
        foreach (var version in contentReport.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            versions.Add(version.Path);
            if (!inventories.TryGetValue(version.Path, out var inventory))
                Exit(version.Path).Input.Add(new("missing-resource-inventory", "缺少完整索引清单。"));
            else if (!version.IsStable || version.ScannedStamp != inventory.ScannedStamp
                || version.StampAfterRead != inventory.ScannedStamp)
                Exit(version.Path).Versions.Add(version);
        }
        var observed = new HashSet<(string Path, int Ordinal, ResourceKey Key)>();
        foreach (var comparison in contentReport.Comparisons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excludedTypes.Contains(comparison.Key.Type)) continue;
            foreach (var occurrence in comparison.ContentGroups.SelectMany(group => group.Occurrences).Concat(comparison.Uncompared))
            {
                cancellationToken.ThrowIfCancellationRequested();
                observed.Add((occurrence.PackagePath.ToUpperInvariant(), occurrence.Ordinal, comparison.Key));
                if (!occurrence.IsSuccess || occurrence.Issue is not null)
                    Exit(occurrence.PackagePath).Resources.Add(occurrence);
                if (!inventories.TryGetValue(occurrence.PackagePath, out var inventory))
                    Exit(occurrence.PackagePath).Input.Add(new("missing-resource-inventory", "缺少完整索引清单。", comparison.Key));
                else if (occurrence.PackageStamp != inventory.ScannedStamp)
                    Exit(occurrence.PackagePath).Input.Add(new("occurrence-version-mismatch", "资源版本与索引版本不一致。", comparison.Key));
                if (!versions.Contains(occurrence.PackagePath))
                    Exit(occurrence.PackagePath).Input.Add(new("missing-package-version", "缺少内容读取版本证据。", comparison.Key));
            }
        }
        foreach (var candidate in scanReport.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excludedTypes.Contains(candidate.Key.Type)) continue;
            foreach (var occurrence in candidate.Occurrences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!observed.Contains((occurrence.PackagePath.ToUpperInvariant(), occurrence.Ordinal, candidate.Key)))
                    Exit(occurrence.PackagePath).Input.Add(new("missing-resource-comparison", "缺少撞号资源内容比较。", candidate.Key));
            }
        }
        var pairs = new ConflictPairSummaryBuilder().BuildGroupingPackagePairs(contentReport, excludedTypes,
            new HashSet<string>(exits.Keys, PathRules.Comparer), cancellationToken);
        var files = new List<PackageConflictGroupingFile>();
        foreach (var inventory in inventories.Values.OrderBy(item => item.Path, PathRules.Comparer).ThenBy(item => item.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (exits.ContainsKey(inventory.Path)) continue;
            var count = inventory.ResourceCount - inventory.ResourceTypeCounts!
                .Where(entry => excludedTypes.Contains(entry.Key)).Sum(entry => entry.Value);
            files.Add(new(inventory.Path, inventory.ScannedStamp, count));
        }
        var excludedFiles = new List<PackageConflictGroupingExcludedFile>();
        foreach (var (path, exit) in exits.OrderBy(entry => entry.Key, PathRules.Comparer).ThenBy(entry => entry.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            excludedFiles.Add(new(path,
                ReadOnlyLists.Freeze(exit.Scan.OrderBy(issue => issue.Code, StringComparer.Ordinal).ThenBy(issue => issue.Detail, StringComparer.Ordinal)),
                ReadOnlyLists.Freeze(exit.Resources.Distinct().OrderBy(item => item.Key.Type).ThenBy(item => item.Key.Group)
                    .ThenBy(item => item.Key.Instance).ThenBy(item => item.Ordinal)),
                ReadOnlyLists.Freeze(exit.Versions.Distinct().OrderBy(version => version.ScannedStamp.Length)
                    .ThenBy(version => version.ScannedStamp.LastWriteTimeUtc).ThenBy(version => version.StampAfterRead?.Length)
                    .ThenBy(version => version.StampAfterRead?.LastWriteTimeUtc).ThenBy(version => version.IsStable)),
                ReadOnlyLists.Freeze(exit.Input.Distinct().OrderBy(issue => issue.Code, StringComparer.Ordinal)
                    .ThenBy(issue => issue.Key?.Tgi, StringComparer.Ordinal))));
        }
        var comparisons = contentReport.Comparisons.Where(item => !excludedTypes.Contains(item.Key.Type))
            .OrderBy(item => item.Key.Type).ThenBy(item => item.Key.Group).ThenBy(item => item.Key.Instance);
        var input = new PackageConflictGroupingInput(excludeNameMap, ReadOnlyLists.Freeze(files), pairs,
            ReadOnlyLists.Freeze(comparisons), ReadOnlyLists.Freeze(excludedFiles));
        cancellationToken.ThrowIfCancellationRequested();
        return input;
    }

    public PackageConflictGroupingResult Build(PackageConflictGroupingInput input, PackageConflictGroupingOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinimumContainedResourceCount, 1);
        if (!double.IsFinite(options.SameModThreshold) || options.SameModThreshold < 0.1 || options.SameModThreshold > 1)
            throw new ArgumentOutOfRangeException(nameof(options.SameModThreshold));
        var files = input.Files.ToDictionary(file => file.Path, PathRules.Comparer);
        var relations = ClassifyPairs(input.Pairs, files, options, cancellationToken);
        var groups = new List<PackageConflictGroup>();
        BuildBundleGroups();
        BuildSameModGroups();
        foreach (var card in OverrideConflictCardRules.Build(relations, options.OverrideCardRule, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddGroup(PackageConflictRelationKind.OverrideConflict, card);
        }
        var frozenGroups = ReadOnlyLists.Freeze(groups);
        var groupsByFile = new Dictionary<string, List<PackageConflictGroup>>(PathRules.Comparer);
        foreach (var group in frozenGroups)
        {
            foreach (var member in group.Members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!groupsByFile.TryGetValue(member.Path, out var references)) groupsByFile.Add(member.Path, references = []);
                references.Add(group);
            }
        }
        var frozenIndex = new Dictionary<string, IReadOnlyList<PackageConflictGroup>>(PathRules.Comparer);
        foreach (var (path, references) in groupsByFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            frozenIndex.Add(path, ReadOnlyLists.Freeze(references));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(relations, frozenGroups, input.ExcludedFiles)
        {
            GroupsByFile = new ReadOnlyDictionary<string, IReadOnlyList<PackageConflictGroup>>(frozenIndex),
        };

        void AddGroup(PackageConflictRelationKind kind, IReadOnlyList<PackageConflictPairRelation> edges,
            string? bundlePath = null)
        {
            var paths = edges.SelectMany(edge => new[] { edge.Pair.FirstPath, edge.Pair.SecondPath })
                .ToHashSet(PathRules.Comparer);
            var allCounts = CalculateMemberCounts(paths, input.Pairs, cancellationToken);
            var displayCounts = kind == PackageConflictRelationKind.OverrideConflict
                ? CalculateMemberCounts(paths, edges.Select(edge => edge.Pair), cancellationToken) : allCounts;
            var members = new List<PackageConflictGroupMember>();
            foreach (var path in paths.Order(PathRules.Comparer).ThenBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[path];
                var isBundle = bundlePath is not null && PathRules.Comparer.Equals(path, bundlePath);
                bool? identical = bundlePath is null || isBundle ? null
                    : !edges.Single(edge => PathRules.Comparer.Equals(edge.ContainedPath, path)).HasDifferentContent;
                members.Add(new(path, file.ScannedStamp, file.ResourceCount, displayCounts[path].Count,
                    file.ResourceCount - allCounts[path].Count, isBundle, identical));
            }
            var keys = displayCounts.Values.SelectMany(set => set).Distinct().OrderBy(key => key.Type)
                .ThenBy(key => key.Group).ThenBy(key => key.Instance);
            // 合集独有数只扣包含边的并集，其他被包含文件之间的撞号不影响 B。
            int? unique = bundlePath is null ? null : files[bundlePath].ResourceCount
                - edges.SelectMany(edge => edge.Pair.Resources).Select(match => match.Key).Distinct().Count();
            groups.Add(new(kind, ReadOnlyLists.Freeze(members), ReadOnlyLists.Freeze(keys), edges, bundlePath, unique));
        }

        void BuildBundleGroups()
        {
            foreach (var bundle in relations.Where(edge => edge.Kind == PackageConflictRelationKind.BundleContainment)
                .GroupBy(edge => edge.BundlePath!, PathRules.Comparer)
                .OrderBy(group => group.Key, PathRules.Comparer).ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddGroup(PackageConflictRelationKind.BundleContainment, ReadOnlyLists.Freeze(bundle), bundle.Key);
            }
        }

        void BuildSameModGroups()
        {
            var candidates = relations.Where(edge => edge.Kind == PackageConflictRelationKind.SuspectedSameMod).ToArray();
            var neighbors = new Dictionary<string, HashSet<string>>(PathRules.Comparer);
            foreach (var edge in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddNeighbor(edge.Pair.FirstPath, edge.Pair.SecondPath);
                AddNeighbor(edge.Pair.SecondPath, edge.Pair.FirstPath);
            }
            void AddNeighbor(string first, string second)
            {
                if (!neighbors.TryGetValue(first, out var set)) neighbors.Add(first, set = new(PathRules.Comparer));
                set.Add(second);
            }
            var assignments = new Dictionary<string, HashSet<string>>(PathRules.Comparer);
            var sameGroups = new List<HashSet<string>>();
            foreach (var edge in candidates.OrderByDescending(edge => edge.Overlap).ThenByDescending(edge => edge.SharedResourceCount)
                .ThenBy(edge => edge.Pair.FirstPath, PathRules.Comparer).ThenBy(edge => edge.Pair.FirstPath, StringComparer.Ordinal)
                .ThenBy(edge => edge.Pair.SecondPath, PathRules.Comparer).ThenBy(edge => edge.Pair.SecondPath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                assignments.TryGetValue(edge.Pair.FirstPath, out var first);
                assignments.TryGetValue(edge.Pair.SecondPath, out var second);
                if (first is not null && second is not null) continue;
                if (first is null && second is null)
                {
                    var set = new HashSet<string>(PathRules.Comparer) { edge.Pair.FirstPath, edge.Pair.SecondPath };
                    sameGroups.Add(set);
                    assignments.Add(edge.Pair.FirstPath, set);
                    assignments.Add(edge.Pair.SecondPath, set);
                }
                else
                {
                    var set = first ?? second!;
                    var path = first is null ? edge.Pair.FirstPath : edge.Pair.SecondPath;
                    if (set.All(member => neighbors[path].Contains(member)))
                    {
                        set.Add(path);
                        assignments.Add(path, set);
                    }
                }
            }
            foreach (var set in sameGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddGroup(PackageConflictRelationKind.SuspectedSameMod,
                    ReadOnlyLists.Freeze(candidates.Where(edge => set.Contains(edge.Pair.FirstPath) && set.Contains(edge.Pair.SecondPath))));
            }
        }
    }

    private static IReadOnlyList<PackageConflictPairRelation> ClassifyPairs(IReadOnlyList<PackageConflictPairSummary> pairs,
        Dictionary<string, PackageConflictGroupingFile> files, PackageConflictGroupingOptions options, CancellationToken cancellationToken)
    {
        var relations = new List<PackageConflictPairRelation>();
        foreach (var pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = files[pair.FirstPath].ResourceCount;
            var second = files[pair.SecondPath].ResourceCount;
            var shared = pair.Resources.Count;
            var overlap = shared / (double)Math.Max(first, second);
            string? contained = null;
            string? bundle = null;
            if (shared == first && first < second && first >= options.MinimumContainedResourceCount)
                (contained, bundle) = (pair.FirstPath, pair.SecondPath);
            else if (shared == second && second < first && second >= options.MinimumContainedResourceCount)
                (contained, bundle) = (pair.SecondPath, pair.FirstPath);
            var kind = contained is not null ? PackageConflictRelationKind.BundleContainment
                : overlap >= options.SameModThreshold ? PackageConflictRelationKind.SuspectedSameMod
                : pair.HasConfirmedDifference ? PackageConflictRelationKind.OverrideConflict
                : PackageConflictRelationKind.Hidden;
            relations.Add(new(pair, kind, overlap, contained, bundle));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ReadOnlyLists.Freeze(relations);
    }

    private static Dictionary<string, HashSet<ResourceKey>> CalculateMemberCounts(HashSet<string> paths,
        IEnumerable<PackageConflictPairSummary> pairs, CancellationToken cancellationToken)
    {
        var counts = paths.ToDictionary(path => path, _ => new HashSet<ResourceKey>(), PathRules.Comparer);
        foreach (var pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!paths.Contains(pair.FirstPath) || !paths.Contains(pair.SecondPath)) continue;
            foreach (var resource in pair.Resources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                counts[pair.FirstPath].Add(resource.Key);
                counts[pair.SecondPath].Add(resource.Key);
            }
        }
        return counts;
    }

    private sealed class Exclusion
    {
        public List<PackageConflictScanIssue> Scan { get; } = [];
        public List<ResourceContentOccurrence> Resources { get; } = [];
        public List<ResourceContentPackageVersion> Versions { get; } = [];
        public List<PackageConflictGroupingInputIssue> Input { get; } = [];
    }
}
