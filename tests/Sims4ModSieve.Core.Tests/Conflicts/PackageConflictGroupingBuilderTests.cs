using System.Collections;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;

namespace Sims4ModSieve.Core.Tests.Conflicts;

[TestClass]
public sealed class PackageConflictGroupingBuilderTests
{
    private readonly PackageConflictGroupingBuilder builder = new();
    internal static readonly FileStamp Stamp = new(100, DateTime.UnixEpoch);
    internal static PackageConflictGroupingOptions Options(int minimum = 1, double threshold = 0.5,
        OverrideConflictCardRule? rule = null) => new(minimum, rule ?? new(OverrideConflictCardRuleKind.ByPair), threshold);

    [TestMethod]
    public void ExcludesUniqueToolingTypesAndNameMapCanBePreparedAgainWithoutScanning()
    {
        var resource = Resource(1, ("a", "x"), ("b", "y"));
        var name = Resource(2, ("a", "same"), ("b", "same")) with { Key = new(0x0166038C, 0, 0) };
        var scan = Scan(("a", 7), ("b", 7)) with
        {
            ToolingMetadataTypes = new HashSet<uint> { 0x7FB6AD8A, 0x6BF15BBE, 0x01357924 },
            ResourceInventories = new[] { "a", "b" }.Select(path => new PackageResourceInventory(path, Stamp, 7)
            {
                ResourceTypeCounts = new Dictionary<uint, int> { [1] = 3, [0x0166038C] = 1,
                    [0x7FB6AD8A] = 1, [0x6BF15BBE] = 1, [0x01357924] = 1 },
            }).ToArray(),
        };
        var report = Contents(resource, name);
        var excluded = builder.Prepare(scan, report);
        Assert.IsTrue(excluded.Files.All(file => file.ResourceCount == 3));
        Assert.AreEqual(1, excluded.Pairs.Single().Resources.Count);
        var included = builder.Prepare(scan, report, excludeNameMap: false);
        Assert.IsTrue(included.Files.All(file => file.ResourceCount == 4));
        Assert.AreEqual(2, included.Pairs.Single().Resources.Count);
        Assert.AreEqual(PackageConflictRelationKind.OverrideConflict, builder.Build(excluded, Options()).Relations.Single().Kind);
        Assert.AreEqual(PackageConflictRelationKind.SuspectedSameMod, builder.Build(included, Options()).Relations.Single().Kind);
    }

    [TestMethod]
    public void EntirelyExcludedResourcesLeaveEmptyFilesAndNoPairsEvenWhenExcludedContentFailed()
    {
        var key = new ResourceKey(0x0166038C, 0, 0);
        var resource = Resource(1, ("a", null), ("b", "same")) with { Key = key };
        var scan = Scan(("a", 1), ("b", 1)) with
        {
            ResourceInventories = new[] { "a", "b" }.Select(path => new PackageResourceInventory(path, Stamp, 1)
                { ResourceTypeCounts = new Dictionary<uint, int> { [key.Type] = 1 } }).ToArray(),
        };
        var input = builder.Prepare(scan, Contents(resource));
        Assert.AreEqual(2, input.FileCount);
        Assert.IsTrue(input.Files.All(file => file.ResourceCount == 0));
        Assert.AreEqual(0, input.ExcludedFiles.Count);
        Assert.AreEqual(0, builder.Build(input, Options()).Groups.Count);
        Assert.AreEqual(1, builder.Prepare(scan, Contents(resource), false).ExcludedFiles.Count);
    }

    [TestMethod]
    public void MissingOrIncompleteFullTypeCountsAreReportedWithoutGuessing()
    {
        var scan = Scan(("a", 3), ("b", 3));
        scan = scan with { ResourceInventories = [scan.ResourceInventories[0] with { ResourceTypeCounts = null },
            scan.ResourceInventories[1] with { ResourceTypeCounts = new Dictionary<uint, int> { [1] = 2 } }] };
        var input = builder.Prepare(scan, Contents(Resource(1, ("a", "x"), ("b", "y"))));
        Assert.AreEqual(0, input.FileCount);
        Assert.AreEqual(2, input.ExcludedFiles.Count);
        Assert.IsTrue(input.ExcludedFiles.All(file => file.InputIssues.Single().Code == "missing-resource-type-counts"));
    }

    [TestMethod]
    public void MissingEntireInventoryIsExplicitlyRejectedEvenWithoutCollisions()
    {
        var scan = Scan(("a", 3)) with { ResourceInventories = [] };
        var error = Assert.ThrowsExactly<ArgumentException>(() => builder.Prepare(scan, Contents()));
        StringAssert.Contains(error.Message, "分组输入缺数据");
    }

    [TestMethod]
    public void InventoryCopiesTypeCountsAndMissingContentOrOccurrenceVersionExitsFile()
    {
        var counts = new Dictionary<uint, int> { [1] = 3 };
        var inventory = new PackageResourceInventory("a", Stamp, 3) { ResourceTypeCounts = counts };
        counts[1] = 100;
        Assert.AreEqual(3, inventory.ResourceTypeCounts![1]);
        var key = new ResourceKey(1, 0, 1);
        var candidate = new PackageConflictCandidate(key, [new(key, "a", Stamp, [], 0, 1, PackageCompression.None, "NONE")]);
        var scan = Scan(("a", 3)) with { Candidates = [candidate] };
        Assert.AreEqual("missing-resource-comparison", builder.Prepare(scan, Contents()).ExcludedFiles.Single().InputIssues.Single().Code);
        var resource = Resource(1, ("a", "same"), ("b", "same"));
        resource = resource with { ContentGroups = resource.ContentGroups.Select(group => group with
            { Occurrences = group.Occurrences.Select(item => item.PackagePath == "a" ? item with { PackageStamp = Stamp with { Length = 101 } } : item).ToArray() }).ToArray() };
        var input = builder.Prepare(Scan(("a", 3), ("b", 3)), Contents(resource));
        Assert.AreEqual("occurrence-version-mismatch", input.ExcludedFiles.Single().InputIssues.Single().Code);
        Assert.AreEqual(0, input.Pairs.Count);
    }

    [TestMethod]
    [DataRow(0.1)]
    [DataRow(1.0)]
    public void SameModThresholdRangeEndpointsAndMinimumValidation(double threshold)
    {
        var input = builder.Prepare(Scan(), Contents());
        Assert.AreEqual(0, builder.Build(input, Options(threshold: threshold)).Groups.Count);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => builder.Build(input, Options(minimum: 0)));
    }

    [TestMethod]
    [DataRow(1, PackageConflictRelationKind.BundleContainment)]
    [DataRow(3, PackageConflictRelationKind.BundleContainment)]
    [DataRow(5, PackageConflictRelationKind.OverrideConflict)]
    public void StrictContainmentUsesResourceSetsEvenWhenEveryContentDiffers(int minimum, PackageConflictRelationKind kind)
    {
        var input = builder.Prepare(Scan(("a", 3), ("b", 7)), Contents(
            Resource(1, ("a", "x"), ("b", "y")), Resource(2, ("a", "x"), ("b", "y")), Resource(3, ("a", "x"), ("b", "y"))));
        var result = builder.Build(input, Options(minimum));
        Assert.AreEqual(kind, result.Relations.Single().Kind);
        if (kind == PackageConflictRelationKind.BundleContainment)
        {
            Assert.AreEqual("b", result.Groups.Single().BundlePath);
            Assert.AreEqual(false, result.Groups.Single().Members.Single(member => member.Path == "a").ContainedContentIdentical);
        }
    }

    [TestMethod]
    public void EqualSetsAreSuspectedAndHighOverlapDifferencesTakePriorityOverOverride()
    {
        var input = builder.Prepare(Scan(("a", 2), ("b", 2)), Contents(
            Resource(1, ("a", "x"), ("b", "y")), Resource(2, ("a", "x"), ("b", "y"))));
        var relation = builder.Build(input, Options()).Relations.Single();
        Assert.AreEqual(PackageConflictRelationKind.SuspectedSameMod, relation.Kind);
        Assert.AreEqual(1, relation.Overlap);
        Assert.IsNull(relation.ContainedPath);
    }

    [TestMethod]
    public void BundleCardsMergeByBAllowRepeatedAAndSubtractSharedUnion()
    {
        var input = builder.Prepare(Scan(("a", 2), ("c", 2), ("b", 7), ("d", 8)), Contents(
            Resource(1, ("a", "same"), ("c", "same"), ("b", "same"), ("d", "same")),
            Resource(2, ("a", "same"), ("b", "same"), ("d", "same")),
            Resource(3, ("c", "different"), ("b", "same"))));
        var result = builder.Build(input, Options());
        var bundles = result.Groups.Where(group => group.Kind == PackageConflictRelationKind.BundleContainment).ToArray();
        Assert.AreEqual(2, bundles.Length);
        var b = bundles.Single(group => group.BundlePath == "b");
        Assert.AreEqual(3, b.Members.Count);
        Assert.AreEqual(4, b.BundleUniqueResourceCount);
        Assert.AreEqual(true, b.Members.Single(member => member.Path == "a").ContainedContentIdentical);
        Assert.AreEqual(false, b.Members.Single(member => member.Path == "c").ContainedContentIdentical);
        Assert.AreEqual(2, bundles.Count(group => group.Members.Any(member => member.Path == "a")));
    }

    [TestMethod]
    public void OverlapUsesLargerFileAndIncludesBoundaryInEitherDirection()
    {
        foreach (var reverse in new[] { false, true })
        {
            var input = builder.Prepare(Scan(("a", reverse ? 4 : 2), ("b", reverse ? 2 : 4)),
                Contents(Resource(1, ("a", "x"), ("b", "y")), Resource(2, ("a", "x"), ("b", "y"))));
            Assert.AreEqual(0.5, builder.Build(input, Options(3)).Relations.Single().Overlap);
            Assert.AreEqual(PackageConflictRelationKind.SuspectedSameMod, builder.Build(input, Options(3)).Relations.Single().Kind);
            Assert.AreEqual(PackageConflictRelationKind.OverrideConflict, builder.Build(input, Options(3, 0.500001)).Relations.Single().Kind);
        }
    }

    [TestMethod]
    public void IdenticalPartialOverlapSurvivesPreparationButHiddenDoesNotMakeCards()
    {
        var resource = Resource(1, ("a", "same"), ("b", "same"));
        var input = builder.Prepare(Scan(("a", 10), ("b", 10)), Contents(resource));
        Assert.AreSame(resource, input.Pairs.Single().Resources.Single().Comparison);
        var result = builder.Build(input, Options());
        Assert.AreEqual(PackageConflictRelationKind.Hidden, result.Relations.Single().Kind);
        Assert.AreEqual(0, result.Groups.Count);
    }

    [TestMethod]
    public void SameModGreedyDoesNotConnectThroughMiddleFile()
    {
        var input = builder.Prepare(Scan(("a", 4), ("b", 4), ("c", 4)), Contents(
            Resource(1, ("a", "x"), ("b", "x"), ("c", "x")),
            Resource(2, ("a", "x"), ("b", "x")), Resource(3, ("a", "x"), ("b", "x")),
            Resource(4, ("b", "x"), ("c", "x"))));
        var group = builder.Build(input, Options(5)).Groups.Single();
        CollectionAssert.AreEqual(new[] { "a", "b" }, group.Members.Select(member => member.Path).ToArray());
    }

    [TestMethod]
    public void JoiningRequiresEveryMemberPairToBeSuspectedRatherThanMerelyOverThreshold()
    {
        var input = builder.Prepare(Scan(("a", 4), ("b", 6), ("c", 6)), Contents(
            Resource(1, ("a", "x"), ("b", "x"), ("c", "x")),
            Resource(2, ("a", "x"), ("b", "x"), ("c", "x")),
            Resource(3, ("a", "x"), ("b", "x"), ("c", "x")),
            Resource(4, ("a", "x"), ("c", "x")), Resource(5, ("b", "x"), ("c", "x")), Resource(6, ("b", "x"), ("c", "x"))));
        var result = builder.Build(input, Options());
        var group = result.Groups.Single(group => group.Kind == PackageConflictRelationKind.SuspectedSameMod);
        CollectionAssert.AreEqual(new[] { "b", "c" }, group.Members.Select(member => member.Path).ToArray());
        Assert.AreEqual(PackageConflictRelationKind.BundleContainment,
            result.Relations.Single(edge => edge.Pair.FirstPath == "a" && edge.Pair.SecondPath == "c").Kind);
    }

    [TestMethod]
    public void ExistingSameModGroupsDoNotMergeAndFilesOccurAtMostOnce()
    {
        var resources = Enumerable.Range(1, 3).Select(index => Resource((ulong)index,
            ("a", "x"), ("b", "x"), ("c", "x"), ("d", "x"))).Concat(
            new[] { Resource(4, ("a", "x"), ("b", "x")), Resource(5, ("c", "x"), ("d", "x")) }).ToArray();
        var result = builder.Build(builder.Prepare(Scan(("a", 8), ("b", 8), ("c", 8), ("d", 8)), Contents(resources)), Options(1, 0.3));
        Assert.AreEqual(2, result.Groups.Count);
        Assert.IsTrue(result.Groups.All(group => group.Members.Count == 2));
        Assert.AreEqual(4, result.Groups.SelectMany(group => group.Members).Select(member => member.Path).Distinct().Count());
    }

    [TestMethod]
    public void CardCountsArePerFileUseAllSharedKeysAndDeduplicateRecords()
    {
        var first = Resource(1, ("a", "x"), ("b", "y"));
        var second = Resource(2, ("a", "x"), ("b", "y"), ("c", "y"));
        var third = Resource(3, ("b", "x"), ("c", "y"));
        var identical = Resource(4, ("a", "same"), ("b", "same"), ("c", "same"));
        var input = builder.Prepare(Scan(("a", 10), ("b", 10), ("c", 10)), Contents(first, second, third, identical, identical));
        var result = builder.Build(input, Options(rule: new(OverrideConflictCardRuleKind.BySharedKeyOverlap, 0.5)));
        var group = result.Groups.Single();
        CollectionAssert.AreEqual(new[] { 3, 4, 3 }, group.Members.Select(member => member.SharedResourceCount).ToArray());
        CollectionAssert.AreEqual(new[] { 7, 6, 7 }, group.Members.Select(member => member.UniqueResourceCount).ToArray());
        Assert.AreEqual(4, group.ResourceKeys.Count);
        Assert.IsTrue(group.ResourceKeys.Contains(identical.Key));
    }

    [TestMethod]
    [DataRow(OverrideConflictCardRuleKind.ByExactSharedKeySet)]
    [DataRow(OverrideConflictCardRuleKind.BySharedKeyOverlap)]
    public void SuspectedRelationDoesNotExpandOverrideKeysOrCounts(OverrideConflictCardRuleKind rule)
    {
        var input = MixedRelationInput(contains: false);
        var result = builder.Build(input, new(ScopedRule(rule)));
        var red = result.Groups.Single(group => group.Kind == PackageConflictRelationKind.OverrideConflict);
        var yellow = result.Groups.Single(group => group.Kind == PackageConflictRelationKind.SuspectedSameMod);
        CollectionAssert.AreEqual(new[] { new ResourceKey(1, 0, 1) }, red.ResourceKeys.ToArray());
        CollectionAssert.AreEqual(new[] { 1, 1, 1 }, red.Members.Select(member => member.SharedResourceCount).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 1, 9 }, red.Members.Select(member => member.UniqueResourceCount).ToArray());
        Assert.AreEqual(3, yellow.ResourceKeys.Count);
        Assert.IsTrue(yellow.Members.All(member => member.SharedResourceCount == 3 && member.UniqueResourceCount == 1));
        Assert.IsTrue(red.Relations.All(edge => edge.Kind == PackageConflictRelationKind.OverrideConflict));
        Assert.AreSame(input.Pairs.Single(pair => pair.FirstPath == "a" && pair.SecondPath == "c"), red.Relations[0].Pair);
        Assert.AreSame(input.Pairs.Single(pair => pair.FirstPath == "a" && pair.SecondPath == "c").Resources.Single().Comparison,
            red.Relations[0].Pair.Resources.Single().Comparison);
        Assert.AreEqual(2, result.GroupsByFile["A"].Count);
        Assert.AreSame(red, result.GroupsByFile["A"].Single(group => group.Kind == red.Kind));
        Assert.AreSame(yellow, result.GroupsByFile["A"].Single(group => group.Kind == yellow.Kind));
    }

    [TestMethod]
    [DataRow(OverrideConflictCardRuleKind.ByExactSharedKeySet)]
    [DataRow(OverrideConflictCardRuleKind.BySharedKeyOverlap)]
    public void ContainmentDoesNotExpandOverrideScopeAndBundleKeepsOriginalCounts(OverrideConflictCardRuleKind rule)
    {
        var result = builder.Build(MixedRelationInput(contains: true), new(ScopedRule(rule)));
        var red = result.Groups.Single(group => group.Kind == PackageConflictRelationKind.OverrideConflict);
        var yellow = result.Groups.Single(group => group.Kind == PackageConflictRelationKind.BundleContainment);
        Assert.AreEqual(1, red.ResourceKeys.Count);
        Assert.IsTrue(red.Members.All(member => member.SharedResourceCount == 1));
        Assert.AreEqual(0, red.Members.Single(member => member.Path == "a").UniqueResourceCount);
        Assert.AreEqual(3, red.Members.Single(member => member.Path == "b").UniqueResourceCount);
        Assert.AreEqual(3, yellow.ResourceKeys.Count);
        Assert.AreEqual(3, yellow.BundleUniqueResourceCount);
        Assert.IsTrue(yellow.Members.All(member => member.SharedResourceCount == 3));
        Assert.AreEqual(true, yellow.Members.Single(member => member.Path == "a").ContainedContentIdentical);
        Assert.AreEqual(2, result.GroupsByFile["B"].Count);
        Assert.AreSame(yellow, result.GroupsByFile["B"].Single(group => group.Kind == yellow.Kind));
    }

    [TestMethod]
    public void OverlapCardCountsOnlyKeysInEachMembersOwnCollectedOverridePairs()
    {
        var input = builder.Prepare(Scan(("a", 10), ("b", 10), ("c", 10)), Contents(
            Resource(1, ("a", "x"), ("b", "y")), Resource(2, ("a", "x"), ("b", "y"), ("c", "y")),
            Resource(3, ("b", "x"), ("c", "y")), Resource(4, ("a", "x"), ("c", "y")), Resource(5, ("a", "x"), ("c", "y"))));
        var group = builder.Build(input, new(new(OverrideConflictCardRuleKind.BySharedKeyOverlap, 0.5))).Groups.Single(group => group.Relations.Count == 2);
        Assert.AreEqual(3, group.ResourceKeys.Count);
        // A–C 是单独存在的覆盖对，却没有被本卡收录；不能从成员集合回填它的键。
        CollectionAssert.AreEqual(new[] { 2, 3, 2 }, group.Members.Select(member => member.SharedResourceCount).ToArray());
        CollectionAssert.AreEqual(new[] { 6, 7, 6 }, group.Members.Select(member => member.UniqueResourceCount).ToArray());
        Assert.AreEqual(2, group.Relations.Count);
    }

    [TestMethod]
    public void GroupsByFileIncludesMultipleOverrideCardsAndIsReadOnlyAcrossRecalculation()
    {
        var input = builder.Prepare(Scan(("a", 10), ("b", 10), ("c", 10)), Contents(
            Resource(1, ("a", "x"), ("b", "y")), Resource(2, ("a", "x"), ("c", "y"))));
        var initial = builder.Build(input, new(new(OverrideConflictCardRuleKind.ByExactSharedKeySet)));
        var references = initial.GroupsByFile["A"];
        Assert.AreEqual(2, references.Count);
        Assert.AreEqual(2, references.Distinct(ReferenceEqualityComparer.Instance).Count());
        foreach (var group in initial.Groups) Assert.AreSame(group, references.Single(reference => ReferenceEquals(reference, group)));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IDictionary<string, IReadOnlyList<PackageConflictGroup>>)initial.GroupsByFile).Clear());
        AssertReadOnly(references);
        var updated = builder.Build(input, new(new(OverrideConflictCardRuleKind.BySharedKeyOverlap, 0.5)));
        Assert.AreNotSame(initial.GroupsByFile, updated.GroupsByFile);
        Assert.AreSame(references, initial.GroupsByFile["a"]);
        Assert.AreEqual(2, initial.GroupsByFile["a"].Count);
        Assert.AreNotSame(initial.Groups[0], updated.Groups[0]);
        Assert.AreSame(updated.Groups[0], updated.GroupsByFile["a"][0]);
    }

    [TestMethod]
    public void DefaultOptionsUseFormalConfiguration()
    {
        var options = new PackageConflictGroupingOptions();
        Assert.AreEqual(3, options.MinimumContainedResourceCount);
        Assert.AreEqual(OverrideConflictCardRuleKind.ByExactSharedKeySet, options.OverrideCardRule.Kind);
        Assert.IsNull(options.OverrideCardRule.SharedKeyOverlapThreshold);
        Assert.AreEqual(0.5, options.SameModThreshold);
    }

    [TestMethod]
    public void DefaultUsesExactSharedKeysWhileExplicitOverlapRemainsAvailable()
    {
        var input = builder.Prepare(Scan(("a", 10), ("b", 10), ("c", 10)), Contents(
            Resource(1, ("a", "x"), ("b", "y"), ("c", "z")), Resource(2, ("a", "x"), ("c", "z"))));
        var result = builder.Build(input, new PackageConflictGroupingOptions());
        Assert.IsTrue(result.Relations.All(edge => edge.Kind == PackageConflictRelationKind.OverrideConflict));
        Assert.AreEqual(2, result.Groups.Count);
        var singleKey = result.Groups.Single(group => group.ResourceKeys.Count == 1);
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, singleKey.Members.Select(member => member.Path).ToArray());
        Assert.AreEqual(2, singleKey.Relations.Count);
        var twoKeys = result.Groups.Single(group => group.ResourceKeys.Count == 2);
        CollectionAssert.AreEqual(new[] { "a", "c" }, twoKeys.Members.Select(member => member.Path).ToArray());
        Assert.AreEqual(1, twoKeys.Relations.Count);

        var experimental = new PackageConflictGroupingOptions(new(OverrideConflictCardRuleKind.BySharedKeyOverlap, 0.5));
        var merged = builder.Build(input, experimental).Groups.Single();
        Assert.AreEqual(OverrideConflictCardRuleKind.BySharedKeyOverlap, experimental.OverrideCardRule.Kind);
        Assert.AreEqual(0.5, experimental.OverrideCardRule.SharedKeyOverlapThreshold);
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, merged.Members.Select(member => member.Path).ToArray());
        Assert.AreEqual(3, merged.Relations.Count);
        Assert.AreEqual(2, merged.ResourceKeys.Count);
    }

    [TestMethod]
    public void FormalMinimumDefaultsToThreeWhileExplicitMinimumRemainsAvailable()
    {
        var rule = new OverrideConflictCardRule(OverrideConflictCardRuleKind.ByExactSharedKeySet);
        var options = new PackageConflictGroupingOptions(rule);
        Assert.AreEqual(3, options.MinimumContainedResourceCount);
        Assert.AreEqual(0.5, options.SameModThreshold);
        Assert.AreSame(rule, options.OverrideCardRule);
        Assert.AreEqual(1, new PackageConflictGroupingOptions(1, rule).MinimumContainedResourceCount);
        var input = builder.Prepare(Scan(("a", 2), ("b", 10)), Contents(
            Resource(1, ("a", "same"), ("b", "same")), Resource(2, ("a", "same"), ("b", "same"))));
        Assert.AreEqual(PackageConflictRelationKind.Hidden, builder.Build(input, options).Relations.Single().Kind);
        Assert.AreEqual(PackageConflictRelationKind.BundleContainment,
            builder.Build(input, new(1, rule)).Relations.Single().Kind);
    }

    [TestMethod]
    public void ABIdenticalAndCDifferentDoesNotMakeABAnOverride()
    {
        var input = builder.Prepare(Scan(("a", 10), ("b", 10), ("c", 10)), Contents(Resource(1, ("a", "same"), ("b", "same"), ("c", "other"))));
        var result = builder.Build(input, Options());
        Assert.AreEqual(PackageConflictRelationKind.Hidden, result.Relations.Single(edge => edge.Pair.FirstPath == "a" && edge.Pair.SecondPath == "b").Kind);
        Assert.AreEqual(2, result.Groups.Count);
    }

    [TestMethod]
    public void OneEffectiveFailureExitsWholeFileButKeepsSuccessfulPairFactsAndOriginalReasons()
    {
        var failed = Resource(1, ("a", "x"), ("b", "x"), ("c", null));
        var success = Resource(2, ("a", "x"), ("b", "y"), ("c", "z"));
        var input = builder.Prepare(Scan(("a", 2), ("b", 2), ("c", 2)), Contents(failed, success));
        Assert.AreEqual(1, input.ExcludedFiles.Count);
        Assert.AreSame(failed.Uncompared.Single(), input.ExcludedFiles.Single().FailedResources.Single());
        Assert.AreEqual(1, input.Pairs.Count);
        Assert.AreEqual(1, input.Pairs.Single().IdenticalResourceCount);
        Assert.AreEqual(1, input.Pairs.Single().DifferentResourceCount);
        Assert.AreSame(success, input.Pairs.Single().Resources[1].Comparison);
    }

    [TestMethod]
    public void IndexFailureAndUnstableVersionKeepStructuredEvidence()
    {
        var issue = new PackageConflictScanIssue("bad-index", PackageConflictScanIssueStage.IndexRead, "broken", "test");
        var scan = Scan(("a", 2), ("b", 2)) with { IncompletePackagePaths = ["broken"], Issues = [issue] };
        var contents = Contents(Resource(1, ("a", "same"), ("b", "same")));
        var version = contents.Packages[1] with { IsStable = false, StampAfterRead = null };
        contents = contents with { Packages = [contents.Packages[0], version] };
        var input = builder.Prepare(scan, contents);
        Assert.AreSame(issue, input.ExcludedFiles.Single(file => file.Path == "broken").ScanIssues.Single());
        Assert.AreSame(version, input.ExcludedFiles.Single(file => file.Path == "b").InvalidVersions.Single());
        Assert.AreEqual(0, input.Pairs.Count);
    }

    [TestMethod]
    public void ShuffledReportsProduceStableCardsAndRecalculationDoesNotMutateInput()
    {
        var scan = Scan(("b", 7), ("a", 3), ("c", 7));
        var contents = Contents(Resource(1, ("b", "y"), ("a", "x"), ("c", "y")),
            Resource(2, ("b", "y"), ("a", "x")), Resource(3, ("b", "y"), ("a", "x")));
        var input = builder.Prepare(scan, contents);
        var initial = builder.Build(input, Options());
        var shuffled = builder.Prepare(scan with { ResourceInventories = scan.ResourceInventories.Reverse().ToArray() },
            contents with { Comparisons = contents.Comparisons.Reverse().ToArray(), Packages = contents.Packages.Reverse().ToArray() });
        Assert.AreEqual(Fingerprint(initial), Fingerprint(builder.Build(shuffled, Options())));
        builder.Build(input, Options(5, 0.8, new(OverrideConflictCardRuleKind.ByExactSharedKeySet)));
        Assert.AreEqual(Fingerprint(initial), Fingerprint(builder.Build(input, Options())));
        AssertReadOnly(input.Files);
        AssertReadOnly(input.Pairs);
        AssertReadOnly(input.Comparisons);
        AssertReadOnly(input.ExcludedFiles);
        AssertReadOnly(initial.Relations);
        AssertReadOnly(initial.Groups);
        foreach (var group in initial.Groups)
        {
            AssertReadOnly(group.Members);
            AssertReadOnly(group.ResourceKeys);
            AssertReadOnly(group.Relations);
        }
    }

    [TestMethod]
    public void CancelDuringPrepareOrBuildThrowsInsteadOfPublishingPartialResults()
    {
        var scan = Scan(("a", 3), ("b", 3));
        var contents = Contents(Resource(1, ("a", "x"), ("b", "y")));
        using var cancellation = new CancellationTokenSource();
        var input = builder.Prepare(scan, contents);
        Assert.ThrowsExactly<OperationCanceledException>(() => builder.Prepare(
            scan with { ResourceInventories = new CancelingList<PackageResourceInventory>(scan.ResourceInventories, cancellation) }, contents,
            cancellationToken: cancellation.Token));
        using var buildCancellation = new CancellationTokenSource();
        Assert.ThrowsExactly<OperationCanceledException>(() => builder.Build(
            input with { Pairs = new CancelingList<PackageConflictPairSummary>(input.Pairs, buildCancellation) }, Options(), buildCancellation.Token));
    }

    [TestMethod]
    [DataRow(0.09)]
    [DataRow(1.01)]
    [DataRow(double.NaN)]
    public void InvalidThresholdIsRejected(double threshold) => Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
        builder.Build(builder.Prepare(Scan(), Contents()), Options(threshold: threshold)));

    internal static PackageConflictScanReport Scan(params (string Path, int Count)[] files) =>
        new(DateTime.UnixEpoch, DateTime.UnixEpoch, [], files.Length, files.Length, [], [], [])
        {
            ResourceInventories = files.Select(file => new PackageResourceInventory(file.Path, Stamp, file.Count)
                { ResourceTypeCounts = new Dictionary<uint, int> { [1] = file.Count } }).ToArray(),
        };

    internal static PackageConflictGroupingInput MixedRelationInput(bool contains, ulong sideKey = 2, ulong overrideKey = 1)
    {
        var scan = Scan(("a", contains ? 3 : 4), ("b", contains ? 6 : 4), ("c", 10));
        return new PackageConflictGroupingBuilder().Prepare(scan, Contents(
            Resource(overrideKey, ("a", "same"), ("b", "same"), ("c", "other")),
            Resource(sideKey, ("a", "same"), ("b", "same")), Resource(3, ("a", "same"), ("b", "same"))));
    }

    internal static OverrideConflictCardRule ScopedRule(OverrideConflictCardRuleKind kind) =>
        new(kind, kind == OverrideConflictCardRuleKind.BySharedKeyOverlap ? 0.5 : null);

    internal static ResourceContentComparison Resource(ulong instance, params (string Path, string? Hash)[] values)
    {
        var key = new ResourceKey(1, 0, instance);
        var occurrences = values.Select((value, index) => new ResourceContentOccurrence(key, value.Path, index, Stamp,
            value.Hash, value.Hash is null ? null : 1, value.Hash is null ? new("test-failure", ResourceContentStage.Read, "test") : null)).ToArray();
        return new(key, occurrences.Where(item => item.IsSuccess).GroupBy(item => item.Sha256!)
            .Select(group => new ResourceContentSubgroup(group.Key, 1, group.ToArray())).ToArray(),
            occurrences.Where(item => !item.IsSuccess).ToArray());
    }

    internal static ResourceContentComparisonReport Contents(params ResourceContentComparison[] resources) =>
        new(DateTime.UnixEpoch, DateTime.UnixEpoch, ResourceContentLimits.Default, resources,
            resources.SelectMany(resource => resource.ContentGroups.SelectMany(group => group.Occurrences).Concat(resource.Uncompared))
                .Select(item => item.PackagePath).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new ResourceContentPackageVersion(path, Stamp, Stamp, true)).ToArray(), 0);

    private static string Fingerprint(PackageConflictGroupingResult result) => string.Join(';', result.Groups.Select(group =>
        $"{group.Kind}:{group.BundlePath}:{string.Join(',', group.Members.Select(member => $"{member.Path}:{member.SharedResourceCount}:{member.UniqueResourceCount}"))}:{string.Join(',', group.ResourceKeys)}"));

    private static void AssertReadOnly<T>(IReadOnlyList<T> items) => Assert.ThrowsExactly<NotSupportedException>(() => ((IList<T>)items).Add(default!));

    private sealed class CancelingList<T>(IReadOnlyList<T> values, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => values.Count;
        public T this[int index] => values[index];
        public IEnumerator<T> GetEnumerator()
        {
            foreach (var value in values) { cancellation.Cancel(); yield return value; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
