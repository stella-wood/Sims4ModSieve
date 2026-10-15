using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;

namespace Sims4ModSieve.Core.Tests.Conflicts;

[TestClass]
public sealed class OverrideConflictCardRulesTests
{
    [TestMethod]
    public void PairRulePreservesEveryOverrideAndIgnoresOtherRelations()
    {
        var first = Pair("a", "b", 1, 2);
        var second = Pair("b", "c", 2, 3);
        var cards = OverrideConflictCardRules.Build(new[] { first, second, first with { Kind = PackageConflictRelationKind.Hidden } },
            new(OverrideConflictCardRuleKind.ByPair));
        Assert.AreEqual(2, cards.Count);
        Assert.IsTrue(cards.All(card => card.Count == 1));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<IReadOnlyList<PackageConflictPairRelation>>)cards).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<PackageConflictPairRelation>)cards[0]).Clear());
    }

    [TestMethod]
    public void ExactSetRuleIgnoresOrderAndMergesOnlyEqualSets()
    {
        var cards = OverrideConflictCardRules.Build(new[] { Pair("a", "b", 1, 2), Pair("b", "c", 2, 1), Pair("c", "d", 2, 3) },
            new(OverrideConflictCardRuleKind.ByExactSharedKeySet));
        Assert.AreEqual(2, cards.Count);
        Assert.AreEqual(2, cards[0].Count);
        Assert.AreEqual(3, cards.SelectMany(card => card).Count());
    }

    [TestMethod]
    [DataRow(OverrideConflictCardRuleKind.ByExactSharedKeySet)]
    [DataRow(OverrideConflictCardRuleKind.BySharedKeyOverlap)]
    public void MixedRelationsOnlyUseOverridePairsAsCardUnits(OverrideConflictCardRuleKind rule)
    {
        var first = Pair("a", "b", 1);
        var second = Pair("b", "c", 1);
        var units = new[] { first, second, Pair("a", "d", 1) with { Kind = PackageConflictRelationKind.SuspectedSameMod },
            Pair("b", "d", 1) with { Kind = PackageConflictRelationKind.BundleContainment } };
        var card = OverrideConflictCardRules.Build(units, PackageConflictGroupingBuilderTests.ScopedRule(rule)).Single();
        Assert.AreEqual(2, card.Count);
        Assert.AreSame(first, card[0]);
        Assert.AreSame(second, card[1]);
    }

    [TestMethod]
    [DataRow(0.5, 2)]
    [DataRow(0.7, 3)]
    [DataRow(0.9, 3)]
    public void OverlapGreedyUsesLargerSetAndNeverConnectsTransitively(double threshold, int expected)
    {
        var units = new[] { Pair("a", "b", 1, 2), Pair("b", "c", 2, 3), Pair("c", "d", 3, 4) };
        var cards = OverrideConflictCardRules.Build(units, new(OverrideConflictCardRuleKind.BySharedKeyOverlap, threshold));
        Assert.AreEqual(expected, cards.Count);
        Assert.AreEqual(3, cards.SelectMany(card => card).Distinct().Count());
        Assert.IsTrue(cards.All(card => card.Count <= 2));
        var reversed = OverrideConflictCardRules.Build(units.Reverse(), new(OverrideConflictCardRuleKind.BySharedKeyOverlap, threshold));
        Assert.AreEqual(Fingerprint(cards), Fingerprint(reversed));
    }

    [TestMethod]
    public void ExistingOverlapGroupsNeverMergeAndSingletonIsPreserved()
    {
        var units = new[] { Pair("a", "b", 1, 2, 3), Pair("b", "c", 1, 2, 3),
            Pair("c", "d", 2, 3, 4), Pair("d", "e", 2, 3, 4), Pair("f", "g", 99) };
        var cards = OverrideConflictCardRules.Build(units, new(OverrideConflictCardRuleKind.BySharedKeyOverlap, 0.5));
        CollectionAssert.AreEqual(new[] { 2, 2, 1 }, cards.Select(card => card.Count).ToArray());
        Assert.AreEqual(units.Length, cards.SelectMany(card => card).Count());
    }

    [TestMethod]
    public void OverlapBoundaryUsesMaximumSetSizeAndSharedCountBreaksTies()
    {
        var units = new[] { Pair("a", "b", 1, 2), Pair("b", "c", 1, 2, 3, 4), Pair("c", "d", 3, 4, 5, 6) };
        var cards = OverrideConflictCardRules.Build(units, new(OverrideConflictCardRuleKind.BySharedKeyOverlap, 0.5));
        Assert.AreEqual(2, cards.Count);
        Assert.AreEqual(2, cards[0].Count);
        Assert.AreEqual(3, OverrideConflictCardRules.Build(units, new(OverrideConflictCardRuleKind.BySharedKeyOverlap, 0.500001)).Count);
    }

    [TestMethod]
    public void SharedCountTakesPriorityOverPathWhenOverlapIsTied()
    {
        var units = new[] { Pair("a", "b", 1, 2), Pair("b", "c", 1, 2, 3, 4), Pair("c", "d", 3, 4, 5, 6, 7, 8, 9, 10) };
        // 第一条边为 2/4，第二条为 2/8，先验证较大分母不会误合。
        Assert.AreEqual(2, OverrideConflictCardRules.Build(units, new(OverrideConflictCardRuleKind.BySharedKeyOverlap, 0.5)).Count);
        units = [Pair("a", "b", 1, 2), Pair("b", "c", 1, 2, 3, 4), Pair("c", "d", 1, 3, 4, 5, 6, 7)];
        // a–b 单元与 b–c 单元为 2/4；b–c 与 c–d 为 3/6，后者共有数更大。
        var cards = OverrideConflictCardRules.Build(units, new(OverrideConflictCardRuleKind.BySharedKeyOverlap, 0.5));
        Assert.AreEqual(2, cards.Count);
        Assert.AreEqual(1, cards[0].Count);
        Assert.AreEqual("b", cards[1][0].Pair.FirstPath);
        Assert.AreEqual("c", cards[1][1].Pair.FirstPath);
    }

    [TestMethod]
    public void CancellationReturnsNoPartialCardsAndMissingOverlapThresholdIsRejected()
    {
        using var cancellation = new CancellationTokenSource();
        IEnumerable<PackageConflictPairRelation> Canceling()
        {
            yield return Pair("a", "b", 1);
            cancellation.Cancel();
            yield return Pair("b", "c", 2);
        }
        Assert.ThrowsExactly<OperationCanceledException>(() => OverrideConflictCardRules.Build(Canceling(),
            new(OverrideConflictCardRuleKind.ByPair), cancellation.Token));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => OverrideConflictCardRules.Build([], new(OverrideConflictCardRuleKind.BySharedKeyOverlap)));
    }

    private static PackageConflictPairRelation Pair(string first, string second, params ulong[] keys)
    {
        var matches = keys.Select(key => new PackagePairResourceMatch(
            PackageConflictGroupingBuilderTests.Resource(key, (first, "x"), (second, "y")), true, false, false)).ToArray();
        return new(new(first, second, matches.Length, 0, 0, false, false, matches), PackageConflictRelationKind.OverrideConflict, 0.1);
    }

    private static string Fingerprint(IReadOnlyList<IReadOnlyList<PackageConflictPairRelation>> cards) =>
        string.Join(';', cards.Select(card => string.Join(',', card.Select(unit => $"{unit.Pair.FirstPath}/{unit.Pair.SecondPath}"))));
}
