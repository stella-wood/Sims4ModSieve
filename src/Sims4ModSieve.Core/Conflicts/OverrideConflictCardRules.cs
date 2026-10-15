using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;

namespace Sims4ModSieve.Core.Conflicts;

public enum OverrideConflictCardRuleKind { ByPair, ByExactSharedKeySet, BySharedKeyOverlap }

/// <summary>实验参数，调用方必须选择规则；重合规则必须同时给出阈值。</summary>
public sealed record OverrideConflictCardRule(OverrideConflictCardRuleKind Kind, double? SharedKeyOverlapThreshold = null);

public static class OverrideConflictCardRules
{
    public static IReadOnlyList<IReadOnlyList<PackageConflictPairRelation>> Build(
        IEnumerable<PackageConflictPairRelation> relations, OverrideConflictCardRule rule,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(rule.Kind)) throw new ArgumentOutOfRangeException(nameof(rule.Kind));
        if (rule.Kind == OverrideConflictCardRuleKind.BySharedKeyOverlap
            && (rule.SharedKeyOverlapThreshold is not { } threshold || !double.IsFinite(threshold) || threshold <= 0 || threshold > 1))
            throw new ArgumentOutOfRangeException(nameof(rule.SharedKeyOverlapThreshold));
        var units = new List<PackageConflictPairRelation>();
        foreach (var relation in relations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (relation.Kind == PackageConflictRelationKind.OverrideConflict) units.Add(relation);
        }
        units = units.OrderBy(edge => edge.Pair.FirstPath, PathRules.Comparer)
            .ThenBy(edge => edge.Pair.FirstPath, StringComparer.Ordinal)
            .ThenBy(edge => edge.Pair.SecondPath, PathRules.Comparer)
            .ThenBy(edge => edge.Pair.SecondPath, StringComparer.Ordinal).ToList();
        var cards = new List<IReadOnlyList<PackageConflictPairRelation>>();
        if (rule.Kind == OverrideConflictCardRuleKind.ByPair)
        {
            foreach (var unit in units)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cards.Add(ReadOnlyLists.Freeze(new[] { unit }));
            }
        }
        else if (rule.Kind == OverrideConflictCardRuleKind.ByExactSharedKeySet)
        {
            var sets = new Dictionary<string, List<PackageConflictPairRelation>>(StringComparer.Ordinal);
            foreach (var unit in units)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var signature = string.Join(',', unit.Pair.Resources.Select(match => match.Key).Distinct()
                    .OrderBy(key => key.Type).ThenBy(key => key.Group).ThenBy(key => key.Instance).Select(key => key.Tgi));
                if (!sets.TryGetValue(signature, out var set)) sets.Add(signature, set = []);
                set.Add(unit);
            }
            foreach (var set in sets.Values) cards.Add(ReadOnlyLists.Freeze(set));
        }
        else
        {
            var keys = units.Select(unit => unit.Pair.Resources.Select(match => match.Key).ToHashSet()).ToArray();
            var edges = new List<(int First, int Second, double Overlap, int Shared)>();
            var neighbors = Enumerable.Range(0, units.Count).Select(_ => new HashSet<int>()).ToArray();
            for (var first = 0; first < units.Count; first++)
            {
                for (var second = first + 1; second < units.Count; second++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var shared = 0;
                    var smaller = keys[first].Count <= keys[second].Count ? keys[first] : keys[second];
                    var larger = ReferenceEquals(smaller, keys[first]) ? keys[second] : keys[first];
                    foreach (var key in smaller)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (larger.Contains(key)) shared++;
                    }
                    var overlap = shared / (double)larger.Count;
                    if (overlap < rule.SharedKeyOverlapThreshold!.Value) continue;
                    edges.Add((first, second, overlap, shared));
                    neighbors[first].Add(second);
                    neighbors[second].Add(first);
                }
            }
            var assignments = new HashSet<int>?[units.Count];
            var sets = new List<HashSet<int>>();
            try
            {
                edges.Sort((left, right) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = right.Overlap.CompareTo(left.Overlap);
                    if (result == 0) result = right.Shared.CompareTo(left.Shared);
                    if (result == 0) result = left.First.CompareTo(right.First);
                    return result == 0 ? left.Second.CompareTo(right.Second) : result;
                });
            }
            catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested)
            {
                // List.Sort 包装比较器异常，调用方仍需要收到取消而不是排序失败。
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            foreach (var edge in edges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = assignments[edge.First];
                var second = assignments[edge.Second];
                if (first is not null && second is not null) continue;
                if (first is null && second is null)
                {
                    var set = new HashSet<int> { edge.First, edge.Second };
                    sets.Add(set);
                    assignments[edge.First] = assignments[edge.Second] = set;
                }
                else
                {
                    var set = first ?? second!;
                    var index = first is null ? edge.First : edge.Second;
                    if (set.All(member => neighbors[index].Contains(member)))
                    {
                        set.Add(index);
                        assignments[index] = set;
                    }
                }
            }
            for (var index = 0; index < units.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (assignments[index] is null) sets.Add([index]);
            }
            foreach (var set in sets.OrderBy(set => set.Min()))
                cards.Add(ReadOnlyLists.Freeze(set.Order().Select(index => units[index])));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ReadOnlyLists.Freeze(cards);
    }
}
