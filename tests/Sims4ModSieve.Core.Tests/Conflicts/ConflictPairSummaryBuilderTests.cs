using System.Collections;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Scripts;

namespace Sims4ModSieve.Core.Tests.Conflicts;

[TestClass]
public sealed class ConflictPairSummaryBuilderTests
{
    private static readonly FileStamp Stamp = new(100, DateTime.UnixEpoch);
    private static readonly ConflictPairSummaryBuilder Builder = new();

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ToolingComparisonsDoNotAffectContainmentOrPairFacts(bool failed)
    {
        const uint toolingType = 0xF00DBA5E;
        var same = Resource(1, ("a", "same"), ("b", "same"));
        var tooling = Resource(2, ("a", "first"), ("b", failed ? null : "second")) with { Key = new(toolingType, 0, 2) };
        var scan = Scan(("a", 2), ("b", 2)) with
        {
            ToolingMetadataTypes = new HashSet<uint> { toolingType },
            ResourceInventories = new[] { "a", "b" }.Select(path => new PackageResourceInventory(path, Stamp, 2)
            {
                ResourceTypeCounts = new Dictionary<uint, int> { [1] = 1, [toolingType] = 1 },
            }).ToArray(),
        };
        var pair = Build(scan, same, tooling).Single();

        Assert.IsTrue(pair.FirstContainedInSecond && pair.SecondContainedInFirst);
        Assert.AreEqual(1, pair.IdenticalResourceCount);
        Assert.AreEqual(0, pair.DifferentResourceCount);
        Assert.AreEqual(0, pair.IncompleteResourceCount);
        Assert.AreSame(same, pair.Resources.Single().Comparison);
        Assert.IsTrue(scan.ResourceInventories.All(inventory => inventory.ResourceCount == 2));
    }

    [TestMethod]
    public void MissingTypeCountsCannotProveContainmentWhenToolingTypesAreDeclared()
    {
        var scan = Scan(("a", 1), ("b", 1)) with { ToolingMetadataTypes = new HashSet<uint> { 0xF00DBA5E } };
        Assert.AreEqual(0, Build(scan, Resource(1, ("a", "same"), ("b", "same"))).Count);
    }

    [TestMethod]
    public void SeveralResourcesMergeIntoOnePairWithIndependentFactsAndSharedDetails()
    {
        var same = Resource(3, ("b", "x"), ("a", "x"));
        var different = Resource(2, ("a", "x"), ("b", "y"));
        var incomplete = Resource(1, ("b", null), ("a", "x"));
        var pair = Build(Scan(("a", 4), ("b", 4)), same, incomplete, different).Single();

        Assert.AreEqual("a", pair.FirstPath);
        Assert.AreEqual("b", pair.SecondPath);
        Assert.AreEqual(1, pair.IdenticalResourceCount);
        Assert.AreEqual(1, pair.DifferentResourceCount);
        Assert.AreEqual(1, pair.IncompleteResourceCount);
        Assert.IsTrue(pair.HasConfirmedDifference && pair.HasConfirmedIdentical && pair.HasIncomplete);
        Assert.IsTrue(pair.IsSuspectedModVersionPair);
        Assert.AreEqual(ConflictPairPriority.NeedsReview, pair.Priority);
        CollectionAssert.AreEqual(new[] { different, same, incomplete }, pair.Resources.Select(item => item.Comparison).ToArray());
        Assert.AreSame(different, pair.Resources[0].Comparison);
        Assert.AreSame(same.ContentGroups, pair.Resources[1].Comparison.ContentGroups);
        Assert.AreSame(incomplete.Uncompared, pair.Resources[2].Comparison.Uncompared);
        Assert.IsFalse(pair.FirstContainedInSecond || pair.SecondContainedInFirst);
        AssertReadOnly(pair.Resources);
    }

    [TestMethod]
    public void ThreeFilesAreClassifiedPairwiseRatherThanUsingGlobalVerdict()
    {
        var resource = Resource(1, ("c", "other"), ("b", "same"), ("a", "same"));
        Assert.AreEqual(ResourceContentVerdict.Different, resource.Verdict);
        var pairs = Build(Scan(("a", 1), ("b", 1), ("c", 1)), resource);

        Assert.AreEqual(3, pairs.Count);
        Assert.AreEqual(1, Pair(pairs, "a", "c").DifferentResourceCount);
        Assert.AreEqual(1, Pair(pairs, "b", "c").DifferentResourceCount);
        var identical = Pair(pairs, "a", "b");
        Assert.IsFalse(identical.HasConfirmedDifference);
        Assert.IsTrue(identical.FirstContainedInSecond && identical.SecondContainedInFirst);
        Assert.AreSame(resource, identical.Resources.Single().Comparison);
    }

    [TestMethod]
    public void ThirdFileFailureDoesNotInvalidateSuccessfulPairOrItsContainment()
    {
        var resource = Resource(1, ("a", "same"), ("b", "same"), ("c", null));
        var content = Contents(resource);
        content = content with { Packages = content.Packages.Select(version => version.Path == "c"
            ? version with { IsStable = false, StampAfterRead = null } : version).ToArray() };
        var pairs = Builder.BuildPackagePairs(Scan(("a", 1), ("b", 1), ("c", 1)), content);

        var successful = Pair(pairs, "a", "b");
        Assert.IsFalse(successful.HasIncomplete || successful.HasConfirmedDifference);
        Assert.IsTrue(successful.FirstContainedInSecond && successful.SecondContainedInFirst);
        foreach (var failed in pairs.Where(pair => pair.SecondPath == "c"))
        {
            Assert.AreEqual(1, failed.IncompleteResourceCount);
            Assert.IsFalse(failed.HasConfirmedDifference || failed.HasConfirmedIdentical);
            Assert.AreEqual(ConflictPairPriority.Incomplete, failed.Priority);
        }
    }

    [TestMethod]
    public void DifferenceAndIncompleteRemainIndependentWithoutIdenticalOverlap()
    {
        var pair = Build(Scan(), Resource(1, ("a", "x"), ("b", "y")),
            Resource(2, ("a", null), ("b", "y"))).Single();
        Assert.IsTrue(pair.HasConfirmedDifference && pair.HasIncomplete);
        Assert.IsFalse(pair.HasConfirmedIdentical || pair.IsSuspectedModVersionPair);
        Assert.AreEqual(ConflictPairPriority.NeedsReview, pair.Priority);
    }

    [TestMethod]
    public void IdenticalOverlapAndIncompleteAreVisibleAsCanOrganizeWithoutContainment()
    {
        var pair = Build(Scan(("a", 2), ("b", 2)), Resource(1, ("a", "x"), ("b", "x")),
            Resource(2, ("a", "x"), ("b", null))).Single();
        Assert.AreEqual(ConflictPairPriority.CanOrganize, pair.Priority);
        Assert.AreEqual(1, pair.IncompleteResourceCount);
        Assert.IsFalse(pair.FirstContainedInSecond || pair.SecondContainedInFirst);
    }

    [TestMethod]
    public void PureIdenticalPartialOverlapIsHiddenAndUniqueResourcesPreventContainment()
    {
        Assert.AreEqual(0, Build(Scan(("a", 2), ("b", 2)), Resource(1, ("a", "x"), ("b", "x"))).Count);
    }

    [TestMethod]
    public void GroupingEntryKeepsIdenticalPartialOverlapAndFiltersBeforeExpanding()
    {
        var resource = Resource(1, ("a", "x"), ("b", "x"), ("c", "y"));
        var pairs = Builder.BuildGroupingPackagePairs(Contents(resource), new HashSet<uint>(), new HashSet<string> { "C" });
        var pair = pairs.Single();
        Assert.AreEqual("a", pair.FirstPath);
        Assert.AreEqual("b", pair.SecondPath);
        Assert.AreEqual(1, pair.IdenticalResourceCount);
        Assert.AreEqual(0, pair.DifferentResourceCount);
        Assert.AreSame(resource, pair.Resources.Single().Comparison);
        Assert.AreEqual(0, Builder.BuildGroupingPackagePairs(Contents(resource), new HashSet<uint> { 1 }, new HashSet<string>()).Count);
        AssertReadOnly(pairs);
    }

    [TestMethod]
    public void FullResourceCountProvesOnlyTheCoveredDirection()
    {
        var pair = Build(Scan(("a", 1), ("b", 2)), Resource(1, ("a", "x"), ("b", "x"))).Single();
        Assert.IsTrue(pair.FirstContainedInSecond);
        Assert.IsFalse(pair.SecondContainedInFirst);
        Assert.AreEqual(ConflictPairPriority.CanOrganize, pair.Priority);

        var reverse = Build(Scan(("a", 2), ("b", 1)), Resource(1, ("b", "x"), ("a", "x"))).Single();
        Assert.IsFalse(reverse.FirstContainedInSecond);
        Assert.IsTrue(reverse.SecondContainedInFirst);
    }

    [TestMethod]
    public void SeveralOtherFilesCannotJointlyProveContainment()
    {
        var pairs = Build(Scan(("a", 2), ("b", 2), ("c", 2)),
            Resource(1, ("a", "x"), ("b", "x")), Resource(2, ("a", "y"), ("c", "y")));
        Assert.AreEqual(0, pairs.Count);
    }

    [TestMethod]
    public void ContainmentVersionAndInventoryLookupUseWindowsPathEquality()
    {
        var resource = Resource(1, ("A", "x"), ("B", "x"));
        var content = Contents(resource) with
        {
            Packages = [new ResourceContentPackageVersion("a", Stamp, Stamp, true),
                new ResourceContentPackageVersion("b", Stamp, Stamp, true)],
        };
        var pair = Builder.BuildPackagePairs(Scan(("a", 1), ("b", 1)), content).Single();
        Assert.IsTrue(pair.FirstContainedInSecond && pair.SecondContainedInFirst);
    }

    [TestMethod]
    public void EntirelyFailedOverlapIsCountedOncePerKeyOrModule()
    {
        var package = Build(Scan(), Resource(1, ("a", null), ("A", null), ("b", null))).Single();
        Assert.AreEqual(1, package.IncompleteResourceCount);
        Assert.IsFalse(package.HasConfirmedDifference || package.HasConfirmedIdentical);
        Assert.AreEqual(ConflictPairPriority.Incomplete, package.Priority);
        var script = Builder.BuildScriptPairs(Scripts(Module("lib", ("a", null), ("b", null), ("B", null)))).Single();
        Assert.AreEqual(1, script.IncompleteModuleCount);
        Assert.IsFalse(script.HasConfirmedDifference || script.HasConfirmedIdentical);
        Assert.AreEqual(ConflictPairPriority.Incomplete, script.Priority);
    }

    [TestMethod]
    public void TwoDirectionsCanBeProvedAcrossSeveralDistinctResources()
    {
        var pairs = Build(Scan(("a", 2), ("b", 2)),
            Resource(1, ("a", "x"), ("b", "x")), Resource(2, ("b", "y"), ("a", "y")));
        var pair = pairs.Single();
        Assert.AreEqual(2, pair.IdenticalResourceCount);
        Assert.IsTrue(pair.FirstContainedInSecond && pair.SecondContainedInFirst);
        AssertReadOnly(pairs);
    }

    [TestMethod]
    [DataRow("no-inventories")]
    [DataRow("missing-first-inventory")]
    [DataRow("missing-second-inventory")]
    [DataRow("no-versions")]
    [DataRow("missing-second-version")]
    [DataRow("unstable")]
    [DataRow("changed-after-read")]
    [DataRow("missing-after-read")]
    [DataRow("changed-scan-version")]
    [DataRow("changed-inventory")]
    [DataRow("changed-occurrence")]
    [DataRow("conflicting-version")]
    [DataRow("zero-resources")]
    public void InsufficientOrChangedEvidenceNeverProvesContainment(string scenario)
    {
        var scan = Scan(("a", 1), ("b", 1));
        var resource = Resource(1, ("a", "x"), ("b", "x"));
        var content = Contents(resource);
        var changed = Stamp with { Length = 101 };
        switch (scenario)
        {
            case "no-inventories": scan = Scan(); break;
            case "missing-first-inventory": scan = Scan(("b", 1)); break;
            case "missing-second-inventory": scan = Scan(("a", 1)); break;
            case "no-versions": content = content with { Packages = [] }; break;
            case "missing-second-version": content = content with { Packages = [content.Packages[0]] }; break;
            case "unstable": content = content with { Packages = [content.Packages[0] with { IsStable = false }, content.Packages[1]] }; break;
            case "changed-after-read": content = content with { Packages = [content.Packages[0] with { StampAfterRead = changed }, content.Packages[1]] }; break;
            case "missing-after-read": content = content with { Packages = [content.Packages[0] with { StampAfterRead = null }, content.Packages[1]] }; break;
            case "changed-scan-version": content = content with { Packages = [content.Packages[0] with { ScannedStamp = changed }, content.Packages[1]] }; break;
            case "changed-inventory": scan = scan with { ResourceInventories = [scan.ResourceInventories[0] with { ScannedStamp = changed }, scan.ResourceInventories[1]] }; break;
            case "changed-occurrence":
                content = Contents(resource with
                {
                    ContentGroups = [resource.ContentGroups[0] with
                    {
                        Occurrences = [resource.ContentGroups[0].Occurrences[0] with { PackageStamp = changed }, resource.ContentGroups[0].Occurrences[1]],
                    }],
                });
                break;
            case "conflicting-version": content = content with { Packages = [.. content.Packages, content.Packages[0] with { ScannedStamp = changed }] }; break;
            case "zero-resources": scan = Scan(("a", 0), ("b", 0)); break;
        }
        Assert.AreEqual(0, Builder.BuildPackagePairs(scan, content).Count, scenario);
    }

    [TestMethod]
    public void FailureAffectingPairBlocksContainmentDespiteOtherIdenticalResources()
    {
        var pair = Build(Scan(("a", 1), ("b", 1)), Resource(1, ("a", "x"), ("b", "x")),
            Resource(2, ("a", null), ("b", "x"))).Single();
        Assert.IsFalse(pair.FirstContainedInSecond || pair.SecondContainedInFirst);
        Assert.AreEqual(1, pair.IncompleteResourceCount);
    }

    [TestMethod]
    public void PathsReverseOrderAndDuplicateOccurrencesDoNotCreateExtraPairsOrCounts()
    {
        var first = Resource(1, ("B", "x"), ("a", "y"), ("A", "y"));
        var second = Resource(2, ("A", "x"), ("b", "y"));
        var duplicate = Resource(1, ("A", "y"), ("b", "x"));
        var pair = Build(Scan(), first, second, duplicate).Single();
        Assert.AreEqual("A", pair.FirstPath);
        Assert.AreEqual("B", pair.SecondPath);
        Assert.AreEqual(2, pair.DifferentResourceCount);
        Assert.AreEqual(2, pair.Resources.Count);
        Assert.AreEqual(0, Build(Scan(), Resource(1, ("a", "x"), ("A", "y"))).Count);
        Assert.AreEqual(0, Build(Scan(), Resource(1, ("a", "x"))).Count);
    }

    [TestMethod]
    public void PackagePairsAndDetailsUsePriorityThenStablePathsAndNumericTgi()
    {
        var resources = new[]
        {
            Resource(1, ("a", null), ("b", "x")),
            Resource(1, ("y", "x"), ("z", "y")),
            Resource(2, ("c", "x"), ("d", "x")),
            Resource(9, ("g", "x"), ("h", "y")),
            Resource(3, ("g", "x"), ("h", "y")),
            Resource(5, ("e", "x"), ("f", "y")),
        };
        var scan = Scan(("c", 1), ("d", 1));
        var pairs = Build(scan, resources);
        CollectionAssert.AreEqual(new[] { "e", "g", "y", "c", "a" }, pairs.Select(pair => pair.FirstPath).ToArray());
        CollectionAssert.AreEqual(new ulong[] { 3, 9 }, Pair(pairs, "g", "h").Resources.Select(item => item.Key.Instance).ToArray());
        var reversed = Build(scan, resources.Reverse().ToArray());
        CollectionAssert.AreEqual(pairs.Select(PackageFingerprint).ToArray(), reversed.Select(PackageFingerprint).ToArray());
    }

    [TestMethod]
    public void ResourceDetailsSortByNumericTypeGroupThenInstance()
    {
        ResourceContentComparison WithKey(ResourceKey key)
        {
            var original = Resource(key.Instance, ("a", "x"), ("b", "y"));
            return original with
            {
                Key = key,
                ContentGroups = original.ContentGroups.Select(group => group with
                {
                    Occurrences = group.Occurrences.Select(occurrence => occurrence with { Key = key }).ToArray(),
                }).ToArray(),
            };
        }
        ResourceKey[] keys = [new(2, 0, 0), new(1, 2, 0), new(1, 1, 9), new(1, 1, 3)];
        var pair = Build(Scan(), keys.Select(WithKey).ToArray()).Single();
        CollectionAssert.AreEqual(keys.Reverse().ToArray(), pair.Resources.Select(resource => resource.Key).ToArray());
    }

    [TestMethod]
    public void ManyParticipantsShareOriginalDetailAndProduceEachUnorderedPairOnce()
    {
        var values = Enumerable.Range(0, 60).Select(index => ($"{index:D3}", (string?)$"hash-{index}")).ToArray();
        var resource = Resource(1, values);
        var packagePairs = Build(Scan(), resource);
        Assert.AreEqual(60 * 59 / 2, packagePairs.Count);
        Assert.IsTrue(packagePairs.All(pair => pair.FirstPath.CompareTo(pair.SecondPath) < 0
            && ReferenceEquals(resource, pair.Resources.Single().Comparison)));
        var module = Module("shared", values);
        var scriptPairs = Builder.BuildScriptPairs(Scripts(module));
        Assert.AreEqual(60 * 59 / 2, scriptPairs.Count);
        Assert.IsTrue(scriptPairs.All(pair => ReferenceEquals(module, pair.Modules.Single().Collision)));
    }

    [TestMethod]
    public void ScriptPairsMergeAcrossTopLevelPackagesAndKeepCaseSensitiveModuleNames()
    {
        var one = Module("Alpha.one", ("b", "x"), ("a", "y"));
        var two = Module("alpha.one", ("A", "y"), ("B", "x"));
        var same = Module("other.two", ("a", "x"), ("b", "x"));
        var failed = Module("third.three", ("b", null), ("a", "x"));
        var pair = Builder.BuildScriptPairs(Scripts(same, two, failed, one, one)).Single();
        Assert.AreEqual("A", pair.FirstPath);
        Assert.AreEqual("B", pair.SecondPath);
        Assert.AreEqual(2, pair.DifferentModuleCount);
        Assert.AreEqual(1, pair.IdenticalModuleCount);
        Assert.AreEqual(1, pair.IncompleteModuleCount);
        Assert.IsTrue(pair.HasConfirmedDifference && pair.HasConfirmedIdentical && pair.HasIncomplete);
        Assert.IsTrue(pair.IsSuspectedModVersionPair);
        Assert.AreEqual(ConflictPairPriority.NeedsReview, pair.Priority);
        CollectionAssert.AreEqual(new[] { "Alpha.one", "alpha.one", "other.two", "third.three" }, pair.Modules.Select(item => item.ModuleName).ToArray());
        Assert.AreSame(one, pair.Modules[0].Collision);
        Assert.AreSame(failed.Uncompared, pair.Modules[3].Collision.Uncompared);
        AssertReadOnly(pair.Modules);
    }

    [TestMethod]
    public void ScriptThirdFileDifferenceAndFailureAreOnlyAssignedToAffectedPairs()
    {
        var difference = Module("lib.one", ("a", "x"), ("b", "x"), ("c", "y"));
        var failure = Module("other.one", ("a", "x"), ("b", "x"), ("c", null));
        var pairs = Builder.BuildScriptPairs(Scripts(difference, failure));
        Assert.AreEqual(2, pairs.Count);
        foreach (var pair in pairs)
        {
            Assert.AreEqual("c", pair.SecondPath);
            Assert.AreEqual(1, pair.DifferentModuleCount);
            Assert.AreEqual(1, pair.IncompleteModuleCount);
            Assert.AreEqual(0, pair.IdenticalModuleCount);
            Assert.AreSame(difference, pair.Modules[0].Collision);
            Assert.AreSame(failure, pair.Modules[1].Collision);
        }
    }

    [TestMethod]
    public void ScriptPureIdenticalPairsAndSelfPairsAreHidden()
    {
        Assert.AreEqual(0, Builder.BuildScriptPairs(Scripts(Module("lib.one", ("a", "x"), ("b", "x")),
            Module("other.one", ("b", "y"), ("a", "y")))).Count);
        Assert.AreEqual(0, Builder.BuildScriptPairs(Scripts(Module("lib.one", ("a", "x"), ("A", "y")))).Count);
    }

    [TestMethod]
    public void ScriptPairsSortByPriorityThenPathsAndPreserveIncompleteWithIdentical()
    {
        var modules = new[]
        {
            Module("z", ("a", null), ("b", "x")),
            Module("same", ("c", "x"), ("d", "x")),
            Module("failed", ("d", null), ("c", "x")),
            Module("z", ("g", "x"), ("h", "y")),
            Module("a", ("h", "x"), ("g", "y")),
            Module("z", ("e", "x"), ("f", "y")),
        };
        var pairs = Builder.BuildScriptPairs(Scripts(modules));
        CollectionAssert.AreEqual(new[] { "e", "g", "c", "a" }, pairs.Select(pair => pair.FirstPath).ToArray());
        var mixed = pairs[2];
        Assert.AreEqual(ConflictPairPriority.CanOrganize, mixed.Priority);
        Assert.IsTrue(mixed.HasIncomplete && mixed.HasConfirmedIdentical);
        CollectionAssert.AreEqual(new[] { "same", "failed" }, mixed.Modules.Select(item => item.ModuleName).ToArray());
        CollectionAssert.AreEqual(new[] { "a", "z" }, pairs[1].Modules.Select(item => item.ModuleName).ToArray());
        var reversed = Builder.BuildScriptPairs(Scripts(modules.Reverse().ToArray()));
        CollectionAssert.AreEqual(pairs.Select(ScriptFingerprint).ToArray(), reversed.Select(ScriptFingerprint).ToArray());
        AssertReadOnly(pairs);
    }

    [TestMethod]
    public void BothBuildersRespondToCancellationBeforeStartingEvenWithEmptyReports()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => Builder.BuildPackagePairs(Scan(), Contents(), cancellation.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => Builder.BuildScriptPairs(Scripts(), cancellation.Token));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PackageBuilderRespondsToCancellationDuringTraversal(bool secondPass)
    {
        using var cancellation = new CancellationTokenSource();
        var resource = Resource(1, Enumerable.Range(0, 200).Select(index => ($"{index:D4}", (string?)"x")).ToArray());
        var group = resource.ContentGroups[0];
        resource = resource with { ContentGroups = [group with
        {
            Occurrences = new CancelingList<ResourceContentOccurrence>(group.Occurrences, cancellation, secondPass ? 250 : 50),
        }] };
        var contents = Contents() with { Comparisons = [resource] };
        Assert.ThrowsExactly<OperationCanceledException>(() => Builder.BuildPackagePairs(Scan(), contents, cancellation.Token));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ScriptBuilderRespondsToCancellationDuringTraversal(bool secondPass)
    {
        using var cancellation = new CancellationTokenSource();
        var collision = Module("many", Enumerable.Range(0, 200).Select(index => ($"{index:D4}", (string?)"x")).ToArray());
        var group = collision.ContentGroups[0];
        collision = collision with { ContentGroups = [group with
        {
            Occurrences = new CancelingList<ScriptModuleOccurrence>(group.Occurrences, cancellation, secondPass ? 250 : 50),
        }] };
        Assert.ThrowsExactly<OperationCanceledException>(() => Builder.BuildScriptPairs(Scripts(collision), cancellation.Token));
    }

    [TestMethod]
    public void LargeVisiblePairInputCanBeCanceledDuringConstruction()
    {
        var values = Enumerable.Range(0, 1000).Select(index => ($"{index:D4}", (string?)$"hash-{index}")).ToArray();
        using var packageCancellation = new CancellationTokenSource();
        var resource = Resource(1, values);
        resource = resource with { ContentGroups = new CancelingList<ResourceContentSubgroup>(resource.ContentGroups,
            packageCancellation, 1001) };
        var contents = Contents() with { Comparisons = [resource] };
        Assert.ThrowsExactly<OperationCanceledException>(() => Builder.BuildPackagePairs(Scan(), contents, packageCancellation.Token));

        using var scriptCancellation = new CancellationTokenSource();
        var collision = Module("many", values);
        collision = collision with { ContentGroups = new CancelingList<ScriptModuleContentGroup>(collision.ContentGroups,
            scriptCancellation, 1001) };
        Assert.ThrowsExactly<OperationCanceledException>(() => Builder.BuildScriptPairs(Scripts(collision), scriptCancellation.Token));
    }

    [TestMethod]
    public void FullyHiddenScriptPairsAllocateLessThan64MiB()
    {
        var values = Enumerable.Range(0, 500).Select(index => ($"{index:D3}", (string?)"same")).ToArray();
        var report = Scripts(Enumerable.Range(0, 10).Select(index => Module($"shared.module{index}", values)).ToArray());
        Assert.AreEqual(0, Builder.BuildScriptPairs(report).Count);
        var stopwatch = new Stopwatch();
        var before = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Start();
        var pairs = Builder.BuildScriptPairs(report);
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0, pairs.Count);
        Assert.IsTrue(allocated < 64L * 1024 * 1024, $"Allocated {allocated} bytes");
        Console.WriteLine($"Scripts: allocated={allocated} bytes, elapsed={stopwatch.Elapsed.TotalMilliseconds:F3} ms");
    }

    [TestMethod]
    public void FullyHiddenPackagePairsAllocateLessThan64MiB()
    {
        var values = Enumerable.Range(0, 500).Select(index => ($"{index:D3}", (string?)"same")).ToArray();
        var scan = Scan(values.Select(value => (value.Item1, 11)).ToArray());
        var report = Contents(Enumerable.Range(0, 10).Select(index => Resource((ulong)index, values)).ToArray());
        Assert.AreEqual(0, Builder.BuildPackagePairs(scan, report).Count);
        var stopwatch = new Stopwatch();
        var before = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Start();
        var pairs = Builder.BuildPackagePairs(scan, report);
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0, pairs.Count);
        Assert.IsTrue(allocated < 64L * 1024 * 1024, $"Allocated {allocated} bytes");
        Console.WriteLine($"Packages: allocated={allocated} bytes, elapsed={stopwatch.Elapsed.TotalMilliseconds:F3} ms");
    }

    [TestMethod]
    public void DistributedPackageCoverageAllocatesLessThan64MiBWithoutContainment()
    {
        var (scan, report) = SparsePackageReports(shiftedBatch: true);
        Assert.AreEqual(0, Builder.BuildPackagePairs(scan, report).Count);
        var stopwatch = new Stopwatch();
        var before = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Start();
        var pairs = Builder.BuildPackagePairs(scan, report);
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0, pairs.Count);
        Assert.IsTrue(allocated < 64L * 1024 * 1024, $"Allocated {allocated} bytes");
        Console.WriteLine($"Distributed coverage: pairs={pairs.Count}, allocated={allocated} bytes, elapsed={stopwatch.Elapsed.TotalMilliseconds:F3} ms");
    }

    [TestMethod]
    public void SparsePackageContainmentAllocatesLessThan64MiBAndRetainsEveryPair()
    {
        var (scan, report) = SparsePackageReports(shiftedBatch: false);
        Assert.AreEqual(250, Builder.BuildPackagePairs(scan, report).Count);
        var stopwatch = new Stopwatch();
        var before = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Start();
        var pairs = Builder.BuildPackagePairs(scan, report);
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(250, pairs.Count);
        Assert.IsTrue(allocated < 64L * 1024 * 1024, $"Allocated {allocated} bytes");
        for (var index = 0; index < pairs.Count; index++)
        {
            var pair = pairs[index];
            Assert.AreEqual($"{index * 2:D3}", pair.FirstPath);
            Assert.AreEqual($"{index * 2 + 1:D3}", pair.SecondPath);
            Assert.IsTrue(pair.FirstContainedInSecond && pair.SecondContainedInFirst);
            Assert.AreEqual(11, pair.IdenticalResourceCount);
            Assert.AreEqual(11, pair.Resources.Count);
            Assert.IsFalse(pair.HasConfirmedDifference || pair.HasIncomplete);
            for (var resource = 0; resource < 10; resource++)
                Assert.AreSame(report.Comparisons[resource], pair.Resources[resource].Comparison);
            Assert.AreSame(report.Comparisons[10 + index], pair.Resources[10].Comparison);
        }
        Console.WriteLine($"Sparse containment: pairs={pairs.Count}, allocated={allocated} bytes, elapsed={stopwatch.Elapsed.TotalMilliseconds:F3} ms");
    }

    [TestMethod]
    public void ContainmentIntersectionUnionsDuplicateTgiRowsWithoutDoubleCounting()
    {
        var first = Resource(1, ("a", "x"), ("b", "x"));
        var duplicate = Resource(1, ("A", "x"), ("c", "x"));
        var second = Resource(2, ("a", "y"), ("b", "y"), ("c", "y"));
        var scan = Scan(("a", 2), ("b", 3), ("c", 3));
        var pairs = Build(scan, first, duplicate, second);
        Assert.AreEqual(2, pairs.Count);
        foreach (var pair in pairs)
        {
            Assert.AreEqual("A", pair.FirstPath);
            Assert.IsTrue(pair.FirstContainedInSecond);
            Assert.IsFalse(pair.SecondContainedInFirst);
            Assert.AreEqual(2, pair.IdenticalResourceCount);
            Assert.AreEqual(2, pair.Resources.Count);
        }
        Assert.AreSame(first, Pair(pairs, "A", "b").Resources[0].Comparison);
        Assert.AreSame(duplicate, Pair(pairs, "A", "c").Resources[0].Comparison);
        CollectionAssert.AreEqual(pairs.Select(PackageFingerprint).ToArray(),
            Build(scan, second, duplicate, first).Select(PackageFingerprint).ToArray());
    }

    [TestMethod]
    public void LatePackageDifferenceOrFailureKeepsEarlierDetailsAndMultipleContainmentPeers()
    {
        var first = Resource(1, ("a", "x"), ("b", "x"), ("c", "x"), ("d", "x"), ("e", "x"));
        var later = Resource(2, ("a", "y"), ("b", "y"), ("c", "y"), ("d", "z"), ("e", null));
        var scan = Scan(("a", 2), ("b", 3), ("c", 3), ("d", 2), ("e", 2));
        var pairs = Build(scan, first, later);
        Assert.AreEqual(9, pairs.Count);
        foreach (var peer in new[] { "b", "c" })
        {
            var contained = Pair(pairs, "a", peer);
            Assert.IsTrue(contained.FirstContainedInSecond);
            Assert.IsFalse(contained.SecondContainedInFirst || contained.HasConfirmedDifference || contained.HasIncomplete);
            Assert.AreEqual(2, contained.IdenticalResourceCount);
        }
        var different = Pair(pairs, "a", "d");
        Assert.IsTrue(different.IsSuspectedModVersionPair);
        Assert.AreEqual(1, different.IdenticalResourceCount);
        Assert.AreSame(first, different.Resources.Single(match => match.HasConfirmedIdentical).Comparison);
        var failed = Pair(pairs, "a", "e");
        Assert.AreEqual(1, failed.IdenticalResourceCount);
        Assert.AreEqual(1, failed.IncompleteResourceCount);
        Assert.AreSame(first, failed.Resources.Single(match => match.HasConfirmedIdentical).Comparison);
        CollectionAssert.AreEqual(pairs.Select(PackageFingerprint).ToArray(),
            Build(scan, later, first).Select(PackageFingerprint).ToArray());
    }

    [TestMethod]
    public void LateScriptDifferenceOrFailureKeepsEarlierIdenticalModules()
    {
        var earlier = Module("shared", ("a", "x"), ("b", "x"), ("c", "x"), ("d", "x"));
        var different = Module("other.different", ("a", "x"), ("c", "y"));
        var failed = Module("third.failed", ("b", "x"), ("d", null));
        var pairs = Builder.BuildScriptPairs(Scripts(earlier, different, failed));
        Assert.AreEqual(2, pairs.Count);
        foreach (var pair in pairs)
        {
            Assert.AreEqual(1, pair.IdenticalModuleCount);
            Assert.AreSame(earlier, pair.Modules.Single(match => match.HasConfirmedIdentical).Collision);
        }
        Assert.IsTrue(pairs[0].IsSuspectedModVersionPair);
        Assert.AreEqual(1, pairs[1].IncompleteModuleCount);
        CollectionAssert.AreEqual(pairs.Select(ScriptFingerprint).ToArray(),
            Builder.BuildScriptPairs(Scripts(failed, different, earlier)).Select(ScriptFingerprint).ToArray());
    }

    private static (PackageConflictScanReport Scan, ResourceContentComparisonReport Report) SparsePackageReports(bool shiftedBatch)
    {
        var values = Enumerable.Range(0, 500).Select(index => ($"{index:D3}", (string?)"same")).ToArray();
        var comparisons = Enumerable.Range(0, 10).Select(index => Resource((ulong)index, values)).ToList();
        for (var index = 0; index < 500; index += 2)
        {
            comparisons.Add(Resource((ulong)(10 + index / 2), values[index], values[index + 1]));
            if (shiftedBatch)
                comparisons.Add(Resource((ulong)(260 + index / 2), values[index + 1], values[(index + 2) % 500]));
        }
        return (Scan(values.Select(value => (value.Item1, shiftedBatch ? 12 : 11)).ToArray()), Contents(comparisons.ToArray()));
    }

    private static PackageConflictScanReport Scan(params (string Path, int Count)[] packages) =>
        new(DateTime.UnixEpoch, DateTime.UnixEpoch, [], packages.Length, packages.Length, [], [], [])
        {
            ResourceInventories = packages.Select(package => new PackageResourceInventory(package.Path, Stamp, package.Count)).ToArray(),
        };

    private static ResourceContentComparison Resource(ulong instance, params (string Path, string? Hash)[] values)
    {
        var key = new ResourceKey(1, 0, instance);
        var occurrences = values.Select((value, ordinal) => new ResourceContentOccurrence(key, value.Path, ordinal,
            Stamp, value.Hash, value.Hash is null ? null : 1,
            value.Hash is null ? new ResourceContentIssue("test-failure", ResourceContentStage.Read, "test") : null)).ToArray();
        return new ResourceContentComparison(key,
            occurrences.Where(item => item.IsSuccess).GroupBy(item => item.Sha256!)
                .Select(group => new ResourceContentSubgroup(group.Key, 1, group.ToArray())).ToArray(),
            occurrences.Where(item => !item.IsSuccess).ToArray());
    }

    private static ResourceContentComparisonReport Contents(params ResourceContentComparison[] comparisons) =>
        new(DateTime.UnixEpoch, DateTime.UnixEpoch, ResourceContentLimits.Default, comparisons,
            comparisons.SelectMany(comparison => comparison.ContentGroups.SelectMany(group => group.Occurrences).Concat(comparison.Uncompared))
                .Select(occurrence => occurrence.PackagePath).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new ResourceContentPackageVersion(path, Stamp, Stamp, true)).ToArray(), 0);

    private static IReadOnlyList<PackageConflictPairSummary> Build(PackageConflictScanReport scan,
        params ResourceContentComparison[] comparisons) => Builder.BuildPackagePairs(scan, Contents(comparisons));

    private static PackageConflictPairSummary Pair(IReadOnlyList<PackageConflictPairSummary> pairs, string first, string second) =>
        pairs.Single(pair => pair.FirstPath == first && pair.SecondPath == second);

    private static ScriptModuleCollision Module(string name, params (string Path, string? Hash)[] values)
    {
        var occurrences = values.Select(value => new ScriptModuleOccurrence(name, value.Path, Stamp, [], $"{name}.py",
            ScriptModuleKind.Module, ScriptModuleFormat.Source, value.Hash, value.Hash is null ? null : 1,
            value.Hash is null ? new ScriptArchiveIssue("test-failure", ScriptArchiveIssueStage.Entry, value.Path, "test") : null)).ToArray();
        return new ScriptModuleCollision(name,
            occurrences.Where(item => item.IsSuccess).GroupBy(item => item.Sha256!)
                .Select(group => new ScriptModuleContentGroup(group.Key, group.ToArray())).ToArray(),
            occurrences.Where(item => !item.IsSuccess).ToArray());
    }

    private static ScriptModuleScanReport Scripts(params ScriptModuleCollision[] collisions) =>
        new(DateTime.UnixEpoch, DateTime.UnixEpoch, [], ScriptModuleLimits.Default, 0, 0, [], 0, collisions, [], 0);

    private static string PackageFingerprint(PackageConflictPairSummary pair) =>
        $"{pair.FirstPath}|{pair.SecondPath}|{pair.Priority}|{string.Join(',', pair.Resources.Select(item => $"{item.Key}:{item.Priority}"))}";

    private static string ScriptFingerprint(ScriptConflictPairSummary pair) =>
        $"{pair.FirstPath}|{pair.SecondPath}|{pair.Priority}|{string.Join(',', pair.Modules.Select(item => $"{item.ModuleName}:{item.Priority}"))}";

    private static void AssertReadOnly<T>(IReadOnlyList<T> items)
    {
        Assert.IsFalse(items is T[] || items is List<T>);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<T>)items).Add(default!));
    }

    private sealed class CancelingList<T>(IReadOnlyList<T> items, CancellationTokenSource cancellation, int cancelAt) : IReadOnlyList<T>
    {
        private int _visits;
        public int Count => items.Count;
        public T this[int index]
        {
            get
            {
                Visit();
                return items[index];
            }
        }
        public IEnumerator<T> GetEnumerator()
        {
            foreach (var item in items)
            {
                Visit();
                yield return item;
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private void Visit()
        {
            if (++_visits == cancelAt) cancellation.Cancel();
        }
    }
}
