using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Packages;

namespace Sims4ModSieve.Core.Tests.Conflicts;

[TestClass]
public sealed class ConflictIgnoreKeyBuilderTests
{
    private static readonly ResourceKey First = new(1, 2, 3);
    private static readonly ResourceKey Second = new(4, 5, 6);

    [TestMethod]
    public void KeyIsStableUnderOrderingDuplicateRecordsAndWindowsPathCase()
    {
        var expected = ConflictIgnoreKeyBuilder.Build(["folder/a.package", "B.package"], [First, Second]);
        Assert.AreEqual(expected, ConflictIgnoreKeyBuilder.Build(["b.PACKAGE", "FOLDER\\A.package", "B.package"], [Second, First, First]));
        Assert.AreEqual(64, expected.Length);
    }

    [TestMethod]
    public void FileAndTgiAdditionOrRemovalChangesKeyAndEncodingHasNoDelimiterAmbiguity()
    {
        var original = ConflictIgnoreKeyBuilder.Build(["a", "b"], [First, Second]);
        Assert.AreNotEqual(original, ConflictIgnoreKeyBuilder.Build(["a"], [First, Second]));
        Assert.AreNotEqual(original, ConflictIgnoreKeyBuilder.Build(["a", "b", "c"], [First, Second]));
        Assert.AreNotEqual(original, ConflictIgnoreKeyBuilder.Build(["a", "b"], [First]));
        Assert.AreNotEqual(original, ConflictIgnoreKeyBuilder.Build(["a", "b"], [First, Second, new(7, 8, 9)]));
        Assert.AreNotEqual(ConflictIgnoreKeyBuilder.Build(["a|b", "c"], [First]), ConflictIgnoreKeyBuilder.Build(["a", "b|c"], [First]));
    }

    [TestMethod]
    public void ContentTimeSizeAndClassificationChangesDoNotChangeIdentity()
    {
        var builder = new PackageConflictGroupingBuilder();
        var scan = PackageConflictGroupingBuilderTests.Scan(("a", 10), ("b", 10));
        var resource = PackageConflictGroupingBuilderTests.Resource(1, ("a", "x"), ("b", "y"));
        var initial = builder.Build(builder.Prepare(scan, PackageConflictGroupingBuilderTests.Contents(resource)),
            PackageConflictGroupingBuilderTests.Options()).Groups.Single();
        var stamp = PackageConflictGroupingBuilderTests.Stamp with { Length = 200, LastWriteTimeUtc = DateTime.UnixEpoch.AddDays(1) };
        scan = scan with { ResourceInventories = scan.ResourceInventories.Select(file => file with { ScannedStamp = stamp }).ToArray() };
        var changed = PackageConflictGroupingBuilderTests.Resource(1, ("a", "new-x"), ("b", "new-y"));
        changed = changed with { ContentGroups = changed.ContentGroups.Select(group => group with
            { Occurrences = group.Occurrences.Select(item => item with { PackageStamp = stamp }).ToArray() }).ToArray() };
        var contents = PackageConflictGroupingBuilderTests.Contents(changed);
        contents = contents with { Packages = contents.Packages.Select(version => version with { ScannedStamp = stamp, StampAfterRead = stamp }).ToArray() };
        var updated = builder.Build(builder.Prepare(scan, contents), PackageConflictGroupingBuilderTests.Options(threshold: 0.1)).Groups.Single();
        Assert.AreNotEqual(initial.Kind, updated.Kind);
        Assert.AreEqual(ConflictIgnoreKeyBuilder.Build(initial.Members.Select(member => member.Path), initial.ResourceKeys),
            ConflictIgnoreKeyBuilder.Build(updated.Members.Select(member => member.Path), updated.ResourceKeys));
    }

    [TestMethod]
    public void AbsolutePathsAreRejected() => Assert.ThrowsExactly<ArgumentException>(() => ConflictIgnoreKeyBuilder.Build(["C:\\Mods\\a.package"], [First]));

    [TestMethod]
    [DataRow(false, OverrideConflictCardRuleKind.ByExactSharedKeySet)]
    [DataRow(false, OverrideConflictCardRuleKind.BySharedKeyOverlap)]
    [DataRow(true, OverrideConflictCardRuleKind.ByExactSharedKeySet)]
    [DataRow(true, OverrideConflictCardRuleKind.BySharedKeyOverlap)]
    public void SideRelationKeyChangesOnlyYellowIgnoreIdentity(bool contains, OverrideConflictCardRuleKind rule)
    {
        var builder = new PackageConflictGroupingBuilder();
        var options = new PackageConflictGroupingOptions(PackageConflictGroupingBuilderTests.ScopedRule(rule));
        var initial = builder.Build(PackageConflictGroupingBuilderTests.MixedRelationInput(contains), options);
        var changed = builder.Build(PackageConflictGroupingBuilderTests.MixedRelationInput(contains, sideKey: 4), options);
        var initialRed = initial.Groups.Single(group => group.Kind == PackageConflictRelationKind.OverrideConflict);
        var changedRed = changed.Groups.Single(group => group.Kind == PackageConflictRelationKind.OverrideConflict);
        var initialYellow = initial.Groups.Single(group => group.Kind != PackageConflictRelationKind.OverrideConflict);
        var changedYellow = changed.Groups.Single(group => group.Kind != PackageConflictRelationKind.OverrideConflict);
        Assert.AreEqual(Key(initialRed), Key(changedRed));
        Assert.AreNotEqual(Key(initialYellow), Key(changedYellow));
        CollectionAssert.AreEqual(new[] { new ResourceKey(1, 0, 1), new ResourceKey(1, 0, 2), new ResourceKey(1, 0, 3) },
            initialYellow.ResourceKeys.ToArray());
        Assert.AreEqual(1, initialRed.ResourceKeys.Count);
    }

    [TestMethod]
    public void OverrideBasisOrMemberChangesAlterIgnoreIdentity()
    {
        var builder = new PackageConflictGroupingBuilder();
        var options = new PackageConflictGroupingOptions(new(OverrideConflictCardRuleKind.ByExactSharedKeySet));
        var initial = builder.Build(PackageConflictGroupingBuilderTests.MixedRelationInput(false), options)
            .Groups.Single(group => group.Kind == PackageConflictRelationKind.OverrideConflict);
        var changedKeys = builder.Build(PackageConflictGroupingBuilderTests.MixedRelationInput(false, overrideKey: 5), options)
            .Groups.Single(group => group.Kind == PackageConflictRelationKind.OverrideConflict);
        Assert.AreNotEqual(Key(initial), Key(changedKeys));
        var input = builder.Prepare(PackageConflictGroupingBuilderTests.Scan(("a", 4), ("b", 4), ("c", 10), ("d", 10)),
            PackageConflictGroupingBuilderTests.Contents(
                PackageConflictGroupingBuilderTests.Resource(1, ("a", "same"), ("b", "same"), ("c", "other"), ("d", "fourth")),
                PackageConflictGroupingBuilderTests.Resource(2, ("a", "same"), ("b", "same")),
                PackageConflictGroupingBuilderTests.Resource(3, ("a", "same"), ("b", "same"))));
        var changedMembers = builder.Build(input, options).Groups.Single(group => group.Kind == PackageConflictRelationKind.OverrideConflict);
        CollectionAssert.AreEqual(initial.ResourceKeys.ToArray(), changedMembers.ResourceKeys.ToArray());
        Assert.AreNotEqual(Key(initial), Key(changedMembers));
    }

    private static string Key(PackageConflictGroup group) =>
        ConflictIgnoreKeyBuilder.Build(group.Members.Select(member => member.Path), group.ResourceKeys);
}
