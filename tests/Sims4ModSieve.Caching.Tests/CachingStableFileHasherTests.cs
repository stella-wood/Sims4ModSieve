using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Tests;

namespace Sims4ModSieve.Caching.Tests;

[TestClass]
public sealed class CachingStableFileHasherTests
{
    [TestMethod]
    public async Task HitsSkipContentReadsAndSizeOrTimeChangesRehash()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("mod.package", "abc");
        var cache = new SqliteSha256Cache(Path.Combine(temp.Path, "hashes.sqlite3"));
        var initial = new PhysicalFileSystemAccess().GetFileStamp(path);
        var fileSystem = new CountingFileSystem();
        using (var cold = new CachingStableFileHasher(cache, fileSystem))
        {
            await cold.HashAsync(path);
            Assert.AreEqual(1, cold.Misses);
            Assert.AreEqual(2, fileSystem.StampReads);
            Assert.IsTrue(cold.Commit());
        }
        using (var warm = new CachingStableFileHasher(cache, fileSystem))
        {
            var hit = await warm.HashAsync(path);
            Assert.AreEqual(initial, hit.Stamp);
            Assert.AreEqual(1, warm.Hits);
            Assert.AreEqual(1, fileSystem.Reads);
            Assert.AreEqual(4, fileSystem.StampReads);
        }

        File.AppendAllText(path, "d");
        File.SetLastWriteTimeUtc(path, initial.LastWriteTimeUtc);
        using (var changedSize = new CachingStableFileHasher(cache, fileSystem))
        {
            var result = await changedSize.HashAsync(path);
            Assert.AreEqual(initial.LastWriteTimeUtc, result.Stamp.LastWriteTimeUtc);
            Assert.AreEqual(4L, result.Stamp.Length);
            Assert.AreEqual(1, changedSize.Misses);
            Assert.IsTrue(changedSize.Commit());
        }
        File.SetLastWriteTimeUtc(path, initial.LastWriteTimeUtc.AddMinutes(1));
        using var changedTime = new CachingStableFileHasher(cache, fileSystem);
        Assert.AreEqual(initial.LastWriteTimeUtc.AddMinutes(1), (await changedTime.HashAsync(path)).Stamp.LastWriteTimeUtc);
        Assert.AreEqual(1, changedTime.Misses);
        Assert.AreEqual(3, fileSystem.Reads);
    }

    [TestMethod]
    public async Task CancelledScanDiscardsAlreadyStagedHashes()
    {
        using var temp = new TempDirectory();
        var first = temp.WriteFile("Mods/a.package", "same");
        var second = temp.WriteFile("Mods/b.package", "same");
        var cache = new SqliteSha256Cache(Path.Combine(temp.Path, "hashes.sqlite3"));
        using var cancellation = new CancellationTokenSource();
        var fileSystem = new CountingFileSystem(path => { if (path == second) cancellation.Cancel(); });
        using (var scan = new CachingStableFileHasher(cache, fileSystem))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await scan.RunAsync(hasher => ScanAsync(temp, fileSystem, hasher, cancellation.Token), cancellation.Token);
                scan.Commit(cancellation.Token);
            });
            Assert.AreEqual(2, fileSystem.Reads);
            Assert.AreEqual(2, scan.Misses);
        }
        Assert.IsNull(cache.GetSha256(first, fileSystem.GetFileStamp(first)));
        Assert.IsNull(cache.GetSha256(second, fileSystem.GetFileStamp(second)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnusableCacheProducesUncachedResultsEvenAfterAnEarlierHit(bool corruptAfterHit)
    {
        using var temp = new TempDirectory();
        var first = temp.WriteFile("Mods/a.package", "same");
        var second = temp.WriteFile("Mods/b.package", "same");
        var fileSystem = new CountingFileSystem();
        var expected = await ScanAsync(temp, fileSystem, new StableFileHasher(fileSystem));
        var database = Path.Combine(temp.Path, "hashes.sqlite3");
        if (!corruptAfterHit) File.WriteAllText(database, "bad database");
        var cache = new SqliteSha256Cache(database);
        if (corruptAfterHit)
        {
            using (var seed = cache.BeginScan())
            {
                // A same-stamp old result could change grouping if it survived the fallback.
                Assert.IsTrue(seed.Stage(first, fileSystem.GetFileStamp(first), SqliteSha256CacheTests.Hash));
                Assert.IsTrue(seed.Stage(second, fileSystem.GetFileStamp(second), expected.Groups.Single().Sha256));
                Assert.IsTrue(seed.Commit());
            }
            using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
            connection.Open();
            connection.CreateCollation("ORDINAL_IGNORE_CASE", StringComparer.OrdinalIgnoreCase.Compare);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE hashes SET checksum = zeroblob(32) WHERE path = $path;";
            command.Parameters.AddWithValue("$path", second);
            command.ExecuteNonQuery();
        }

        using var scan = new CachingStableFileHasher(cache, fileSystem);
        var actual = await scan.RunAsync(hasher => ScanAsync(temp, fileSystem, hasher));
        Assert.AreEqual(corruptAfterHit ? 1 : 0, scan.Hits);
        Assert.IsFalse(scan.Commit());
        Assert.AreEqual(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual with
        {
            StartedAtUtc = expected.StartedAtUtc, CompletedAtUtc = expected.CompletedAtUtc,
        }));
    }

    private static Task<DuplicateReport> ScanAsync(TempDirectory temp, IFileSystemAccess fileSystem,
        IStableFileHasher hasher, CancellationToken token = default) => new DuplicateScanner(fileSystem, hasher, new())
        .ScanAsync(new([new("mods", Path.Combine(temp.Path, "Mods"))], IncludedExtensions: [".package"])
        {
            HashConcurrency = DuplicateHashConcurrency.One,
        }, token);

    private sealed class CountingFileSystem(Action<string>? opening = null) : IFileSystemAccess
    {
        private readonly PhysicalFileSystemAccess _physical = new();
        public int Reads { get; private set; }
        public int StampReads { get; private set; }
        public bool DirectoryExists(string path) => _physical.DirectoryExists(path);
        public IReadOnlyList<string> EnumerateFileSystemEntries(string directory) => _physical.EnumerateFileSystemEntries(directory);
        public FileAttributes GetAttributes(string path) => _physical.GetAttributes(path);
        public FileStamp GetFileStamp(string path)
        {
            StampReads++;
            return _physical.GetFileStamp(path);
        }
        public Stream OpenRead(string path)
        {
            Reads++;
            opening?.Invoke(path);
            return _physical.OpenRead(path);
        }
    }
}
