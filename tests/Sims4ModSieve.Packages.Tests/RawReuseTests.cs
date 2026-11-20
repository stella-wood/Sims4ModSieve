using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Diagnostics;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Tests;
using Sims4ModSieve.Core.Tests.Packages;

namespace Sims4ModSieve.Packages.Tests;

[TestClass]
public sealed partial class RawReuseTests
{
    private static readonly byte[] Content = Enumerable.Range(0, 160_000).Select(i => (byte)(i % 251)).ToArray();
    private static readonly ResourceKey Key = new(0x0904DF10, 0, 1);
    private static readonly ResourceContentTarget Target = new(Key, 0);

    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(4)] [DataRow(8)]
    public async Task IdenticalRawZlibReusesSuccessfulRepresentative(int concurrency)
    {
        using var temp = new TempDirectory();
        var raw = DbpfFixtureBuilder.Zlib(Content);
        var requests = Requests(temp, [raw, raw, raw], concurrency: concurrency);
        using var fs = new ObservedFileSystem();
        var (actual, stats, consumed) = await Read(requests, true, fs);
        var (original, off, _) = await Read(requests, false);
        Equal(original, actual);
        Assert.AreEqual(3L, off.DecompressionAttempts);
        Assert.AreEqual(1L, stats.DecompressionAttempts);
        Assert.AreEqual(2L, stats.SavedDecompressions);
        Assert.AreEqual(2L * Content.Length, stats.SavedOutputBytes);
        Assert.AreEqual(3L * raw.Length, stats.FingerprintPreReadBytes);
        Assert.AreEqual(4L * raw.Length, stats.ConfirmationReadBytes);
        Assert.AreEqual(5L * raw.Length, stats.NetAdditionalReadBytes);
        Assert.AreEqual(8L * raw.Length, stats.ActualInputBytes);
        Assert.AreEqual(Content.Length, stats.ActualOutputBytes);
        Assert.AreEqual(stats.ActualInputBytes + stats.ActualOutputBytes, consumed);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(Content)), actual[2].Results[0].Sha256);
        Assert.AreEqual(0, fs.OpenStreams);
        // 读取并发与缓存句柄分别有界；单 worker 可以保留两侧只读句柄。
        Assert.IsTrue(fs.PeakStreams <= Math.Max(concurrency, requests.Length));
        Assert.AreEqual(3L, stats.ConfirmationHandleOpens);
    }

    [TestMethod]
    public async Task UniqueDeclaredSizeDoesNotHideCorruptZlib()
    {
        using var temp = new TempDirectory();
        var good = DbpfFixtureBuilder.Zlib(Content);
        var requests = Requests(temp, [good, new byte[] { 0x78, 0x9c, 0 }]);
        requests[1] = requests[1] with { PackagePath = temp.WriteBytes("bad.package", new DbpfFixtureBuilder()
            .AddContent([0x78, 0x9c, 0], DbpfFixtureBuilder.CompressionZlib, 123, instanceLo: 1).Build()) };
        requests[1] = requests[1] with { ExpectedStamp = new PhysicalFileSystemAccess().GetFileStamp(requests[1].PackagePath) };
        var (actual, stats, _) = await Read(requests, true);
        Equal((await Read(requests, false)).Results, actual);
        Assert.IsFalse(actual[1].Results[0].IsSuccess);
        Assert.AreEqual(0L, stats.FingerprintPreReadBytes);
    }

    [TestMethod]
    public async Task DifferentEncodingsMergeByContentHash()
    {
        using var temp = new TempDirectory();
        var fast = DbpfFixtureBuilder.Zlib(Content, CompressionLevel.Fastest);
        var optimal = DbpfFixtureBuilder.Zlib(Content, CompressionLevel.Optimal);
        Assert.IsFalse(fast.SequenceEqual(optimal));
        var requests = Requests(temp, [fast, optimal, Content]);
        var nonePath = temp.WriteBytes("none.package", new DbpfFixtureBuilder().AddContent(Content, instanceLo: 1).Build());
        requests[2] = requests[2] with { PackagePath = nonePath, ExpectedStamp = new PhysicalFileSystemAccess().GetFileStamp(nonePath) };
        var (actual, stats, _) = await Read(requests, true);
        Equal((await Read(requests, false)).Results, actual);
        Assert.AreEqual(1, actual.SelectMany(batch => batch.Results).Select(result => result.Sha256).Distinct().Count());
        Assert.AreEqual(0L, stats.SavedDecompressions);
    }

    [TestMethod]
    public async Task DifferentDeclaredSizesOrCompressionDoNotReuse()
    {
        using var temp = new TempDirectory();
        var raw = DbpfFixtureBuilder.Zlib(Content);
        var definitions = new[] { (DbpfFixtureBuilder.CompressionZlib, (uint)Content.Length),
            (DbpfFixtureBuilder.CompressionZlib, (uint)Content.Length + 1), ((ushort)0xFFFF, (uint)Content.Length),
            ((ushort)0xFFFE, (uint)Content.Length), ((ushort)0x1111, (uint)Content.Length), ((ushort)0, (uint)raw.Length) };
        var requests = definitions.Select((definition, index) => Request(temp, $"{index}.package", new DbpfFixtureBuilder()
            .AddContent(raw, definition.Item1, definition.Item2, instanceLo: 1).Build())).ToArray();
        var (actual, stats, _) = await Read(requests, true);
        Equal((await Read(requests, false)).Results, actual);
        Assert.AreEqual(0L, stats.FingerprintedResources);
        Assert.AreEqual(0L, stats.SavedDecompressions);
    }

    [TestMethod]
    [DataRow(32, 1000000L, 2L)]
    [DataRow(1, 1000000L, 1L)]
    [DataRow(32, 1L, 0L)]
    public async Task RawFingerprintCollisionRequiresByteEquality(int collisionSets, long confirmationLimit, long saved)
    {
        using var temp = new TempDirectory();
        var a = DbpfFixtureBuilder.Zlib(new byte[1000], CompressionLevel.NoCompression);
        var b = DbpfFixtureBuilder.Zlib(Enumerable.Repeat((byte)7, 1000).ToArray(), CompressionLevel.NoCompression);
        Assert.AreEqual(a.Length, b.Length);
        Assert.IsFalse(a.SequenceEqual(b));
        var requests = Requests(temp, [a, b, a, b], declared: 1000);
        using var fs = new ObservedFileSystem();
        var hasher = new DbpfResourceContentHasher(fs)
        { RawDigestTransform = _ => "forced-collision", ReuseCollisionSetLimit = collisionSets, ReuseConfirmationByteLimit = confirmationLimit };
        var (actual, stats, _) = await Read(requests, true, fs, hasher);
        Equal((await Read(requests, false)).Results, actual);
        Assert.AreEqual(saved, stats.SavedDecompressions);
        Assert.AreNotEqual(actual[0].Results[0].Sha256, actual[1].Results[0].Sha256);
    }

    [TestMethod]
    public async Task SameTgiDifferentOrdinalsKeepTheirOwnResults()
    {
        using var temp = new TempDirectory();
        var a = DbpfFixtureBuilder.Zlib(Content);
        var b = DbpfFixtureBuilder.Zlib("different"u8.ToArray());
        var bytes = new DbpfFixtureBuilder().AddContent(a, DbpfFixtureBuilder.CompressionZlib, (uint)Content.Length, instanceLo: 1)
            .AddContent(b, DbpfFixtureBuilder.CompressionZlib, 9, instanceLo: 1).Build();
        var targets = new[] { new ResourceContentTarget(Key, 1), Target, new ResourceContentTarget(Key with { Instance = 999 }, 0), new ResourceContentTarget(Key, 100) };
        var requests = new[] { Request(temp, "a.package", bytes, targets), Request(temp, "b.package", bytes, targets) };
        var (actual, stats, _) = await Read(requests, true);
        Equal((await Read(requests, false)).Results, actual);
        Assert.AreEqual(2L, stats.SavedDecompressions);
        Assert.AreNotEqual(actual[0].Results[0].Sha256, actual[0].Results[1].Sha256);
        Assert.AreEqual(ResourceContentIssueCode.IndexMismatch, actual[0].Results[2].Issue?.Code);
    }

    [TestMethod]
    [DataRow("refpack")] [DataRow("streamable")] [DataRow("deleted")] [DataRow("unknown")]
    [DataRow("truncated")] [DataRow("trailing")] [DataRow("checksum")] [DataRow("short")]
    [DataRow("long")] [DataRow("corrupt")] [DataRow("stored-limit")] [DataRow("output-limit")]
    [DataRow("bad-index")] [DataRow("out-of-bounds")]
    public async Task OriginalBoundariesAndFailureOwnershipSurviveReuse(string kind)
    {
        using var temp = new TempDirectory();
        var raw = DbpfFixtureBuilder.Zlib(Content);
        ushort compression = DbpfFixtureBuilder.CompressionZlib;
        var declared = (uint)Content.Length;
        var limits = ResourceContentLimits.Default;
        switch (kind)
        {
            case "refpack": compression = 0xFFFF; break;
            case "streamable": compression = 0xFFFE; break;
            case "deleted": compression = 0xFFE0; break;
            case "unknown": compression = 0x1111; break;
            case "truncated": raw = raw[..^2]; break;
            case "trailing": raw = [.. raw, 0, 1]; break;
            case "checksum": raw[^1] ^= 0xFF; break;
            case "short": declared++; break;
            case "long": declared--; break;
            case "corrupt": raw = [0x78, 0x9C, 0, 0, 0, 0]; break;
            case "stored-limit": limits = limits with { MaxStoredBytesPerResource = raw.Length - 1 }; break;
            case "output-limit": limits = limits with { MaxDecompressedBytesPerResource = Content.Length - 1 }; break;
        }
        var bytes = new DbpfFixtureBuilder().AddContent(raw, compression, declared, instanceLo: 1).Build();
        if (kind == "bad-index") bytes[0] = 0;
        if (kind == "out-of-bounds")
        {
            var index = (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x40));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index + 20), uint.MaxValue);
        }
        var requests = new[] { Request(temp, "a.package", bytes), Request(temp, "b.package", bytes) }
            .Select(request => request with { Limits = limits }).ToArray();
        var (actual, stats, _) = await Read(requests, true);
        Equal((await Read(requests, false)).Results, actual);
        Assert.IsTrue(actual.All(batch => batch.Results.All(result => !result.IsSuccess)));
        Assert.AreEqual(0L, stats.SavedDecompressions);
    }

    [TestMethod]
    [DataRow("representative")] [DataRow("member")] [DataRow("representative-open")] [DataRow("member-open")] [DataRow("transient-fingerprint")] [DataRow("transient-confirm")]
    public async Task RepresentativeAndMemberFailuresFallBackPerMember(string failure)
    {
        using var temp = new TempDirectory();
        var raw = DbpfFixtureBuilder.Zlib(Content);
        var requests = Requests(temp, [raw, raw, raw]);
        var path = requests[failure.StartsWith("representative", StringComparison.Ordinal) ? 0 : 1].PackagePath;
        using var fs = new ObservedFileSystem();
        if (failure.EndsWith("open", StringComparison.Ordinal))
            fs.FailOpen = (phase, file) => file == path && (failure == "representative-open" ? phase is "read" or "fallback" : phase != "locate");
        fs.Fail = (phase, file) => !failure.EndsWith("open", StringComparison.Ordinal) && file == path && (failure switch
        {
            "representative" => phase is "read" or "fallback",
            "member" => phase != "locate",
            "transient-fingerprint" => phase == "fingerprint",
            _ => phase == "confirm",
        });
        var (actual, stats, _) = await Read(requests, true, fs);
        using var baselineFs = new ObservedFileSystem();
        baselineFs.Fail = (_, file) => file == path && failure is "representative" or "member";
        baselineFs.FailOpen = (_, file) => file == path && failure.EndsWith("open", StringComparison.Ordinal);
        Equal((await Read(requests, false, baselineFs)).Results, actual);
        Assert.IsTrue(actual[2].Results[0].IsSuccess);
        Assert.AreEqual(0, fs.OpenStreams);
        Assert.IsTrue(stats.FallbackResources > 0 || stats.SavedDecompressions == 0);
    }

    [TestMethod]
    [DataRow(0)] [DataRow(1)]
    public async Task UnstablePackageRevokesDependenciesAndInvalidatesWholeBatch(int changedPackage)
    {
        using var temp = new TempDirectory();
        var raw = DbpfFixtureBuilder.Zlib(Content);
        var requests = Requests(temp, [raw, raw, raw]);
        requests = requests.Select(request => request with { Targets = [Target, new(new ResourceKey(0x0904DF10, 0, 42), 1)] }).ToArray();
        var path = requests[changedPackage].PackagePath;
        using var fs = new ObservedFileSystem();
        var hasher = new DbpfResourceContentHasher(fs)
        {
            ReuseCheckpoint = (phase, file, _) =>
            {
                fs.SetPhase(file, phase);
                if (phase == "publish" && file == path) fs.Changed.Add(file);
            },
        };
        var (actual, stats, _) = await Read(requests, true, fs, hasher);
        using var baselineFs = new ObservedFileSystem();
        baselineFs.AfterPayloadRead = (phase, file) => { if (phase == "read" && file == path) baselineFs.Changed.Add(file); };
        Equal((await Read(requests, false, baselineFs)).Results, actual);
        Assert.IsTrue(actual[changedPackage].Results.All(result => result.Issue?.Code == ResourceContentIssueCode.ChangedDuringRead));
        Assert.IsTrue(actual.Where((_, index) => index != changedPackage).All(batch => batch.Results.All(result => result.IsSuccess)));
        Assert.AreEqual(changedPackage == 0 ? 0L : 1L, stats.SavedDecompressions);
        if (changedPackage == 0) Assert.IsTrue(stats.RevokedResources >= 2);
    }

    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(4)] [DataRow(8)]
    public async Task DescriptorWindowBypassPreservesOriginalResults(int concurrency)
    {
        using var temp = new TempDirectory();
        var raw = DbpfFixtureBuilder.Zlib(Content);
        var requests = Requests(temp, [raw, raw], concurrency: concurrency);
        using var fs = new ObservedFileSystem();
        var hasher = new DbpfResourceContentHasher(fs) { ReuseDescriptorLimit = 1 };
        var (actual, stats, _) = await Read(requests, true, fs, hasher);
        Equal((await Read(requests, false)).Results, actual);
        StringAssert.Contains(stats.BypassReason!, "窗口");
        Assert.AreEqual(0L, stats.FingerprintedResources);
    }

    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(4)] [DataRow(8)]
    public async Task FiniteBudgetBypassesBeforeAnyRawRead(int concurrency)
    {
        using var temp = new TempDirectory();
        var raw = DbpfFixtureBuilder.Zlib(Content);
        var requests = Requests(temp, [raw, raw], concurrency: concurrency);
        var exact = 2L * (raw.Length + Content.Length);
        foreach (var total in new[] { exact, exact - 1, 1 })
        {
            requests = requests.Select(request => request with { Limits = request.Limits with { MaxTotalProcessedBytes = total } }).ToArray();
            var (actual, stats, consumed) = await Read(requests, true);
            var baseline = await Read(requests, false);
            Equal(baseline.Results, actual);
            Assert.AreEqual(baseline.Consumed, consumed);
            Assert.IsTrue(consumed <= total);
            Assert.AreEqual(0L, stats.FingerprintedResources);
            Assert.AreEqual(total == exact, actual.All(batch => batch.Results.All(result => result.IsSuccess)));
        }
    }

    [TestMethod]
    [DataRow("locate")] [DataRow("fingerprint")] [DataRow("confirm")] [DataRow("read")] [DataRow("fallback")] [DataRow("publish")]
    [DataRow("fingerprint-block")] [DataRow("confirm-block")] [DataRow("read-block")]
    public async Task CancellationDiscardsPendingReuseEvidence(string cancelPhase)
    {
        using var temp = new TempDirectory();
        var raw = DbpfFixtureBuilder.Zlib(Content);
        var requests = Requests(temp, [raw, raw]);
        using var cancel = new CancellationTokenSource();
        using var fs = new ObservedFileSystem();
        if (cancelPhase == "fallback") fs.Fail = (phase, path) => phase == "read" && path == requests[0].PackagePath;
        if (cancelPhase.EndsWith("-block", StringComparison.Ordinal))
            fs.AfterPayloadRead = (phase, _) => { if (phase == cancelPhase[..^6]) cancel.Cancel(); };
        var called = false;
        var hasher = new DbpfResourceContentHasher(fs)
        {
            ReuseCheckpoint = (phase, path, _) => { fs.SetPhase(path, phase); if (phase == cancelPhase) cancel.Cancel(); },
        };
        var budget = new ResourceContentBudget(long.MaxValue);
        await Assert.ThrowsAsync<OperationCanceledException>(() => hasher.TryHashBatchesWithReuseAsync(requests, budget, _ => called = true, cancel.Token));
        Assert.IsFalse(called);
        Assert.AreEqual(0, fs.OpenStreams);
        using var reservation = budget.ReserveUpTo(1);
        Assert.AreEqual(1, reservation.GrantedBytes);
    }

    [TestMethod]
    public async Task ReusePreservesGroupingGuardAndDiagnostics()
    {
        using var temp = new TempDirectory();
        var raw = DbpfFixtureBuilder.Zlib(Content);
        Requests(temp, [raw, raw, raw]);
        var packages = await new PackageConflictScanner(new PhysicalFileSystemAccess(), LlamaLogicPackageIndexReader.CreateDefault())
            .ScanAsync(new([new("source", temp.Path)]));
        string? baseline = null;
        foreach (var mode in new[] { ResourceContentReuseMode.Off, ResourceContentReuseMode.On })
        foreach (var n in new[] { 1, 2, 4, 8 })
        {
            ResourceContentReuseDiagnostics? diagnostic = null;
            var contents = await new ResourceContentComparer(DbpfResourceContentHasher.CreateDefault()).CompareAsync(new(packages.Candidates)
            { Options = new((DuplicateHashConcurrency)n) { ContentReuse = mode, ContentReuseObserved = value => diagnostic = value } });
            ConflictGroupingSize? size = null;
            new PackageConflictGroupingBuilder(1_000_000).Prepare(packages, contents, sizeObserved: value => size = value);
            Assert.IsNotNull(size);
            new PackageConflictGroupingBuilder(size.EstimatedMemoryBytes).Prepare(packages, contents);
            var rejected = Assert.Throws<ConflictGroupingTooLargeException>(() => new PackageConflictGroupingBuilder(size.EstimatedMemoryBytes - 1)
                .Prepare(packages, contents));
            var facts = JsonSerializer.Serialize(new { Size = size, Rejected = rejected.Size });
            baseline ??= facts;
            Assert.AreEqual(baseline, facts);
            Assert.IsNotNull(diagnostic);
            Assert.AreEqual(3L, diagnostic.NeedsDecompressionResources);
            Assert.AreEqual(mode == ResourceContentReuseMode.On ? 2L : 0L, diagnostic.SavedDecompressions);
        }
    }

    [TestMethod]
    public async Task ConfirmationVersionChangeFallsBackWithoutAcceptingNewStamp()
    {
        using var temp = new TempDirectory();
        var raw = DbpfFixtureBuilder.Zlib(Content);
        var requests = Requests(temp, [raw, raw, raw]);
        var changed = requests[0].PackagePath;
        using var fs = new ObservedFileSystem();
        var hasher = new DbpfResourceContentHasher(fs)
        {
            ReuseCheckpoint = (phase, path, _) => { fs.SetPhase(path, phase); if (phase == "confirm") fs.Changed.Add(changed); },
        };
        var (actual, stats, _) = await Read(requests, true, fs, hasher);
        using var baselineFs = new ObservedFileSystem();
        baselineFs.Changed.Add(changed);
        Equal((await Read(requests, false, baselineFs)).Results, actual);
        Assert.AreEqual(ResourceContentIssueCode.ChangedSinceScan, actual[0].Results[0].Issue?.Code);
        Assert.IsTrue(actual[1].Results[0].IsSuccess);
        Assert.AreEqual(0L, stats.SavedDecompressions);
    }

    [TestMethod]
    public async Task CliWritesReuseDiagnosticsOnlyToStandardError()
    {
        using var temp = new TempDirectory();
        Requests(temp, [DbpfFixtureBuilder.Zlib(Content), DbpfFixtureBuilder.Zlib(Content)]);
        var output = new StringWriter();
        var error = new StringWriter();
        await Cli.CliCommands.RunAsync(["scan-conflicts", "--source", temp.Path, "--json", "-"],
            new Core.Settings.DuplicateSettingsStore(temp.CreateDirectory("settings")), output, error);
        using var json = JsonDocument.Parse(output.ToString());
        StringAssert.Contains(error.ToString(), "资源原始字节复用：请求=Off");
        StringAssert.Contains(error.ToString(), "按大小跳过=0");
        Assert.IsFalse(output.ToString().Contains("资源原始字节复用", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(2)] [DataRow(4)] [DataRow(8)]
    public async Task ReverseFingerprintCompletionStillChoosesStableRepresentative(int concurrency)
    {
        using var temp = new TempDirectory();
        var raw = DbpfFixtureBuilder.Zlib(Content);
        var requests = Requests(temp, [raw, raw], concurrency);
        using var finishedSecond = new ManualResetEventSlim();
        using var fs = new ObservedFileSystem();
        fs.ClosedRead = (phase, path) => { if (phase == "fingerprint" && path == requests[1].PackagePath) finishedSecond.Set(); };
        var reads = new ConcurrentBag<string>();
        var hasher = new DbpfResourceContentHasher(fs)
        {
            ReuseCheckpoint = (phase, path, _) =>
            {
                fs.SetPhase(path, phase);
                if (phase == "fingerprint" && path == requests[0].PackagePath)
                    Assert.IsTrue(finishedSecond.Wait(TimeSpan.FromSeconds(5)));
                if (phase == "read") reads.Add(path);
            },
        };
        var (actual, stats, _) = await Read(requests, true, fs, hasher);
        Equal((await Read(requests, false)).Results, actual);
        CollectionAssert.AreEqual(new[] { requests[0].PackagePath }, reads.ToArray());
        Assert.AreEqual(1L, stats.SavedDecompressions);
    }

    private static ResourceContentBatchRequest[] Requests(TempDirectory temp, byte[][] raw, int concurrency = 2, uint? declared = null) =>
        raw.Select((bytes, index) => Request(temp, $"{index}.package", new DbpfFixtureBuilder()
            .AddContent(bytes, DbpfFixtureBuilder.CompressionZlib, declared ?? (uint)Content.Length, instanceLo: 1)
            .AddContent([(byte)index], instanceLo: 42).Build()) with
        { Limits = ResourceContentLimits.Default with { MaxDegreeOfParallelism = concurrency } }).ToArray();

    private static ResourceContentBatchRequest Request(TempDirectory temp, string name, byte[] bytes, ResourceContentTarget[]? targets = null)
    {
        var path = temp.WriteBytes(name, bytes);
        return new(path, new PhysicalFileSystemAccess().GetFileStamp(path), targets ?? [Target], ResourceContentLimits.Default);
    }

    private static async Task<(IReadOnlyList<ResourceContentBatchResult> Results, ResourceContentReuseDiagnostics Stats, long Consumed)> Read(
        ResourceContentBatchRequest[] requests, bool reuse, ObservedFileSystem? fs = null, DbpfResourceContentHasher? hasher = null)
    {
        hasher ??= new DbpfResourceContentHasher((IFileSystemAccess?)fs ?? new PhysicalFileSystemAccess())
        { ReuseCheckpoint = (phase, path, _) => fs?.SetPhase(path, phase) };
        using var performance = ScanPerformance.Start("test");
        using var stage = ScanPerformance.Measure("content")!;
        var budget = new ResourceContentBudget(requests.Min(request => request.Limits.MaxTotalProcessedBytes));
        ResourceContentReuseDiagnostics stats = new();
        var result = reuse ? await hasher.TryHashBatchesWithReuseAsync(requests, budget, value => stats = value) : null;
        if (result is null)
        {
            var originals = new List<ResourceContentBatchResult>();
            foreach (var request in requests)
            {
                fs?.SetPhase(request.PackagePath, "read");
                originals.Add(await hasher.HashAsync(request, budget));
            }
            result = originals;
        }
        if (reuse && stats.BypassReason is null)
        {
            Assert.AreEqual(stats.DigestPotentialSavings, stats.AccountedSavings, "摘要潜在节省必须逐项对账");
            Assert.AreEqual(stats.PostConfirmationLoss, stats.ConfirmationRejectedLoss + stats.ReaderFallbackLoss + stats.PublicationVersionLoss);
            Assert.IsTrue(stats.RepeatedOriginalTargets >= 0);
        }
        stats = stats with { DecompressionAttempts = stage.DecompressionAttempts, ActualInputBytes = stage.ContentInputBytes, ActualOutputBytes = stage.ContentOutputBytes };
        return (result, stats, budget.ConsumedBytes);
    }

    private static void Equal(IReadOnlyList<ResourceContentBatchResult> expected, IReadOnlyList<ResourceContentBatchResult> actual) =>
        Assert.AreEqual(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    private sealed class ObservedFileSystem : IFileSystemAccess, IDisposable
    {
        private readonly PhysicalFileSystemAccess inner = new();
        private readonly ConcurrentDictionary<string, string> phases = new();
        internal readonly ConcurrentBag<string> Changed = [];
        internal Func<string, string, bool>? Fail;
        internal Func<string, string, bool>? FailOpen;
        internal Action<string, string>? ClosedRead;
        internal Action<string, string>? AfterPayloadRead;
        internal int OpenStreams;
        internal int PeakStreams;
        internal void SetPhase(string path, string phase) => phases[path] = phase;
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public IReadOnlyList<string> EnumerateFileSystemEntries(string path) => inner.EnumerateFileSystemEntries(path);
        public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);
        public FileStamp GetFileStamp(string path)
        {
            var stamp = inner.GetFileStamp(path);
            return Changed.Contains(path) ? stamp with { LastWriteTimeUtc = stamp.LastWriteTimeUtc.AddMinutes(1) } : stamp;
        }
        public Stream OpenRead(string path)
        {
            if (FailOpen?.Invoke(phases.GetValueOrDefault(path, "read"), path) == true) throw new IOException("injected open failure");
            var stream = inner.OpenRead(path);
            var header = new byte[96];
            stream.ReadExactly(header);
            stream.Position = 0;
            var payloadEnd = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x40));
            var active = Interlocked.Increment(ref OpenStreams);
            int peak;
            do { peak = PeakStreams; } while (active > peak && Interlocked.CompareExchange(ref PeakStreams, active, peak) != peak);
            return new ObservedStream(stream, payloadEnd, () =>
            {
                var phase = phases.GetValueOrDefault(path, "read");
                if (Fail?.Invoke(phase, path) == true) throw new IOException("injected payload failure");
                AfterPayloadRead?.Invoke(phase, path);
            }, () => { Interlocked.Decrement(ref OpenStreams); ClosedRead?.Invoke(phases.GetValueOrDefault(path, "read"), path); });
        }
        public void Dispose() => Assert.AreEqual(0, OpenStreams);
    }

    private sealed class ObservedStream(Stream inner, long payloadEnd, Action beforePayload, Action closed) : Stream
    {
        private bool disposed;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        private void Observe() { if (Position >= 96 && Position < payloadEnd) beforePayload(); }
        public override int Read(byte[] buffer, int offset, int count) { Observe(); return inner.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { Observe(); return inner.Read(buffer); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Observe(); return inner.ReadAsync(buffer, cancellationToken); }
        protected override void Dispose(bool disposing)
        { if (!disposed) { disposed = true; inner.Dispose(); closed(); } base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
