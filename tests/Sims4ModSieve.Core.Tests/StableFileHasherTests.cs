using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.Core.Tests;

[TestClass]
public sealed class StableFileHasherTests
{
    [TestMethod]
    public async Task RetriesOnceWhenFileChangesAndReturnsSecondStableSnapshot()
    {
        var first = new FileStamp(3, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var changed = new FileStamp(4, first.LastWriteTimeUtc.AddSeconds(1));
        var stable = new FileStamp(4, first.LastWriteTimeUtc.AddSeconds(2));
        var fileSystem = new SequencedStampFileSystem("data"u8.ToArray(), first, changed, stable, stable);
        var hasher = new StableFileHasher(fileSystem, TimeSpan.Zero);

        var result = await hasher.HashAsync("file.package");

        Assert.AreEqual(stable, result.Stamp);
        Assert.AreEqual(2, fileSystem.OpenCount);
    }

    [TestMethod]
    public async Task SuppliedStampStillChecksAfterReadingAndRefreshesBeforeRetry()
    {
        var initial = new FileStamp(3, DateTime.UnixEpoch);
        var changed = new FileStamp(4, DateTime.UnixEpoch.AddSeconds(1));
        var stable = new FileStamp(4, DateTime.UnixEpoch.AddSeconds(2));
        var fileSystem = new SequencedStampFileSystem("data"u8.ToArray(), changed, stable, stable);
        var hasher = new StableFileHasher(fileSystem, TimeSpan.Zero);

        var result = await hasher.HashAsync("file.package", initial, CancellationToken.None);

        Assert.AreEqual(stable, result.Stamp);
        Assert.AreEqual(2, fileSystem.OpenCount);
    }

    [TestMethod]
    public async Task RejectsAFileThatChangesDuringBothAttempts()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fileSystem = new SequencedStampFileSystem(
            "data"u8.ToArray(),
            new FileStamp(4, start),
            new FileStamp(5, start.AddSeconds(1)),
            new FileStamp(5, start.AddSeconds(1)),
            new FileStamp(6, start.AddSeconds(2)));
        var hasher = new StableFileHasher(fileSystem, TimeSpan.Zero);

        await Assert.ThrowsExactlyAsync<FileChangedDuringHashException>(
            () => hasher.HashAsync("file.package"));
        Assert.AreEqual(2, fileSystem.OpenCount);
    }

    [TestMethod]
    public async Task CancellingDuringReadReleasesStreamWithoutRetrying()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new CancellableStream();
        var fileSystem = new SequencedStampFileSystem([], new FileStamp(4, DateTime.UnixEpoch))
        {
            OpenStream = () => stream,
        };
        var hash = new StableFileHasher(fileSystem).HashAsync("file.package", cancellation.Token);
        await stream.Reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => hash);
        Assert.IsTrue(stream.Disposed);
        Assert.AreEqual(1, fileSystem.OpenCount);
    }

    [TestMethod]
    public async Task CancellingDuringRetryDelayReleasesStreamWithoutReopening()
    {
        using var cancellation = new CancellationTokenSource();
        var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new CancellableStream(blockRead: false);
        var fileSystem = new SequencedStampFileSystem([], new FileStamp(4, DateTime.UnixEpoch), new FileStamp(5, DateTime.UnixEpoch))
        {
            OpenStream = () => stream,
            LastStampRead = verifying.SetResult,
        };
        var hasher = new StableFileHasher(fileSystem, TimeSpan.FromMinutes(1));
        var hash = hasher.HashAsync("file.package", cancellation.Token);
        await verifying.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(hash.IsCompleted);
        Assert.IsFalse(stream.Disposed);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => hash.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(stream.Disposed);
        Assert.AreEqual(1, fileSystem.OpenCount);
    }

    private sealed class CancellableStream(bool blockRead = true) : MemoryStream("data"u8.ToArray())
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (blockRead)
            {
                Reading.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
