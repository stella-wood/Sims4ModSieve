using System.Security.Cryptography;

namespace Sims4ModSieve.Core.Duplicates;

public sealed record StableFileHash(string Sha256, FileStamp Stamp);

public sealed class FileChangedDuringHashException(string path)
    : IOException($"File changed while it was being hashed: {path}");

public interface IStableFileHasher
{
    Task<StableFileHash> HashAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class StableFileHasher(
    IFileSystemAccess fileSystem,
    TimeSpan? retryDelay = null) : IStableFileHasher
{
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(50);

    public Task<StableFileHash> HashAsync(string path, CancellationToken cancellationToken = default) =>
        HashAsync(path, initialStamp: null, cancellationToken);

    /// <summary>Uses a just-read stamp for the first attempt and verifies it after reading the file.</summary>
    public async Task<StableFileHash> HashAsync(
        string path,
        FileStamp? initialStamp,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var before = (attempt == 0 ? initialStamp : null) ?? fileSystem.GetFileStamp(path);
            await using var stream = fileSystem.OpenRead(path);
            var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            var after = fileSystem.GetFileStamp(path);

            if (before == after)
            {
                return new StableFileHash(Convert.ToHexString(digest), after);
            }

            if (attempt == 0 && _retryDelay > TimeSpan.Zero)
            {
                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new FileChangedDuringHashException(path);
    }
}
