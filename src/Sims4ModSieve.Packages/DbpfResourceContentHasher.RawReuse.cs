using System.Buffers;
using System.Security.Cryptography;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Diagnostics;

namespace Sims4ModSieve.Packages;

public sealed partial class DbpfResourceContentHasher
{
    // 整轮超过窗口即旁路；不截断业务输入。碰撞桶最多保留 32 个实际字节集合。
    internal int ReuseDescriptorLimit { get; init; } = 131_072;
    internal int ReuseCollisionSetLimit { get; init; } = 32;
    internal long ReuseConfirmationByteLimit { get; init; } = 32L * 1024 * 1024 * 1024;
    internal Func<string, string>? RawDigestTransform { get; init; }
    internal Action<string, string, ResourceContentTarget?>? ReuseCheckpoint { get; init; }

    private sealed class RawItem(int batch, int slot, IndexEntry entry)
    {
        internal int Batch { get; } = batch;
        internal int Slot { get; } = slot;
        internal IndexEntry Entry { get; } = entry;
        internal string? Digest { get; set; }
        internal RawItem? Representative { get; set; }
    }

    public async Task<IReadOnlyList<ResourceContentBatchResult>?> TryHashBatchesWithReuseAsync(
        IReadOnlyList<ResourceContentBatchRequest> requests, ResourceContentBudget budget,
        Action<ResourceContentReuseDiagnostics> observed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (budget.TotalBytes < long.MaxValue || requests.Any(request => request.Limits.MaxTotalProcessedBytes < long.MaxValue))
        {
            observed(new() { BypassReason = "有限总预算：保留原读取路径" });
            return null;
        }
        if (requests.Count > ReuseDescriptorLimit || requests.Sum(request => (long)request.Targets.Count) > ReuseDescriptorLimit)
        {
            observed(new() { BypassReason = "原始字节描述符窗口超限" });
            return null;
        }
        var parallelism = requests.Count == 0 ? 1 : requests.Min(request => request.Limits.MaxDegreeOfParallelism);
        var items = new RawItem[requests.Count][];
        var disqualified = new bool[requests.Count];
        var fallback = new HashSet<RawItem>();
        long fingerprintBytes = 0, fingerprinted = 0, confirmationBytes = 0, digestCandidates = 0, confirmed = 0;
        long revoked = 0;

        async Task Workers(Func<int, CancellationToken, Task> work) => await Parallel.ForEachAsync(
            Enumerable.Range(0, requests.Count), new ParallelOptions
            { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken }, async (index, token) => await work(index, token).ConfigureAwait(false)).ConfigureAwait(false);

        void Checkpoint(string phase, int batch, ResourceContentTarget? target = null) =>
            ReuseCheckpoint?.Invoke(phase, requests[batch].PackagePath, target);

        bool Stable(int batch) => TryGetStamp(requests[batch].PackagePath, out _) == requests[batch].ExpectedStamp;

        await Workers(async (batch, token) =>
        {
            var request = requests[batch];
            request.Limits.Validate();
            items[batch] = [];
            try
            {
                Checkpoint("locate", batch);
                if (!Stable(batch)) { disqualified[batch] = true; return; }
                await using var stream = fileSystem.OpenRead(request.PackagePath);
                var located = await LocateAsync(stream, request.ExpectedStamp.Length, request.Targets, token).ConfigureAwait(false);
                if (located.Issue is not null) { disqualified[batch] = true; return; }
                items[batch] = located.Entries.Select((entry, slot) => (entry, slot))
                    .Where(pair => pair.entry is { Compression: CompressionZlib } entry
                        && ValidateEntry(request.ExpectedStamp.Length, request.Targets[pair.slot], entry, request.Limits) is null)
                    .Select(pair => new RawItem(batch, pair.slot, pair.entry!.Value)).ToArray();
                if (!Stable(batch)) disqualified[batch] = true;
            }
            catch (Exception exception) when (IsRawReadFailure(exception)) { disqualified[batch] = true; }
        }).ConfigureAwait(false);

        // 枚举顺序由路径与 ordinal 决定，不依赖 worker 完成顺序。
        var ordered = items.SelectMany(batch => batch)
            .OrderBy(item => requests[item.Batch].PackagePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => requests[item.Batch].Targets[item.Slot].Ordinal)
            .ThenBy(item => item.Slot).ToArray();
        var eligible = ordered.Where(item => !disqualified[item.Batch]).ToArray();
        var metadata = eligible.GroupBy(item => (item.Entry.Key, item.Entry.Compression, item.Entry.StoredSize, item.Entry.DeclaredSize)).ToArray();
        var multiple = metadata.Where(group => group.Skip(1).Any()).SelectMany(group => group).ToHashSet();
        var singletons = metadata.Count(group => !group.Skip(1).Any());
        var fingerprintWork = eligible.Where(multiple.Contains).GroupBy(item => item.Batch)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Entry.Position).ThenBy(item => item.Slot).ToArray());
        await Workers(async (batch, token) =>
        {
            if (!fingerprintWork.TryGetValue(batch, out var work) || disqualified[batch]) return;
            try
            {
                if (!Stable(batch)) { disqualified[batch] = true; return; }
                await using var stream = fileSystem.OpenRead(requests[batch].PackagePath);
                foreach (var item in work)
                {
                    token.ThrowIfCancellationRequested();
                    Checkpoint("fingerprint", batch, requests[batch].Targets[item.Slot]);
                    stream.Seek(item.Entry.Position, SeekOrigin.Begin);
                    using var stored = new BoundedReadStream(stream, item.Entry.StoredSize, budget, token);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
                    try
                    {
                        int read;
                        while ((read = await stored.ReadAsync(buffer.AsMemory(0, ChunkSize), token).ConfigureAwait(false)) > 0)
                            hash.AppendData(buffer, 0, read);
                        var digest = Convert.ToHexString(hash.GetHashAndReset());
                        item.Digest = RawDigestTransform?.Invoke(digest) ?? digest;
                        Interlocked.Increment(ref fingerprinted);
                    }
                    finally
                    {
                        Interlocked.Add(ref fingerprintBytes, stored.Consumed);
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                if (!Stable(batch)) disqualified[batch] = true;
            }
            catch (Exception exception) when (IsRawReadFailure(exception)) { disqualified[batch] = true; }
        }).ConfigureAwait(false);

        var leftBuffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var rightBuffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            foreach (var group in metadata)
            {
                foreach (var digestGroup in group.Where(item => item.Digest is not null && !disqualified[item.Batch]).GroupBy(item => item.Digest))
                {
                    var representatives = new List<RawItem>();
                    foreach (var item in digestGroup)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (disqualified[item.Batch]) continue;
                        var matched = false;
                        foreach (var representative in representatives)
                        {
                            if (disqualified[representative.Batch]) continue;
                            // 先检查整个比较的最大逻辑输入，超限只影响优化机会。
                            if (2L * item.Entry.StoredSize > ReuseConfirmationByteLimit - confirmationBytes) break;
                            digestCandidates++;
                            try
                            {
                                var equal = await EqualAsync(representative, item).ConfigureAwait(false);
                                if (equal)
                                {
                                    confirmed++;
                                    item.Representative = representative;
                                    matched = true;
                                    break;
                                }
                            }
                            catch (Exception exception) when (IsRawReadFailure(exception))
                            {
                                // 不猜 IO 发生在何种业务边界；两边各由原 reader 重新判定。
                                disqualified[item.Batch] = true;
                                disqualified[representative.Batch] = true;
                                break;
                            }
                        }
                        if (!matched && !disqualified[item.Batch] && representatives.Count < ReuseCollisionSetLimit)
                            representatives.Add(item);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(leftBuffer);
            ArrayPool<byte>.Shared.Return(rightBuffer);
        }

        async Task<bool> EqualAsync(RawItem left, RawItem right)
        {
            Checkpoint("confirm", right.Batch, requests[right.Batch].Targets[right.Slot]);
            if (!Stable(left.Batch) || !Stable(right.Batch))
                throw new IOException("Raw comparison version changed.");
            Stream? leftStream = null, rightStream = null;
            try
            {
                if (parallelism > 1)
                {
                    leftStream = fileSystem.OpenRead(requests[left.Batch].PackagePath);
                    rightStream = fileSystem.OpenRead(requests[right.Batch].PackagePath);
                }
                long offset = 0;
                while (offset < left.Entry.StoredSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(ChunkSize, left.Entry.StoredSize - offset);
                    await ReadBlock(left, leftStream, offset, leftBuffer.AsMemory(0, count)).ConfigureAwait(false);
                    await ReadBlock(right, rightStream, offset, rightBuffer.AsMemory(0, count)).ConfigureAwait(false);
                    if (!leftBuffer.AsSpan(0, count).SequenceEqual(rightBuffer.AsSpan(0, count))) return false;
                    offset += count;
                }
                if (!Stable(left.Batch) || !Stable(right.Batch)) throw new IOException("Raw comparison version changed.");
                return true;
            }
            finally
            {
                if (leftStream is not null) await leftStream.DisposeAsync().ConfigureAwait(false);
                if (rightStream is not null) await rightStream.DisposeAsync().ConfigureAwait(false);
            }
        }

        async Task ReadBlock(RawItem item, Stream? opened, long offset, Memory<byte> buffer)
        {
            // 并发 1 不同时持有两个包句柄，每块交替重开并复核文件版本。
            if (opened is null)
            {
                if (!Stable(item.Batch)) throw new IOException("Raw block version changed.");
                await using var stream = fileSystem.OpenRead(requests[item.Batch].PackagePath);
                await ReadBlock(item, stream, offset, buffer).ConfigureAwait(false);
                if (!Stable(item.Batch)) throw new IOException("Raw block version changed.");
                return;
            }
            opened.Seek(item.Entry.Position + offset, SeekOrigin.Begin);
            using var stored = new BoundedReadStream(opened, buffer.Length, budget, cancellationToken);
            try { await stored.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false); }
            finally { confirmationBytes += stored.Consumed; }
        }

        // 对失稳包的所有直接依赖撤销。没有代表链，也没有 worker 等待其他 worker。
        foreach (var item in ordered)
        {
            if (disqualified[item.Batch]) fallback.Add(item);
            if (item.Representative is { } rep && (disqualified[item.Batch] || disqualified[rep.Batch]))
            { item.Representative = null; fallback.Add(item); revoked++; }
        }

        var representativeCount = ordered.LongCount(item => item.Digest is not null && item.Representative is null && !disqualified[item.Batch]);
        var pending = requests.Select(request => new ResourceContentResult?[request.Targets.Count]).ToArray();
        var versions = new ResourceContentBatchResult?[requests.Count];
        var reusedItems = ordered.Where(item => item.Representative is not null).ToArray();
        var skippedSlots = reusedItems.Select(item => (item.Batch, item.Slot)).ToHashSet();
        await Workers(async (batch, token) =>
        {
            var slots = Enumerable.Range(0, requests[batch].Targets.Count).Where(slot => !skippedSlots.Contains((batch, slot))).ToArray();
            if (slots.Length == 0) return;
            Checkpoint("read", batch);
            var result = await HashAsync(requests[batch] with { Targets = slots.Select(slot => requests[batch].Targets[slot]).ToArray() }, budget, token)
                .ConfigureAwait(false);
            versions[batch] = result;
            for (var index = 0; index < slots.Length; index++) pending[batch][slots[index]] = result.Results[index];
        }).ConfigureAwait(false);

        var final = new ResourceContentBatchResult[requests.Count];
        var fullFallback = new bool[requests.Count];
        var invalid = new bool[requests.Count];
        var publishCheckpoint = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var retry = new HashSet<int>();
            foreach (var item in reusedItems)
            {
                if (fullFallback[item.Batch] || invalid[item.Batch]) continue;
                var rep = item.Representative!;
                var evidence = pending[rep.Batch][rep.Slot];
                var ownIssue = versions[item.Batch]?.Results.FirstOrDefault(result => result.Issue?.Stage is ResourceContentStage.Access or ResourceContentStage.Stability
                    || result.Issue?.Code == ResourceContentIssueCode.IndexUnreadable);
                if (invalid[rep.Batch] || evidence?.IsSuccess != true || ownIssue is not null)
                {
                    retry.Add(item.Batch);
                    if (invalid[rep.Batch]) revoked++;
                }
                else pending[item.Batch][item.Slot] = ResourceContentResult.Success(requests[item.Batch].Targets[item.Slot], evidence.Sha256!, evidence.DecompressedLength!.Value);
            }
            if (retry.Count > 0)
            {
                await Parallel.ForEachAsync(retry, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                    async (batch, token) =>
                    {
                        Checkpoint("fallback", batch);
                        var result = await HashAsync(requests[batch], budget, token).ConfigureAwait(false);
                        versions[batch] = result;
                        pending[batch] = result.Results.Cast<ResourceContentResult?>().ToArray();
                        fullFallback[batch] = true;
                    }).ConfigureAwait(false);
                foreach (var batch in retry)
                    foreach (var item in items[batch]) fallback.Add(item);
                continue;
            }
            if (!publishCheckpoint)
            {
                for (var batch = 0; batch < requests.Count; batch++) Checkpoint("publish", batch);
                publishCheckpoint = true;
            }
            var newlyInvalid = false;
            for (var batch = 0; batch < requests.Count; batch++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (invalid[batch]) continue;
                var results = pending[batch].Select(result => result!).ToArray();
                // 原 reader 观察到的失稳不可由随后的戳恢复抹掉。
                var prior = versions[batch];
                final[batch] = prior is not null && prior.Results.Any(result => result.Issue?.Stage == ResourceContentStage.Stability)
                    && prior.Results.Count == requests[batch].Targets.Count
                    ? prior : await FinishAsync(requests[batch].PackagePath, requests[batch].ExpectedStamp, results).ConfigureAwait(false);
                if (final[batch].StampAfterRead != requests[batch].ExpectedStamp
                    || final[batch].Results.Any(result => result.Issue?.Stage == ResourceContentStage.Stability))
                {
                    invalid[batch] = true;
                    newlyInvalid = true;
                    pending[batch] = final[batch].Results.Cast<ResourceContentResult?>().ToArray();
                }
            }
            if (!newlyInvalid) break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var saved = reusedItems.Where(item => !fullFallback[item.Batch] && !invalid[item.Batch] && !invalid[item.Representative!.Batch]
            && final[item.Batch].Results[item.Slot].IsSuccess).ToArray();
        observed(new()
        {
            NeedsDecompressionResources = eligible.LongLength,
            DeclaredOutputBytes = eligible.Sum(item => (long)item.Entry.DeclaredSize),
            SingletonMetadataResources = singletons,
            FingerprintedResources = fingerprinted, FingerprintPreReadBytes = fingerprintBytes,
            DigestCandidates = digestCandidates, ConfirmedComparisons = confirmed, ConfirmationReadBytes = confirmationBytes,
            Representatives = representativeCount,
            EquivalentSets = saved.Select(item => item.Representative).Distinct().LongCount(),
            SavedDecompressions = saved.LongLength, SavedOutputBytes = saved.Sum(item => final[item.Batch].Results[item.Slot].DecompressedLength!.Value),
            AvoidedOriginalReadBytes = saved.Sum(item => (long)item.Entry.StoredSize),
            FallbackResources = fallback.Count, RevokedResources = revoked,
        });
        // 进入 Core 分组前，主动断开本轮描述符、指纹及依赖引用。
        Array.Clear(items);
        Array.Clear(ordered);
        Array.Clear(eligible);
        Array.Clear(metadata);
        Array.Clear(reusedItems);
        Array.Clear(saved);
        Array.Clear(pending);
        Array.Clear(versions);
        fingerprintWork.Clear();
        multiple.Clear();
        fallback.Clear();
        skippedSlots.Clear();
        return final;
    }

    private static bool IsRawReadFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or NotSupportedException;
}
