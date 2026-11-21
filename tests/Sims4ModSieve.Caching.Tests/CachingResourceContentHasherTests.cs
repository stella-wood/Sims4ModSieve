using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Tests;

namespace Sims4ModSieve.Caching.Tests;

[TestClass]
public sealed class CachingResourceContentHasherTests
{
    private static readonly ResourceContentTarget First = new(new(1, 2, ulong.MaxValue), 0);
    private static readonly ResourceContentTarget Second = new(new(2, 3, ulong.MaxValue), 1);

    [TestMethod]
    public async Task SecondScanSkipsLiveHasherForUnchangedPackage()
    {
        using var fixture = new ScanFixture();
        var request = fixture.Request(First, Second);
        var cold = await fixture.ScanAsync(request);
        var warm = await fixture.ScanAsync(request);

        Assert.HasCount(1, fixture.Hasher.Requests);
        Assert.AreEqual(2L, fixture.Hits);
        Assert.AreEqual(0L, fixture.Misses);
        AssertBatchEqual(cold, warm);
    }

    [TestMethod]
    public async Task PartialHitsReadOnlyMissesAndKeepRequestOrder()
    {
        using var fixture = new ScanFixture();
        var third = new ResourceContentTarget(new(3, 4, ulong.MaxValue), 2);
        await fixture.ScanAsync(fixture.Request(Second));
        var request = fixture.Request(third, Second, First);
        var result = await fixture.ScanAsync(request);

        CollectionAssert.AreEqual(new[] { third, First }, fixture.Hasher.Requests[^1].Targets.ToArray());
        CollectionAssert.AreEqual(request.Targets.Select(Success).ToArray(), result.Results.ToArray());
        Assert.AreEqual(1L, fixture.Hits);
        Assert.AreEqual(2L, fixture.Misses);
        AssertBatchEqual(result, await fixture.ScanAsync(request));
        Assert.HasCount(2, fixture.Hasher.Requests);
    }

    [TestMethod]
    public async Task ChangedStampRechecksWholeBatchAndRetiresOldRowsOnCommit()
    {
        using var fixture = new ScanFixture();
        var original = fixture.Request(First, Second);
        await fixture.ScanAsync(original);
        fixture.ChangeStamp();

        var rejected = await fixture.ScanAsync(original);
        CollectionAssert.AreEqual(original.Targets.ToArray(), fixture.Hasher.Requests[^1].Targets.ToArray());
        Assert.AreEqual(0L, fixture.Hits);
        Assert.IsTrue(rejected.Results.All(result => result.Issue?.Code == ResourceContentIssueCode.ChangedSinceScan));

        var current = fixture.Request(First, Second);
        var result = await fixture.ScanAsync(current);
        Assert.AreNotEqual(original.ExpectedStamp, result.StampAfterRead);
        Assert.AreEqual(0L, fixture.Hits);
        Assert.AreEqual(2L, fixture.Misses);
        Assert.AreEqual(2L, fixture.CountRows());
        AssertBatchEqual(result, await fixture.ScanAsync(current));
        Assert.HasCount(3, fixture.Hasher.Requests);
    }

    [TestMethod]
    public async Task SameTgiWithDifferentOrdinalHasSeparateHash()
    {
        using var fixture = new ScanFixture();
        var otherOrdinal = First with { Ordinal = 1 };
        await fixture.ScanAsync(fixture.Request(First));

        var result = await fixture.ScanAsync(fixture.Request(First, otherOrdinal));
        CollectionAssert.AreEqual(new[] { otherOrdinal }, fixture.Hasher.Requests[^1].Targets.ToArray());
        CollectionAssert.AreEqual(new[] { Success(First), Success(otherOrdinal) }, result.Results.ToArray());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ChangedReadLimitsMissEvenForSameStampAndTarget(bool changeStoredLimit)
    {
        using var fixture = new ScanFixture();
        var request = fixture.Request(First);
        await fixture.ScanAsync(request);
        var limits = changeStoredLimit
            ? request.Limits with { MaxStoredBytesPerResource = 1 }
            : request.Limits with { MaxDecompressedBytesPerResource = 1 };

        await fixture.ScanAsync(request with { Limits = limits });

        Assert.HasCount(2, fixture.Hasher.Requests);
        Assert.AreEqual(0L, fixture.Hits);
        Assert.AreEqual(1L, fixture.Misses);
    }

    [TestMethod]
    public async Task FileChangedDuringReadInvalidatesPartialHitsAndDoesNotStage()
    {
        using var fixture = new ScanFixture();
        await fixture.ScanAsync(fixture.Request(First));
        var request = fixture.Request(First, Second);
        fixture.Hasher.DuringRead = fixture.ChangeStamp;

        var changed = await fixture.ScanAsync(request);

        Assert.AreNotEqual(request.ExpectedStamp, changed.StampAfterRead);
        Assert.IsTrue(changed.Results.All(result => result.Issue?.Code == ResourceContentIssueCode.ChangedDuringRead));
        CollectionAssert.AreEqual(new[] { Second }, fixture.Hasher.Requests[^1].Targets.ToArray());
        Assert.AreEqual(1L, fixture.CountRows());

        // Even if a reader supplies a digest with a different final stamp, it cannot be staged.
        fixture.Hasher.ReturnHashesAfterChange = true;
        await fixture.ScanAsync(fixture.Request(First, Second));
        Assert.AreEqual(1L, fixture.CountRows());
    }

    [TestMethod]
    [DataRow("disabled")]
    [DataRow("bad-database")]
    [DataRow("bad-record")]
    public async Task DisabledOrBadCacheReturnsExactlyTheLiveResults(string mode)
    {
        using var fixture = new ScanFixture();
        var firstRequest = fixture.Request(First);
        var other = fixture.NewPackage();
        var secondRequest = fixture.Request(Second) with { PackagePath = other, ExpectedStamp = fixture.Files.GetFileStamp(other) };
        async Task<ResourceContentBatchResult[]> ReadBothAsync(IResourceContentHasher hasher) =>
            [await hasher.HashAsync(firstRequest, Unlimited()), await hasher.HashAsync(secondRequest, Unlimited())];
        var expected = await ReadBothAsync(fixture.Hasher);
        if (mode == "bad-database") File.WriteAllText(fixture.Database, "bad database");
        if (mode == "bad-record")
        {
            // A wrong but intact hit in the first package must be discarded after the second reveals corruption.
            fixture.Hasher.HashTarget = target => ResourceContentResult.Success(target, new string('F', 64), 0);
            await fixture.WithCacheAsync(ReadBothAsync);
            fixture.Hasher.HashTarget = Success;
            fixture.Sql("UPDATE resource_hashes SET checksum = zeroblob(32) WHERE path = $path;", other);
        }

        var actual = mode == "disabled"
            ? await ReadBothAsync(fixture.Hasher) : await fixture.WithCacheAsync(ReadBothAsync, expectCommit: false);

        AssertBatchEqual(expected[0], actual[0]);
        AssertBatchEqual(expected[1], actual[1]);
        Assert.AreEqual(mode == "bad-record" ? 1L : 0L, fixture.Hits);
        if (mode == "disabled") Assert.IsFalse(File.Exists(fixture.Database));
    }

    [TestMethod]
    public async Task FailuresAndBudgetExhaustionNeverEnterCache()
    {
        using var fixture = new ScanFixture();
        const string failed = ResourceContentIssueCode.UnsupportedCompression;
        fixture.Hasher.HashTarget = target => target == Second
            ? ResourceContentResult.Failure(target, new(failed, ResourceContentStage.Decompress, "unsupported"))
            : Success(target);
        var request = fixture.Request(First, Second);
        var cold = await fixture.ScanAsync(request);
        Assert.IsTrue(cold.Results[0].IsSuccess);
        Assert.AreEqual(failed, cold.Results[1].Issue?.Code);

        var constrained = request with { Limits = request.Limits with { MaxTotalProcessedBytes = 1 } };
        var exhausted = await fixture.ScanAsync(constrained, new ResourceContentBudget(1));
        Assert.IsTrue(exhausted.Results.All(result => result.Issue?.Code == ResourceContentIssueCode.BudgetExhausted));
        CollectionAssert.AreEqual(request.Targets.ToArray(), fixture.Hasher.Requests[^1].Targets.ToArray());
        Assert.AreEqual(1L, fixture.CountRows());

        AssertBatchEqual(cold, await fixture.ScanAsync(request));
        CollectionAssert.AreEqual(new[] { Second }, fixture.Hasher.Requests[^1].Targets.ToArray());
        Assert.AreEqual(1L, fixture.CountRows());
    }

    private static ResourceContentResult Success(ResourceContentTarget target) => ResourceContentResult.Success(
        target, Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(target.Ordinal))), target.Ordinal + 7);

    private static ResourceContentBudget Unlimited() => new(long.MaxValue);

    private static void AssertBatchEqual(ResourceContentBatchResult expected, ResourceContentBatchResult actual)
    {
        Assert.AreEqual(expected.PackagePath, actual.PackagePath);
        Assert.AreEqual(expected.StampAfterRead, actual.StampAfterRead);
        CollectionAssert.AreEqual(expected.Results.ToArray(), actual.Results.ToArray());
    }

    private sealed class LiveHasher(PhysicalFileSystemAccess files) : IResourceContentHasher
    {
        public List<ResourceContentBatchRequest> Requests { get; } = [];
        public Func<ResourceContentTarget, ResourceContentResult> HashTarget { get; set; } = Success;
        public Action? DuringRead { get; set; }
        public bool ReturnHashesAfterChange { get; set; }

        public Task<ResourceContentBatchResult> HashAsync(ResourceContentBatchRequest request,
            ResourceContentBudget budget, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var before = files.GetFileStamp(request.PackagePath);
            if (before != request.ExpectedStamp) return Task.FromResult(InvalidBatch(before, ResourceContentIssueCode.ChangedSinceScan));
            var results = request.Targets.Select(target => budget.TryConsume(2) ? HashTarget(target)
                : ResourceContentResult.Failure(target, new(ResourceContentIssueCode.BudgetExhausted, ResourceContentStage.Budget, "budget"))).ToArray();
            DuringRead?.Invoke();
            var after = files.GetFileStamp(request.PackagePath);
            return Task.FromResult(after == before || ReturnHashesAfterChange
                ? new ResourceContentBatchResult(request.PackagePath, after, results)
                : InvalidBatch(after, ResourceContentIssueCode.ChangedDuringRead));

            ResourceContentBatchResult InvalidBatch(FileStamp stamp, string code) => new(request.PackagePath, stamp,
                request.Targets.Select(target => ResourceContentResult.Failure(target, new(code, ResourceContentStage.Stability, code))).ToArray());
        }
    }

    private sealed class ScanFixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly string _package;

        public ScanFixture()
        {
            _package = _temp.WriteFile("test.package", "fixture");
            Hasher = new(Files);
        }

        public string Database => Path.Combine(_temp.Path, "hashes.sqlite3");
        public PhysicalFileSystemAccess Files { get; } = new();
        public LiveHasher Hasher { get; }
        public long Hits { get; private set; }
        public long Misses { get; private set; }

        public string NewPackage() => _temp.WriteFile("other.package", "fixture");
        public void ChangeStamp() => File.SetLastWriteTimeUtc(_package, Files.GetFileStamp(_package).LastWriteTimeUtc.AddMinutes(1));
        public ResourceContentBatchRequest Request(params ResourceContentTarget[] targets) =>
            new(_package, Files.GetFileStamp(_package), targets, ResourceContentLimits.Default);

        public Task<ResourceContentBatchResult> ScanAsync(ResourceContentBatchRequest request, ResourceContentBudget? budget = null) =>
            WithCacheAsync(hasher => hasher.HashAsync(request, budget ?? Unlimited()));

        public async Task<T> WithCacheAsync<T>(Func<IResourceContentHasher, Task<T>> run, bool expectCommit = true)
        {
            using var cache = new CachingStableFileHasher(new SqliteSha256Cache(Database), Files);
            var result = await cache.RunAsync(Hasher, (_, hasher) => run(hasher));
            Hits = cache.ResourceHits;
            Misses = cache.ResourceMisses;
            Assert.AreEqual(expectCommit, cache.Commit());
            return result;
        }

        public long CountRows() => (long)Sql("SELECT count(*) FROM resource_hashes;")!;

        public object? Sql(string sql, string? path = null)
        {
            using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
            connection.Open();
            connection.CreateCollation("ORDINAL_IGNORE_CASE", StringComparer.OrdinalIgnoreCase.Compare);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (path is not null) command.Parameters.AddWithValue("$path", path);
            return command.ExecuteScalar();
        }

        public void Dispose() => _temp.Dispose();
    }
}
