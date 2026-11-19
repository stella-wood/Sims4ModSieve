using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Cli;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Diagnostics;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Settings;
using Sims4ModSieve.Core.Tests;
using Sims4ModSieve.Core.Tests.Packages;

namespace Sims4ModSieve.Packages.Tests;

[TestClass]
public sealed class ScanConcurrencyIntegrationTests
{
    [TestMethod]
    public async Task FullConflictResultsMatchAcrossConcurrencyLevels()
    {
        using var temp = new TempDirectory();
        var root = CreateCorpus(temp);
        // 损坏资源和坏包也进入完整结果比较。
        temp.WriteBytes("Mods/broken-content.package", new DbpfFixtureBuilder()
            .AddContent([1, 2], DbpfFixtureBuilder.CompressionZlib, 100, instanceLo: 1).Build());
        temp.WriteBytes("Mods/broken-index.package", new DbpfFixtureBuilder().WithMagic("NOPE").Build());
        string? baseline = null;
        foreach (var n in new[] { 1, 2, 4, 8 })
        {
            var (packages, contents, duplicates) = await Scan(root, n);
            var builder = new PackageConflictGroupingBuilder(1_000_000);
            var input = builder.Prepare(packages, contents, duplicates: duplicates);
            var result = builder.Build(input, new());
            var merged = ConflictResultGrouping.MergeDuplicatePackageGroups(input, result, duplicates, new HashSet<string>(), packages, null);
            var pairs = new ConflictPairSummaryBuilder().BuildPackagePairs(packages, contents);
            var snapshot = JsonSerializer.Serialize(new
            {
                Packages = packages with { StartedAtUtc = DateTime.UnixEpoch, CompletedAtUtc = DateTime.UnixEpoch },
                Contents = contents with { StartedAtUtc = DateTime.UnixEpoch, CompletedAtUtc = DateTime.UnixEpoch,
                    Limits = contents.Limits with { MaxDegreeOfParallelism = 1 } },
                Duplicates = duplicates with { StartedAtUtc = DateTime.UnixEpoch, CompletedAtUtc = DateTime.UnixEpoch },
                Input = input, Result = result, Merged = merged.Result, Representatives = merged.Representatives, Pairs = pairs,
            });
            baseline ??= snapshot;
            Assert.AreEqual(baseline, snapshot, $"并发 {n}");
            Assert.IsTrue(packages.IncompletePackagePaths.Count > 0);
            Assert.IsTrue(contents.Comparisons.Any(item => !item.IsComplete));
            Assert.IsTrue(input.ExcludedFiles.Count > 0);
            Assert.IsTrue(duplicates.Groups.Count > 0);
            Assert.IsTrue(result.Groups.Any(group => group.Kind == PackageConflictRelationKind.BundleContainment));
            Assert.IsTrue(pairs.Any(pair => pair.FirstContainedInSecond || pair.SecondContainedInFirst));
            Assert.AreEqual(2, merged.Representatives.Count);
        }
    }

    [TestMethod]
    public async Task GroupingGuardMatchesAcrossConcurrencyLevels()
    {
        using var temp = new TempDirectory();
        var root = CreateCorpus(temp);
        string? baseline = null;
        foreach (var n in new[] { 1, 2, 4, 8 })
        {
            var (packages, contents, duplicates) = await Scan(root, n);
            ConflictGroupingSize? size = null;
            new PackageConflictGroupingBuilder(1_000_000).Prepare(packages, contents, duplicates: duplicates, sizeObserved: value => size = value);
            Assert.IsNotNull(size);
            var exact = size.EstimatedMemoryBytes;
            var snapshots = new List<ConflictGroupingSize>();
            new PackageConflictGroupingBuilder(exact).Prepare(packages, contents, duplicates: duplicates, sizeObserved: snapshots.Add);
            Assert.IsFalse(snapshots.Single().ExceedsLimit);
            var error = Assert.Throws<ConflictGroupingTooLargeException>(() => new PackageConflictGroupingBuilder(exact - 1)
                .Prepare(packages, contents, duplicates: duplicates));
            snapshots.Add(error.Size);
            Assert.IsTrue(error.Size.ExceedsLimit);
            var snapshot = JsonSerializer.Serialize(snapshots);
            baseline ??= snapshot;
            Assert.AreEqual(baseline, snapshot, $"并发 {n}");
        }
    }

    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(4)] [DataRow(8)]
    public async Task ConcurrentRealReadsNeverExceedFiniteBudget(int concurrency)
    {
        using var temp = new TempDirectory();
        var root = CreateMixedCorpus(temp, out _, out var hashes);
        var packages = await Index(root);
        var fs = new ObservedFileSystem();
        const long total = 16_001;
        var contents = await new ResourceContentComparer(new DbpfResourceContentHasher(fs)).CompareAsync(new(packages.Candidates,
            ResourceContentLimits.Default with { MaxTotalProcessedBytes = total, MaxDegreeOfParallelism = concurrency })
        {
            Options = new(DuplicateHashConcurrency.Eight),
        });
        Assert.IsTrue(contents.ProcessedBytes <= total);
        var successes = contents.Comparisons.SelectMany(item => item.ContentGroups).ToArray();
        Assert.IsTrue(fs.PayloadBytes + successes.Sum(group => group.DecompressedLength * group.Occurrences.Count) <= contents.ProcessedBytes);
        Assert.IsTrue(successes.All(group => hashes.Contains(group.Sha256)), "不得发布部分内容哈希");
        Assert.IsTrue(contents.Comparisons.All(item => !item.IsComplete));
        Assert.IsTrue(contents.Comparisons.SelectMany(item => item.Uncompared).All(item => item.Issue!.Code == ResourceContentIssueCode.BudgetExhausted));
        Assert.AreEqual(0, fs.OpenStreams);
    }

    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(4)] [DataRow(8)]
    public async Task ExactBudgetSucceedsAndOneByteLessRemainsIncomplete(int concurrency)
    {
        using var temp = new TempDirectory();
        var root = CreateMixedCorpus(temp, out var exact, out _);
        var packages = await Index(root);
        foreach (var total in new[] { exact, exact - 1 })
        {
            var contents = await new ResourceContentComparer(DbpfResourceContentHasher.CreateDefault()).CompareAsync(new(packages.Candidates,
                ResourceContentLimits.Default with { MaxTotalProcessedBytes = total, MaxDegreeOfParallelism = concurrency })
            {
                Options = new(DuplicateHashConcurrency.Eight),
            });
            Assert.IsTrue(contents.ProcessedBytes <= total);
            Assert.AreEqual(total == exact, contents.Comparisons.All(item => item.IsComplete));
            if (total == exact) Assert.AreEqual(exact, contents.ProcessedBytes);
            else Assert.IsTrue(contents.Comparisons.SelectMany(item => item.Uncompared).All(item => item.Sha256 is null));
        }
    }

    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(4)] [DataRow(8)]
    public async Task BudgetWaitCancellationReleasesAllWorkers(int concurrency)
    {
        using var temp = new TempDirectory();
        var root = CreateMixedCorpus(temp, out _, out _);
        var packages = await Index(root);
        using var cancel = new CancellationTokenSource();
        var fs = new ObservedFileSystem(blockPayload: true, expectedStreams: concurrency);
        var observed = new BudgetObserver(new DbpfResourceContentHasher(fs));
        var phases = new List<ResourceContentComparisonPhase>();
        var scan = new ResourceContentComparer(observed).CompareAsync(new(packages.Candidates,
            ResourceContentLimits.Default with { MaxTotalProcessedBytes = 64, MaxDegreeOfParallelism = concurrency }),
            new InlineProgress<ResourceContentComparisonProgress>(p => phases.Add(p.Phase)), cancel.Token);
        try
        {
            await fs.PayloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fs.IndexesRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var budget = observed.Budget!;
            var waiter = budget.ReserveUpToAsync(1, cancellationToken: cancel.Token).AsTask();
            Assert.IsFalse(waiter.IsCompleted, "真实读取正在持有全部预算");
            cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);
            await Assert.ThrowsAsync<OperationCanceledException>(() => scan.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, observed.Active);
            Assert.AreEqual(0, fs.OpenStreams);
            Assert.AreEqual(0L, budget.ConsumedBytes);
            Assert.IsTrue(budget.TryConsume(64), "取消后预留额度全部释放");
            Assert.IsFalse(phases.Contains(ResourceContentComparisonPhase.Completed));
        }
        finally
        {
            cancel.Cancel();
            try { await scan; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    [DataRow(false, false, 4, "SSD")]
    [DataRow(true, true, 1, "HDD")]
    [DataRow(false, true, 1, "SSD+HDD")]
    [DataRow(null, null, 2, "未知")]
    [DataRow(false, null, 2, "SSD+未知")]
    public async Task CliUsesStorageForAllThreeStagesAndWritesDiagnostics(bool? first, bool? second, int expected, string storageClass)
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("first");
        var b = temp.CreateDirectory("second");
        var bytes = new DbpfFixtureBuilder().AddContent([1, 2, 3], instanceLo: 1).Build();
        temp.WriteBytes("first/a.package", bytes);
        temp.WriteBytes("second/b.package", bytes);
        var output = new StringWriter();
        var error = new StringWriter();
        var storage = new FakeStorage(path => path == a ? first : second);
        using var performance = ScanPerformance.Start("CLI");
        var exit = await CliCommands.RunAsync(["scan-conflicts", "--source", a, "--source", b, "--json", "-"],
            new DuplicateSettingsStore(temp.CreateDirectory("settings")), output, error, storageProfile: storage);
        Assert.AreEqual(1, exit);
        // 全 SSD 时 package 两阶段为 min(逻辑 CPU, 8)，整文件哈希仍为 4；其余存储三阶段一致。
        int Expected(string stage) => storageClass == "SSD" && stage != "整文件哈希" ? PackageConflictScanner.SsdAutoMaxDegreeOfParallelism : expected;
        foreach (var stage in new[] { "package 索引", "资源内容", "整文件哈希" })
        {
            var line = error.ToString().Split('\n').Single(line => line.StartsWith($"阶段={stage}；", StringComparison.Ordinal));
            StringAssert.Contains(line, $"请求档位=Auto；策略上限={Expected(stage)}；采用上限={Expected(stage)}；存储分类={storageClass}；回退原因=");
        }
        foreach (var (name, stage) in new[] { ("Package · 排序和读取索引", "package 索引"), ("资源内容 · 复核索引、读取、解压和哈希", "资源内容"), ("整文件核对 · 整文件 SHA-256", "整文件哈希") })
            Assert.AreEqual(Expected(stage), performance.Stages.Single(s => s.Name == name).Parallelism);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.IsTrue(json.RootElement.GetProperty("complete").GetBoolean());
    }

    [TestMethod]
    public async Task CliUsesSavedConcurrencyAndDuplicateCommandGetsStorage()
    {
        using var temp = new TempDirectory();
        var root = CreateCorpus(temp);
        var store = new DuplicateSettingsStore(temp.CreateDirectory("settings"));
        store.Save(new(root, []) { HashConcurrency = DuplicateHashConcurrency.Eight });
        var error = new StringWriter();
        await CliCommands.RunAsync(["scan-conflicts", "--source", root, "--json", "-"], store, new StringWriter(), error,
            storageProfile: new FakeStorage(_ => true));
        Assert.AreEqual(3, error.ToString().Split('\n').Count(line => line.Contains("请求档位=8；策略上限=8；采用上限=8")));
        var emptyStore = new DuplicateSettingsStore(temp.CreateDirectory("no-settings"));
        error = new StringWriter();
        await CliCommands.RunAsync(["scan-duplicates", "--source", root, "--json", "-"], emptyStore, new StringWriter(), error,
            storageProfile: new FakeStorage(_ => false));
        StringAssert.Contains(error.ToString(), "阶段=整文件哈希；请求档位=Auto；策略上限=4；采用上限=4；存储分类=SSD");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CliUnreadableOrUnknownConcurrencyFallsBackToAuto(bool malformedSettings)
    {
        using var temp = new TempDirectory();
        var root = CreateCorpus(temp);
        var store = new DuplicateSettingsStore(temp.CreateDirectory("settings"));
        store.Save(new(root, []) { HashConcurrency = (DuplicateHashConcurrency)3 });
        if (malformedSettings) File.WriteAllText(Path.Combine(store.DataDirectory, "duplicate-settings.json"), "{broken");
        var error = new StringWriter();
        var exit = await CliCommands.RunAsync(["scan-conflicts", "--source", root, "--json", "-"],
            store, new StringWriter(), error, storageProfile: new FakeStorage(_ => false));
        Assert.AreEqual(1, exit);
        var lines = error.ToString().Split('\n');
        var package = PackageConflictScanner.SsdAutoMaxDegreeOfParallelism;
        Assert.AreEqual(1, lines.Count(line => line.StartsWith("阶段=整文件哈希；", StringComparison.Ordinal) && line.Contains("请求档位=Auto；策略上限=4；采用上限=4")));
        Assert.AreEqual(2, lines.Count(line => (line.StartsWith("阶段=package 索引；", StringComparison.Ordinal) || line.StartsWith("阶段=资源内容；", StringComparison.Ordinal))
            && line.Contains($"请求档位=Auto；策略上限={package}；采用上限={package}")));
    }

    private static string CreateCorpus(TempDirectory temp)
    {
        var root = temp.CreateDirectory("Mods");
        var small = new DbpfFixtureBuilder().AddContent([1, 2], instanceLo: 1).AddContent([3, 4], instanceLo: 2)
            .AddContent([5, 6], instanceLo: 3).Build();
        temp.WriteBytes("Mods/small.package", small);
        temp.WriteBytes("Mods/copy.package", small);
        temp.WriteBytes("Mods/bundle.package", new DbpfFixtureBuilder().AddContent([1, 2], instanceLo: 1)
            .AddContent([3, 4], instanceLo: 2).AddContent([5, 6], instanceLo: 3).AddContent([7, 8], instanceLo: 4).Build());
        temp.WriteBytes("Mods/different.package", new DbpfFixtureBuilder().AddContent([9, 8], instanceLo: 1)
            .AddContent([7, 6], instanceLo: 5).Build());
        return root;
    }

    private static string CreateMixedCorpus(TempDirectory temp, out long exact, out HashSet<string> hashes)
    {
        var root = temp.CreateDirectory("Mods");
        var raw = new byte[8192];
        new Random(123).NextBytes(raw);
        var zlib = DbpfFixtureBuilder.Zlib(raw);
        hashes = [Convert.ToHexString(SHA256.HashData(raw))];
        exact = 0;
        for (var i = 0; i < 8; i++)
        {
            var stored = i % 2 == 0 ? raw : zlib;
            temp.WriteBytes($"Mods/{i}.package", new DbpfFixtureBuilder().AddContent(stored,
                i % 2 == 0 ? DbpfFixtureBuilder.CompressionNone : DbpfFixtureBuilder.CompressionZlib,
                (uint)raw.Length, instanceLo: 1).Build());
            exact += stored.Length + raw.Length;
        }
        return root;
    }

    private static Task<PackageConflictScanReport> Index(string root) => new PackageConflictScanner(new PhysicalFileSystemAccess(),
        LlamaLogicPackageIndexReader.CreateDefault()).ScanAsync(new([new("mods", root)]));

    private static async Task<(PackageConflictScanReport, ResourceContentComparisonReport, DuplicateReport)> Scan(string root, int n)
    {
        var options = new ConflictScanOptions((DuplicateHashConcurrency)n);
        var packages = await new PackageConflictScanner(new PhysicalFileSystemAccess(), LlamaLogicPackageIndexReader.CreateDefault())
            .ScanAsync(new([new("mods", root)]) { Options = options });
        var contents = await new ResourceContentComparer(DbpfResourceContentHasher.CreateDefault()).CompareAsync(new(packages.Candidates) { Options = options });
        var duplicates = await DuplicateScanner.CreateDefault().ScanAsync(new([new("mods", root)], root, [".package"]) { HashConcurrency = (DuplicateHashConcurrency)n });
        return (packages, contents, duplicates);
    }

    private sealed class FakeStorage(Func<string, bool?> seek) : IStorageProfile
    {
        public StorageVolumeProfile GetVolume(string path) => new(path, seek(path));
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private sealed class BudgetObserver(IResourceContentHasher inner) : IResourceContentHasher
    {
        public ResourceContentBudget? Budget;
        public int Active;
        public async Task<ResourceContentBatchResult> HashAsync(ResourceContentBatchRequest request, ResourceContentBudget budget,
            CancellationToken cancellationToken = default)
        {
            Interlocked.CompareExchange(ref Budget, budget, null);
            Interlocked.Increment(ref Active);
            try { return await inner.HashAsync(request, budget, cancellationToken); }
            finally { Interlocked.Decrement(ref Active); }
        }
    }

    private sealed class ObservedFileSystem(bool blockPayload = false, int expectedStreams = 0) : IFileSystemAccess
    {
        private readonly PhysicalFileSystemAccess inner = new();
        public int OpenStreams;
        public long PayloadBytes;
        private readonly bool BlockPayload = blockPayload;
        private readonly int ExpectedStreams = expectedStreams;
        private int indexesRead;
        public TaskCompletionSource PayloadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource IndexesRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public IReadOnlyList<string> EnumerateFileSystemEntries(string path) => inner.EnumerateFileSystemEntries(path);
        public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);
        public FileStamp GetFileStamp(string path) => inner.GetFileStamp(path);
        public Stream OpenRead(string path)
        {
            var indexStart = (long)BinaryPrimitives.ReadUInt64LittleEndian(File.ReadAllBytes(path).AsSpan(0x40));
            Interlocked.Increment(ref OpenStreams);
            return new ObservedStream(inner.OpenRead(path), this, indexStart);
        }

        private sealed class ObservedStream(Stream stream, ObservedFileSystem owner, long indexStart) : Stream
        {
            public override bool CanRead => stream.CanRead;
            public override bool CanSeek => stream.CanSeek;
            public override bool CanWrite => false;
            public override long Length => stream.Length;
            public override long Position { get => stream.Position; set => stream.Position = value; }
            public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var position = stream.Position;
                if (position >= 96 && position < indexStart && owner.BlockPayload)
                {
                    owner.PayloadEntered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (position >= 96 && position < indexStart) Interlocked.Add(ref owner.PayloadBytes, count);
                if (position >= indexStart && stream.Position == stream.Length
                    && Interlocked.Increment(ref owner.indexesRead) == owner.ExpectedStreams) owner.IndexesRead.TrySetResult();
                return count;
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { stream.Dispose(); Interlocked.Decrement(ref owner.OpenStreams); }
                base.Dispose(disposing);
            }
            public override void Flush() => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
