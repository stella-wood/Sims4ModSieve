using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Desktop.Services;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class WindowsRecycleBinIntegrationTests
{
    [TestMethod]
    [TestCategory("WindowsIntegration")]
    public async Task DisabledModCanRoundTripThroughWindowsRecycleBin()
    {
        if (Environment.GetEnvironmentVariable("SIMS4_MOD_SIEVE_RUN_RECYCLE_BIN_TEST") != "1")
            Assert.Inconclusive("Set SIMS4_MOD_SIEVE_RUN_RECYCLE_BIN_TEST=1 to run the real Recycle Bin test.");
        var directory = Path.Combine(Path.GetTempPath(), $"Sims4ModSieve-DisabledRecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "已禁用回收站验证.package");
        var disabledPath = path + ".off";
        const string expected = "disabled mod recycle bin round trip";
        var adapter = new WindowsRecycleBinAdapter();
        var store = new FileManagementStore(directory);
        var mods = new ModDisableService(directory);
        var items = new List<RecycleBinItem>();
        try
        {
            await File.WriteAllTextAsync(path, expected);
            var stamp = File.GetLastWriteTimeUtc(path);
            var disabled = mods.Disable([path]);
            Assert.AreEqual(0, disabled.Failures.Count, string.Join("\n", disabled.Failures));
            var record = mods.Load().Records.Single();
            Assert.IsFalse(File.Exists(path));
            await File.WriteAllTextAsync(path, "do-not-overwrite");
            var actions = new FileActionService(adapter, new PhysicalFileMetadataAccess(), store, mods);
            var deleted = await actions.DeleteAsync(actions.Preflight(
                [new(disabledPath, record.Size, record.LastWriteTimeUtc, "", record)], []));
            items.AddRange(store.Load().Deleted.Select(entry => entry.Item));
            Assert.AreEqual(0, deleted.Failures.Count, string.Join("\n", deleted.Failures));
            CollectionAssert.AreEqual(new[] { disabledPath }, deleted.CompletedPaths.ToArray());
            Assert.IsFalse(File.Exists(disabledPath));
            Assert.AreEqual("do-not-overwrite", await File.ReadAllTextAsync(path));
            var restartedMods = new ModDisableService(directory);
            var restarted = new FileActionService(adapter, new PhysicalFileMetadataAccess(), new FileManagementStore(directory), restartedMods);
            Assert.AreEqual(0, restartedMods.Load().Records.Count);
            var history = await restarted.GetRecentlyDeletedAsync();
            Assert.AreEqual(0, history.Failures.Count, string.Join("\n", history.Failures));
            var entry = history.Files.Single();
            Assert.AreEqual(disabledPath, entry.Item.OriginalPath);
            Assert.AreEqual(record.Id, entry.DisabledMod!.Id);
            var restored = await restarted.RestoreDeletedAsync([entry.Id]);
            Assert.AreEqual(0, restored.Failures.Count, string.Join("\n", restored.Failures));
            CollectionAssert.AreEqual(new[] { disabledPath }, restored.CompletedPaths.ToArray());
            Assert.AreEqual(expected, await File.ReadAllTextAsync(disabledPath));
            Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(disabledPath));
            Assert.AreEqual(record.Id, restartedMods.Load().Records.Single().Id);
            Assert.AreEqual("do-not-overwrite", await File.ReadAllTextAsync(path));
            Assert.AreEqual(0, restartedMods.Restore([record.Id]).CompletedPaths.Count);
            Assert.AreEqual("do-not-overwrite", await File.ReadAllTextAsync(path));
            File.Delete(path);
            var enabled = restartedMods.Restore([record.Id]);
            Assert.AreEqual(0, enabled.Failures.Count, string.Join("\n", enabled.Failures));
            CollectionAssert.AreEqual(new[] { path }, enabled.CompletedPaths.ToArray());
            Assert.AreEqual(expected, await File.ReadAllTextAsync(path));
            Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(path));
            Assert.IsFalse(File.Exists(disabledPath));
            Assert.AreEqual(0, restartedMods.Load().Records.Count);
            Assert.AreEqual(0, (await restarted.GetRecentlyDeletedAsync()).Files.Count);
        }
        finally
        {
            var ownItems = items.Concat(store.Load().Deleted.Select(entry => entry.Item))
                .Concat(mods.GetRecycledRecords().Select(entry => entry.Item))
                .Where(item => string.Equals(item.OriginalPath, disabledPath, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(item => Convert.ToHexString(item.AbsolutePidl));
            foreach (var item in ownItems)
            {
                var presence = await adapter.QueryAsync([item]);
                if (presence.Failures.Count > 0) throw new IOException("无法清理测试回收条目：" + string.Join("\n", presence.Failures));
                if (presence.PresentItems.Count == 0) continue;
                if (File.Exists(disabledPath)) File.Delete(disabledPath);
                var cleanup = await adapter.RestoreAsync([item]);
                if (cleanup.Failures.Count > 0) throw new IOException("无法恢复测试回收条目：" + string.Join("\n", cleanup.Failures));
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("WindowsIntegration")]
    public async Task PersistentHistoryValidatesExactRecycleItemAndNeverOverwrites()
    {
        if (Environment.GetEnvironmentVariable("SIMS4_MOD_SIEVE_RUN_RECYCLE_BIN_TEST") != "1")
            Assert.Inconclusive("Set SIMS4_MOD_SIEVE_RUN_RECYCLE_BIN_TEST=1 to run the real Recycle Bin test.");
        var directory = Path.Combine(Path.GetTempPath(), $"Sims4ModSieve-History-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "历史与同名保护.package");
        var adapter = new WindowsRecycleBinAdapter();
        var store = new FileManagementStore(directory);
        var service = new FileActionService(adapter, new PhysicalFileMetadataAccess(), store);
        try
        {
            await File.WriteAllTextAsync(path, "original");
            var info = new FileInfo(path);
            var deleted = await service.DeleteAsync(service.Preflight([new(path, info.Length, info.LastWriteTimeUtc, "第1组")], []));
            Assert.AreEqual(0, deleted.Failures.Count, string.Join("\n", deleted.Failures));
            var restarted = new FileActionService(adapter, new PhysicalFileMetadataAccess(), new FileManagementStore(directory));
            var history = await restarted.GetRecentlyDeletedAsync();
            Assert.AreEqual(0, history.Failures.Count, string.Join("\n", history.Failures));
            Assert.AreEqual(1, history.Files.Count);
            await File.WriteAllTextAsync(path, "do-not-overwrite");
            var blocked = await restarted.RestoreDeletedAsync([history.Files[0].Id]);
            Assert.AreEqual(0, blocked.CompletedPaths.Count);
            Assert.AreEqual("do-not-overwrite", await File.ReadAllTextAsync(path));
            Assert.AreEqual(1, (await restarted.GetRecentlyDeletedAsync()).Files.Count);
            File.Delete(path);
            var restored = await restarted.RestoreDeletedAsync([history.Files[0].Id]);
            Assert.AreEqual(0, restored.Failures.Count, string.Join("\n", restored.Failures));
            Assert.AreEqual("original", await File.ReadAllTextAsync(path));
            var presence = await adapter.QueryAsync([history.Files[0].Item]);
            Assert.AreEqual(0, presence.Failures.Count, string.Join("\n", presence.Failures));
            Assert.AreEqual(1, presence.MissingItems.Count);
            Assert.AreEqual(0, (await restarted.GetRecentlyDeletedAsync()).Files.Count);
        }
        finally
        {
            foreach (var record in store.Load().Deleted)
                if (!File.Exists(record.Item.OriginalPath)) await adapter.RestoreAsync([record.Item]);
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("WindowsIntegration")]
    public async Task UnicodeFileCanRoundTripThroughWindowsRecycleBin()
    {
        if (Environment.GetEnvironmentVariable("SIMS4_MOD_SIEVE_RUN_RECYCLE_BIN_TEST") != "1")
        {
            Assert.Inconclusive("Set SIMS4_MOD_SIEVE_RUN_RECYCLE_BIN_TEST=1 to run the real Recycle Bin test.");
        }

        var directory = Path.Combine(Path.GetTempPath(), $"Sims4ModSieve-Recycle-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "中文回收站验证.package");
        const string expected = "Sims4ModSieve recycle bin round trip";
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, expected);

        try
        {
            var adapter = new WindowsRecycleBinAdapter();
            Assert.IsTrue(adapter.CanRecycle(path));

            var deleted = await adapter.MoveToRecycleBinAsync([path]);
            Assert.AreEqual(0, deleted.Failures.Count, string.Join(Environment.NewLine, deleted.Failures));
            Assert.AreEqual(1, deleted.CompletedItems.Count);
            Assert.IsFalse(File.Exists(path));

            var restored = await adapter.RestoreAsync(deleted.CompletedItems);
            Assert.AreEqual(0, restored.Failures.Count, string.Join(Environment.NewLine, restored.Failures));
            Assert.AreEqual(1, restored.CompletedPaths.Count);
            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual(expected, await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
