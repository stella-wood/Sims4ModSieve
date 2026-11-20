using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Benchmarks;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Packages;
using Sims4ModSieve.Core.Tests;
using Sims4ModSieve.Core.Tests.Packages;

namespace Sims4ModSieve.Benchmarks.Tests;

[TestClass]
public sealed class RawReuseStatsTests
{
    private static readonly byte[] Content = Enumerable.Range(0, 40000).Select(index => (byte)(index % 251)).ToArray();

    [TestMethod]
    public async Task SameTgiAndRawZlibSavesOneAndReportsReadCostWithoutWritingSources()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        var stored = DbpfFixtureBuilder.Zlib(Content);
        WritePair(source, stored);
        var before = Digests(source);
        var manifest = InputManifest.Capture([source]);
        var output = Path.Combine(temp.Path, "output");
        Assert.AreEqual(0, await Program.ExecuteAsync(["raw-reuse-stats", "--source", source, "--source", source, "--output", output]));
        var stats = JsonSerializer.Deserialize<RawReuseReport>(await File.ReadAllTextAsync(Path.Combine(output, "stats.json")), ResultSnapshot.JsonOptions)!;
        Assert.AreEqual(2L, stats.CandidateResources);
        Assert.AreEqual(2L, stats.NeedsDecompressionResources);
        Assert.AreEqual(2L * stored.Length, stats.StoredBytes);
        Assert.AreEqual(2L * Content.Length, stats.DeclaredOutputBytes);
        Assert.AreEqual(1L, stats.EquivalentSets);
        Assert.AreEqual(2L, stats.EquivalentResources);
        Assert.AreEqual(1L, stats.SavedDecompressions);
        Assert.AreEqual(0.5, stats.SavedDecompressionRatio);
        Assert.AreEqual((long)Content.Length, stats.SavedOutputBytes);
        Assert.AreEqual(0.5, stats.SavedOutputRatio);
        Assert.AreEqual(2L * stored.Length, stats.FingerprintPreReadBytes);
        Assert.AreEqual(2L * stored.Length, stats.ConfirmationReadBytes);
        Assert.AreEqual(3L * stored.Length, stats.NetAdditionalReadBytes);
        Assert.AreEqual(0L, stats.WholeFileCopies.RedundantPackages);
        Assert.AreEqual(new SetSizeCount(2, 1, 2), stats.SetSizeDistribution.Single());
        Assert.AreEqual(new TypeSavings("0x0904DF10", 1, 1, Content.Length), stats.TopTypes.Single());
        Assert.IsTrue(stats.ElapsedMilliseconds > 0);
        StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(output, "stats.md")), "当前生产内容阶段仍逐份读取");
        CollectionAssert.AreEqual(before, Digests(source));
        Assert.AreEqual(manifest.Fingerprint, InputManifest.Capture([source]).Fingerprint);
    }

    [TestMethod]
    public async Task DifferentZlibLevelsDoNotReuseEvenWithIdenticalDecodedContent()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        var fast = DbpfFixtureBuilder.Zlib(Content, CompressionLevel.Fastest);
        var small = DbpfFixtureBuilder.Zlib(Content, CompressionLevel.SmallestSize);
        Assert.IsFalse(fast.SequenceEqual(small));
        WritePackage(source, "a.package", fast, 1);
        WritePackage(source, "b.package", small, 2);
        var stats = await Collect(source);
        Assert.AreEqual(2L, stats.ValidatedDecompressionResources);
        Assert.AreEqual(0L, stats.SavedDecompressions);
        Assert.AreEqual(0L, stats.EquivalentSets);
        Assert.AreEqual(0L, stats.ConfirmationReadBytes);
        if (fast.Length != small.Length)
        {
            Assert.AreEqual(2L, stats.SingletonMetadataResources);
            Assert.AreEqual(0L, stats.FingerprintPreReadBytes);
        }
    }

    [TestMethod]
    public async Task DifferentRawBytesInSameMetadataBucketPayPreReadWithoutSavings()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        var changed = Content.ToArray();
        changed[0] ^= 0xFF;
        var first = DbpfFixtureBuilder.Zlib(Content, CompressionLevel.NoCompression);
        var second = DbpfFixtureBuilder.Zlib(changed, CompressionLevel.NoCompression);
        Assert.AreEqual(first.Length, second.Length);
        WritePackage(source, "a.package", first, 1);
        WritePackage(source, "b.package", second, 2);
        var stats = await Collect(source);
        Assert.AreEqual(0L, stats.SingletonMetadataResources);
        Assert.AreEqual(2L, stats.ValidatedDecompressionResources);
        Assert.AreEqual(2L * first.Length, stats.FingerprintPreReadBytes);
        Assert.AreEqual(0L, stats.ConfirmationReadBytes);
        Assert.AreEqual(0L, stats.SavedDecompressions);
        Assert.AreEqual(2L * first.Length, stats.NetAdditionalReadBytes);
    }

    [TestMethod]
    public async Task NoneStorageDoesNotEnterDecompressionDenominator()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        WritePair(source, Content, DbpfFixtureBuilder.CompressionNone);
        var stats = await Collect(source);
        Assert.AreEqual(2L, stats.CandidateResources);
        Assert.AreEqual(2L, stats.NoneResources);
        Assert.AreEqual(0L, stats.NeedsDecompressionResources);
        Assert.AreEqual(0L, stats.StoredBytes);
        Assert.AreEqual(0L, stats.SavedDecompressions);
        Assert.AreEqual(0L, stats.FingerprintPreReadBytes);
        Assert.IsNull(stats.SavedDecompressionRatio);
        Assert.IsNull(stats.SavedOutputRatio);
    }

    [TestMethod]
    public async Task WholeFileCopiesRemainInProductionCandidatesAndAreReportedSeparately()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        var bytes = new DbpfFixtureBuilder().AddContent(DbpfFixtureBuilder.Zlib(Content),
            DbpfFixtureBuilder.CompressionZlib, (uint)Content.Length).Build();
        File.WriteAllBytes(Path.Combine(source, "a.package"), bytes);
        File.WriteAllBytes(Path.Combine(source, "b.package"), bytes);
        var stats = await Collect(source);
        Assert.AreEqual(2L, stats.CandidateResources);
        Assert.AreEqual(2L, stats.NeedsDecompressionResources);
        Assert.AreEqual(1L, stats.SavedDecompressions);
        Assert.AreEqual(new WholeFileCopies(1, 1, 1, 1, 1, Content.Length), stats.WholeFileCopies);
    }

    [TestMethod]
    public async Task CorruptTruncatedAndUnsupportedResourcesAreSkippedAndHealthyPairsContinue()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        WritePair(source, DbpfFixtureBuilder.Zlib(Content));
        var corrupt = DbpfFixtureBuilder.Zlib(Content);
        corrupt[^1] ^= 0xFF;
        WritePackage(source, "bad-checksum.package", corrupt, 3);
        WritePackage(source, "truncated-zlib.package", DbpfFixtureBuilder.Zlib(Content)[..^3], 4);
        WritePackage(source, "refpack.package", [1, 2, 3], 5, DbpfFixtureBuilder.CompressionInternal);
        WritePackage(source, "unknown.package", [1, 2, 3], 6, 0x1234);
        File.WriteAllBytes(Path.Combine(source, "bad-package.package"), [0, 1, 2]);
        var stats = await Collect(source);
        Assert.AreEqual(7, stats.DiscoveredPackages);
        Assert.AreEqual(6L, stats.CandidateResources);
        Assert.AreEqual(4L, stats.NeedsDecompressionResources);
        Assert.AreEqual(2L, stats.ValidatedDecompressionResources);
        Assert.AreEqual(1L, stats.SavedDecompressions);
        Assert.AreEqual(4L, stats.SkippedResources);
        Assert.AreEqual(1L, stats.ResourceSkipCounts["RefPack"]);
        Assert.AreEqual(1L, stats.ResourceSkipCounts["UnknownCompression"]);
        Assert.AreEqual(2L, stats.ResourceSkipCounts.Where(pair => pair.Key is not ("RefPack" or "UnknownCompression")).Sum(pair => pair.Value));
        Assert.AreEqual(1L, stats.ScanIssueCounts.Values.Sum());
    }

    [TestMethod]
    public async Task MatchingCorruptRawBytesNeverCountAsReusableSavings()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        WritePair(source, [0x78, 0x9c, 0]);
        var stats = await Collect(source);
        Assert.AreEqual(2L, stats.NeedsDecompressionResources);
        Assert.AreEqual(2L, stats.SkippedResources);
        Assert.AreEqual(0L, stats.EquivalentSets);
        Assert.AreEqual(0L, stats.SavedOutputBytes);
        Assert.AreEqual(6L, stats.FingerprintPreReadBytes);
        Assert.AreEqual(6L, stats.ConfirmationReadBytes);
        Assert.AreEqual(12L, stats.NetAdditionalReadBytes);
    }

    [TestMethod]
    public async Task DisabledAndUniqueTgiResourcesAreExcludedAndMetadataSingletonsCostNoRead()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        var stored = DbpfFixtureBuilder.Zlib(Content);
        WritePackage(source, "a.package", stored, 1);
        WritePackage(source, "b.package", Content, 2, DbpfFixtureBuilder.CompressionNone);
        WritePackage(source, "disabled.package.off", stored, 3);
        File.WriteAllBytes(Path.Combine(source, "unique.package"), new DbpfFixtureBuilder()
            .AddContent(stored, DbpfFixtureBuilder.CompressionZlib, (uint)Content.Length, instanceLo: 999).Build());
        var stats = await Collect(source);
        Assert.AreEqual(3, stats.DiscoveredPackages);
        Assert.AreEqual(2L, stats.CandidateResources);
        Assert.AreEqual(1L, stats.NeedsDecompressionResources);
        Assert.AreEqual(1L, stats.SingletonMetadataResources);
        Assert.AreEqual(0L, stats.FingerprintPreReadBytes);
        Assert.AreEqual(0L, stats.EquivalentSets);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OutputInsideAnySourceIsRejectedBeforeWriting(bool sameDirectory)
    {
        using var temp = new TempDirectory();
        var first = temp.CreateDirectory("first");
        var second = temp.CreateDirectory("second");
        var output = sameDirectory ? second : Path.Combine(second, "nested", "results");
        Assert.AreEqual(2, await Program.ExecuteAsync(["raw-reuse-stats", "--source", first, "--source", second, "--output", output]));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(second).Length);
    }

    [TestMethod]
    public async Task OutputThroughLinkedParentIntoSourceIsRejected()
    {
        if (OperatingSystem.IsWindows()) return; // Windows 创建符号链接需要开发者模式或额外权限。
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        var link = Path.Combine(temp.Path, "alias");
        Directory.CreateSymbolicLink(link, source);
        Assert.AreEqual(2, await Program.ExecuteAsync(["raw-reuse-stats", "--source", source, "--output", Path.Combine(link, "output")]));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(source).Length);
    }

    [TestMethod]
    [DataRow(0u, false)]
    [DataRow(7u, false)]
    [DataRow(3u, true)]
    public async Task IndependentLocatorMatchesProductionForCommonFieldsAndLegacyOffsets(uint flags, bool legacy)
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        var compressed = DbpfFixtureBuilder.Zlib(Content);
        for (var index = 0; index < 2; index++)
        {
            var builder = new DbpfFixtureBuilder().WithIndexType(flags)
                .AddContent(compressed, DbpfFixtureBuilder.CompressionZlib, (uint)Content.Length, instanceLo: 1)
                .AddResource(instanceLo: (uint)(100 + index), extended: false);
            if (legacy) builder.WithLegacyIndexPosition();
            File.WriteAllBytes(Path.Combine(source, $"{index}.package"), builder.Build());
        }
        var stats = await Collect(source);
        Assert.AreEqual(2L, stats.ValidatedDecompressionResources);
        Assert.AreEqual(1L, stats.SavedDecompressions);
        Assert.AreEqual(0L, stats.SkippedResources);
    }

    [TestMethod]
    public async Task StatisticsAndProductionUseIdenticalMetadataAndDigestGroups()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        WritePair(source, DbpfFixtureBuilder.Zlib(Content));
        WritePackage(source, "bad-a.package", [0x78, 0x9c, 0], 3);
        WritePackage(source, "bad-b.package", [0x78, 0x9c, 0], 4);
        WritePackage(source, "none.package", Content, 5, DbpfFixtureBuilder.CompressionNone);
        var stats = await Collect(source);
        var packages = await new PackageConflictScanner(new PhysicalFileSystemAccess(), LlamaLogicPackageIndexReader.CreateDefault())
            .ScanAsync(new([new("source", source)]));
        foreach (var concurrency in new[] { 1, 2, 4, 8 })
        {
            ResourceContentReuseDiagnostics? actual = null;
            await new ResourceContentComparer(DbpfResourceContentHasher.CreateDefault()).CompareAsync(new(packages.Candidates)
            { Options = new((DuplicateHashConcurrency)concurrency)
                { ContentReuse = ResourceContentReuseMode.On, ContentReuseObserved = value => actual = value } });
            Assert.IsNotNull(actual);
            Assert.AreEqual(stats.CandidateResources, actual.TargetResources);
            Assert.AreEqual(stats.NeedsDecompressionResources, actual.NeedsDecompressionResources);
            Assert.AreEqual(stats.StoredBytes, actual.StoredBytes);
            Assert.AreEqual(stats.MetadataGroups, actual.MetadataGroups);
            Assert.AreEqual(stats.SingletonMetadataResources, actual.SingletonMetadataResources);
            Assert.AreEqual(stats.DigestPotentialSavings, actual.DigestPotentialSavings);
            Assert.AreEqual(stats.ConfirmationReadBytes, actual.EstimatedConfirmationBytes);
            Assert.AreEqual(stats.SavedDecompressions, actual.SavedDecompressions);
            Assert.AreEqual(1L, actual.PostConfirmationLoss);
            Assert.AreEqual(stats.InvalidContentSavingsExcluded, actual.PostConfirmationLoss);
            Assert.AreEqual(actual.DigestPotentialSavings, actual.AccountedSavings);
        }
    }

    [TestMethod]
    public async Task WholeBatchFallbackAccountsForLostSavingsAndRepeatedReads()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        for (var package = 0; package < 3; package++)
        {
            var builder = new DbpfFixtureBuilder();
            for (uint key = 1; key <= 2; key++)
                builder.AddContent(DbpfFixtureBuilder.Zlib(Content), DbpfFixtureBuilder.CompressionZlib, (uint)Content.Length, instanceLo: key);
            builder.AddContent([0x78, 0x9c, 0], DbpfFixtureBuilder.CompressionZlib, 1000, instanceLo: 3);
            builder.AddContent(DbpfFixtureBuilder.Zlib(Enumerable.Repeat((byte)package, 1000).ToArray()),
                DbpfFixtureBuilder.CompressionZlib, 1000, instanceLo: 4);
            File.WriteAllBytes(Path.Combine(source, $"{package}.package"), builder.Build());
        }
        var stats = await Collect(source);
        var packages = await new PackageConflictScanner(new PhysicalFileSystemAccess(), LlamaLogicPackageIndexReader.CreateDefault())
            .ScanAsync(new([new("source", source)]));
        foreach (var concurrency in new[] { 1, 2, 4, 8 })
        {
            ResourceContentReuseDiagnostics? off = null, on = null;
            await new ResourceContentComparer(DbpfResourceContentHasher.CreateDefault()).CompareAsync(new(packages.Candidates)
            { Options = new((DuplicateHashConcurrency)concurrency) { ContentReuseObserved = value => off = value } });
            await new ResourceContentComparer(DbpfResourceContentHasher.CreateDefault()).CompareAsync(new(packages.Candidates)
            { Options = new((DuplicateHashConcurrency)concurrency)
                { ContentReuse = ResourceContentReuseMode.On, ContentReuseObserved = value => on = value } });
            Assert.IsNotNull(off);
            Assert.IsNotNull(on);
            Assert.AreEqual(4L, stats.SavedDecompressions);
            Assert.AreEqual(0L, on.SavedDecompressions);
            Assert.AreEqual(6L, on.ReaderFallbackLoss);
            Assert.AreEqual(2L, stats.InvalidContentSavingsExcluded);
            Assert.AreEqual(2L, on.RepeatedOriginalTargets);
            Assert.AreEqual(off.DecompressionAttempts + 2, on.DecompressionAttempts);
            Assert.AreEqual(stats.SavedDecompressions - on.SavedDecompressions,
                on.ReaderFallbackLoss - stats.InvalidContentSavingsExcluded);
        }
    }

    private static Task<RawReuseReport> Collect(string source) => RawReuseStats.CollectAsync([new ScanSource("source", source)]);

    private static void WritePair(string source, byte[] stored, ushort compression = DbpfFixtureBuilder.CompressionZlib)
    {
        WritePackage(source, "a.package", stored, 1, compression);
        WritePackage(source, "b.package", stored, 2, compression);
    }

    private static void WritePackage(string source, string name, byte[] stored, uint marker,
        ushort compression = DbpfFixtureBuilder.CompressionZlib) => File.WriteAllBytes(Path.Combine(source, name),
        new DbpfFixtureBuilder().AddContent(stored, compression, (uint)Content.Length, instanceLo: 1)
            .AddContent([(byte)marker], instanceLo: 100 + marker).Build());

    private static string[] Digests(string source) => Directory.EnumerateFiles(source).Order()
        .Select(path => $"{Path.GetFileName(path)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}").ToArray();
}
