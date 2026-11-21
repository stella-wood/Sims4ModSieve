using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Tests;

namespace Sims4ModSieve.Caching.Tests;

[TestClass]
public sealed class SqliteSha256CacheTests
{
    internal static readonly FileStamp Stamp = new(123, new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc));
    private static readonly FileStamp IdentifiedStamp = Stamp with { Identity = new FileIdentity(1, 2, 3) };
    internal static readonly string Hash = Convert.ToHexString(SHA256.HashData("first"u8));
    private static readonly string OtherHash = Convert.ToHexString(SHA256.HashData("second"u8));

    [TestMethod]
    public void CommittedHashesSurviveReopenAndUnicodeCaseChanges()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "缓存", "哈希.sqlite3");
        var cache = new SqliteSha256Cache(database);
        Assert.IsTrue(cache.IsAvailable);
        var path = Path.Combine(temp.Path, "中文 Mods", "Étage-Σ-𐐀.package");
        using (var scan = cache.BeginScan())
        {
            Assert.IsTrue(scan.Stage(path, IdentifiedStamp, Hash.ToLowerInvariant()));
            Assert.IsTrue(scan.Commit());
        }
        var reopened = new SqliteSha256Cache(database);
        Assert.AreEqual(Hash, reopened.GetSha256(path.ToLowerInvariant(), IdentifiedStamp));
        Assert.AreEqual(Hash, reopened.GetSha256(Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path)), IdentifiedStamp));
        Assert.AreEqual(Hash, reopened.GetSha256(path + Path.DirectorySeparatorChar, IdentifiedStamp));
        Assert.IsNull(reopened.GetSha256(path + ".off", IdentifiedStamp));
    }

    [TestMethod]
    public void RootPathsKeepTheirRootAndDoNotAliasOtherPaths()
    {
        using var temp = new TempDirectory();
        var cache = CreateCache(temp);
        var root = Path.GetPathRoot(temp.Path)!;
        Save(cache, root, Stamp, Hash);
        Assert.AreEqual(Hash, cache.GetSha256(root, Stamp));
        Assert.IsNull(cache.GetSha256(temp.Path, Stamp));
    }

    [TestMethod]
    public void UnicodeNamesThatOnlyCollideWhenUppercasedKeepSeparateRecords()
    {
        using var temp = new TempDirectory();
        var cache = CreateCache(temp);
        var longS = Path.Combine(temp.Path, "ſ.package");
        var asciiS = Path.Combine(temp.Path, "S.package");
        Assert.IsFalse(StringComparer.OrdinalIgnoreCase.Equals(longS, asciiS));
        Save(cache, longS, IdentifiedStamp, Hash);
        Save(cache, asciiS, IdentifiedStamp, OtherHash);
        Assert.AreEqual(Hash, cache.GetSha256(longS, IdentifiedStamp));
        Assert.AreEqual(OtherHash, cache.GetSha256(asciiS.ToLowerInvariant(), IdentifiedStamp));
    }

    [TestMethod]
    public void UnidentifiedCaseVariantsWithEqualFileMetadataKeepSeparateHashes()
    {
        using var temp = new TempDirectory();
        var cache = CreateCache(temp);
        var lowerPath = temp.WriteFile("sample.package", "abc");
        var upperPath = Path.Combine(temp.Path, "SAMPLE.package");
        var lowerHash = Convert.ToHexString(SHA256.HashData("abc"u8));
        var upperHash = Convert.ToHexString(SHA256.HashData("xyz"u8));
        var stamp = new FileStamp(3, Stamp.LastWriteTimeUtc);
        if (!File.Exists(upperPath))
        {
            temp.WriteFile("SAMPLE.package", "xyz");
            File.SetLastWriteTimeUtc(lowerPath, stamp.LastWriteTimeUtc);
            File.SetLastWriteTimeUtc(upperPath, stamp.LastWriteTimeUtc);
            var fileSystem = new PhysicalFileSystemAccess();
            Assert.AreEqual(stamp, fileSystem.GetFileStamp(lowerPath) with { Identity = null });
            Assert.AreEqual(stamp, fileSystem.GetFileStamp(upperPath) with { Identity = null });
            Assert.AreEqual(lowerHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(lowerPath))));
            Assert.AreEqual(upperHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(upperPath))));
        }

        Save(cache, lowerPath, stamp, lowerHash);
        Assert.IsNull(cache.GetSha256(upperPath, stamp));
        using var scan = cache.BeginScan();
        Assert.IsTrue(scan.Stage(lowerPath, stamp, lowerHash));
        Assert.IsTrue(scan.Stage(upperPath, stamp, upperHash));
        Assert.IsTrue(scan.Commit());

        var reopened = CreateCache(temp);
        Assert.AreEqual(lowerHash, reopened.GetSha256(lowerPath, stamp));
        Assert.AreEqual(upperHash, reopened.GetSha256(upperPath, stamp));
    }

    [TestMethod]
    public void ChangedSizeTimeAndIdentityMissAndCommitReplacesOldVersions()
    {
        using var temp = new TempDirectory();
        var cache = CreateCache(temp);
        var path = temp.WriteFile("真实文件.package", "abc");
        var fileSystem = new PhysicalFileSystemAccess();
        var before = fileSystem.GetFileStamp(path);
        Save(cache, path, before, Hash);
        File.AppendAllText(path, "changed");
        Assert.IsNull(cache.GetSha256(path, fileSystem.GetFileStamp(path)));
        File.WriteAllText(path, "abc");
        File.SetLastWriteTimeUtc(path, before.LastWriteTimeUtc.AddMinutes(1));
        Assert.IsNull(cache.GetSha256(path, fileSystem.GetFileStamp(path)));
        var identity = new FileIdentity(ulong.MaxValue, 1UL << 63, ulong.MaxValue - 1);
        var identified = Stamp with { Identity = identity };
        Save(cache, path, identified, OtherHash);
        Assert.AreEqual(OtherHash, cache.GetSha256(path, identified));
        Assert.IsNull(cache.GetSha256(path, before));
        Assert.IsNull(cache.GetSha256(path, Stamp));
        Assert.IsNull(cache.GetSha256(path, identified with { Identity = identity with { FileIdHigh = 0 } }));
        Assert.IsNull(cache.GetSha256(path, identified with { Identity = identity with { FileIdLow = 0 } }));
        Assert.IsNull(cache.GetSha256(path, identified with { Identity = identity with { VolumeSerialNumber = 0 } }));
        Save(cache, path, Stamp, Hash);
        Assert.AreEqual(Hash, cache.GetSha256(path, Stamp));
        Assert.IsNull(cache.GetSha256(path, identified));
        Assert.IsNull(cache.GetSha256(path + "-hardlink-alias", identified));
    }

    [TestMethod]
    public void CommitKeepsOnlyLastStagedVersionAndLeavesOtherPaths()
    {
        using var temp = new TempDirectory();
        var cache = CreateCache(temp);
        var path = Path.Combine(temp.Path, "mod.package");
        var alias = Path.Combine(temp.Path, "alias.package");
        var middle = IdentifiedStamp with { Length = 200 };
        var latest = IdentifiedStamp with { LastWriteTimeUtc = IdentifiedStamp.LastWriteTimeUtc.AddMinutes(1) };
        Save(cache, path, IdentifiedStamp, Hash);
        Save(cache, alias, IdentifiedStamp, Hash);
        using var scan = cache.BeginScan();
        Assert.IsTrue(scan.Stage(path, middle, OtherHash));
        Assert.IsTrue(scan.Stage(path.ToUpperInvariant(), latest, OtherHash));
        Assert.AreEqual(Hash, cache.GetSha256(path, IdentifiedStamp));
        Assert.IsTrue(scan.Commit());
        Assert.IsNull(cache.GetSha256(path, IdentifiedStamp));
        Assert.IsNull(cache.GetSha256(path, middle));
        Assert.AreEqual(OtherHash, cache.GetSha256(path, latest));
        Assert.AreEqual(Hash, cache.GetSha256(alias, IdentifiedStamp));
    }

    [TestMethod]
    public void StagingIsInvisibleAndCommitPublishesAllRecordsTogether()
    {
        using var temp = new TempDirectory();
        var cache = CreateCache(temp);
        var reader = CreateCache(temp);
        var first = Path.Combine(temp.Path, "a.package");
        var second = Path.Combine(temp.Path, "b.package");
        Save(cache, first, Stamp, Hash);
        using var scan = cache.BeginScan();
        using var readerScan = reader.BeginScan();
        Assert.IsTrue(scan.Stage(first, Stamp, OtherHash));
        Assert.IsTrue(scan.Stage(second, Stamp, OtherHash));
        Assert.AreEqual(Hash, cache.GetSha256(first, Stamp));
        Assert.AreEqual(Hash, scan.GetSha256(first, Stamp));
        Assert.IsNull(scan.GetSha256(second, Stamp));
        Assert.AreEqual(Hash, reader.GetSha256(first, Stamp));
        Assert.IsNull(reader.GetSha256(second, Stamp));
        Assert.IsNull(readerScan.GetSha256(second, Stamp));
        Assert.IsTrue(scan.Commit());
        Assert.IsNull(scan.GetSha256(first, Stamp));
        Assert.AreEqual(OtherHash, readerScan.GetSha256(first, Stamp));
        Assert.AreEqual(OtherHash, readerScan.GetSha256(second, Stamp));
        Assert.AreEqual(OtherHash, reader.GetSha256(first, Stamp));
        Assert.AreEqual(OtherHash, reader.GetSha256(second, Stamp));
        Assert.IsFalse(scan.Commit());
    }

    [TestMethod]
    public void DisposeAndCancelledCommitDiscardStagingAndPreserveCommittedData()
    {
        using var temp = new TempDirectory();
        var cache = CreateCache(temp);
        var path = Path.Combine(temp.Path, "a.package");
        Save(cache, path, Stamp, Hash);
        using (var discarded = cache.BeginScan())
            Assert.IsTrue(discarded.Stage(path, Stamp, OtherHash));
        Assert.AreEqual(Hash, cache.GetSha256(path, Stamp));
        using var scan = cache.BeginScan();
        Assert.IsTrue(scan.Stage(path, Stamp, OtherHash));
        Assert.IsTrue(scan.Stage(path + "-new", Stamp, Hash));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => scan.Commit(cancellation.Token));
        Assert.IsFalse(scan.Commit());
        Assert.AreEqual(Hash, cache.GetSha256(path, Stamp));
        Assert.IsNull(cache.GetSha256(path + "-new", Stamp));
        Assert.ThrowsExactly<OperationCanceledException>(() => cache.GetSha256(path, Stamp, cancellation.Token));
    }

    [TestMethod]
    public async Task IndependentScansCanStageConcurrentlyWithoutSharingConnections()
    {
        using var temp = new TempDirectory();
        var cache = CreateCache(temp);
        using var first = cache.BeginScan();
        using var second = cache.BeginScan();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            Assert.IsTrue((i % 2 == 0 ? first : second).Stage(Path.Combine(temp.Path, $"{i}.package"), Stamp, Hash));
        })));
        var commits = await Task.WhenAll(Task.Run(() => first.Commit()), Task.Run(() => second.Commit()));
        Assert.IsTrue(commits.All(success => success));
        for (var i = 0; i < 20; i++)
            Assert.AreEqual(Hash, cache.GetSha256(Path.Combine(temp.Path, $"{i}.package"), Stamp));
    }

    [TestMethod]
    public void AlgorithmVersionChangeInvalidatesOldDataAndRejectsLateCommit()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "cache.sqlite3");
        var old = new SqliteSha256Cache(database);
        var path = Path.Combine(temp.Path, "a.package");
        Save(old, path, Stamp, Hash);
        using var late = old.BeginScan();
        Assert.IsTrue(late.Stage(path, Stamp, OtherHash));
        var current = new SqliteSha256Cache(database, "SHA-256/v2");
        Assert.IsTrue(current.IsAvailable);
        Assert.IsNull(current.GetSha256(path, Stamp));
        Assert.IsNull(old.GetSha256(path, Stamp));
        Assert.IsFalse(late.Commit());
        Save(current, path, Stamp, OtherHash);
        Assert.AreEqual(OtherHash, current.GetSha256(path, Stamp));
        var switchedBack = new SqliteSha256Cache(database);
        Assert.IsNull(switchedBack.GetSha256(path, Stamp));
    }

    [TestMethod]
    public void LegacyApplicationVersionIsRemovedWithoutInvalidatingCompatibleHashes()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "cache.sqlite3");
        var cache = CreateCache(temp);
        var path = Path.Combine(temp.Path, "a.package");
        Save(cache, path, Stamp, Hash);
        using var scan = cache.BeginScan();
        Assert.IsTrue(scan.Stage(path, Stamp, OtherHash));
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE metadata ADD COLUMN application_version TEXT NOT NULL DEFAULT 'old-app';";
            command.ExecuteNonQuery();
        }

        var reopened = CreateCache(temp);
        Assert.IsTrue(reopened.IsAvailable);
        Assert.AreEqual(Hash, reopened.GetSha256(path, Stamp));
        Assert.IsTrue(scan.Commit());
        Assert.AreEqual(OtherHash, reopened.GetSha256(path, Stamp));
        using var inspection = new SqliteConnection($"Data Source={database};Pooling=False");
        inspection.Open();
        using var columns = inspection.CreateCommand();
        columns.CommandText = "SELECT count(*) FROM pragma_table_info('metadata') WHERE name = 'application_version';";
        Assert.AreEqual(0L, columns.ExecuteScalar());
    }

    [TestMethod]
    public void PreviousPathKeyVersionDiscardsOldHashes()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "cache.sqlite3");
        var cache = CreateCache(temp);
        var path = Path.Combine(temp.Path, "Old.package");
        Save(cache, path, Stamp, Hash);
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            connection.CreateCollation("ORDINAL_IGNORE_CASE", StringComparer.OrdinalIgnoreCase.Compare);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE hashes SET path_key = path; UPDATE metadata SET path_key_version = 1;";
            command.ExecuteNonQuery();
        }

        var reopened = CreateCache(temp);
        Assert.IsTrue(reopened.IsAvailable);
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM hashes NOT INDEXED;";
            Assert.AreEqual(0L, command.ExecuteScalar());
        }
        Save(reopened, path, Stamp, OtherHash);
        Assert.AreEqual(OtherHash, reopened.GetSha256(path, Stamp));
    }

    [TestMethod]
    public async Task KilledProcessCannotPublishStagedRows()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "cache.sqlite3");
        var cache = new SqliteSha256Cache(database);
        Save(cache, database + "-old", Stamp, Hash);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(SqliteSha256CacheTests).Assembly.Location);
        start.ArgumentList.Add(database);
        using var child = Process.Start(start)!;
        try
        {
            Assert.AreEqual("staged", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        var reopened = new SqliteSha256Cache(database);
        Assert.IsTrue(reopened.IsAvailable);
        Assert.AreEqual(Hash, reopened.GetSha256(database + "-old", Stamp));
        Assert.IsNull(reopened.GetSha256(database + "-new-1", Stamp));
        Assert.IsNull(reopened.GetSha256(database + "-new-2", Stamp));
    }

    [TestMethod]
    public void CorruptDatabaseAndUnavailableDirectoryBehaveAsEmptyCache()
    {
        using var temp = new TempDirectory();
        var database = temp.WriteFile("坏缓存.sqlite3", "not a SQLite database");
        AssertUnavailable(new SqliteSha256Cache(database), temp.Path);
        AssertUnavailable(new SqliteSha256Cache(Path.Combine(database, "cache.sqlite3")), temp.Path);
        Assert.AreEqual("not a SQLite database", File.ReadAllText(database));
    }

    [TestMethod]
    public void CorruptionAfterOpenDisablesFurtherReadsAndWrites()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "cache.sqlite3");
        var cache = new SqliteSha256Cache(database);
        Save(cache, temp.Path, Stamp, Hash);
        File.WriteAllText(database, "corrupt after initialization");
        Assert.IsNull(cache.GetSha256(temp.Path, Stamp));
        AssertUnavailable(cache, temp.Path);
    }

    [TestMethod]
    public void DamagedRecordChecksumIsRejectedAndDisablesCache()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "cache.sqlite3");
        var cache = new SqliteSha256Cache(database);
        Save(cache, temp.Path, Stamp, Hash);
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            connection.CreateCollation("ORDINAL_IGNORE_CASE", StringComparer.OrdinalIgnoreCase.Compare);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE hashes SET sha256 = zeroblob(32);";
            command.ExecuteNonQuery();
        }
        Assert.IsNull(cache.GetSha256(temp.Path, Stamp));
        AssertUnavailable(cache, temp.Path);
    }

    [TestMethod]
    public void FailedPublicationRollsBackAllChangesAndKeepsOldCommittedHash()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "cache.sqlite3");
        var cache = new SqliteSha256Cache(database);
        var first = Path.Combine(temp.Path, "a.package");
        var second = Path.Combine(temp.Path, "b.package");
        Save(cache, first, Stamp, Hash);
        using var scan = cache.BeginScan();
        Assert.IsTrue(scan.Stage(first, Stamp, OtherHash));
        Assert.IsTrue(scan.Stage(second, Stamp, OtherHash));
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            connection.CreateCollation("ORDINAL_IGNORE_CASE", StringComparer.OrdinalIgnoreCase.Compare);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER fail_publication BEFORE INSERT ON hashes
                WHEN (SELECT count(*) FROM hashes WHERE sha256 = NEW.sha256) > 0
                BEGIN SELECT RAISE(FAIL, 'simulated write failure'); END;
                """;
            command.ExecuteNonQuery();
        }
        Assert.IsFalse(scan.Commit());
        var reopened = new SqliteSha256Cache(database);
        Assert.AreEqual(Hash, reopened.GetSha256(first, Stamp));
        Assert.IsNull(reopened.GetSha256(second, Stamp));
    }

    [TestMethod]
    public void CommitTimesOutWhileAnotherConnectionHoldsWriterLockAndPublishesNothing()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "cache.sqlite3");
        var cache = CreateCache(temp);
        var first = Path.Combine(temp.Path, "old.package");
        var second = Path.Combine(temp.Path, "new-1.package");
        var third = Path.Combine(temp.Path, "new-2.package");
        Save(cache, first, Stamp, Hash);
        using var scan = cache.BeginScan();
        using (var writer = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            writer.Open();
            using var transaction = writer.BeginTransaction(deferred: false);
            Assert.AreEqual(Hash, scan.GetSha256(first, Stamp));
            Assert.IsTrue(scan.Stage(first, Stamp, OtherHash));
            Assert.IsTrue(scan.Stage(second, Stamp, OtherHash));
            Assert.IsTrue(scan.Stage(third, Stamp, Hash));
            var start = Stopwatch.GetTimestamp();
            Assert.IsFalse(scan.Commit());
            Assert.IsTrue(Stopwatch.GetElapsedTime(start) >= TimeSpan.FromMilliseconds(500));
            Assert.IsFalse(cache.IsAvailable);
            transaction.Rollback();
        }

        var reopened = CreateCache(temp);
        Assert.IsTrue(reopened.IsAvailable);
        Assert.AreEqual(Hash, reopened.GetSha256(first, Stamp));
        Assert.IsNull(reopened.GetSha256(second, Stamp));
        Assert.IsNull(reopened.GetSha256(third, Stamp));
    }

    [TestMethod]
    public void InvalidDigestCannotBePublishedAsSuccessfulHash()
    {
        using var temp = new TempDirectory();
        var cache = CreateCache(temp);
        using var scan = cache.BeginScan();
        foreach (var invalid in new[] { "", "1234", new string('x', 64), new string('a', 66) })
            Assert.ThrowsExactly<ArgumentException>(() => scan.Stage(temp.Path, Stamp, invalid));
        Assert.IsTrue(scan.Commit());
        Assert.IsNull(cache.GetSha256(temp.Path, Stamp));
    }

    private static SqliteSha256Cache CreateCache(TempDirectory temp) =>
        new(Path.Combine(temp.Path, "cache.sqlite3"));

    private static void Save(SqliteSha256Cache cache, string path, FileStamp stamp, string hash)
    {
        using var scan = cache.BeginScan();
        Assert.IsTrue(scan.Stage(path, stamp, hash));
        Assert.IsTrue(scan.Commit());
    }

    private static void AssertUnavailable(SqliteSha256Cache cache, string path)
    {
        Assert.IsFalse(cache.IsAvailable);
        Assert.IsNull(cache.GetSha256(path, Stamp));
        using var scan = cache.BeginScan();
        Assert.IsFalse(scan.Stage(path, Stamp, Hash));
        Assert.IsFalse(scan.Commit());
    }
}
