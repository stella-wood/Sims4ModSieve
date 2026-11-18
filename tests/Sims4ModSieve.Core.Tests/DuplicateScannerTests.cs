using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Diagnostics;
using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.Core.Tests;

[TestClass]
public sealed class DuplicateScannerTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task MixedProgramDirectoryScansModsAndSkipsOnlyPayload(bool sharedRoot, bool explicitSource)
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var app = sharedRoot ? mods : temp.CreateDirectory("Mods", "Sieve");
        var nested = Directory.CreateDirectory(Path.Combine(app, "nested")).FullName;
        var first = Path.Combine(app, "first.package");
        var second = Path.Combine(nested, "second.package");
        File.WriteAllText(first, "same");
        File.WriteAllText(second, "same");
        var sibling = temp.WriteFile("Mods/Sieve-backup/third.ts4script", "same");
        foreach (var name in new[] { "Sims4ModSieve.exe", "coreclr.dll", "Sims4ModSieve.runtimeconfig.json", "BUILD-INFO.txt" })
            File.WriteAllText(Path.Combine(app, name), "same");
        foreach (var folder in new[] { "Assets", "licenses", "zh-Hans" })
        {
            Directory.CreateDirectory(Path.Combine(app, folder));
            File.WriteAllText(Path.Combine(app, folder, "payload.package"), "same");
        }
        var sources = new List<ScanSource> { new("mods", mods) };
        if (explicitSource) { sources.Add(new("app", app)); sources.Add(new("nested", nested)); }
        var fileSystem = new PhysicalFileSystemAccess();
        var scanner = new DuplicateScanner(fileSystem, new StableFileHasher(fileSystem), new(), app);
        var report = await scanner.ScanAsync(new(sources));
        Assert.AreEqual(3, report.DiscoveredFileCount);
        CollectionAssert.AreEquivalent(new[] { first, second, sibling }, report.Groups.Single().Files.Select(file => file.Path).ToArray());
        Assert.AreEqual(0, report.Issues.Count);
    }

    [TestMethod]
    public async Task FindsExactDuplicatesAcrossSourcesAndPrefersModsCopy()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("模拟人生", "Mods");
        var downloads = temp.CreateDirectory("下载");
        var modsCopy = temp.WriteFile(Path.Combine("模拟人生", "Mods", "chair.package"), "same");
        var externalCopy = temp.WriteFile(Path.Combine("下载", "chair-copy.package"), "same");
        temp.WriteFile(Path.Combine("下载", "same-size.package"), "diff");
        temp.WriteFile(Path.Combine("下载", "unique.package"), "a unique file size");
        temp.WriteFile(Path.Combine("下载", "ignored.txt"), "same");

        var scanner = DuplicateScanner.CreateDefault();
        var report = await scanner.ScanAsync(new DuplicateScanRequest(
            [new ScanSource("mods", mods), new ScanSource("downloads", downloads)],
            ModsRoot: mods,
            IncludedExtensions: [".package"]));

        Assert.AreEqual(4, report.DiscoveredFileCount);
        Assert.AreEqual(3, report.HashedFileCount);
        Assert.AreEqual(1, report.Groups.Count);
        Assert.AreEqual(DuplicateSection.Ordinary, report.Groups[0].Section);
        Assert.AreEqual(modsCopy, report.Groups[0].SuggestedKeepPath);
        CollectionAssert.AreEqual(new[] { externalCopy }, report.Groups[0].SuggestedDeletePaths.ToArray());
        Assert.AreEqual(0, report.Issues.Count);
    }

    [TestMethod]
    public async Task DisabledSourceIsNotScanned()
    {
        using var temp = new TempDirectory();
        var enabled = temp.CreateDirectory("enabled");
        var disabled = temp.CreateDirectory("disabled");
        temp.WriteFile(Path.Combine("enabled", "one.package"), "same");
        temp.WriteFile(Path.Combine("disabled", "two.package"), "same");

        var report = await DuplicateScanner.CreateDefault().ScanAsync(new DuplicateScanRequest(
            [new ScanSource("enabled", enabled), new ScanSource("disabled", disabled, Enabled: false)]));

        Assert.AreEqual(1, report.DiscoveredFileCount);
        Assert.AreEqual(0, report.Groups.Count);
        Assert.AreEqual(1, report.Sources.Count);
        Assert.AreEqual("enabled", report.Sources[0].Id);
    }

    [TestMethod]
    public async Task OverlappingParentAndChildReadEachFileOnceAndCreateFocusSection()
    {
        using var temp = new TempDirectory();
        var parent = temp.CreateDirectory("Mods");
        var focus = temp.CreateDirectory("Mods", "家具");
        var outsideFile = temp.WriteFile(Path.Combine("Mods", "outside.package"), "duplicate");
        var focusFile = temp.WriteFile(Path.Combine("Mods", "家具", "inside.package"), "duplicate");
        var fileSystem = new CountingFileSystemAccess(new PhysicalFileSystemAccess());
        var scanner = new DuplicateScanner(
            fileSystem,
            new StableFileHasher(fileSystem, TimeSpan.Zero),
            new DuplicateSelectionService());

        var report = await scanner.ScanAsync(new DuplicateScanRequest(
            [new ScanSource("parent", parent), new ScanSource("focus", focus)]));

        Assert.AreEqual(2, report.DiscoveredFileCount);
        Assert.AreEqual(1, report.Groups.Count);
        Assert.AreEqual(DuplicateSection.FocusCrossScope, report.Groups[0].Section);
        Assert.AreEqual(1, fileSystem.OpenCounts[outsideFile]);
        Assert.AreEqual(1, fileSystem.OpenCounts[focusFile]);

        var focused = report.Groups[0].Files.Single(file => file.Path == focusFile);
        CollectionAssert.AreEqual(new[] { "parent", "focus" }, focused.SourceIds.ToArray());
        CollectionAssert.AreEqual(new[] { "focus" }, focused.PrimarySourceIds.ToArray());
    }

    [TestMethod]
    public async Task DirectoryPermissionFailureDoesNotDiscardOtherSources()
    {
        using var temp = new TempDirectory();
        var blocked = temp.CreateDirectory("blocked");
        var available = temp.CreateDirectory("available");
        temp.WriteFile(Path.Combine("available", "one.package"), "one");
        var physical = new PhysicalFileSystemAccess();
        var fileSystem = new FaultingFileSystemAccess(physical, blocked);
        var scanner = new DuplicateScanner(
            fileSystem,
            new StableFileHasher(fileSystem, TimeSpan.Zero),
            new DuplicateSelectionService());

        var report = await scanner.ScanAsync(new DuplicateScanRequest(
            [new ScanSource("blocked", blocked), new ScanSource("available", available)]));

        Assert.AreEqual(1, report.DiscoveredFileCount);
        Assert.AreEqual(1, report.Issues.Count);
        Assert.AreEqual("directory-enumeration-failed", report.Issues[0].Code);
        Assert.AreEqual(blocked, report.Issues[0].Path);
    }

    [TestMethod]
    public async Task ReparsePointDirectoryIsReportedAndNotFollowed()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("root");
        var reparse = temp.CreateDirectory("root", "linked");
        temp.WriteFile(Path.Combine("root", "linked", "hidden.package"), "same");
        var physical = new PhysicalFileSystemAccess();
        var fileSystem = new ReparsePointFileSystemAccess(physical, reparse);
        var scanner = new DuplicateScanner(
            fileSystem,
            new StableFileHasher(fileSystem, TimeSpan.Zero),
            new DuplicateSelectionService());

        var report = await scanner.ScanAsync(
            new DuplicateScanRequest([new ScanSource("root", root)]));

        Assert.AreEqual(0, report.DiscoveredFileCount);
        Assert.AreEqual(1, report.Issues.Count);
        Assert.AreEqual("reparse-point-skipped", report.Issues[0].Code);
    }

    [TestMethod]
    public async Task ReparsePointIssuesExplainInChineseAndShowLinkTarget()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("root");
        var reparse = temp.CreateDirectory("root", "linked");
        var target = Path.Combine(temp.Path, "real-mods");
        var physical = new PhysicalFileSystemAccess();
        var scanner = new DuplicateScanner(
            new ReparsePointFileSystemAccess(physical, reparse, target),
            new StableFileHasher(physical, TimeSpan.Zero),
            new DuplicateSelectionService());

        var nested = await scanner.ScanAsync(new DuplicateScanRequest([new ScanSource("root", root)]));
        var source = await scanner.ScanAsync(new DuplicateScanRequest([new ScanSource("linked", reparse)]));

        Assert.AreEqual("reparse-point-skipped", nested.Issues.Single().Code);
        StringAssert.Contains(nested.Issues.Single().Message, "没有跟进去扫描");
        StringAssert.Contains(nested.Issues.Single().Message, "真实位置：" + target);
        Assert.AreEqual("source-is-reparse-point", source.Issues.Single().Code);
        StringAssert.Contains(source.Issues.Single().Message, "真实位置：" + target);
    }

    [TestMethod]
    public async Task ProducesDeterministicGroupAndFileOrdering()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("root");
        temp.WriteFile(Path.Combine("root", "z.package"), "aaaa");
        temp.WriteFile(Path.Combine("root", "a.package"), "aaaa");
        temp.WriteFile(Path.Combine("root", "d.package"), "bbbbb");
        temp.WriteFile(Path.Combine("root", "c.package"), "bbbbb");
        var scanner = DuplicateScanner.CreateDefault();
        var request = new DuplicateScanRequest([new ScanSource("root", root)]);

        var first = await scanner.ScanAsync(request);
        var second = await scanner.ScanAsync(request);

        CollectionAssert.AreEqual(
            first.Groups.Select(group => $"{group.Sha256}:{string.Join('|', group.Files.Select(file => file.Path))}").ToArray(),
            second.Groups.Select(group => $"{group.Sha256}:{string.Join('|', group.Files.Select(file => file.Path))}").ToArray());
        CollectionAssert.AreEqual(
            first.Groups[0].Files.Select(file => file.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            first.Groups[0].Files.Select(file => file.Path).ToArray());
    }

    [TestMethod]
    public async Task SupportsLongUnicodePaths()
    {
        using var temp = new TempDirectory();
        var parts = Enumerable.Range(0, 10).Select(index => $"很长的目录_{index:D2}_abcdefghijkl").ToArray();
        var directory = temp.CreateDirectory(parts);
        var relativeDirectory = Path.GetRelativePath(temp.Path, directory);
        temp.WriteFile(Path.Combine(relativeDirectory, "副本一.package"), "长路径重复内容");
        temp.WriteFile(Path.Combine(relativeDirectory, "副本二.package"), "长路径重复内容");
        Assert.IsGreaterThan(260, directory.Length);

        var report = await DuplicateScanner.CreateDefault().ScanAsync(
            new DuplicateScanRequest([new ScanSource("long", directory)]));

        Assert.AreEqual(1, report.Groups.Count);
        Assert.AreEqual(0, report.Issues.Count);
    }

    [TestMethod]
    public async Task ReportsDiscoveryHashingAndCompletionProgress()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("root");
        temp.WriteFile(Path.Combine("root", "one.package"), "same");
        temp.WriteFile(Path.Combine("root", "two.package"), "same");
        var updates = new List<DuplicateScanProgress>();

        var report = await DuplicateScanner.CreateDefault().ScanAsync(
            new DuplicateScanRequest([new ScanSource("root", root)]),
            new InlineProgress<DuplicateScanProgress>(updates.Add));

        Assert.AreEqual(1, report.Groups.Count);
        CollectionAssert.Contains(updates.Select(update => update.Phase).ToList(), DuplicateScanPhase.Starting);
        CollectionAssert.Contains(updates.Select(update => update.Phase).ToList(), DuplicateScanPhase.Discovering);
        CollectionAssert.Contains(updates.Select(update => update.Phase).ToList(), DuplicateScanPhase.Hashing);
        Assert.AreEqual(DuplicateScanPhase.Completed, updates[^1].Phase);
        Assert.AreEqual(2, updates[^1].DiscoveredFileCount);
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(4, false)]
    [DataRow(8, false)]
    [DataRow(8, true)]
    public async Task ReverseHashCompletionPreservesTheWholeReportAndFailureProgress(int degree, bool changed)
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var backup = temp.CreateDirectory("Backup");
        var paths = Enumerable.Range(0, 8).Select(index => temp.WriteFile(
                Path.Combine(index < 4 ? "Backup" : "Mods", $"{index}.package"), index % 2 == 0 ? "cats" : "dogs"))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var fileSystem = new PhysicalFileSystemAccess();
        var realHasher = new StableFileHasher(fileSystem);
        Task<StableFileHash> ReadAsync(string path, CancellationToken token) => path == paths[^1]
            ? Task.FromException<StableFileHash>(changed
                ? new FileChangedDuringHashException(path) : new IOException("sample read failed"))
            : realHasher.HashAsync(path, token);
        var request = new DuplicateScanRequest([new("mods", mods), new("backup", backup)], mods)
        {
            StorageProfile = new DelegateStorageProfile(_ => throw new AssertFailedException("Manual mode should not probe storage.")),
        };
        var baseline = await new DuplicateScanner(fileSystem, new DelegateHasher(ReadAsync), new()).ScanAsync(
            request with { HashConcurrency = DuplicateHashConcurrency.One });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = CreateGates(paths.Length);
        var release = CreateGates(paths.Length);
        var updates = new List<DuplicateScanProgress>();
        var reported = CreateGates(paths.Length);
        var hasher = new DelegateHasher(async (path, token) =>
        {
            var index = Array.IndexOf(paths, path);
            started[index].SetResult();
            await release[index].Task.WaitAsync(token);
            return await ReadAsync(path, token);
        });
        var scanner = new DuplicateScanner(fileSystem, hasher, new());
        var scan = scanner.ScanAsync(request with { HashConcurrency = (DuplicateHashConcurrency)degree },
            new InlineProgress<DuplicateScanProgress>(update =>
            {
                updates.Add(update);
                if (update.Phase == DuplicateScanPhase.Hashing && update.CompletedItems > 0)
                    reported[update.CompletedItems - 1].SetResult();
            }), timeout.Token);

        for (var batch = 0; batch < paths.Length; batch += degree)
        {
            await Task.WhenAll(started.Skip(batch).Take(degree).Select(gate => gate.Task)).WaitAsync(timeout.Token);
            Assert.AreEqual(batch + degree, started.Count(gate => gate.Task.IsCompleted));
            for (var offset = 0; offset < degree; offset++)
            {
                release[batch + degree - offset - 1].SetResult();
                await reported[batch + offset].Task.WaitAsync(timeout.Token);
            }
        }

        var result = await scan;
        static string BusinessFacts(DuplicateReport report) =>
            JsonSerializer.Serialize(report with { StartedAtUtc = default, CompletedAtUtc = default });
        Assert.AreEqual(BusinessFacts(baseline), BusinessFacts(result));
        Assert.AreEqual(paths.Length - 1, result.HashedFileCount);
        Assert.AreEqual(changed ? "file-changed-during-hash" : "hash-failed", result.Issues.Single().Code);
        Assert.AreEqual(paths[^1], result.Issues.Single().Path);
        CollectionAssert.AreEqual(Enumerable.Range(0, paths.Length + 1).ToArray(),
            updates.Where(update => update.Phase == DuplicateScanPhase.Hashing).Select(update => update.CompletedItems).ToArray());
        Assert.AreEqual(DuplicateScanPhase.Completed, updates[^1].Phase);
    }

    [TestMethod]
    public async Task CancellingWithQueuedFilesWaitsForActiveWorkersAndPublishesNoReport()
    {
        using var temp = new TempDirectory();
        for (var index = 0; index < 4; index++) temp.WriteFile($"{index}.package", "same");
        using var cancellation = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = CreateGates(2);
        var cancelled = CreateGates(2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = 0;
        var updates = new List<DuplicateScanProgress>();
        var scanner = new DuplicateScanner(new PhysicalFileSystemAccess(), new DelegateHasher(async (_, token) =>
        {
            var index = Interlocked.Increment(ref invoked) - 1;
            started[index].SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            }
            finally
            {
                cancelled[index].SetResult();
                await release.Task.WaitAsync(timeout.Token);
            }
        }), new());
        var scan = scanner.ScanAsync(new DuplicateScanRequest([new("mods", temp.Path)]) { HashConcurrency = DuplicateHashConcurrency.Two },
            new InlineProgress<DuplicateScanProgress>(updates.Add), cancellation.Token);
        try
        {
            await Task.WhenAll(started.Select(gate => gate.Task)).WaitAsync(timeout.Token);
            cancellation.Cancel();
            await Task.WhenAll(cancelled.Select(gate => gate.Task)).WaitAsync(timeout.Token);
            Assert.IsFalse(scan.IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
            release.SetResult();
        }
        await Assert.ThrowsAsync<OperationCanceledException>(() => scan);
        Assert.AreEqual(2, invoked);
        Assert.IsFalse(updates.Any(update => update.Phase == DuplicateScanPhase.Completed));
    }

    [TestMethod]
    [DataRow(false, false, 4)]
    [DataRow(false, true, 1)]
    [DataRow(true, false, 2)]
    [DataRow(true, true, 1)]
    public async Task AutoUsesMostConservativeVolumeAmongActualHashSources(bool unknown, bool hdd, int expected)
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var backup = temp.CreateDirectory("Backup");
        var extra = temp.CreateDirectory("Extra");
        var unique = temp.CreateDirectory("Unique");
        var disabled = temp.CreateDirectory("Disabled");
        temp.WriteFile(Path.Combine("Mods", "nested", "a.package"), "same");
        temp.WriteFile(Path.Combine("Backup", "b.package"), "same");
        temp.WriteFile(Path.Combine("Extra", "c.package"), "same");
        temp.WriteFile(Path.Combine("Unique", "unique.package"), "different");
        temp.WriteFile(Path.Combine("Disabled", "off.package"), "same");
        var probed = new List<string>();
        var storage = new DelegateStorageProfile(path =>
        {
            probed.Add(path);
            if (path == backup && unknown) return new(null, null, "test unknown");
            return new(path, path == extra && hdd);
        });
        var sources = new ScanSource[] { new("mods", mods), new("backup", backup), new("extra", extra),
            new("unique", unique), new("off", disabled, Enabled: false) };
        using var timing = ScanPerformance.Start("duplicate-test");
        var report = await DuplicateScanner.CreateDefault().ScanAsync(new DuplicateScanRequest(sources) { StorageProfile = storage });
        var hashStage = timing.Stages.Single(stage => stage.Name.EndsWith("SHA-256", StringComparison.Ordinal));
        Assert.AreEqual(expected, hashStage.Parallelism);
        Assert.AreEqual(3, report.HashedFileCount);
        CollectionAssert.AreEquivalent(new[] { mods, backup, extra }, probed);
    }

    [TestMethod]
    public async Task EmptySourcesSkipStorageAndHaveZeroWork()
    {
        var storage = new DelegateStorageProfile(_ => throw new AssertFailedException("Empty scan should not probe storage."));
        var updates = new List<DuplicateScanProgress>();
        var report = await DuplicateScanner.CreateDefault().ScanAsync(new DuplicateScanRequest([]) { StorageProfile = storage },
            new InlineProgress<DuplicateScanProgress>(updates.Add));
        Assert.AreEqual(0, report.HashedFileCount);
        Assert.AreEqual(0, report.Issues.Count);
        Assert.AreEqual(0, updates[^1].TotalItems);
        Assert.IsTrue(updates.All(update => update.CompletedItems == 0));
    }

    private static TaskCompletionSource[] CreateGates(int count) =>
        Enumerable.Range(0, count).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();

    private sealed class DelegateHasher(Func<string, CancellationToken, Task<StableFileHash>> hash) : IStableFileHasher
    {
        public Task<StableFileHash> HashAsync(string path, CancellationToken cancellationToken = default) => hash(path, cancellationToken);
    }

    private sealed class DelegateStorageProfile(Func<string, StorageVolumeProfile> probe) : IStorageProfile
    {
        public StorageVolumeProfile GetVolume(string directoryPath) => probe(directoryPath);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
