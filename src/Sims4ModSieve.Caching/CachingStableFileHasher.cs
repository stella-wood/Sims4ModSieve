using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.Caching;

public sealed class CachingStableFileHasher(SqliteSha256Cache cache, IFileSystemAccess fileSystem)
    : IStableFileHasher, IDisposable
{
    private readonly StableFileHasher _hasher = new(fileSystem);
    private readonly SqliteSha256Cache.Scan _scan = cache.BeginScan();
    private int _hits;
    private int _misses;

    public int Hits => Volatile.Read(ref _hits);
    public int Misses => Volatile.Read(ref _misses);

    public static CachingStableFileHasher Open(string dataDirectory) => new(
        new SqliteSha256Cache(Path.Combine(dataDirectory, "cache", "sha256.sqlite3")), new PhysicalFileSystemAccess());

    public async Task<StableFileHash> HashAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cache.IsAvailable)
        {
            var before = fileSystem.GetFileStamp(path);
            var sha256 = _scan.GetSha256(path, before, cancellationToken);
            if (sha256 is not null && before == fileSystem.GetFileStamp(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _hits);
                return new(sha256, before);
            }
            Interlocked.Increment(ref _misses);
        }

        var result = await _hasher.HashAsync(path, cancellationToken).ConfigureAwait(false);
        if (cache.IsAvailable) _scan.Stage(path, result.Stamp, result.Sha256, cancellationToken);
        return result;
    }

    public async Task<T> RunAsync<T>(Func<IStableFileHasher, Task<T>> scan, CancellationToken cancellationToken = default)
    {
        var result = await scan(this).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Discard results that depended on cache entries if a later operation found the cache unusable.
        return Hits > 0 && !cache.IsAvailable ? await scan(_hasher).ConfigureAwait(false) : result;
    }

    public bool Commit(CancellationToken cancellationToken = default) => _scan.Commit(cancellationToken);

    public void Dispose() => _scan.Dispose();
}
