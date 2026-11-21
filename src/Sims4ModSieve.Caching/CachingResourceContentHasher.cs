using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.Caching;

internal sealed class CachingResourceContentHasher(
    SqliteSha256Cache.Scan scan, IFileSystemAccess fileSystem, IResourceContentHasher hasher) : IResourceContentHasher
{
    private long _hits;
    private long _misses;

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);

    public async Task<ResourceContentBatchResult> HashAsync(ResourceContentBatchRequest request,
        ResourceContentBudget budget, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request.Limits.Validate();
        // A hit doesn't consume IO budget. Keep finite-budget scheduling and outcomes on the live path.
        if (request.Limits.MaxTotalProcessedBytes != long.MaxValue || budget.TotalBytes != long.MaxValue
            || request.Targets.Count == 0)
            return await hasher.HashAsync(request, budget, cancellationToken).ConfigureAwait(false);

        var cached = scan.GetResourceHashes(request, cancellationToken);
        if (cached is null)
            return await hasher.HashAsync(request, budget, cancellationToken).ConfigureAwait(false);

        var hitCount = cached.Count(result => result is not null);
        if (hitCount == cached.Length && IsCurrent(request))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Add(ref _hits, hitCount);
            return new(request.PackagePath, request.ExpectedStamp, Array.ConvertAll(cached, result => result!));
        }

        if (hitCount == 0 || hitCount == cached.Length)
        {
            Interlocked.Add(ref _misses, cached.Length);
            return await ReadAndStageAsync(request, budget, cancellationToken).ConfigureAwait(false);
        }

        var missing = Enumerable.Range(0, cached.Length).Where(index => cached[index] is null).ToArray();
        var read = await ReadAndStageAsync(request with { Targets = missing.Select(index => request.Targets[index]).ToArray() },
            budget, cancellationToken).ConfigureAwait(false);
        if (read.StampAfterRead != request.ExpectedStamp)
        {
            Interlocked.Add(ref _misses, cached.Length);
            // The live hasher returns the same package-wide issue for every target when the stamp fails.
            var issue = read.Results[0].Issue!;
            return read with { Results = request.Targets.Select(target => ResourceContentResult.Failure(target, issue)).ToArray() };
        }

        Interlocked.Add(ref _hits, hitCount);
        Interlocked.Add(ref _misses, missing.Length);
        for (var i = 0; i < missing.Length; i++) cached[missing[i]] = read.Results[i];
        return read with { Results = Array.ConvertAll(cached, result => result!) };
    }

    private bool IsCurrent(ResourceContentBatchRequest request)
    {
        try { return fileSystem.GetFileStamp(request.PackagePath) == request.ExpectedStamp; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private async Task<ResourceContentBatchResult> ReadAndStageAsync(ResourceContentBatchRequest request,
        ResourceContentBudget budget, CancellationToken token)
    {
        var read = await hasher.HashAsync(request, budget, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (read.StampAfterRead == request.ExpectedStamp) scan.StageResourceHashes(request, read.Results, token);
        return read;
    }
}
