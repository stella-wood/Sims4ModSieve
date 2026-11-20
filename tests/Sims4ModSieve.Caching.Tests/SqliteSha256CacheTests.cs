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
    internal static readonly string Hash = Convert.ToHexString(SHA256.HashData("first"u8));
    private static readonly string OtherHash = Convert.ToHexString(SHA256.HashData("second"u8));

    [TestMethod]
    public void CommittedHashesSurviveReopenAndUnicodeCaseChanges()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "缓存", "哈希.sqlite3");
        var cache = new SqliteSha256Cache(database, "app-1");
        Assert.IsTrue(cache.IsAvailable);
        var path = Path.Combine(temp.Path, "中文 Mods", "Étage-Σ-𐐀.package");
        using (var scan = cache.BeginScan())
        {
            Assert.IsTrue(scan.Stage(path, Stamp, Hash.ToLowerInvariant()));
            Assert.IsTrue(scan.Commit());
        }
        var reopened = new SqliteSha256Cache(database, "app-1");
        Assert.AreEqual(Hash, reopened.GetSha256(path.ToLowerInvariant(), Stamp));
        Assert.AreEqual(Hash, reopened.GetSha256(Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path)), Stamp));
        Assert.AreEqual(Hash, reopened.GetSha256(path + Path.DirectorySeparatorChar, Stamp));
        Assert.IsNull(reopened.GetSha256(path + ".off", Stamp));
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
        Save(cache, longS, Stamp, Hash);
        Save(cache, asciiS, Stamp, OtherHash);
        Assert.AreEqual(Hash, cache.GetSha256(longS, Stamp));
        Assert.AreEqual(OtherHash, cache.GetSha256(asciiS.ToLowerInvariant(), Stamp));
    }

    [TestMethod]
    public void ChangedSizeTimeAndIdentityMissWithoutDestroyingOtherVersions()
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
        Assert.IsNull(cache.GetSha256(path, Stamp));
        Assert.IsNull(cache.GetSha256(path, identified with { Identity = identity with { FileIdHigh = 0 } }));
        Assert.IsNull(cache.GetSha256(path, identified with { Identity = identity with { FileIdLow = 0 } }));
        Assert.IsNull(cache.GetSha256(path, identified with { Identity = identity with { VolumeSerialNumber = 0 } }));
        Save(cache, path, Stamp, Hash);
        Assert.AreEqual(Hash, cache.GetSha256(path, Stamp));
        Assert.AreEqual(OtherHash, cache.GetSha256(path, identified));
        Assert.IsNull(cache.GetSha256(path + "-hardlink-alias", identified));
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
        Assert.IsTrue(scan.Stage(first, Stamp, OtherHash));
        Assert.IsTrue(scan.Stage(second, Stamp, OtherHash));
        Assert.AreEqual(Hash, cache.GetSha256(first, Stamp));
        Assert.AreEqual(Hash, reader.GetSha256(first, Stamp));
        Assert.IsNull(reader.GetSha256(second, Stamp));
        Assert.IsTrue(scan.Commit());
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
    [DataRow("app-2", "SHA-256/v1")]
    [DataRow("app-1", "SHA-256/v2")]
    public void VersionChangeInvalidatesOldDataAndRejectsLateCommit(string app, string algorithm)
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "cache.sqlite3");
        var old = new SqliteSha256Cache(database, "app-1");
        var path = Path.Combine(temp.Path, "a.package");
        Save(old, path, Stamp, Hash);
        using var late = old.BeginScan();
        Assert.IsTrue(late.Stage(path, Stamp, OtherHash));
        var current = new SqliteSha256Cache(database, app, algorithm);
        Assert.IsTrue(current.IsAvailable);
        Assert.IsNull(current.GetSha256(path, Stamp));
        Assert.IsNull(old.GetSha256(path, Stamp));
        Assert.IsFalse(late.Commit());
        Save(current, path, Stamp, OtherHash);
        Assert.AreEqual(OtherHash, current.GetSha256(path, Stamp));
        var switchedBack = new SqliteSha256Cache(database, "app-1");
        Assert.IsNull(switchedBack.GetSha256(path, Stamp));
    }

    [TestMethod]
    public async Task KilledProcessCannotPublishStagedRows()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "cache.sqlite3");
        var cache = new SqliteSha256Cache(database, "app-1");
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
        var reopened = new SqliteSha256Cache(database, "app-1");
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
        AssertUnavailable(new SqliteSha256Cache(database, "app-1"), temp.Path);
        AssertUnavailable(new SqliteSha256Cache(Path.Combine(database, "cache.sqlite3"), "app-1"), temp.Path);
        Assert.AreEqual("not a SQLite database", File.ReadAllText(database));
    }

    [TestMethod]
    public void CorruptionAfterOpenDisablesFurtherReadsAndWrites()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "cache.sqlite3");
        var cache = new SqliteSha256Cache(database, "app-1");
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
        var cache = new SqliteSha256Cache(database, "app-1");
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
        var cache = new SqliteSha256Cache(database, "app-1");
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
        var reopened = new SqliteSha256Cache(database, "app-1");
        Assert.AreEqual(Hash, reopened.GetSha256(first, Stamp));
        Assert.IsNull(reopened.GetSha256(second, Stamp));
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
        new(Path.Combine(temp.Path, "cache.sqlite3"), "app-1");

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
