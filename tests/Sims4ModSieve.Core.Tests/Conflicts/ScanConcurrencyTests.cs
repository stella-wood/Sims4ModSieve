using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Diagnostics;
using Sims4ModSieve.Core.Duplicates;
using static Sims4ModSieve.Core.Tests.Conflicts.CandidateFactory;

namespace Sims4ModSieve.Core.Tests.Conflicts;

[TestClass]
public sealed class ScanConcurrencyTests
{
    [TestMethod]
    public async Task PackageResultsMatchAcrossConcurrencyLevels()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("Mods");
        foreach (var name in new[] { "a", "b", "c", "d", "e", "f", "g", "h", "bad" })
            temp.WriteFile($"Mods/{name}.package", "x");
        string? baseline = null;
        foreach (var n in new[] { 1, 2, 4, 8 })
        {
            var reader = new FakePackageIndexReader().WithFailure("bad.package");
            foreach (var (name, i) in new[] { "a", "b", "c", "d", "e", "f", "g", "h" }.Select((name, i) => (name, i)))
                reader.WithIndex(name + ".package", Key(), Key((uint)(i / 2 + 2)))
                    .WithDelay(name + ".package", TimeSpan.FromMilliseconds((8 - i) * 2));
            ScanConcurrencyDecision? decision = null;
            var report = await new PackageConflictScanner(new ReversedEnumerationFileSystem(new PhysicalFileSystemAccess()), reader)
                .ScanAsync(new([new ScanSource("mods", root)])
                {
                    Options = new((DuplicateHashConcurrency)n) { ConcurrencyDecided = value => decision = value },
                });
            Assert.AreEqual(n, decision!.AdoptedLimit);
            Assert.IsTrue(reader.MaxInFlight <= n);
            Assert.AreEqual(8, report.AnalyzedPackageCount);
            Assert.AreEqual(1, report.IncompletePackagePaths.Count);
            var json = JsonSerializer.Serialize(report with { StartedAtUtc = DateTime.UnixEpoch, CompletedAtUtc = DateTime.UnixEpoch });
            baseline ??= json;
            Assert.AreEqual(baseline, json, $"并发 {n}");
        }
    }

    [TestMethod]
    public async Task ContentResultsMatchAcrossConcurrencyLevels()
    {
        string? baseline = null;
        foreach (var n in new[] { 1, 2, 4, 8 })
        {
            var hasher = new FakeResourceContentHasher();
            var paths = Enumerable.Range(0, 8).Select(i => ($"p{i}.package", 0)).ToArray();
            foreach (var (path, i) in paths.Select((p, i) => (p.Item1, i)))
            {
                hasher.WithDelay(path, TimeSpan.FromMilliseconds((8 - i) * 2));
                if (i == 7) hasher.WithFailure(path, 0);
                else hasher.WithContent(path, 0, new string(i % 2 == 0 ? 'A' : 'B', 64));
            }
            var report = await new ResourceContentComparer(hasher).CompareAsync(new([Candidate(Key(), paths)])
            {
                Options = new((DuplicateHashConcurrency)n),
            });
            Assert.AreEqual(n, report.Limits.MaxDegreeOfParallelism);
            Assert.IsTrue(hasher.MaxInFlight <= n);
            Assert.IsFalse(report.Comparisons.Single().IsComplete);
            Assert.IsTrue(report.Comparisons.Single().HasConfirmedDifference);
            var json = JsonSerializer.Serialize(report with
            {
                StartedAtUtc = DateTime.UnixEpoch, CompletedAtUtc = DateTime.UnixEpoch,
                Limits = report.Limits with { MaxDegreeOfParallelism = 1 },
            });
            baseline ??= json;
            Assert.AreEqual(baseline, json, $"并发 {n}");
        }
    }

    [TestMethod]
    [DataRow(DuplicateHashConcurrency.Auto, 2)]
    [DataRow(DuplicateHashConcurrency.Auto, 3)]
    [DataRow(DuplicateHashConcurrency.Eight, 2)]
    [DataRow(DuplicateHashConcurrency.Eight, 3)]
    public async Task FiniteBudgetPreservesCallerConcurrency(DuplicateHashConcurrency setting, int original)
    {
        var hasher = new FakeResourceContentHasher().WithContent("a.package", 0, new string('A', 64));
        var limits = new ResourceContentLimits(123, 456, 789, original);
        ScanConcurrencyDecision? decision = null;
        var report = await new ResourceContentComparer(hasher).CompareAsync(new([Candidate(Key(), ("a.package", 0))], limits)
        {
            Options = new(setting, new FakeStorage(false)) { ConcurrencyDecided = value => decision = value },
            MaxDegreeOfParallelism = 8,
        });
        Assert.AreEqual(limits, report.Limits);
        Assert.AreEqual(limits, hasher.Requests.Single().Limits);
        Assert.AreEqual(original, decision!.AdoptedLimit);
        Assert.AreEqual(setting == DuplicateHashConcurrency.Auto ? PackageConflictScanner.SsdAutoMaxDegreeOfParallelism : 8, decision.PolicyLimit);
        StringAssert.Contains(decision.FallbackReason!, "有限总预算");
    }

    [TestMethod]
    public async Task FiniteBudgetWithoutSnapshotAlsoPreservesOriginalLimits()
    {
        var limits = new ResourceContentLimits(123, 456, 789, 3);
        var hasher = new FakeResourceContentHasher().WithContent("a.package", 0, new string('A', 64));
        var report = await new ResourceContentComparer(hasher).CompareAsync(new([Candidate(Key(), ("a.package", 0))], limits)
        {
            MaxDegreeOfParallelism = 8,
        });
        Assert.AreEqual(limits, report.Limits);
        Assert.AreEqual(limits, hasher.Requests.Single().Limits);
    }

    [TestMethod]
    public async Task UnlimitedBudgetChangesOnlyConcurrencyAndExplicitLowLevelWins()
    {
        var limits = new ResourceContentLimits(123, 456, long.MaxValue, 3);
        foreach (var explicitLimit in new int?[] { null, 5 })
        {
            var hasher = new FakeResourceContentHasher().WithContent("a.package", 0, new string('A', 64));
            var report = await new ResourceContentComparer(hasher).CompareAsync(new([Candidate(Key(), ("a.package", 0))], limits)
            {
                Options = new(DuplicateHashConcurrency.Eight), MaxDegreeOfParallelism = explicitLimit,
            });
            Assert.AreEqual(limits with { MaxDegreeOfParallelism = explicitLimit ?? 8 }, report.Limits);
        }
    }

    [TestMethod]
    [DataRow(false, false, -1, "SSD")]
    [DataRow(false, true, 1, "SSD+HDD")]
    [DataRow(false, null, 2, "SSD+未知")]
    [DataRow(true, null, 1, "HDD+未知")]
    [DataRow(null, null, 2, "未知")]
    public async Task StoragePolicyUsesOnlyParticipatingRoots(bool? first, bool? second, int expected, string classification)
    {
        // -1 表示全 SSD：package 阶段采用按逻辑 CPU 封顶的 8。
        if (expected == -1) expected = PackageConflictScanner.SsdAutoMaxDegreeOfParallelism;
        using var temp = new TempDirectory();
        var ssd = temp.CreateDirectory("first");
        var other = temp.CreateDirectory("second");
        var empty = temp.CreateDirectory("script-only");
        var disabled = temp.CreateDirectory("disabled");
        var a = temp.WriteFile("first/a.package", "x");
        var b = temp.WriteFile("second/b.package", "x");
        temp.WriteFile("script-only/a.ts4script", "x");
        temp.WriteFile("disabled/c.package", "x");
        var storage = new FakeStorage(true, new() { [ssd] = first, [other] = second });
        var decisions = new List<ScanConcurrencyDecision>();
        var options = new ConflictScanOptions(StorageProfile: storage) { ConcurrencyDecided = decisions.Add };
        await new PackageConflictScanner(new PhysicalFileSystemAccess(), new FakePackageIndexReader())
            .ScanAsync(new([new("first", ssd), new("second", other), new("empty", empty), new("disabled", disabled, false)]) { Options = options });
        await new ResourceContentComparer(new FakeResourceContentHasher().WithContent(a, 0, new string('A', 64))
            .WithContent(b, 0, new string('A', 64))).CompareAsync(new([Candidate(Key(), (a, 0), (b, 0))]) { Options = options });
        Assert.AreEqual(2, decisions.Count);
        Assert.IsTrue(decisions.All(value => value.AdoptedLimit == expected && value.StorageClassification == classification));
        CollectionAssert.AreEquivalent(new[] { ssd, other, ssd, other }, storage.Paths.ToArray());
    }

    [TestMethod]
    public void PackageSsdAutoLimitIsEightCappedByCpu() =>
        Assert.AreEqual(Math.Clamp(Environment.ProcessorCount, 1, 8), PackageConflictScanner.SsdAutoMaxDegreeOfParallelism);

    [TestMethod]
    public async Task ContentStoragePolicyExcludesPackagesWithoutCandidates()
    {
        using var temp = new TempDirectory();
        var ssd = temp.CreateDirectory("SSD");
        var hdd = temp.CreateDirectory("HDD");
        var a = temp.WriteFile("SSD/a.package", "x");
        var b = temp.WriteFile("SSD/b.package", "x");
        temp.WriteFile("HDD/unique.package", "x");
        var storage = new FakeStorage(true, new() { [ssd] = false });
        var decisions = new List<ScanConcurrencyDecision>();
        var options = new ConflictScanOptions(StorageProfile: storage) { ConcurrencyDecided = decisions.Add };
        var packages = await new PackageConflictScanner(new PhysicalFileSystemAccess(), new FakePackageIndexReader()
            .WithIndex("a.package", Key()).WithIndex("b.package", Key()).WithIndex("unique.package", Key(2)))
            .ScanAsync(new([new("ssd", ssd), new("hdd", hdd)]) { Options = options });
        await new ResourceContentComparer(new FakeResourceContentHasher().WithContent(a, 0, new string('A', 64))
            .WithContent(b, 0, new string('A', 64))).CompareAsync(new(packages.Candidates) { Options = options });
        Assert.AreEqual(1, decisions[0].AdoptedLimit);
        Assert.AreEqual(PackageConflictScanner.SsdAutoMaxDegreeOfParallelism, decisions[1].AdoptedLimit);
        Assert.AreEqual(1, storage.Paths.Count(path => path == hdd));
    }

    [TestMethod]
    public async Task LegacyCallsKeepDefaultsAndEmptyStagesHaveNoDecision()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("Mods");
        var decisions = new List<ScanConcurrencyDecision>();
        var storage = new FakeStorage(false);
        var options = new ConflictScanOptions(StorageProfile: storage) { ConcurrencyDecided = decisions.Add };
        await new PackageConflictScanner(new PhysicalFileSystemAccess(), new FakePackageIndexReader())
            .ScanAsync(new([new("mods", root)]) { Options = options });
        await new ResourceContentComparer(new FakeResourceContentHasher()).CompareAsync(new([]) { Options = options });
        Assert.AreEqual(0, decisions.Count);
        Assert.AreEqual(0, storage.Paths.Count);
        using var timing = ScanPerformance.Start("旧调用");
        temp.WriteFile("Mods/a.package", "x");
        await new PackageConflictScanner(new PhysicalFileSystemAccess(), new FakePackageIndexReader()).ScanAsync(new([new("mods", root)]));
        var report = await new ResourceContentComparer(new FakeResourceContentHasher()).CompareAsync(new([]));
        Assert.AreEqual(ResourceContentLimits.Default, report.Limits);
        Assert.AreEqual(PackageConflictScanner.DefaultMaxDegreeOfParallelism, timing.Stages.Single(stage => stage.Name.Contains("排序和读取索引")).Parallelism);
    }

    [TestMethod]
    public async Task ExplicitIndexConcurrencyWinsOverSnapshot()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("Mods");
        temp.WriteFile("Mods/a.package", "x");
        ScanConcurrencyDecision? decision = null;
        await new PackageConflictScanner(new PhysicalFileSystemAccess(), new FakePackageIndexReader())
            .ScanAsync(new([new("mods", root)], 3)
            {
                Options = new(StorageProfile: new FakeStorage(false)) { ConcurrencyDecided = value => decision = value },
            });
        Assert.AreEqual(PackageConflictScanner.SsdAutoMaxDegreeOfParallelism, decision!.PolicyLimit);
        Assert.AreEqual(3, decision.AdoptedLimit);
        StringAssert.Contains(decision.FallbackReason!, "显式低层");
    }

    private sealed class FakeStorage(bool? fallback, Dictionary<string, bool?>? volumes = null) : IStorageProfile
    {
        public List<string> Paths { get; } = [];
        public StorageVolumeProfile GetVolume(string path)
        {
            Paths.Add(path);
            return new(path, volumes is not null && volumes.TryGetValue(path, out var value) ? value : fallback);
        }
    }
}
