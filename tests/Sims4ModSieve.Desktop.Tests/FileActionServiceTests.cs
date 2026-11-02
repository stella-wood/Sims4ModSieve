using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Services;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class FileActionServiceTests
{
    private static readonly FileIdentity Identity = new(1, 2, 3);

    [TestMethod]
    public void PreflightRejectsFilesChangedAfterScan()
    {
        var metadata = new FakeMetadataAccess();
        metadata.Set(@"D:\Mods\changed.package", new FileStamp(200, DateTime.UnixEpoch.AddMinutes(1), Identity));
        var adapter = new FakeRecycleBinAdapter();
        var service = new FileActionService(adapter, metadata);

        var result = service.Preflight([
            new FileDeletionCandidate(@"D:\Mods\changed.package", 100, DateTime.UnixEpoch, "第 1 组", ExpectedIdentity: Identity),
        ], []);

        Assert.IsFalse(result.CanExecute);
        Assert.AreEqual(1, result.Problems.Count);
        StringAssert.Contains(result.Problems[0].Message, "发生变化");
    }

    [TestMethod]
    public async Task DeleteAndUndoUseTheSingleRecycleBinAdapter()
    {
        const string path = @"D:\Mods\copy.package";
        var metadata = new FakeMetadataAccess();
        metadata.Set(path, new FileStamp(100, DateTime.UnixEpoch, Identity));
        var adapter = new FakeRecycleBinAdapter();
        var service = new FileActionService(adapter, metadata);
        var preflight = service.Preflight([
            new FileDeletionCandidate(path, 100, DateTime.UnixEpoch, "第 1 组", ExpectedIdentity: Identity),
        ], []);

        var deletion = await service.DeleteAsync(preflight);

        Assert.AreEqual(1, deletion.CompletedPaths.Count);
        Assert.IsTrue(service.CanUndoLastDelete);
        Assert.AreEqual(1, adapter.DeleteCalls);

        var restore = await service.UndoLastDeleteAsync();

        Assert.AreEqual(1, restore.CompletedPaths.Count);
        Assert.IsFalse(service.CanUndoLastDelete);
        Assert.AreEqual(1, adapter.RestoreCalls);
    }

    [TestMethod]
    public async Task DeleteRechecksStabilityImmediatelyBeforeFileAction()
    {
        const string path = @"D:\Mods\copy.package";
        var metadata = new FakeMetadataAccess();
        metadata.Set(path, new FileStamp(100, DateTime.UnixEpoch, Identity));
        var adapter = new FakeRecycleBinAdapter();
        var service = new FileActionService(adapter, metadata);
        var preflight = service.Preflight([
            new FileDeletionCandidate(path, 100, DateTime.UnixEpoch, "第 1 组", ExpectedIdentity: Identity),
        ], []);
        metadata.Set(path, new FileStamp(101, DateTime.UnixEpoch, Identity));

        var result = await service.DeleteAsync(preflight);

        Assert.AreEqual(0, result.CompletedPaths.Count);
        Assert.AreEqual(1, result.Failures.Count);
        Assert.AreEqual(0, adapter.DeleteCalls);
    }

    [TestMethod]
    public async Task ClearingLastDeleteRemovesUndoState()
    {
        const string path = @"D:\Mods\copy.package";
        var metadata = new FakeMetadataAccess();
        metadata.Set(path, new FileStamp(100, DateTime.UnixEpoch, Identity));
        var service = new FileActionService(new FakeRecycleBinAdapter(), metadata);
        var preflight = service.Preflight([
            new FileDeletionCandidate(path, 100, DateTime.UnixEpoch, "第 1 组", ExpectedIdentity: Identity),
        ], []);
        await service.DeleteAsync(preflight);
        Assert.IsTrue(service.CanUndoLastDelete);

        service.ClearLastDelete();

        Assert.IsFalse(service.CanUndoLastDelete);
    }

    [TestMethod]
    public async Task DeleteRejectsSameStampContentChangeAndContinuesOtherFiles()
    {
        const string changed = @"D:\Mods\changed.package";
        const string stable = @"D:\Mods\stable.package";
        var metadata = new FakeMetadataAccess();
        metadata.Set(changed, new(4, DateTime.UnixEpoch, Identity), [1, 1, 1, 1]);
        metadata.Set(stable, new(4, DateTime.UnixEpoch, Identity), [2, 2, 2, 2]);
        var adapter = new FakeRecycleBinAdapter();
        var service = new FileActionService(adapter, metadata);
        var preflight = service.Preflight([
            new(changed, 4, DateTime.UnixEpoch, "第 1 组", ExpectedIdentity: Identity,
                ExpectedSha256: Convert.ToHexString(SHA256.HashData(new byte[] { 1, 1, 1, 1 }))),
            new(stable, 4, DateTime.UnixEpoch, "第 1 组", ExpectedIdentity: Identity,
                ExpectedSha256: Convert.ToHexString(SHA256.HashData(new byte[] { 2, 2, 2, 2 }))),
        ], []);
        metadata.Set(changed, new(4, DateTime.UnixEpoch, Identity), [9, 9, 9, 9]);

        var result = await service.DeleteAsync(preflight);

        CollectionAssert.AreEqual(new[] { stable }, result.CompletedPaths.ToArray());
        Assert.AreEqual(changed, result.Failures.Single().Path);
        Assert.AreEqual(1, adapter.DeleteCalls);
    }

    [TestMethod]
    public async Task DeleteRejectsSameStampIdentityReplacement()
    {
        const string path = @"D:\Mods\copy.package";
        var metadata = new FakeMetadataAccess();
        metadata.Set(path, new(4, DateTime.UnixEpoch, Identity));
        var adapter = new FakeRecycleBinAdapter();
        var service = new FileActionService(adapter, metadata);
        var preflight = service.Preflight([new(path, 4, DateTime.UnixEpoch, "第 1 组", ExpectedIdentity: Identity)], []);
        metadata.Set(path, new(4, DateTime.UnixEpoch, new FileIdentity(1, 9, 3)));

        var result = await service.DeleteAsync(preflight);

        Assert.AreEqual(0, result.CompletedPaths.Count);
        Assert.AreEqual(1, result.Failures.Count);
        Assert.AreEqual(0, adapter.DeleteCalls);
    }

    [TestMethod]
    public async Task DeleteRejectsUnreadableContentAndKeepsOtherCandidates()
    {
        const string unreadable = @"D:\Mods\unreadable.package";
        const string stable = @"D:\Mods\stable.package";
        var metadata = new FakeMetadataAccess();
        metadata.Set(unreadable, new(4, DateTime.UnixEpoch, Identity));
        metadata.Set(stable, new(4, DateTime.UnixEpoch, Identity));
        var adapter = new FakeRecycleBinAdapter();
        var service = new FileActionService(adapter, metadata);
        var hash = Convert.ToHexString(SHA256.HashData(new byte[4]));
        var preflight = service.Preflight([
            new(unreadable, 4, DateTime.UnixEpoch, "第 1 组", ExpectedIdentity: Identity, ExpectedSha256: hash),
            new(stable, 4, DateTime.UnixEpoch, "第 1 组", ExpectedIdentity: Identity, ExpectedSha256: hash),
        ], []);
        metadata.FailRead(unreadable);

        var result = await service.DeleteAsync(preflight);

        CollectionAssert.AreEqual(new[] { stable }, result.CompletedPaths.ToArray());
        Assert.AreEqual(unreadable, result.Failures.Single().Path);
    }

    [TestMethod]
    public void PreflightRejectsUnavailableIdentityWithoutBlockingReadOnlyStamp()
    {
        const string path = @"D:\Mods\copy.package";
        var metadata = new FakeMetadataAccess();
        metadata.Set(path, new(4, DateTime.UnixEpoch));
        Assert.IsNull(metadata.GetFileStamp(path).Identity);

        var result = new FileActionService(new FakeRecycleBinAdapter(), metadata)
            .Preflight([new(path, 4, DateTime.UnixEpoch, "第 1 组")], []);

        Assert.IsFalse(result.CanExecute);
        StringAssert.Contains(result.Problems.Single().Message, "文件身份");
    }

    [TestMethod]
    public async Task DeleteRechecksEarlierFileBeforeSubmittingBatch()
    {
        const string first = @"D:\Mods\first.package";
        const string second = @"D:\Mods\second.package";
        var metadata = new FakeMetadataAccess();
        metadata.Set(first, new(4, DateTime.UnixEpoch, Identity));
        metadata.Set(second, new(4, DateTime.UnixEpoch, Identity));
        metadata.AfterRead(second, () => metadata.Set(first, new(4, DateTime.UnixEpoch, new FileIdentity(1, 9, 3))));
        var adapter = new FakeRecycleBinAdapter();
        var service = new FileActionService(adapter, metadata);
        var hash = Convert.ToHexString(SHA256.HashData(new byte[4]));
        var preflight = service.Preflight([
            new(first, 4, DateTime.UnixEpoch, "第 1 组", ExpectedIdentity: Identity, ExpectedSha256: hash),
            new(second, 4, DateTime.UnixEpoch, "第 1 组", ExpectedIdentity: Identity, ExpectedSha256: hash),
        ], []);

        var result = await service.DeleteAsync(preflight);

        CollectionAssert.AreEqual(new[] { second }, result.CompletedPaths.ToArray());
        Assert.AreEqual(first, result.Failures.Single().Path);
    }

    [TestMethod]
    public void PhysicalPreflightRejectsSameStampReplacement()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Sims4ModSieve-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "copy.package");
            File.WriteAllText(path, "AAAA");
            var metadata = new PhysicalFileMetadataAccess();
            var scanned = metadata.GetFileStamp(path);
            Assert.IsNotNull(scanned.Identity);
            File.Move(path, Path.Combine(directory, "original.package"));
            File.WriteAllText(path, "BBBB");
            File.SetLastWriteTimeUtc(path, scanned.LastWriteTimeUtc);

            var preflight = new FileActionService(new FakeRecycleBinAdapter(), metadata).Preflight([
                new(path, scanned.Length, scanned.LastWriteTimeUtc, "第 1 组", ExpectedIdentity: scanned.Identity),
            ], []);

            Assert.IsFalse(preflight.CanExecute);
            StringAssert.Contains(preflight.Problems.Single().Message, "发生变化");
            Assert.AreEqual("BBBB", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task UndoPersistsFirstRestoredItemWhenSecondIsCanceled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Sims4ModSieve-undo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var metadata = new FakeMetadataAccess();
            var paths = new[] { @"D:\Mods\first.package", @"D:\Mods\second.package" };
            foreach (var path in paths) metadata.Set(path, new(4, DateTime.UnixEpoch, Identity));
            var adapter = new FakeRecycleBinAdapter { RestoreFailureAt = 2, RestoreException = new OperationCanceledException() };
            var store = new FileManagementStore(directory);
            var service = new FileActionService(adapter, metadata, store);
            await service.DeleteAsync(service.Preflight(paths.Select(path =>
                new FileDeletionCandidate(path, 4, DateTime.UnixEpoch, "第 1 组", ExpectedIdentity: Identity)).ToArray(), []));

            var result = await service.UndoLastDeleteAsync();

            CollectionAssert.AreEqual(new[] { paths[0] }, result.CompletedPaths.ToArray());
            Assert.AreEqual(paths[1], result.Failures.Single().Path);
            Assert.IsTrue(result.CanUndo);
            Assert.AreEqual(paths[1], store.Load().Deleted.Single().Item.OriginalPath);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task RestoreDeletedPersistsFirstItemWhenSecondThrowsIo()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Sims4ModSieve-restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            var metadata = new FakeMetadataAccess();
            var paths = new[] { @"D:\Mods\first.package", @"D:\Mods\second.package" };
            foreach (var path in paths) metadata.Set(path, new(4, DateTime.UnixEpoch, Identity));
            var adapter = new FakeRecycleBinAdapter { RestoreFailureAt = 2, RestoreException = new IOException("模拟恢复失败") };
            var store = new FileManagementStore(directory);
            var service = new FileActionService(adapter, metadata, store);
            await service.DeleteAsync(service.Preflight(paths.Select(path =>
                new FileDeletionCandidate(path, 4, DateTime.UnixEpoch, "第 1 组", ExpectedIdentity: Identity)).ToArray(), []));
            var ids = store.Load().Deleted.Select(record => record.Id).ToArray();

            var result = await service.RestoreDeletedAsync(ids);

            CollectionAssert.AreEqual(new[] { paths[0] }, result.CompletedPaths.ToArray());
            Assert.AreEqual(paths[1], result.Failures.Single().Path);
            Assert.AreEqual(paths[1], store.Load().Deleted.Single().Item.OriginalPath);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class FakeMetadataAccess : IFileMetadataAccess
    {
        private readonly Dictionary<string, FileStamp> stamps = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> contents = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> unreadable = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Action> afterRead = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => stamps.ContainsKey(path);

        public FileStamp GetFileStamp(string path) => stamps[path];

        public Stream OpenRead(string path)
        {
            if (unreadable.Contains(path)) throw new IOException("读取失败");
            var stream = new MemoryStream(contents.TryGetValue(path, out var content)
                ? content : new byte[stamps[path].Length]);
            if (afterRead.TryGetValue(path, out var callback)) callback();
            return stream;
        }

        public void Set(string path, FileStamp stamp) => stamps[path] = stamp;

        public void Set(string path, FileStamp stamp, byte[] content) { stamps[path] = stamp; contents[path] = content; }

        public void FailRead(string path) => unreadable.Add(path);

        public void AfterRead(string path, Action callback) => afterRead[path] = callback;
    }

    private sealed class FakeRecycleBinAdapter : IRecycleBinAdapter
    {
        private int nextPidl;
        public int DeleteCalls { get; private set; }

        public int RestoreCalls { get; private set; }

        public int RestoreFailureAt { get; init; }

        public Exception? RestoreException { get; init; }

        public bool CanRecycle(string path) => true;

        public Task<RecycleBinDeleteResult> MoveToRecycleBinAsync(
            IReadOnlyList<string> paths,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return Task.FromResult(new RecycleBinDeleteResult(
                paths.Select(path => new RecycleBinItem(path, [1, 2, (byte)++nextPidl, 0])).ToArray(),
                []));
        }

        public Task<RecycleBinPresence> QueryAsync(IReadOnlyList<RecycleBinItem> items,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecycleBinPresence(items, [], []));

        public Task<FileActionBatchResult> RestoreAsync(
            IReadOnlyList<RecycleBinItem> items,
            CancellationToken cancellationToken = default)
        {
            RestoreCalls++;
            if (RestoreCalls == RestoreFailureAt) throw RestoreException!;
            return Task.FromResult(new FileActionBatchResult(
                items.Select(item => item.OriginalPath).ToArray(),
                [],
                false));
        }
    }
}
