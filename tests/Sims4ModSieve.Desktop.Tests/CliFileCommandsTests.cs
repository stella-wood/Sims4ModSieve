using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Cli;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Settings;
using Sims4ModSieve.Core.Tests;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.SettingsViewModelTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class CliFileCommandsTests
{
    [TestMethod]
    [DataRow("scan-duplicates")]
    [DataRow("scan-conflicts")]
    [DataRow("scan-scripts")]
    public Task CliModScansShareExternalLedgerWithDesktopAndRestoreByEitherPath(string command) => WpfTestHost.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var settings = new DuplicateSettingsStore(temp.Path);
        var package = temp.WriteFile("Mods/Nested/garden.packageOFF", "package");
        var script = temp.WriteFile("Mods/story.ts4script.disabled", "script");
        var unrelated = temp.WriteFile("Mods/notes.off", "notes");
        var backup = temp.WriteFile("ModsBackup/backup.package.off", "backup");
        var known = temp.WriteFile("Mods/known.package", "known");
        var mods = Path.GetDirectoryName(known)!;
        settings.Save(new(mods, []));
        new ModDisableService(settings.DataDirectory).Disable([known]);

        Assert.HasCount(1, (await Run(settings, null, ["list-disabled"])).Files);
        var scan = await Run(settings, null, [command, "--no-cache",
            "--source", mods, "--source", Path.GetDirectoryName(backup)!]);
        Assert.AreEqual(0, scan.ExitCode);
        var list = await Run(settings, null, ["list-disabled"]);
        Assert.AreEqual(0, list.ExitCode);
        Assert.HasCount(3, list.Files);
        var sources = list.Files.ToDictionary(file => file!["path"]!.GetValue<string>(), file => file!["source"]!.GetValue<string>());
        Assert.AreEqual("sieve", sources[known + ".off"]);
        Assert.AreEqual("external", sources[package]);
        Assert.AreEqual("external", sources[script]);
        Assert.IsFalse(sources.ContainsKey(backup));
        Assert.IsFalse(sources.ContainsKey(unrelated));
        var model = new MainWindowViewModel(new DuplicateRunService(DuplicateScanner.CreateDefault()), new Picker(), settings,
            fileActionDialogs: new Dialogs(), confirmModChange: _ => true);
        Assert.AreEqual(list.Files.Count, model.DisabledFiles.Files.Count);

        var ledger = Path.Combine(settings.DataDirectory, "disabled-mods.json");
        var before = File.ReadAllText(ledger);
        var preview = await Run(settings, null, ["restore", "--all"]);
        Assert.HasCount(3, preview.Files);
        Assert.IsTrue(preview.Files.All(file => file!["status"]!.GetValue<string>() == "preview"));
        Assert.AreEqual(before, File.ReadAllText(ledger));
        Assert.IsTrue(File.Exists(package) && File.Exists(script));
        var wrongPath = await Run(settings, null, ["restore", "--path", package[..^3] + ".off", "--apply"]);
        Assert.AreEqual(3, wrongPath.ExitCode);
        Assert.IsTrue(File.Exists(package));
        var restored = await Run(settings, null, ["restore", "--path", package,
            "--path", script[..^".disabled".Length], "--apply"]);
        Assert.AreEqual(0, restored.ExitCode);
        Assert.AreEqual("package", File.ReadAllText(package[..^3]));
        Assert.AreEqual("script", File.ReadAllText(script[..^".disabled".Length]));
        Assert.HasCount(1, (await Run(settings, null, ["list-disabled"])).Files);
        Assert.IsTrue(File.Exists(backup) && File.Exists(unrelated));
    });

    [TestMethod]
    [TestCategory("WindowsIntegration")]
    public async Task ExternalDisabledFileRoundTripsThroughWindowsRecycleBin()
    {
        if (Environment.GetEnvironmentVariable("SIMS4_MOD_SIEVE_RUN_RECYCLE_BIN_TEST") != "1")
            Assert.Inconclusive("Set SIMS4_MOD_SIEVE_RUN_RECYCLE_BIN_TEST=1 to run the real Recycle Bin test.");
        using var temp = new TempDirectory();
        var settings = new DuplicateSettingsStore(temp.Path);
        var disabled = temp.WriteFile("Mods/Temporary.packageOFF", "temporary mod");
        var service = new ModDisableService(settings.DataDirectory);
        var record = service.RegisterExternal([disabled])!.Records.Single();
        var bin = new WindowsRecycleBinAdapter();
        var history = new FileManagementStore(settings.DataDirectory);
        var actions = new FileActionService(bin, new PhysicalFileMetadataAccess(), history, service);
        try
        {
            var preflight = actions.Preflight(
                [new(disabled, record.Size, record.LastWriteTimeUtc, "", record, record.Identity)], []);
            Assert.IsTrue(preflight.CanExecute);
            var deleted = await actions.DeleteAsync(preflight);
            Assert.IsEmpty(deleted.Failures);
            Assert.IsFalse(File.Exists(disabled));
            Assert.IsEmpty(service.Load().Records);
            var undo = await actions.UndoLastDeleteAsync();
            Assert.IsEmpty(undo.Failures);
            CollectionAssert.AreEqual(new[] { disabled }, undo.CompletedPaths.ToArray());
            Assert.AreEqual("temporary mod", File.ReadAllText(disabled));
            Assert.IsFalse(File.Exists(record.OriginalPath));
            Assert.AreEqual(record, service.Load().Records.Single());
        }
        finally
        {
            await bin.RestoreAsync(history.Load().Deleted.Where(item => !File.Exists(item.Item.OriginalPath))
                .Select(item => item.Item).ToArray());
        }
    }

    [TestMethod]
    public async Task FileCommandPreviewsAndListingLeaveFilesAndLedgersUntouched()
    {
        using var temp = new TempDirectory();
        var settings = new DuplicateSettingsStore(temp.Path);
        var bin = new FileManagementTests.TemporaryRecycleBin(temp.CreateDirectory("Recycle"));
        var live = temp.WriteFile("live.package", "live");
        var disable = await Run(settings, bin, ["disable", "--path", live]);
        var recycle = await Run(settings, bin, ["recycle", "--path", live]);
        Assert.AreEqual("preview", disable.Status(0));
        Assert.AreEqual("preview", recycle.Status(0));
        Assert.AreEqual(4L, disable.File(0)["size"]!.GetValue<long>());
        Assert.AreEqual(4L, recycle.File(0)["size"]!.GetValue<long>());
        Assert.IsFalse(Directory.Exists(settings.DataDirectory));

        var hidden = temp.WriteFile("hidden.ts4script", "hidden");
        var reconciled = temp.WriteFile("reconciled.package", "reconciled");
        new ModDisableService(settings.DataDirectory).Disable([hidden, reconciled]);
        File.Move(reconciled + ".off", reconciled);
        var alreadyDeleted = temp.WriteFile("deleted.package", "deleted");
        await DeleteWithDesktop(settings, bin, alreadyDeleted);
        var before = Directory.GetFiles(settings.DataDirectory).ToDictionary(path => path, File.ReadAllText);

        var list = await Run(settings, bin, ["list-disabled"]);
        var restore = await Run(settings, bin, ["restore", "--all"]);
        var undo = await Run(settings, bin, ["undo"]);
        Assert.AreEqual(1, list.Files.Count);
        Assert.AreEqual("listed", list.Status(0));
        Assert.AreEqual(hidden + ".off", list.File(0)["path"]!.GetValue<string>());
        Assert.AreEqual("preview", restore.Status(0));
        Assert.AreEqual(6L, restore.File(0)["size"]!.GetValue<long>());
        Assert.AreEqual("preview", undo.Status(0));
        Assert.AreEqual(7L, undo.File(0)["size"]!.GetValue<long>());
        CollectionAssert.AreEquivalent(before.Keys.ToArray(), Directory.GetFiles(settings.DataDirectory));
        foreach (var entry in before) Assert.AreEqual(entry.Value, File.ReadAllText(entry.Key));
        Assert.AreEqual("live", File.ReadAllText(live));
        Assert.AreEqual("hidden", File.ReadAllText(hidden + ".off"));
        Assert.IsFalse(File.Exists(hidden));
        Assert.IsFalse(File.Exists(alreadyDeleted));
        Assert.AreEqual("reconciled", File.ReadAllText(reconciled));
    }

    [TestMethod]
    public Task CliAndDesktopRestoreEachOthersDisabledFiles() => WpfTestHost.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var settings = new DuplicateSettingsStore(temp.Path);
        var path = temp.WriteFile("共享.ts4script", "content");
        var cliDisable = await Run(settings, null, ["disable", "--path", path, "--apply"]);
        Assert.AreEqual(0, cliDisable.ExitCode);
        Assert.IsTrue(File.Exists(path + ".off"));

        var model = new MainWindowViewModel(new DuplicateRunService(DuplicateScanner.CreateDefault()), new Picker(), settings,
            fileActionDialogs: new Dialogs(), confirmModChange: _ => true);
        Assert.AreEqual(1, model.DisabledFiles.Files.Count);
        await model.DisabledFiles.Files.Single().RestoreCommand!.ExecuteAsync();
        Assert.AreEqual("content", File.ReadAllText(path));
        Assert.AreEqual(0, (await Run(settings, null, ["list-disabled"])).Files.Count);

        await model.DisableFilesAsync([path]);
        var restored = await Run(settings, null, ["restore", "--path", path + ".off", "--apply"]);
        Assert.AreEqual(0, restored.ExitCode);
        Assert.AreEqual("completed", restored.Status(0));
        Assert.AreEqual("content", File.ReadAllText(path));
        Assert.IsFalse(File.Exists(path + ".off"));
    });

    [TestMethod]
    public async Task RecycleChecksFreshHashesInPathOrderAndReportsPartialFailure()
    {
        using var temp = new TempDirectory();
        var settings = new DuplicateSettingsStore(temp.Path);
        var bin = new FileManagementTests.TemporaryRecycleBin(temp.CreateDirectory("Recycle"));
        var changed = temp.WriteFile("Mods/changed.package", "old!");
        var stamped = File.GetLastWriteTimeUtc(changed);
        var expected = Hash(changed);
        var other = temp.WriteFile("Mods/other.ts4script", "keep processing");
        var scan = await Run(settings, bin, ["scan-duplicates", "--source", Path.GetDirectoryName(changed)!]);
        Assert.AreEqual(0, scan.ExitCode);
        Assert.IsTrue(File.Exists(Path.Combine(settings.DataDirectory, "cache", "sha256.sqlite3")));
        File.WriteAllText(changed, "new!");
        File.SetLastWriteTimeUtc(changed, stamped);
        var missingHash = await Run(settings, bin, ["recycle", "--path", other, "--apply"]);
        Assert.AreEqual(2, missingHash.ExitCode);
        Assert.IsTrue(File.Exists(other));

        var result = await Run(settings, bin, ["recycle", "--path", changed, "--path", other,
            "--expect-sha256", expected, "--expect-sha256", Hash(other), "--apply"]);
        Assert.AreEqual(3, result.ExitCode);
        Assert.AreEqual("failed", result.Status(0));
        Assert.AreEqual("completed", result.Status(1));
        Assert.AreEqual("new!", File.ReadAllText(changed));
        Assert.IsFalse(File.Exists(other));
        CollectionAssert.AreEqual(new[] { other }, bin.DeleteBatches.Single());
        var log = File.ReadAllLines(Path.Combine(settings.DataDirectory, "cli-file-operations.jsonl"));
        Assert.AreEqual(2, log.Length);
        var entry = JsonNode.Parse(log[1])!;
        Assert.AreEqual("recycle", entry["command"]!.GetValue<string>());
        Assert.IsNotNull(entry["time"]);
        Assert.AreEqual("failed", entry["result"]!.GetValue<string>());
        Assert.AreEqual("completed", entry["files"]![1]!["status"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task FileOperationsRejectUnsupportedTypesDirectoriesAndSieveAssets()
    {
        using var temp = new TempDirectory();
        var settings = new DuplicateSettingsStore(temp.Path);
        var application = temp.CreateDirectory("App");
        var protectedMod = temp.WriteFile("App/Assets/owned.package", "owned");
        var executable = temp.WriteFile("App/Sims4ModSieve.Cli.exe", "owned");
        var text = temp.WriteFile("notes.txt", "text");
        var off = temp.WriteFile("hidden.package.off", "off");
        var folder = temp.CreateDirectory("folder.ts4script");
        var allowed = temp.WriteFile("allowed.PACKAGE", "allow");
        var paths = new[] { protectedMod, executable, text, off, folder };
        var arguments = paths.SelectMany(path => new[] { "--path", path }).ToArray();
        var bin = new FileManagementTests.TemporaryRecycleBin(temp.CreateDirectory("Recycle"));

        var result = await Run(settings, bin, ["disable", .. arguments, "--path", allowed, "--apply"], application);
        Assert.AreEqual(3, result.ExitCode);
        for (var index = 0; index < paths.Length; index++) Assert.AreEqual("failed", result.Status(index));
        StringAssert.Contains(result.File(0)["messages"]![0]!.GetValue<string>(), "程序自身");
        Assert.AreEqual("completed", result.Status(paths.Length));
        Assert.IsTrue(File.Exists(allowed + ".off"));

        var hashes = paths.SelectMany(_ => new[] { "--expect-sha256", new string('0', 64) }).ToArray();
        var recycled = await Run(settings, bin, ["recycle", .. arguments, .. hashes, "--apply"], application);
        Assert.AreEqual(3, recycled.ExitCode);
        for (var index = 0; index < paths.Length; index++) Assert.AreEqual("failed", recycled.Status(index));
        Assert.AreEqual(0, bin.DeleteBatches.Count);
        Assert.IsTrue(paths.Take(4).All(File.Exists));
        Assert.IsTrue(Directory.Exists(folder));
    }

    [TestMethod]
    public async Task ApplyBlockedByAnotherWriterStillWritesAuditAndCanBeRetried()
    {
        using var temp = new TempDirectory();
        var settings = new DuplicateSettingsStore(temp.Path);
        Directory.CreateDirectory(settings.DataDirectory);
        var path = temp.WriteFile("blocked.package", "keep");
        var args = new[] { "disable", "--path", path, "--apply" };
        using (File.Open(Path.Combine(settings.DataDirectory, ".file-management.lock"),
            FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            var blocked = await Run(settings, null, args);
            Assert.AreEqual(3, blocked.ExitCode);
            Assert.AreEqual("failed", blocked.Status(0));
            Assert.AreEqual("keep", File.ReadAllText(path));
            Assert.IsFalse(File.Exists(path + ".off"));
        }

        var resumed = await Run(settings, null, args);
        Assert.AreEqual(0, resumed.ExitCode);
        var log = File.ReadAllLines(Path.Combine(settings.DataDirectory, "cli-file-operations.jsonl"));
        CollectionAssert.AreEqual(new[] { "failed", "completed" }, log.Select(line => JsonNode.Parse(line)!["result"]!.GetValue<string>()).ToArray());
        Assert.AreEqual("keep", File.ReadAllText(path + ".off"));
    }

    [TestMethod]
    public async Task UndoRestoresOnlyLatestSharedBatchAndRetriesRemainingFiles()
    {
        using var temp = new TempDirectory();
        var settings = new DuplicateSettingsStore(temp.Path);
        var bin = new FileManagementTests.TemporaryRecycleBin(temp.CreateDirectory("Recycle"));
        var older = temp.WriteFile("older.package", "older");
        var desktop = await DeleteWithDesktop(settings, bin, older);
        var first = temp.WriteFile("first.package", "one");
        var second = temp.WriteFile("second.ts4script", "two");
        var deleted = await Run(settings, bin, ["recycle", "--path", first, "--expect-sha256", Hash(first),
            "--path", second, "--expect-sha256", Hash(second), "--apply"]);
        Assert.AreEqual(0, deleted.ExitCode);
        Assert.AreEqual(3, (await desktop.GetRecentlyDeletedAsync()).Files.Count);
        Assert.AreEqual(2, (await Run(settings, bin, ["undo"])).Files.Count);

        File.WriteAllText(second, "replacement");
        var partial = await Run(settings, bin, ["undo", "--apply"]);
        Assert.AreEqual(3, partial.ExitCode);
        Assert.AreEqual("one", File.ReadAllText(first));
        Assert.AreEqual("replacement", File.ReadAllText(second));
        Assert.IsFalse(File.Exists(older));
        File.Delete(second);
        var retry = await Run(settings, bin, ["undo", "--apply"]);
        Assert.AreEqual(0, retry.ExitCode);
        Assert.AreEqual("two", File.ReadAllText(second));
        Assert.AreEqual(0, (await Run(settings, bin, ["undo", "--apply"])).Files.Count);
        Assert.IsFalse(File.Exists(older));
        Assert.AreEqual(older, (await desktop.GetRecentlyDeletedAsync()).Files.Single().Item.OriginalPath);
    }

    [TestMethod]
    [TestCategory("WindowsIntegration")]
    public async Task CliRoundTripsThroughWindowsRecycleBin()
    {
        if (Environment.GetEnvironmentVariable("SIMS4_MOD_SIEVE_RUN_RECYCLE_BIN_TEST") != "1")
            Assert.Inconclusive("Set SIMS4_MOD_SIEVE_RUN_RECYCLE_BIN_TEST=1 to run the real Recycle Bin test.");
        using var temp = new TempDirectory();
        var settings = new DuplicateSettingsStore(temp.Path);
        var path = temp.WriteFile("CLI 回收站.package", "temporary mod");
        var bin = new WindowsRecycleBinAdapter();
        try
        {
            var deleted = await Run(settings, bin, ["recycle", "--path", path, "--expect-sha256", Hash(path), "--apply"]);
            Assert.AreEqual(0, deleted.ExitCode, deleted.Json.ToJsonString());
            Assert.IsFalse(File.Exists(path));
            var restored = await Run(settings, bin, ["undo", "--apply"]);
            Assert.AreEqual(0, restored.ExitCode, restored.Json.ToJsonString());
            Assert.AreEqual("temporary mod", File.ReadAllText(path));
        }
        finally
        {
            var data = new FileManagementStore(settings.DataDirectory).Load();
            await bin.RestoreAsync(data.Deleted.Where(record => !File.Exists(record.Item.OriginalPath)).Select(record => record.Item).ToArray());
        }
    }

    [TestMethod, DoNotParallelize, TestCategory("WindowsIntegration")]
    [DataRow(1, 8, "已禁用")]
    [DataRow(0, 1, "超过")]
    public async Task CliPreservesOriginalWhenRecycleIsDisabledOrFileExceedsCapacity(int disabled, int capacityMb, string reason)
    {
        if (Environment.GetEnvironmentVariable("SIMS4_MOD_SIEVE_RUN_RECYCLE_BIN_TEST") != "1")
            Assert.Inconclusive("Set SIMS4_MOD_SIEVE_RUN_RECYCLE_BIN_TEST=1 to run the real Recycle Bin test.");
        using var temp = new TempDirectory();
        var content = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        var path = temp.WriteBytes("保留原文件.package", content);
        var volume = new StringBuilder(64);
        Assert.IsTrue(GetVolumeNameForVolumeMountPoint(Path.GetPathRoot(path)!, volume, volume.Capacity));
        var key = $@"Software\Sims4ModSieve-RecycleTest-{Guid.NewGuid():N}";
        var currentUser = new IntPtr(unchecked((int)0x80000001));
        try
        {
            using var isolated = Registry.CurrentUser.CreateSubKey(key);
            using var settings = isolated.CreateSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{volume.ToString()[10..^1]}");
            settings.SetValue("NukeOnDelete", disabled);
            settings.SetValue("MaxCapacity", capacityMb);
            // Remap this test host only. Explorer and other applications keep the real settings.
            Assert.AreEqual(0, RegOverridePredefKey(currentUser, isolated.Handle.DangerousGetHandle()));
            try
            {
                var result = await Run(new DuplicateSettingsStore(temp.Path), new WindowsRecycleBinAdapter(),
                    ["recycle", "--path", path, "--expect-sha256", Hash(path), "--apply"]);
                Assert.AreEqual(3, result.ExitCode, result.Json.ToJsonString());
                Assert.AreEqual("failed", result.Status(0));
                StringAssert.Contains(result.File(0)["messages"]![0]!.GetValue<string>(), reason);
                CollectionAssert.AreEqual(content, File.ReadAllBytes(path));
            }
            finally { Assert.AreEqual(0, RegOverridePredefKey(currentUser, IntPtr.Zero)); }
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false); }
    }

    [DllImport("advapi32.dll")]
    private static extern int RegOverridePredefKey(IntPtr original, IntPtr replacement);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder volume, int length);

    private static async Task<FileActionService> DeleteWithDesktop(DuplicateSettingsStore settings, IRecycleBinAdapter bin, string path)
    {
        var metadata = new PhysicalFileMetadataAccess();
        var service = new FileActionService(bin, metadata, new FileManagementStore(settings.DataDirectory));
        var stamp = metadata.GetFileStamp(path);
        var preflight = service.Preflight([new(path, stamp.Length, stamp.LastWriteTimeUtc, "", ExpectedIdentity: stamp.Identity)], []);
        var result = await service.DeleteAsync(preflight);
        CollectionAssert.AreEqual(new[] { path }, result.CompletedPaths.ToArray());
        return service;
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task<RunResult> Run(DuplicateSettingsStore settings, IRecycleBinAdapter? bin, string[] args,
        string? application = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CliCommands.RunAsync([.. args, "--json", "-"], settings, output, error,
            recycleBin: bin, applicationDirectory: application);
        return new(exitCode, JsonNode.Parse(output.ToString()) ?? throw new AssertFailedException(error.ToString()));
    }

    private sealed record RunResult(int ExitCode, JsonNode Json)
    {
        public JsonArray Files => Json["files"]!.AsArray();
        public JsonNode File(int index) => Files[index]!;
        public string Status(int index) => File(index)["status"]!.GetValue<string>();
    }
}
