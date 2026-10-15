using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Scripts;

namespace Sims4ModSieve.Core.Conflicts;

public enum ConflictPairPriority
{
    NeedsReview,
    CanOrganize,
    Incomplete,
}

public sealed record PackagePairResourceMatch(
    ResourceContentComparison Comparison,
    bool HasConfirmedDifference,
    bool HasConfirmedIdentical,
    bool HasIncomplete)
{
    public ResourceKey Key => Comparison.Key;
    public ConflictPairPriority Priority => ConflictPairSummaryBuilder.PriorityFor(
        HasConfirmedDifference, HasConfirmedIdentical);
}

public sealed record PackageConflictPairSummary(
    string FirstPath,
    string SecondPath,
    int DifferentResourceCount,
    int IdenticalResourceCount,
    int IncompleteResourceCount,
    bool FirstContainedInSecond,
    bool SecondContainedInFirst,
    IReadOnlyList<PackagePairResourceMatch> Resources)
{
    public bool HasConfirmedDifference => DifferentResourceCount > 0;
    public bool HasConfirmedIdentical => IdenticalResourceCount > 0;
    public bool HasIncomplete => IncompleteResourceCount > 0;
    public bool IsSuspectedModVersionPair => HasConfirmedIdentical && HasConfirmedDifference;
    public ConflictPairPriority Priority => ConflictPairSummaryBuilder.PriorityFor(
        HasConfirmedDifference, HasConfirmedIdentical);
}

public sealed record ScriptPairModuleMatch(
    ScriptModuleCollision Collision,
    bool HasConfirmedDifference,
    bool HasConfirmedIdentical,
    bool HasIncomplete)
{
    public string ModuleName => Collision.ModuleName;
    public ConflictPairPriority Priority => ConflictPairSummaryBuilder.PriorityFor(
        HasConfirmedDifference, HasConfirmedIdentical);
}

public sealed record ScriptConflictPairSummary(
    string FirstPath,
    string SecondPath,
    int DifferentModuleCount,
    int IdenticalModuleCount,
    int IncompleteModuleCount,
    IReadOnlyList<ScriptPairModuleMatch> Modules)
{
    public bool HasConfirmedDifference => DifferentModuleCount > 0;
    public bool HasConfirmedIdentical => IdenticalModuleCount > 0;
    public bool HasIncomplete => IncompleteModuleCount > 0;
    public bool IsSuspectedModVersionPair => HasConfirmedIdentical && HasConfirmedDifference;
    public ConflictPairPriority Priority => ConflictPairSummaryBuilder.PriorityFor(
        HasConfirmedDifference, HasConfirmedIdentical);
}

/// <summary>只整理已有报告，不读取文件；明细共享原始资源比较和模块碰撞。</summary>
public sealed class ConflictPairSummaryBuilder
{
    /// <summary>v2 分组专用：先排除类型和退出文件，保留包括纯一致局部重叠的所有配对。</summary>
    public IReadOnlyList<PackageConflictPairSummary> BuildGroupingPackagePairs(
        ResourceContentComparisonReport comparisonReport,
        IReadOnlySet<uint> excludedTypes,
        IReadOnlySet<string> excludedPaths,
        CancellationToken cancellationToken = default)
    {
        var excluded = new HashSet<string>(Walk(excludedPaths, cancellationToken), PathRules.Comparer);
        var comparisons = Walk(comparisonReport.Comparisons, cancellationToken)
            .Where(comparison => !excludedTypes.Contains(comparison.Key.Type)).ToArray();
        var paths = CanonicalPaths(comparisons.SelectMany(comparison => PackageOccurrences(comparison, cancellationToken))
            .Where(occurrence => !excluded.Contains(occurrence.PackagePath))
            .Select(occurrence => occurrence.PackagePath), cancellationToken);
        var pairs = new Dictionary<(string First, string Second), PackagePairData>();
        foreach (var comparison in Walk(comparisons, cancellationToken))
        {
            var participants = new Dictionary<string, Participant>(PathRules.Comparer);
            for (var group = 0; group < comparison.ContentGroups.Count; group++)
            {
                foreach (var occurrence in Walk(comparison.ContentGroups[group].Occurrences, cancellationToken))
                {
                    if (!excluded.Contains(occurrence.PackagePath))
                        GetParticipant(participants, paths[occurrence.PackagePath]).Groups.Add(group);
                }
            }
            foreach (var occurrence in Walk(comparison.Uncompared, cancellationToken))
            {
                if (!excluded.Contains(occurrence.PackagePath))
                    GetParticipant(participants, paths[occurrence.PackagePath]).HasIncomplete = true;
            }
            var members = Order(participants.Values, (left, right) => ComparePaths(left.Path, right.Path), cancellationToken);
            for (var first = 0; first < members.Count; first++)
            {
                for (var second = first + 1; second < members.Count; second++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = (members[first].Path, members[second].Path);
                    if (!pairs.TryGetValue(key, out var pair)) pairs.Add(key, pair = new PackagePairData());
                    var facts = FactsFor(members[first], members[second]);
                    var match = new PackagePairResourceMatch(comparison, facts.Different, facts.Identical, facts.Incomplete);
                    if (pair.Resources.TryGetValue(comparison.Key, out var previous))
                    {
                        match = previous with
                        {
                            HasConfirmedDifference = previous.HasConfirmedDifference || match.HasConfirmedDifference,
                            HasConfirmedIdentical = previous.HasConfirmedIdentical || match.HasConfirmedIdentical,
                            HasIncomplete = previous.HasIncomplete || match.HasIncomplete,
                        };
                    }
                    pair.Resources[comparison.Key] = match;
                }
            }
        }
        var summaries = new List<PackageConflictPairSummary>();
        foreach (var (pathsPair, pair) in Walk(pairs, cancellationToken))
        {
            var resources = Order(pair.Resources.Values, (left, right) =>
            {
                var result = left.Key.Type.CompareTo(right.Key.Type);
                if (result == 0) result = left.Key.Group.CompareTo(right.Key.Group);
                return result == 0 ? left.Key.Instance.CompareTo(right.Key.Instance) : result;
            }, cancellationToken);
            summaries.Add(new PackageConflictPairSummary(pathsPair.First, pathsPair.Second,
                resources.Count(resource => resource.HasConfirmedDifference),
                resources.Count(resource => resource.HasConfirmedIdentical),
                resources.Count(resource => resource.HasIncomplete), false, false, resources));
        }
        return Order(summaries, (left, right) =>
        {
            var result = ComparePaths(left.FirstPath, right.FirstPath);
            return result == 0 ? ComparePaths(left.SecondPath, right.SecondPath) : result;
        }, cancellationToken);
    }

    public IReadOnlyList<PackageConflictPairSummary> BuildPackagePairs(
        PackageConflictScanReport scanReport,
        ResourceContentComparisonReport comparisonReport,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inventories = new Dictionary<string, PackageResourceInventory>(PathRules.Comparer);
        foreach (var inventory in Walk(scanReport.ResourceInventories, cancellationToken))
        {
            inventories.Add(inventory.Path, inventory);
        }

        var stableStamps = new Dictionary<string, FileStamp>(PathRules.Comparer);
        var invalidVersions = new HashSet<string>(PathRules.Comparer);
        foreach (var version in Walk(comparisonReport.Packages, cancellationToken))
        {
            if (inventories.TryGetValue(version.Path, out var inventory)
                && version.IsStable && version.ScannedStamp == inventory.ScannedStamp
                && version.StampAfterRead == inventory.ScannedStamp)
            {
                stableStamps[version.Path] = inventory.ScannedStamp;
            }
            else
            {
                invalidVersions.Add(version.Path);
            }
        }
        foreach (var path in Walk(invalidVersions, cancellationToken))
        {
            stableStamps.Remove(path);
        }

        var paths = CanonicalPaths(Walk(comparisonReport.Comparisons, cancellationToken)
            .SelectMany(comparison => PackageOccurrences(comparison, cancellationToken))
            .Select(occurrence => occurrence.PackagePath), cancellationToken);
        var comparisons = new List<PackageComparisonData>();
        var candidates = new HashSet<(string First, string Second)>();
        foreach (var comparison in Walk(comparisonReport.Comparisons, cancellationToken))
        {
            var participants = new Dictionary<string, Participant>(PathRules.Comparer);
            for (var group = 0; group < comparison.ContentGroups.Count; group++)
            {
                foreach (var occurrence in Walk(comparison.ContentGroups[group].Occurrences, cancellationToken))
                {
                    var participant = GetParticipant(participants, paths[occurrence.PackagePath]);
                    participant.Groups.Add(group);
                    participant.HasStableStamp &= stableStamps.TryGetValue(occurrence.PackagePath, out var stamp)
                        && stamp == occurrence.PackageStamp;
                }
            }
            foreach (var occurrence in Walk(comparison.Uncompared, cancellationToken))
            {
                GetParticipant(participants, paths[occurrence.PackagePath]).HasIncomplete = true;
            }

            comparisons.Add(new PackageComparisonData(comparison, participants));
            foreach (var (first, second) in ReviewPairs(participants, cancellationToken))
            {
                candidates.Add(PairKey(first.Path, second.Path));
            }
        }

        AddContainmentCandidates(comparisons, inventories, candidates, cancellationToken);
        var neighbors = CandidateNeighbors(candidates, cancellationToken);
        var pairs = new Dictionary<(string First, string Second), PackagePairData>();
        foreach (var data in Walk(comparisons, cancellationToken))
        {
            var comparison = data.Comparison;
            foreach (var (first, second) in CandidatePairs(data.Participants, neighbors, cancellationToken))
            {
                var key = (first.Path, second.Path);
                if (!pairs.TryGetValue(key, out var pair))
                {
                    pair = new PackagePairData();
                    pairs.Add(key, pair);
                }
                var facts = FactsFor(first, second);
                var match = new PackagePairResourceMatch(comparison, facts.Different, facts.Identical, facts.Incomplete);
                if (pair.Resources.TryGetValue(comparison.Key, out var previous))
                {
                    match = previous with
                    {
                        HasConfirmedDifference = previous.HasConfirmedDifference || match.HasConfirmedDifference,
                        HasConfirmedIdentical = previous.HasConfirmedIdentical || match.HasConfirmedIdentical,
                        HasIncomplete = previous.HasIncomplete || match.HasIncomplete,
                    };
                }
                pair.Resources[comparison.Key] = match;
                pair.HasStableStamps &= first.HasStableStamp && second.HasStableStamp;
            }
        }

        var summaries = new List<PackageConflictPairSummary>();
        foreach (var (pathsPair, pair) in Walk(pairs, cancellationToken))
        {
            var resources = Order(pair.Resources.Values, (left, right) =>
            {
                var result = left.Priority.CompareTo(right.Priority);
                if (result == 0) result = left.Key.Type.CompareTo(right.Key.Type);
                if (result == 0) result = left.Key.Group.CompareTo(right.Key.Group);
                return result == 0 ? left.Key.Instance.CompareTo(right.Key.Instance) : result;
            }, cancellationToken);
            var different = 0;
            var identical = 0;
            var incomplete = 0;
            foreach (var resource in Walk(resources, cancellationToken))
            {
                if (resource.HasConfirmedDifference) different++;
                if (resource.HasConfirmedIdentical) identical++;
                if (resource.HasIncomplete) incomplete++;
            }
            bool IsContained(string path) => pair.HasStableStamps && different == 0 && incomplete == 0
                && stableStamps.ContainsKey(path)
                && inventories.TryGetValue(path, out var inventory)
                && inventory.ResourceCount > 0 && identical == inventory.ResourceCount;

            var firstContained = IsContained(pathsPair.First);
            var secondContained = IsContained(pathsPair.Second);
            if (different > 0 || incomplete > 0 || firstContained || secondContained)
            {
                summaries.Add(new PackageConflictPairSummary(pathsPair.First, pathsPair.Second,
                    different, identical, incomplete, firstContained, secondContained, resources));
            }
        }
        return Order(summaries, (left, right) => ComparePairs(
            left.Priority, left.FirstPath, left.SecondPath, right.Priority, right.FirstPath, right.SecondPath),
            cancellationToken);
    }

    public IReadOnlyList<ScriptConflictPairSummary> BuildScriptPairs(
        ScriptModuleScanReport report,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = CanonicalPaths(Walk(report.Collisions, cancellationToken)
            .SelectMany(collision => ScriptOccurrences(collision, cancellationToken))
            .Select(occurrence => occurrence.ArchivePath), cancellationToken);
        var collisions = new List<ScriptCollisionData>();
        var candidates = new HashSet<(string First, string Second)>();
        foreach (var collision in Walk(report.Collisions, cancellationToken))
        {
            var participants = new Dictionary<string, Participant>(PathRules.Comparer);
            for (var group = 0; group < collision.ContentGroups.Count; group++)
            {
                foreach (var occurrence in Walk(collision.ContentGroups[group].Occurrences, cancellationToken))
                {
                    GetParticipant(participants, paths[occurrence.ArchivePath]).Groups.Add(group);
                }
            }
            foreach (var occurrence in Walk(collision.Uncompared, cancellationToken))
            {
                GetParticipant(participants, paths[occurrence.ArchivePath]).HasIncomplete = true;
            }
            collisions.Add(new ScriptCollisionData(collision, participants));
            foreach (var (first, second) in ReviewPairs(participants, cancellationToken))
            {
                candidates.Add(PairKey(first.Path, second.Path));
            }
        }

        var neighbors = CandidateNeighbors(candidates, cancellationToken);
        var pairs = new Dictionary<(string First, string Second), Dictionary<string, ScriptPairModuleMatch>>();
        foreach (var data in Walk(collisions, cancellationToken))
        {
            var collision = data.Collision;
            foreach (var (first, second) in CandidatePairs(data.Participants, neighbors, cancellationToken))
            {
                var key = (first.Path, second.Path);
                if (!pairs.TryGetValue(key, out var modules))
                {
                    modules = new Dictionary<string, ScriptPairModuleMatch>(StringComparer.Ordinal);
                    pairs.Add(key, modules);
                }
                var facts = FactsFor(first, second);
                var match = new ScriptPairModuleMatch(collision, facts.Different, facts.Identical, facts.Incomplete);
                if (modules.TryGetValue(collision.ModuleName, out var previous))
                {
                    match = previous with
                    {
                        HasConfirmedDifference = previous.HasConfirmedDifference || match.HasConfirmedDifference,
                        HasConfirmedIdentical = previous.HasConfirmedIdentical || match.HasConfirmedIdentical,
                        HasIncomplete = previous.HasIncomplete || match.HasIncomplete,
                    };
                }
                modules[collision.ModuleName] = match;
            }
        }

        var summaries = new List<ScriptConflictPairSummary>();
        foreach (var (pathsPair, matches) in Walk(pairs, cancellationToken))
        {
            var modules = Order(matches.Values, (left, right) =>
            {
                var result = left.Priority.CompareTo(right.Priority);
                return result == 0 ? StringComparer.Ordinal.Compare(left.ModuleName, right.ModuleName) : result;
            }, cancellationToken);
            var different = 0;
            var identical = 0;
            var incomplete = 0;
            foreach (var module in Walk(modules, cancellationToken))
            {
                if (module.HasConfirmedDifference) different++;
                if (module.HasConfirmedIdentical) identical++;
                if (module.HasIncomplete) incomplete++;
            }
            if (different > 0 || incomplete > 0)
            {
                summaries.Add(new ScriptConflictPairSummary(pathsPair.First, pathsPair.Second,
                    different, identical, incomplete, modules));
            }
        }
        return Order(summaries, (left, right) => ComparePairs(
            left.Priority, left.FirstPath, left.SecondPath, right.Priority, right.FirstPath, right.SecondPath),
            cancellationToken);
    }

    internal static ConflictPairPriority PriorityFor(bool different, bool identical) =>
        different ? ConflictPairPriority.NeedsReview
        : identical ? ConflictPairPriority.CanOrganize : ConflictPairPriority.Incomplete;

    private static (bool Different, bool Identical, bool Incomplete) FactsFor(Participant first, Participant second) =>
        (first.Groups.Any(left => second.Groups.Any(right => left != right)),
            first.Groups.Overlaps(second.Groups), first.HasIncomplete || second.HasIncomplete);

    private static Participant GetParticipant(Dictionary<string, Participant> participants, string path)
    {
        if (!participants.TryGetValue(path, out var participant))
        {
            participant = new Participant(path);
            participants.Add(path, participant);
        }
        return participant;
    }

    private static IEnumerable<(Participant First, Participant Second)> ReviewPairs(
        Dictionary<string, Participant> participants, CancellationToken cancellationToken)
    {
        var groups = new Dictionary<int, List<Participant>>();
        var failed = new List<Participant>();
        foreach (var participant in Walk(participants.Values, cancellationToken))
        {
            if (participant.HasIncomplete) failed.Add(participant);
            foreach (var group in Walk(participant.Groups, cancellationToken))
            {
                if (!groups.TryGetValue(group, out var members))
                {
                    members = [];
                    groups.Add(group, members);
                }
                members.Add(participant);
            }
        }

        foreach (var first in Walk(failed, cancellationToken))
        {
            foreach (var second in Walk(participants.Values, cancellationToken))
            {
                if (first.Path != second.Path && (!second.HasIncomplete || ComparePaths(first.Path, second.Path) < 0))
                    yield return (first, second);
            }
        }
        var contentGroups = Walk(groups.Values, cancellationToken).ToArray();
        for (var firstGroup = 0; firstGroup < contentGroups.Length; firstGroup++)
        {
            for (var secondGroup = firstGroup + 1; secondGroup < contentGroups.Length; secondGroup++)
            {
                foreach (var first in Walk(contentGroups[firstGroup], cancellationToken))
                {
                    foreach (var second in Walk(contentGroups[secondGroup], cancellationToken))
                    {
                        if (first.Path != second.Path) yield return (first, second);
                    }
                }
            }
        }
    }

    private static void AddContainmentCandidates(
        IReadOnlyList<PackageComparisonData> comparisons,
        Dictionary<string, PackageResourceInventory> inventories,
        HashSet<(string First, string Second)> candidates,
        CancellationToken cancellationToken)
    {
        var coverage = new Dictionary<string, HashSet<ResourceKey>>(PathRules.Comparer);
        var byKey = new Dictionary<ResourceKey, List<PackageComparisonData>>();
        foreach (var data in Walk(comparisons, cancellationToken))
        {
            if (!byKey.TryGetValue(data.Comparison.Key, out var rows))
            {
                rows = [];
                byKey.Add(data.Comparison.Key, rows);
            }
            rows.Add(data);
            foreach (var participant in Walk(data.Participants.Values, cancellationToken))
            {
                if (!participant.HasStableStamp || participant.Groups.Count == 0) continue;
                if (!coverage.TryGetValue(participant.Path, out var keys))
                {
                    keys = [];
                    coverage.Add(participant.Path, keys);
                }
                keys.Add(data.Comparison.Key);
            }
        }

        foreach (var (path, keys) in Walk(coverage, cancellationToken))
        {
            var resourceCount = inventories[path].ResourceCount;
            // 连可用的重叠资源总数都不足时，不为这个文件展开一致配对。
            if (resourceCount <= 0 || keys.Count < resourceCount) continue;
            var required = Order(Walk(keys, cancellationToken).Select(key =>
                (Rows: byKey[key], ParticipantCount: Walk(byKey[key], cancellationToken)
                    .Sum(data => (long)data.Participants.Count))),
                (left, right) => left.ParticipantCount.CompareTo(right.ParticipantCount), cancellationToken);
            HashSet<string>? peers = null;
            foreach (var resource in Walk(required, cancellationToken))
            {
                if (peers is null)
                {
                    peers = new HashSet<string>(PathRules.Comparer);
                    foreach (var data in Walk(resource.Rows, cancellationToken))
                    {
                        if (!data.Participants.TryGetValue(path, out var first) || !first.HasStableStamp) continue;
                        foreach (var second in Walk(data.Participants.Values, cancellationToken))
                        {
                            if (path != second.Path && second.HasStableStamp && first.Groups.Overlaps(second.Groups))
                                peers.Add(second.Path);
                        }
                    }
                }
                else
                {
                    peers.RemoveWhere(peer => !HasStableIdenticalPeer(resource.Rows, path, peer, cancellationToken));
                }
                if (peers.Count == 0) break;
            }
            foreach (var peer in Walk(peers!, cancellationToken))
            {
                candidates.Add(PairKey(path, peer));
            }
        }
    }

    private static bool HasStableIdenticalPeer(
        IReadOnlyList<PackageComparisonData> rows, string path, string peer, CancellationToken cancellationToken)
    {
        foreach (var data in Walk(rows, cancellationToken))
        {
            if (data.Participants.TryGetValue(path, out var first) && first.HasStableStamp
                && data.Participants.TryGetValue(peer, out var second) && second.HasStableStamp
                && first.Groups.Overlaps(second.Groups)) return true;
        }
        return false;
    }

    private static (string First, string Second) PairKey(string first, string second) =>
        ComparePaths(first, second) < 0 ? (first, second) : (second, first);

    private static Dictionary<string, List<string>> CandidateNeighbors(
        HashSet<(string First, string Second)> candidates, CancellationToken cancellationToken)
    {
        var neighbors = new Dictionary<string, List<string>>(PathRules.Comparer);
        foreach (var (first, second) in Walk(candidates, cancellationToken))
        {
            if (!neighbors.TryGetValue(first, out var peers))
            {
                peers = [];
                neighbors.Add(first, peers);
            }
            peers.Add(second);
        }
        return neighbors;
    }

    private static IEnumerable<(Participant First, Participant Second)> CandidatePairs(
        Dictionary<string, Participant> participants,
        Dictionary<string, List<string>> neighbors, CancellationToken cancellationToken)
    {
        foreach (var first in Walk(participants.Values, cancellationToken))
        {
            if (!neighbors.TryGetValue(first.Path, out var peers)) continue;
            foreach (var peer in Walk(peers, cancellationToken))
            {
                if (participants.TryGetValue(peer, out var second)) yield return (first, second);
            }
        }
    }

    private static Dictionary<string, string> CanonicalPaths(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var canonical = new Dictionary<string, string>(PathRules.Comparer);
        foreach (var path in Walk(paths, cancellationToken))
        {
            if (!canonical.TryGetValue(path, out var previous) || StringComparer.Ordinal.Compare(path, previous) < 0)
            {
                canonical[path] = path;
            }
        }
        return canonical;
    }

    private static IEnumerable<ResourceContentOccurrence> PackageOccurrences(
        ResourceContentComparison comparison, CancellationToken cancellationToken) =>
        Walk(comparison.ContentGroups, cancellationToken)
            .SelectMany(group => Walk(group.Occurrences, cancellationToken))
            .Concat(Walk(comparison.Uncompared, cancellationToken));

    private static IEnumerable<ScriptModuleOccurrence> ScriptOccurrences(
        ScriptModuleCollision collision, CancellationToken cancellationToken) =>
        Walk(collision.ContentGroups, cancellationToken)
            .SelectMany(group => Walk(group.Occurrences, cancellationToken))
            .Concat(Walk(collision.Uncompared, cancellationToken));

    private static int ComparePaths(string left, string right)
    {
        var result = PathRules.Comparer.Compare(left, right);
        return result == 0 ? StringComparer.Ordinal.Compare(left, right) : result;
    }

    private static int ComparePairs(ConflictPairPriority leftPriority, string leftFirst, string leftSecond,
        ConflictPairPriority rightPriority, string rightFirst, string rightSecond)
    {
        var result = leftPriority.CompareTo(rightPriority);
        if (result == 0) result = ComparePaths(leftFirst, rightFirst);
        return result == 0 ? ComparePaths(leftSecond, rightSecond) : result;
    }

    private static IReadOnlyList<T> Order<T>(IEnumerable<T> items, Comparison<T> comparison,
        CancellationToken cancellationToken)
    {
        var ordered = Walk(items, cancellationToken).ToList();
        // List.Sort 会包装比较器抛出的异常，取消必须仍向调用方传播为 OperationCanceledException。
        try
        {
            ordered.Sort((left, right) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return comparison(left, right);
            });
        }
        catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        return ReadOnlyLists.Freeze(Walk(ordered, cancellationToken));
    }

    private static IEnumerable<T> Walk<T>(IEnumerable<T> items, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class Participant(string path)
    {
        public string Path { get; } = path;
        public HashSet<int> Groups { get; } = [];
        public bool HasIncomplete { get; set; }
        public bool HasStableStamp { get; set; } = true;
    }

    private sealed class PackagePairData
    {
        public Dictionary<ResourceKey, PackagePairResourceMatch> Resources { get; } = [];
        public bool HasStableStamps { get; set; } = true;
    }

    private sealed record PackageComparisonData(
        ResourceContentComparison Comparison, Dictionary<string, Participant> Participants);

    private sealed record ScriptCollisionData(
        ScriptModuleCollision Collision, Dictionary<string, Participant> Participants);
}
