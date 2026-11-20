using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Tests;
using Sims4ModSieve.Core.Tests.Packages;

namespace Sims4ModSieve.Packages.Tests;

public sealed partial class RawReuseTests
{
    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(4)] [DataRow(8)]
    public async Task ConfirmationGroupsRunConcurrentlyAndReusePackageHandles(int concurrency)
    {
        using var temp = new TempDirectory();
        var requests = ManyGroups(temp, concurrency);
        using var fs = new ObservedFileSystem();
        using var concurrent = new ManualResetEventSlim();
        var entered = 0;
        var reads = new ConcurrentBag<string>();
        var hasher = new DbpfResourceContentHasher(fs)
        {
            ReuseCheckpoint = (phase, path, target) =>
            {
                fs.SetPhase(path, phase);
                if (phase == "read") reads.Add(path);
                if (phase != "confirm" || path != requests[1].PackagePath || target!.Value.Ordinal >= 2) return;
                if (Interlocked.Increment(ref entered) == 2) concurrent.Set();
                if (concurrency > 1) Assert.IsTrue(concurrent.Wait(TimeSpan.FromSeconds(10)), "两个不同摘要组必须能够同时进入确认");
            },
        };
        var (actual, stats, _) = await Read(requests, true, fs, hasher);
        Equal((await Read(requests, false)).Results, actual);
        Assert.AreEqual(120L, stats.SavedDecompressions);
        Assert.AreEqual(6L, stats.ConfirmationHandleOpens, "24 个组重复比较同六个包，每包只打开一次");
        Assert.IsTrue(stats.ConfirmationHandleHits > stats.ConfirmationHandleOpens);
        Assert.AreEqual(12L, stats.ConfirmationStampChecks);
        Assert.AreEqual(0L, stats.ConfirmationHandleEvictions);
        Assert.AreEqual(0L, stats.RepeatedOriginalTargets);
        CollectionAssert.AreEqual(new[] { requests[0].PackagePath }, reads.ToArray());
        Assert.AreEqual(0, fs.OpenStreams);
    }

    [TestMethod]
    [DataRow(1, 0)] [DataRow(2, 0)] [DataRow(4, 0)] [DataRow(8, 0)]
    [DataRow(1, 1)] [DataRow(2, 1)] [DataRow(4, 1)] [DataRow(8, 1)]
    public async Task CachedHandleMutationDuringConfirmationRevokesDependencies(int concurrency, int changedPackage)
    {
        using var temp = new TempDirectory();
        var requests = ManyGroups(temp, concurrency);
        var changed = requests[changedPackage].PackagePath;
        using var fs = new ObservedFileSystem();
        var mutated = 0;
        fs.AfterPayloadRead = (phase, _) =>
        {
            if (phase == "confirm" && Interlocked.Exchange(ref mutated, 1) == 0)
                using (var stream = new FileStream(changed, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    stream.WriteByte(0);
        };
        var (actual, stats, _) = await Read(requests, true, fs);
        Equal((await Read(requests, false)).Results, actual);
        Assert.AreEqual(1, mutated);
        Assert.IsTrue(actual[changedPackage].Results.All(result => !result.IsSuccess));
        Assert.IsTrue(stats.ConfirmationRejectedLoss >= 24);
        Assert.AreEqual(changedPackage == 0 ? 0L : 96L, stats.SavedDecompressions);
        Assert.AreEqual(6L, stats.ConfirmationHandleOpens);
        Assert.AreEqual(0, fs.OpenStreams);
    }

    [TestMethod]
    public async Task ConfirmationCacheEvictsIdleHandlesAndReleasesThemAfterReadFailure()
    {
        using var temp = new TempDirectory();
        var requests = ManyGroups(temp, 2);
        using var fs = new ObservedFileSystem();
        var hasher = new DbpfResourceContentHasher(fs)
        {
            ReuseHandleLimit = 2,
            ReuseCheckpoint = (phase, path, _) => fs.SetPhase(path, phase),
        };
        fs.Fail = (phase, path) => phase == "confirm" && path == requests[1].PackagePath;
        var (actual, stats, _) = await Read(requests, true, fs, hasher);
        Equal((await Read(requests, false)).Results, actual);
        Assert.IsTrue(stats.ConfirmationHandleEvictions > 0);
        Assert.IsTrue(stats.ConfirmationReadFailureLoss > 0);
        Assert.IsTrue(stats.PostConfirmationLoss > 0);
        Assert.IsTrue(fs.PeakStreams <= 2);
        Assert.AreEqual(0, fs.OpenStreams);
    }

    [TestMethod]
    public async Task ConfirmationByteAllowanceIsDeterministicAcrossParallelism()
    {
        using var temp = new TempDirectory();
        var requests = ManyGroups(temp, 1);
        var second = Content.ToArray();
        second[0] = 1;
        var limit = 2L * (5 * DbpfFixtureBuilder.Zlib(Content).Length + 2 * DbpfFixtureBuilder.Zlib(second).Length);
        string? baseline = null;
        foreach (var concurrency in new[] { 1, 2, 4, 8 })
        {
            requests = requests.Select(request => request with
            { Limits = request.Limits with { MaxDegreeOfParallelism = concurrency } }).ToArray();
            using var fs = new ObservedFileSystem();
            var hasher = new DbpfResourceContentHasher(fs) { ReuseConfirmationByteLimit = limit };
            var (actual, stats, _) = await Read(requests, true, fs, hasher);
            Equal((await Read(requests, false)).Results, actual);
            Assert.AreEqual(7L, stats.SavedDecompressions);
            Assert.AreEqual(113L, stats.ConfirmationByteLimitLoss);
            Assert.AreEqual(limit, stats.ConfirmationReadBytes);
            var accounting = $"{stats.DigestPotentialSavings}/{stats.ConfirmedComparisons}/{stats.ConfirmationByteLimitLoss}";
            baseline ??= accounting;
            Assert.AreEqual(baseline, accounting);
        }
    }

    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(4)] [DataRow(8)]
    public async Task ConcurrentDigestCollisionsStillRequireCompleteByteEquality(int concurrency)
    {
        using var temp = new TempDirectory();
        var requests = ManyGroups(temp, concurrency, collide: true);
        using var fs = new ObservedFileSystem();
        var hasher = new DbpfResourceContentHasher(fs) { RawDigestTransform = _ => "forced-collision" };
        var (actual, stats, _) = await Read(requests, true, fs, hasher);
        Equal((await Read(requests, false)).Results, actual);
        Assert.AreEqual(96L, stats.SavedDecompressions);
        Assert.AreEqual(24L, stats.ByteMismatchLoss);
        Assert.AreNotEqual(actual[0].Results[0].Sha256, actual[1].Results[0].Sha256);
    }

    private static ResourceContentBatchRequest[] ManyGroups(TempDirectory temp, int concurrency, bool collide = false) =>
        Enumerable.Range(0, 6).Select(package =>
        {
            var builder = new DbpfFixtureBuilder();
            var content = collide ? new byte[100_000] : Content.ToArray();
            for (var ordinal = 0; ordinal < 24; ordinal++)
            {
                if (collide) content[^1] = (byte)(package % 2);
                else content[0] = (byte)ordinal;
                var raw = DbpfFixtureBuilder.Zlib(content, collide ? System.IO.Compression.CompressionLevel.NoCompression
                    : System.IO.Compression.CompressionLevel.Optimal);
                builder.AddContent(raw, DbpfFixtureBuilder.CompressionZlib, (uint)content.Length, instanceLo: (uint)ordinal + 1);
            }
            return Request(temp, $"{package}.package", builder.Build(), Enumerable.Range(0, 24)
                .Select(ordinal => new ResourceContentTarget(Key with { Instance = (uint)ordinal + 1 }, ordinal)).ToArray()) with
            { Limits = ResourceContentLimits.Default with { MaxDegreeOfParallelism = concurrency } };
        }).ToArray();
}
