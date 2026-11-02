using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ModDisableServiceTests
{
    [TestMethod]
    public void DisableRejectsSameStampPhysicalReplacementFromScan()
    {
        using var files = new Scenario();
        var path = files.Mod("replacement.package", "AAAA");
        var info = new FileInfo(path);
        var expected = new FileStamp(info.Length, info.LastWriteTimeUtc, PhysicalFileIdentity.Read(path));
        Assert.IsNotNull(expected.Identity);
        File.Move(path, path + ".old");
        File.WriteAllText(path, "BBBB");
        File.SetLastWriteTimeUtc(path, expected.LastWriteTimeUtc);

        var result = files.Service.Disable([path], new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase)
            { [path] = expected });

        Assert.AreEqual(0, result.CompletedPaths.Count);
        Assert.AreEqual(1, result.Failures.Count);
        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(File.Exists(path + ".off"));
    }

    [TestMethod]
    public void RestoreKeepsLegacyRecordWithoutIdentityForManualReview()
    {
        using var files = new Scenario();
        var path = files.Mod("legacy.package", "AAAA");
        Assert.AreEqual(1, files.Service.Disable([path]).CompletedPaths.Count);
        var record = files.Service.Load().Records.Single();
        var legacy = record with { Identity = null };
        File.WriteAllText(files.Ledger, JsonSerializer.Serialize(new[] { legacy }));

        var result = new ModDisableService(files.Data).Restore([record.Id]);

        Assert.AreEqual(0, result.CompletedPaths.Count);
        Assert.IsTrue(result.Failures.Any(failure => failure.Message.Contains("人工核对", StringComparison.Ordinal)));
        Assert.IsTrue(File.Exists(path + ".off"));
        Assert.AreEqual(1, new ModDisableService(files.Data).Load().Records.Count);
    }

    [TestMethod]
    public void PackageAndScriptDisableInPlaceAndRestoreAcrossRestart()
    {
        using var files = new Scenario();
        var package = files.Mod("nested/mod.package", "package");
        var script = files.Mod("nested/mod.ts4script", "script");
        var modified = File.GetLastWriteTimeUtc(package);
        var result = files.Service.Disable([package, script, package.ToUpperInvariant()]);
        CollectionAssert.AreEquivalent(new[] { package, script }, result.CompletedPaths.ToArray());
        Assert.AreEqual(0, result.Failures.Count);
        Assert.IsFalse(File.Exists(package));
        Assert.AreEqual("package", File.ReadAllText(package + ".off"));
        Assert.AreEqual("script", File.ReadAllText(script + ".off"));
        Assert.AreEqual(modified, File.GetLastWriteTimeUtc(package + ".off"));
        var restarted = new ModDisableService(files.Data);
        var records = restarted.Load().Records;
        Assert.AreEqual(2, records.Count);
        Assert.AreEqual(records.Single(record => record.OriginalPath == package).Identity,
            PhysicalFileIdentity.Read(package + ".off"));
        var restored = restarted.Restore(records.Select(record => record.Id).ToArray());
        Assert.AreEqual(2, restored.CompletedPaths.Count);
        Assert.AreEqual(0, restored.Failures.Count);
        Assert.AreEqual("package", File.ReadAllText(package));
        Assert.AreEqual("script", File.ReadAllText(script));
        Assert.AreEqual(0, new ModDisableService(files.Data).Load().Records.Count);
    }

    [TestMethod]
    public void DisableCollisionReportsPartialSuccessWithoutOverwriting()
    {
        using var files = new Scenario();
        var blocked = files.Mod("blocked.package", "original");
        var allowed = files.Mod("allowed.ts4script", "script");
        File.WriteAllText(blocked + ".off", "existing");
        var result = files.Service.Disable([blocked, allowed]);
        CollectionAssert.AreEqual(new[] { allowed }, result.CompletedPaths.ToArray());
        Assert.AreEqual(1, result.Failures.Count);
        Assert.AreEqual("original", File.ReadAllText(blocked));
        Assert.AreEqual("existing", File.ReadAllText(blocked + ".off"));
        Assert.AreEqual(allowed, files.Service.Load().Records.Single().OriginalPath);
    }

    [TestMethod]
    public void RestoreCollisionReportsPartialSuccessAndRetainsBlockedRecord()
    {
        using var files = new Scenario();
        var blocked = files.Mod("blocked.package", "original");
        var allowed = files.Mod("allowed.package", "allowed");
        files.Service.Disable([blocked, allowed]);
        File.WriteAllText(blocked, "replacement");
        var result = files.Service.Restore(files.Service.Load().Records.Select(record => record.Id).ToArray());
        CollectionAssert.AreEqual(new[] { allowed }, result.CompletedPaths.ToArray());
        Assert.AreEqual(1, result.Failures.Count);
        Assert.AreEqual("replacement", File.ReadAllText(blocked));
        Assert.AreEqual("original", File.ReadAllText(blocked + ".off"));
        Assert.AreEqual(blocked, new ModDisableService(files.Data).Load().Records.Single().OriginalPath);
    }

    [TestMethod]
    public void UnsupportedMissingAndAlreadyDisabledFilesAreNotRenamed()
    {
        using var files = new Scenario();
        var text = files.Mod("notes.txt", "notes");
        var off = files.Mod("old.package.off", "off");
        var missing = Path.Combine(files.Root, "missing.package");
        var result = files.Service.Disable([text, off, missing]);
        Assert.AreEqual(0, result.CompletedPaths.Count);
        Assert.AreEqual(3, result.Failures.Count);
        Assert.AreEqual("notes", File.ReadAllText(text));
        Assert.AreEqual("off", File.ReadAllText(off));
        Assert.AreEqual(0, files.Service.Load().Records.Count);
    }

    [TestMethod]
    public void FailedLedgerSaveDoesNotRenameOrLoseExistingRecords()
    {
        using var files = new Scenario();
        var first = files.Mod("first.package", "first");
        var second = files.Mod("second.package", "second");
        files.Service.Disable([first]);
        var before = File.ReadAllText(files.Ledger);
        using (File.Open(files.Ledger, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = files.Service.Disable([second]);
            Assert.AreEqual(0, result.CompletedPaths.Count);
            Assert.AreEqual(1, result.Failures.Count);
        }
        Assert.IsTrue(File.Exists(second));
        Assert.IsFalse(File.Exists(second + ".off"));
        Assert.AreEqual(before, File.ReadAllText(files.Ledger));
        Assert.AreEqual(first, files.Service.Load().Records.Single().OriginalPath);
    }

    [TestMethod]
    public void RestoreCompletedBeforeLedgerSaveFailureIsReconciledAfterRestart()
    {
        using var files = new Scenario();
        var original = files.Mod("restore.package", "original");
        files.Service.Disable([original]);
        var record = files.Service.Load().Records.Single();
        using (File.Open(files.Ledger, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = files.Service.Restore([record.Id]);
            CollectionAssert.AreEqual(new[] { original }, result.CompletedPaths.ToArray());
            Assert.AreEqual(1, result.Failures.Count);
        }
        Assert.AreEqual("original", File.ReadAllText(original));
        Assert.AreEqual(0, new ModDisableService(files.Data).Load().Records.Count);
        Assert.AreEqual(0, JsonSerializer.Deserialize<DisabledModRecord[]>(File.ReadAllText(files.Ledger))!.Length);
    }

    [TestMethod]
    public void InterruptedDisableBeforeRenameClearsOnlyMatchingPendingRecord()
    {
        using var files = new Scenario();
        var original = files.Mod("pending.package", "original");
        files.WriteLedger(files.Record(original));
        var result = new ModDisableService(files.Data).Load();
        Assert.AreEqual(0, result.Records.Count);
        Assert.AreEqual(0, result.Failures.Count);
        Assert.AreEqual("original", File.ReadAllText(original));
        Assert.AreEqual(0, JsonSerializer.Deserialize<DisabledModRecord[]>(File.ReadAllText(files.Ledger))!.Length);
    }

    [TestMethod]
    public void InterruptedDisableAfterRenameRetainsRecoverableRecord()
    {
        using var files = new Scenario();
        var original = files.Mod("pending.ts4script", "original");
        var record = files.Record(original);
        files.WriteLedger(record);
        File.Move(original, record.DisabledPath);
        var service = new ModDisableService(files.Data);
        Assert.AreEqual(record, service.Load().Records.Single());
        Assert.AreEqual(1, service.Restore([record.Id]).CompletedPaths.Count);
        Assert.AreEqual("original", File.ReadAllText(original));
    }

    [TestMethod]
    public void MissingAndChangedFilesRetainRecordsAndCannotBeRestored()
    {
        using var files = new Scenario();
        var missing = files.Mod("missing.package", "original");
        var changed = files.Mod("changed.package", "original");
        var pending = files.Mod("pending.package", "original");
        files.Service.Disable([missing, changed]);
        var records = files.Service.Load().Records.ToList();
        records.Add(files.Record(pending));
        files.WriteLedger(records.ToArray());
        File.Delete(missing + ".off");
        File.WriteAllText(changed + ".off", "changed content");
        File.WriteAllText(pending, "replacement pending");
        var state = new ModDisableService(files.Data).Load();
        Assert.AreEqual(3, state.Records.Count);
        Assert.AreEqual(3, state.Failures.Count);
        var result = files.Service.Restore(state.Records.Select(record => record.Id).ToArray());
        Assert.AreEqual(0, result.CompletedPaths.Count);
        Assert.AreEqual("changed content", File.ReadAllText(changed + ".off"));
        Assert.AreEqual("replacement pending", File.ReadAllText(pending));
        Assert.AreEqual(3, files.Service.Load().Records.Count);
    }

    [TestMethod]
    public void UnrecordedOffFilesAreNeverAdoptedOrRestoredByPath()
    {
        using var files = new Scenario();
        var off = files.Mod("manual.package.off", "manual");
        Assert.AreEqual(0, files.Service.Load().Records.Count);
        var result = files.Service.Restore([off]);
        Assert.AreEqual(0, result.CompletedPaths.Count);
        Assert.AreEqual(1, result.Failures.Count);
        Assert.AreEqual("manual", File.ReadAllText(off));
    }

    [TestMethod]
    public void DirectoryTargetsAreNeverOverwrittenOrMoved()
    {
        using var files = new Scenario();
        var original = files.Mod("directory.package", "original");
        Directory.CreateDirectory(original + ".off");
        Assert.AreEqual(0, files.Service.Disable([original]).CompletedPaths.Count);
        Assert.AreEqual("original", File.ReadAllText(original));
        Directory.Delete(original + ".off");
        files.Service.Disable([original]);
        Directory.CreateDirectory(original);
        var result = files.Service.Restore([files.Service.Load().Records.Single().Id]);
        Assert.AreEqual(0, result.CompletedPaths.Count);
        Assert.IsTrue(Directory.Exists(original));
        Assert.AreEqual("original", File.ReadAllText(original + ".off"));
    }

    [TestMethod]
    public void MalformedLedgerCannotRenameFilesOrBeOverwritten()
    {
        using var files = new Scenario();
        var original = files.Mod("safe.package", "safe");
        var valid = files.Record(original);
        foreach (var bad in new[]
        {
            "{}", "null", "[null]",
            JsonSerializer.Serialize(new[] { valid with { OriginalPath = "relative.package" } }),
            JsonSerializer.Serialize(new[] { valid with { OriginalPath = Path.Combine(files.Root, "notes.txt") } }),
            JsonSerializer.Serialize(new[] { valid with { OriginalPath = Path.Combine(files.Root, "folder", "..", "safe.package") } }),
            JsonSerializer.Serialize(new[] { valid with { Size = -1 } }),
            JsonSerializer.Serialize(new[] { valid, valid }),
        })
        {
            Directory.CreateDirectory(files.Data);
            File.WriteAllText(files.Ledger, bad);
            if (bad == "{}")
            {
                Assert.ThrowsExactly<JsonException>(() => files.Service.Load());
                Assert.ThrowsExactly<JsonException>(() => files.Service.Disable([original]));
                Assert.ThrowsExactly<JsonException>(() => files.Service.Restore([valid.Id]));
            }
            else
            {
                Assert.ThrowsExactly<InvalidDataException>(() => files.Service.Load());
                Assert.ThrowsExactly<InvalidDataException>(() => files.Service.Disable([original]));
                Assert.ThrowsExactly<InvalidDataException>(() => files.Service.Restore([valid.Id]));
            }
            Assert.AreEqual("safe", File.ReadAllText(original));
            Assert.IsFalse(File.Exists(original + ".off"));
            Assert.AreEqual(bad, File.ReadAllText(files.Ledger));
        }
    }

    [TestMethod]
    public void SerializedDisabledPathCannotRedirectRestore()
    {
        using var files = new Scenario();
        var original = files.Mod("mod.package", "mod");
        var unrelated = files.Mod("unrelated.package.off", "unrelated");
        var record = files.Record(original);
        files.WriteLedger(record);
        File.Move(original, record.DisabledPath);
        var json = JsonSerializer.Serialize(new[] { new { record.Id, record.OriginalPath, record.Size,
            record.LastWriteTimeUtc, record.DisabledAtUtc, record.Identity, DisabledPath = unrelated } });
        File.WriteAllText(files.Ledger, json);
        var result = files.Service.Restore([record.Id]);
        Assert.AreEqual(1, result.CompletedPaths.Count);
        Assert.AreEqual("mod", File.ReadAllText(original));
        Assert.AreEqual("unrelated", File.ReadAllText(unrelated));
    }

    [TestMethod]
    public void LegacyManagementDataKeepsIgnoreAndDeletedRecordsWithoutQuarantine()
    {
        using var files = new Scenario();
        var original = files.Mod("old.package", "old");
        var ignored = new IgnoredFileRecord(original, 3, DateTime.UtcNow);
        var deleted = new DeletedFileRecord("deleted", new(original, [0, 0]), 3, DateTime.UtcNow);
        Directory.CreateDirectory(files.Data);
        var path = Path.Combine(files.Data, "file-management.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { Ignored = new[] { ignored }, Deleted = new[] { deleted },
            QuarantineRoot = Path.Combine(files.Root, "Quarantine") }));
        var store = new FileManagementStore(files.Data);
        var state = store.Load();
        Assert.AreEqual(ignored, state.Ignored.Single());
        Assert.AreEqual(deleted.Id, state.Deleted.Single().Id);
        CollectionAssert.AreEqual(deleted.Item.AbsolutePidl, state.Deleted.Single().Item.AbsolutePidl);
        store.Update(data => data);
        Assert.IsFalse(File.ReadAllText(path).Contains("QuarantineRoot", StringComparison.Ordinal));
        Assert.AreEqual(1, new FileManagementStore(files.Data).Load().Deleted.Count);
    }

    [TestMethod]
    public async Task DeletedDisabledFilesRestoreAsOffAcrossRestartWithoutTouchingOriginal()
    {
        using var files = new Scenario();
        var package = files.Mod("mod.package", "package");
        var script = files.Mod("mod.ts4script", "script");
        files.Service.Disable([package, script]);
        var disabled = files.Service.Load().Records.ToArray();
        File.WriteAllText(package, "replacement");
        var bin = new TemporaryRecycleBin(files.Root);
        var actions = files.Actions(bin);
        var result = await actions.DeleteAsync(actions.Preflight(disabled.Select(Candidate).ToArray(), []));
        CollectionAssert.AreEquivalent(disabled.Select(record => record.DisabledPath).ToArray(), result.CompletedPaths.ToArray());
        Assert.AreEqual(0, result.Failures.Count);
        Assert.AreEqual(0, files.Service.Load().Records.Count);
        Assert.AreEqual(0, files.Service.Load().Failures.Count);
        Assert.AreEqual("replacement", File.ReadAllText(package));
        var restartedMods = new ModDisableService(files.Data);
        var restarted = files.Actions(bin, restartedMods);
        var recent = await restarted.GetRecentlyDeletedAsync();
        Assert.AreEqual(2, recent.Files.Count);
        Assert.IsTrue(recent.Files.All(record => record.DisabledMod is not null && record.Item.OriginalPath.EndsWith(".off", StringComparison.Ordinal)));
        var restored = await restarted.RestoreDeletedAsync(recent.Files.Select(record => record.Id).ToArray());
        Assert.AreEqual(2, restored.CompletedPaths.Count);
        Assert.AreEqual(0, restored.Failures.Count);
        CollectionAssert.AreEquivalent(disabled.Select(record => record.Id).ToArray(), restartedMods.Load().Records.Select(record => record.Id).ToArray());
        Assert.AreEqual("package", File.ReadAllText(package + ".off"));
        Assert.AreEqual("script", File.ReadAllText(script + ".off"));
        Assert.AreEqual("replacement", File.ReadAllText(package));
        Assert.IsFalse(File.Exists(script));
        Assert.AreEqual(0, (await restarted.GetRecentlyDeletedAsync()).Files.Count);
        Assert.AreEqual(0, restartedMods.GetRecycledRecords().Count);
        var enable = restartedMods.Restore(disabled.Select(record => record.Id).ToArray());
        CollectionAssert.AreEqual(new[] { script }, enable.CompletedPaths.ToArray());
        Assert.AreEqual("replacement", File.ReadAllText(package));
        Assert.AreEqual("script", File.ReadAllText(script));
    }

    [TestMethod]
    public async Task UndoDisabledDeletionKeepsOffSuffixAndRestoreFailureKeepsHiddenRecord()
    {
        using var files = new Scenario();
        var original = files.Mod("undo.package", "disabled");
        files.Service.Disable([original]);
        var record = files.Service.Load().Records.Single();
        var bin = new TemporaryRecycleBin(files.Root);
        var actions = files.Actions(bin);
        await actions.DeleteAsync(actions.Preflight([Candidate(record)], []));
        bin.FailRestore = true;
        var failed = await actions.UndoLastDeleteAsync();
        Assert.AreEqual(0, failed.CompletedPaths.Count);
        Assert.AreEqual(1, failed.Failures.Count);
        Assert.IsTrue(actions.CanUndoLastDelete);
        Assert.AreEqual(0, new ModDisableService(files.Data).Load().Records.Count);
        Assert.IsFalse(File.Exists(record.DisabledPath));
        Assert.AreEqual(1, (await actions.GetRecentlyDeletedAsync()).Files.Count);
        bin.FailRestore = false;
        var restored = await actions.UndoLastDeleteAsync();
        CollectionAssert.AreEqual(new[] { record.DisabledPath }, restored.CompletedPaths.ToArray());
        Assert.AreEqual(0, restored.Failures.Count);
        Assert.IsFalse(actions.CanUndoLastDelete);
        Assert.IsFalse(File.Exists(original));
        Assert.AreEqual("disabled", File.ReadAllText(record.DisabledPath));
        Assert.AreEqual(record.Id, new ModDisableService(files.Data).Load().Records.Single().Id);
        Assert.AreEqual(0, (await actions.GetRecentlyDeletedAsync()).Files.Count);
    }

    [TestMethod]
    public async Task DisabledDeleteRechecksChangesAndRequiresSavedIntentBeforeRecycling()
    {
        using var files = new Scenario();
        var original = files.Mod("protected.package", "disabled");
        files.Service.Disable([original]);
        var record = files.Service.Load().Records.Single();
        var bin = new TemporaryRecycleBin(files.Root);
        var actions = files.Actions(bin);
        var preflight = actions.Preflight([Candidate(record)], []);
        Assert.IsTrue(preflight.CanExecute);
        Assert.AreEqual(record.Id, files.Service.Load().Records.Single().Id, "仅预检或取消确认不会移除记录");
        Assert.AreEqual(0, bin.DeleteCalls);
        File.AppendAllText(record.DisabledPath, "changed");
        var changed = await actions.DeleteAsync(preflight);
        Assert.AreEqual(0, changed.CompletedPaths.Count);
        Assert.AreEqual(0, bin.DeleteCalls);
        Assert.AreEqual(record.Id, files.Service.Load().Records.Single().Id);
        File.WriteAllText(record.DisabledPath, "disabled");
        File.SetLastWriteTimeUtc(record.DisabledPath, record.LastWriteTimeUtc);
        var before = File.ReadAllText(files.Ledger);
        using (File.Open(files.Ledger, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var blocked = await actions.DeleteAsync(preflight);
            Assert.AreEqual(0, blocked.CompletedPaths.Count);
            Assert.AreEqual(1, blocked.Failures.Count);
            Assert.AreEqual(0, bin.DeleteCalls);
        }
        Assert.AreEqual(before, File.ReadAllText(files.Ledger));
        Assert.AreEqual("disabled", File.ReadAllText(record.DisabledPath));
        Assert.AreEqual(record.Id, files.Service.Load().Records.Single().Id);
    }

    [TestMethod]
    public async Task DisabledDeletionPartialFailureLeavesOnlyFailedFileActive()
    {
        using var files = new Scenario();
        var first = files.Mod("first.package", "first");
        var second = files.Mod("second.ts4script", "second");
        files.Service.Disable([first, second]);
        var records = files.Service.Load().Records.ToArray();
        var bin = new TemporaryRecycleBin(files.Root) { FailDeletePath = first + ".off" };
        var actions = files.Actions(bin);
        var deleted = await actions.DeleteAsync(actions.Preflight(records.Select(Candidate).ToArray(), []));
        CollectionAssert.AreEqual(new[] { second + ".off" }, deleted.CompletedPaths.ToArray());
        Assert.AreEqual(1, deleted.Failures.Count);
        Assert.AreEqual(first, files.Service.Load().Records.Single().OriginalPath);
        Assert.AreEqual(0, files.Service.Load().Failures.Count);
        Assert.AreEqual(second + ".off", (await actions.GetRecentlyDeletedAsync()).Files.Single().Item.OriginalPath);
        Assert.AreEqual("first", File.ReadAllText(first + ".off"));
    }

    [TestMethod]
    public async Task FailedDeletionHistorySaveRecoversExactDisabledEntryFromMirrorAfterRestart()
    {
        using var files = new Scenario();
        var original = files.Mod("mirror.package", "disabled");
        files.Service.Disable([original]);
        var record = files.Service.Load().Records.Single();
        var history = new FileManagementStore(files.Data);
        history.Update(data => data);
        var historyPath = Path.Combine(files.Data, "file-management.json");
        var bin = new TemporaryRecycleBin(files.Root);
        var actions = files.Actions(bin);
        using (File.Open(historyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var deleted = await actions.DeleteAsync(actions.Preflight([Candidate(record)], []));
            Assert.AreEqual(1, deleted.CompletedPaths.Count);
            Assert.AreEqual(1, deleted.Failures.Count);
        }
        Assert.AreEqual(0, new FileManagementStore(files.Data).Load().Deleted.Count);
        var restartedMods = new ModDisableService(files.Data);
        var mirrored = restartedMods.GetRecycledRecords().Single();
        Assert.AreEqual(0, restartedMods.Load().Records.Count);
        var restartedActions = files.Actions(bin, restartedMods);
        var recovered = (await restartedActions.GetRecentlyDeletedAsync()).Files.Single();
        Assert.AreEqual(mirrored.Id, recovered.Id);
        CollectionAssert.AreEqual(mirrored.Item.AbsolutePidl, recovered.Item.AbsolutePidl);
        Assert.AreEqual(record.Id, recovered.DisabledMod!.Id);
        Assert.AreEqual(1, (await restartedActions.RestoreDeletedAsync([recovered.Id])).CompletedPaths.Count);
        Assert.AreEqual("disabled", File.ReadAllText(record.DisabledPath));
        Assert.AreEqual(record.Id, restartedMods.Load().Records.Single().Id);
    }

    [TestMethod]
    public async Task OldRecycledDisabledVersionCannotOverwriteNewOffAndClearedBinLeavesNoGhost()
    {
        using var files = new Scenario();
        var original = files.Mod("version.package", "old version");
        files.Service.Disable([original]);
        var old = files.Service.Load().Records.Single();
        var bin = new TemporaryRecycleBin(files.Root);
        var actions = files.Actions(bin);
        await actions.DeleteAsync(actions.Preflight([Candidate(old)], []));
        var deleted = (await actions.GetRecentlyDeletedAsync()).Files.Single();
        File.WriteAllText(original, "new version contents");
        Assert.AreEqual(1, files.Service.Disable([original]).CompletedPaths.Count);
        var current = files.Service.Load().Records.Single();
        Assert.AreNotEqual(old.Id, current.Id);
        var collision = await actions.RestoreDeletedAsync([deleted.Id]);
        Assert.AreEqual(0, collision.CompletedPaths.Count);
        Assert.AreEqual(1, collision.Failures.Count);
        Assert.AreEqual("new version contents", File.ReadAllText(current.DisabledPath));
        Assert.AreEqual(current.Id, files.Service.Load().Records.Single().Id);
        Assert.AreEqual(1, (await actions.GetRecentlyDeletedAsync()).Files.Count);
        bin.Empty();
        Assert.AreEqual(0, (await actions.GetRecentlyDeletedAsync()).Files.Count);
        Assert.AreEqual(0, files.Service.GetRecycledRecords().Count);
        var restartedMods = new ModDisableService(files.Data);
        Assert.AreEqual(current.Id, restartedMods.Load().Records.Single().Id);
        Assert.AreEqual(0, restartedMods.Load().Failures.Count);
        Assert.AreEqual(0, (await files.Actions(bin, restartedMods).GetRecentlyDeletedAsync()).Files.Count);
    }

    [TestMethod]
    public async Task InterruptedRecycleAndRestoreReconcileOnlyRecordedTransition()
    {
        using var files = new Scenario();
        var original = files.Mod("interrupted.package", "disabled");
        var second = files.Mod("interrupted.ts4script", "script");
        files.Service.Disable([original, second]);
        var record = files.Service.Load().Records.Single(item => item.OriginalPath == original);
        var other = files.Service.Load().Records.Single(item => item.OriginalPath == second);
        files.Service.PrepareRecycle([record]);
        Assert.AreEqual(record.Id, new ModDisableService(files.Data).Load().Records.Single(item => item.OriginalPath == original).Id,
            "回收前中断时实际.off仍在，应继续显示");
        files.Service.PrepareRecycle([record]);
        files.Service.PrepareRecycle([other]);
        var prepared = JsonSerializer.Deserialize<DisabledModRecord[]>(File.ReadAllText(files.Ledger))!;
        Assert.AreEqual(2, prepared.Count(item => item.RecyclePending), "准备后续候选不能清掉本批已有回收意图");
        var bin = new TemporaryRecycleBin(files.Root);
        var recycled = (await bin.MoveToRecycleBinAsync([record.DisabledPath, other.DisabledPath])).CompletedItems
            .Single(item => item.OriginalPath == record.DisabledPath);
        var restarted = new ModDisableService(files.Data);
        Assert.AreEqual(0, restarted.Load().Records.Count);
        Assert.AreEqual(0, restarted.Load().Failures.Count);
        var persisted = JsonSerializer.Deserialize<DisabledModRecord[]>(File.ReadAllText(files.Ledger))!.Single(item => item.Id == record.Id);
        Assert.AreEqual(record.Id, persisted.Id, "Shell返回前中断也必须保留原名恢复信息");
        restarted.PrepareRecycleRestore(record);
        Assert.AreEqual(0, new ModDisableService(files.Data).Load().Records.Count,
            "只有恢复意图而.off尚未回来时不能发布已禁用项");
        await bin.RestoreAsync([recycled]);
        var afterRestore = new ModDisableService(files.Data).Load();
        Assert.AreEqual(record.Id, afterRestore.Records.Single().Id);
        Assert.AreEqual(0, afterRestore.Failures.Count);
        Assert.IsFalse(File.Exists(original));
        Assert.AreEqual("disabled", File.ReadAllText(record.DisabledPath));
    }

    private static FileDeletionCandidate Candidate(DisabledModRecord record) =>
        new(record.DisabledPath, record.Size, record.LastWriteTimeUtc, "", record, record.Identity);

    private sealed class TemporaryRecycleBin(string root) : IRecycleBinAdapter
    {
        private readonly string directory = Path.Combine(root, "Recycle");
        private int sequence;
        public int DeleteCalls { get; private set; }
        public bool FailRestore { get; set; }
        public string? FailDeletePath { get; set; }
        public bool CanRecycle(string path) => true;

        public Task<RecycleBinDeleteResult> MoveToRecycleBinAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            Directory.CreateDirectory(directory);
            var completed = new List<RecycleBinItem>();
            var failures = new List<FileActionFailure>();
            foreach (var path in paths)
            {
                if (path == FailDeletePath) { failures.Add(new(path, "模拟回收失败")); continue; }
                var target = Path.Combine(directory, (++sequence).ToString());
                File.Move(path, target);
                completed.Add(new(path, [4, 0, (byte)sequence, 0, 0, 0], target));
            }
            return Task.FromResult(new RecycleBinDeleteResult(completed, failures));
        }

        public Task<RecycleBinPresence> QueryAsync(IReadOnlyList<RecycleBinItem> items, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecycleBinPresence(items.Where(item => File.Exists(item.RecycledPath)).ToArray(),
                items.Where(item => !File.Exists(item.RecycledPath)).ToArray(), []));

        public Task<FileActionBatchResult> RestoreAsync(IReadOnlyList<RecycleBinItem> items, CancellationToken cancellationToken = default)
        {
            var completed = new List<string>();
            var failures = new List<FileActionFailure>();
            foreach (var item in items)
            {
                if (FailRestore || File.Exists(item.OriginalPath)) { failures.Add(new(item.OriginalPath, "模拟恢复失败或目标已存在")); continue; }
                File.Move(item.RecycledPath!, item.OriginalPath);
                completed.Add(item.OriginalPath);
            }
            return Task.FromResult(new FileActionBatchResult(completed, failures, false));
        }

        public void Empty()
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
        }
    }

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Sims4ModSieve-disable-" + Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(Root, "data");
        public string Ledger => Path.Combine(Data, "disabled-mods.json");
        public ModDisableService Service { get; }

        public Scenario()
        {
            Directory.CreateDirectory(Root);
            Service = new(Data);
        }

        public string Mod(string relative, string content)
        {
            var path = Path.GetFullPath(Path.Combine(Root, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public DisabledModRecord Record(string path) => new(Guid.NewGuid().ToString("N"), path,
            new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), DateTime.UtcNow,
            Identity: PhysicalFileIdentity.Read(path));

        public void WriteLedger(params DisabledModRecord[] records)
        {
            Directory.CreateDirectory(Data);
            File.WriteAllText(Ledger, JsonSerializer.Serialize(records));
        }

        public FileActionService Actions(IRecycleBinAdapter bin, ModDisableService? mods = null) =>
            new(bin, new PhysicalFileMetadataAccess(), new FileManagementStore(Data), mods ?? Service);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
