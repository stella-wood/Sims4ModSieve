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
