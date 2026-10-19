using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Tests;
using Sims4ModSieve.Core.Tests.Packages;

namespace Sims4ModSieve.Packages.Tests;

/// <summary>
/// 候选冲突扫描接上真实读取器，跑自造的 DBPF fixture。不使用任何真实玩家 Mod。
/// </summary>
[TestClass]
public sealed class ConflictScanIntegrationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegacyContainmentUsesGameResourcesAndStillRejectsUniqueResources(bool hasUniqueResource)
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var small = new DbpfFixtureBuilder().AddContent([1], type: 0x7FB6AD8A, instanceLo: 0);
        var bundle = new DbpfFixtureBuilder().AddContent([2], type: 0x7FB6AD8A, instanceLo: 0);
        for (uint instance = 1; instance <= 3; instance++)
        {
            small.AddContent([(byte)instance], instanceLo: instance);
            bundle.AddContent([(byte)instance], instanceLo: instance);
        }
        if (hasUniqueResource) small.AddContent([5], instanceLo: 5);
        bundle.AddContent([4], instanceLo: 4);
        var smallPath = temp.WriteBytes(Path.Combine("Mods", "a.package"), small.Build());
        temp.WriteBytes(Path.Combine("Mods", "b.package"), bundle.Build());
        var scan = await new PackageConflictScanner(new PhysicalFileSystemAccess(), LlamaLogicPackageIndexReader.CreateDefault())
            .ScanAsync(new([new ScanSource("mods", mods)]));
        var contents = await new ResourceContentComparer(DbpfResourceContentHasher.CreateDefault()).CompareAsync(new(scan.Candidates));
        var pairs = new ConflictPairSummaryBuilder().BuildPackagePairs(scan, contents);

        Assert.AreEqual(hasUniqueResource ? 5 : 4, scan.ResourceInventories.Single(file => file.Path == smallPath).ResourceCount);
        Assert.AreEqual(3, contents.Comparisons.Count);
        if (hasUniqueResource)
            Assert.AreEqual(0, pairs.Count);
        else
        {
            var pair = pairs.Single();
            Assert.AreEqual(smallPath, pair.FirstPath);
            Assert.IsTrue(pair.FirstContainedInSecond);
            Assert.IsFalse(pair.SecondContainedInFirst);
            Assert.AreEqual(3, pair.IdenticalResourceCount);
        }
    }

    [TestMethod]
    [DataRow(0x7FB6AD8Au)]
    [DataRow(0x6BF15BBEu)]
    [DataRow(0x01357924u)]
    public async Task ToolingOnlyCollisionsProduceNoComparisonsOrConflictGroups(uint toolingType)
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        temp.WriteBytes(Path.Combine("Mods", "a.package"), new DbpfFixtureBuilder()
            .AddContent([1, 2], compression: DbpfFixtureBuilder.CompressionZlib, declaredSize: 20, type: toolingType, instanceLo: 0).Build());
        temp.WriteBytes(Path.Combine("Mods", "b.package"), new DbpfFixtureBuilder()
            .AddContent([3, 4], compression: DbpfFixtureBuilder.CompressionZlib, declaredSize: 20, type: toolingType, instanceLo: 0).Build());
        var scan = await new PackageConflictScanner(new PhysicalFileSystemAccess(), LlamaLogicPackageIndexReader.CreateDefault())
            .ScanAsync(new([new ScanSource("mods", mods)]));
        var contents = await new ResourceContentComparer(DbpfResourceContentHasher.CreateDefault()).CompareAsync(new(scan.Candidates));
        var builder = new PackageConflictGroupingBuilder();
        var input = builder.Prepare(scan, contents);
        var result = builder.Build(input, new PackageConflictGroupingOptions());

        Assert.AreEqual(2, scan.AnalyzedPackageCount);
        Assert.AreEqual(0, scan.Candidates.Count);
        Assert.IsTrue(scan.ResourceInventories.All(item => item.ResourceCount == 1));
        Assert.AreEqual(0, contents.Comparisons.Count);
        Assert.AreEqual(0, contents.Packages.Count);
        Assert.AreEqual(0L, contents.ProcessedBytes);
        Assert.IsTrue(input.Files.All(file => file.ResourceCount == 0));
        Assert.AreEqual(0, input.ExcludedFiles.Count);
        Assert.AreEqual(0, result.Groups.Count);
        Assert.AreEqual(0, new ConflictPairSummaryBuilder().BuildPackagePairs(scan, contents).Count);
    }

    [TestMethod]
    [DataRow(0x034AEECBu)]
    [DataRow(0xF00DBA5Eu)]
    public async Task MixedToolingAndResourceCollisionsOnlyCompareAndCountTheResource(uint resourceType)
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        foreach (var (name, content) in new[] { ("a.package", (byte)1), ("b.package", (byte)2) })
        {
            temp.WriteBytes(Path.Combine("Mods", name), new DbpfFixtureBuilder()
                .AddContent([content], type: 0x7FB6AD8A, instanceLo: 0)
                .AddContent([content], type: 0x6BF15BBE, instanceLo: 0)
                .AddContent([content], type: 0x01357924, instanceLo: 0)
                .AddContent([content], type: resourceType, instanceLo: 1).Build());
        }
        var scan = await new PackageConflictScanner(new PhysicalFileSystemAccess(), LlamaLogicPackageIndexReader.CreateDefault())
            .ScanAsync(new([new ScanSource("mods", mods)]));
        var contents = await new ResourceContentComparer(DbpfResourceContentHasher.CreateDefault()).CompareAsync(new(scan.Candidates));
        var builder = new PackageConflictGroupingBuilder();
        var input = builder.Prepare(scan, contents);
        var result = builder.Build(input, new PackageConflictGroupingOptions());

        Assert.AreEqual(resourceType, scan.Candidates.Single().Key.Type);
        Assert.IsTrue(scan.Candidates.Single().Occurrences.All(item => item.Ordinal == 3));
        Assert.IsTrue(scan.ResourceInventories.All(item => item.ResourceCount == 4));
        Assert.AreEqual(resourceType, contents.Comparisons.Single().Key.Type);
        Assert.IsTrue(contents.Comparisons.Single().HasConfirmedDifference);
        Assert.IsTrue(input.Files.All(file => file.ResourceCount == 1));
        Assert.AreEqual(0, input.ExcludedFiles.Count);
        var group = result.Groups.Single();
        Assert.AreEqual(resourceType, group.ResourceKeys.Single().Type);
        Assert.IsTrue(group.Members.All(member => member.ResourceCount == 1 && member.SharedResourceCount == 1));
    }

    [TestMethod]
    public async Task GroupingCanRecalculateAfterTemporaryPackagesHaveBeenMovedAway()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var parked = temp.CreateDirectory("Parked");
        var first = temp.WriteBytes(Path.Combine("Mods", "a.package"), new DbpfFixtureBuilder()
            .AddContent([1, 2], instanceLo: 1).Build());
        var second = temp.WriteBytes(Path.Combine("Mods", "b.package"), new DbpfFixtureBuilder()
            .AddContent([3, 4], instanceLo: 1).AddContent([5, 6], instanceLo: 2).Build());
        var scan = await new PackageConflictScanner(new PhysicalFileSystemAccess(), LlamaLogicPackageIndexReader.CreateDefault())
            .ScanAsync(new PackageConflictScanRequest([new ScanSource("mods", mods)]));
        var contents = await new ResourceContentComparer(DbpfResourceContentHasher.CreateDefault())
            .CompareAsync(new ResourceContentComparisonRequest(scan.Candidates));
        File.Move(first, Path.Combine(parked, "a.package"));
        File.Move(second, Path.Combine(parked, "b.package"));
        Assert.IsFalse(File.Exists(first) || File.Exists(second));
        var builder = new PackageConflictGroupingBuilder();
        var input = builder.Prepare(scan, contents);
        var bundle = builder.Build(input, new(1, new(OverrideConflictCardRuleKind.ByPair)));
        Assert.AreEqual(PackageConflictRelationKind.BundleContainment, bundle.Groups.Single().Kind);
        var same = builder.Build(input, new(3, new(OverrideConflictCardRuleKind.ByExactSharedKeySet)));
        Assert.AreEqual(PackageConflictRelationKind.SuspectedSameMod, same.Groups.Single().Kind);
        var conflict = builder.Build(input, new(3, new(OverrideConflictCardRuleKind.BySharedKeyOverlap, 0.7), 0.9));
        Assert.AreEqual(PackageConflictRelationKind.OverrideConflict, conflict.Groups.Single().Kind);
        Assert.AreSame(contents.Comparisons.Single(), conflict.Groups.Single().Relations.Single().Pair.Resources.Single().Comparison);
    }

    [TestMethod]
    public async Task TwoFixturePackagesSharingATgiProduceACandidate()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var hairA = temp.WriteBytes(Path.Combine("Mods", "发型A.package"), new DbpfFixtureBuilder()
            .AddResource(type: 0x034AEECB, group: 0x0000000A, instanceLo: 0xAAAA0001)
            .AddResource(type: 0x00B2D882, group: 0x00000000, instanceLo: 0xBBBB0001)
            .Build());
        var hairB = temp.WriteBytes(Path.Combine("Mods", "发型B.package"), new DbpfFixtureBuilder()
            .AddResource(type: 0x00B2D882, group: 0x00000000, instanceLo: 0xBBBB0002)
            .AddResource(type: 0x034AEECB, group: 0x0000000A, instanceLo: 0xAAAA0001)
            .Build());
        temp.WriteBytes(Path.Combine("Mods", "无关.package"), new DbpfFixtureBuilder()
            .AddResource(type: 0x0333406C, group: 0x00000000, instanceLo: 0xCCCC0001)
            .Build());
        var broken = temp.WriteBytes(Path.Combine("Mods", "损坏.package"), new DbpfFixtureBuilder()
            .WithMagic("NOPE")
            .AddResource()
            .Build());

        var fileSystem = new PhysicalFileSystemAccess();
        var scanner = new PackageConflictScanner(
            fileSystem,
            new LlamaLogicPackageIndexReader(new PackagePrechecker(fileSystem), fileSystem));

        var report = await scanner.ScanAsync(
            new PackageConflictScanRequest([new ScanSource("mods", mods)]));

        Assert.AreEqual(4, report.DiscoveredPackageCount);
        Assert.AreEqual(3, report.AnalyzedPackageCount);
        CollectionAssert.AreEqual(new[] { broken }, report.IncompletePackagePaths.ToArray());
        Assert.AreEqual(PackageReadIssueCode.MagicMismatch, report.Issues.Single().Code);

        var candidate = report.Candidates.Single();
        Assert.AreEqual("034AEECB:0000000A:00000000AAAA0001", candidate.Key.Tgi);
        CollectionAssert.AreEqual(
            new[] { hairA, hairB },
            candidate.Occurrences.Select(occurrence => occurrence.PackagePath).ToArray());
        CollectionAssert.AreEqual(
            new[] { 0, 1 },
            candidate.Occurrences.Select(occurrence => occurrence.Ordinal).ToArray());
    }
}
