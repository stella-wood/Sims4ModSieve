using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ConflictScanFlowTests
{
    [TestMethod]
    public async Task RealTemporaryPackagesAndScriptsReachGroupedResultsAndKeepReadFailures()
    {
        using var files = new TemporaryFiles();
        var mods = files.Directory("Mods");
        WritePackage(mods, "different-a.package", 1, "first");
        WritePackage(mods, "different-b.package", 1, "second");
        WritePackage(mods, "same-a.package", 2, "same");
        WritePackage(mods, "same-b.package", 2, "same");
        var badPackage = Path.Combine(mods, "broken.package");
        File.WriteAllText(badPackage, "not a DBPF file");
        WriteScript(mods, "script-a.ts4script", "print('first')", "print('same')");
        WriteScript(mods, "script-b.ts4script", "print('second')", "print('same')");

        var viewModel = CreateViewModel(mods);
        viewModel.OpenConflictCommand.Execute(null);
        viewModel.OpenConflictSectionCommand.Execute("basic");
        await viewModel.StartConflictScanCommand.ExecuteAsync();

        Assert.AreEqual("complete", viewModel.ConflictRunState);
        Assert.IsFalse(viewModel.IsBusy);
        Assert.AreEqual(2, viewModel.ConflictResources.Count, Diagnose(viewModel));
        Assert.AreEqual(1, viewModel.ConflictResources.Count(row => row.IsDifferent));
        Assert.AreEqual(1, viewModel.ConflictResources.Count(row => row.IsIdentical));
        Assert.AreEqual(1, viewModel.ConflictScripts.Count);
        var library = viewModel.ConflictScripts.Single();
        Assert.AreEqual("library", library.Name);
        Assert.AreEqual(2, library.Modules.Count);
        Assert.AreEqual(2, library.FileCount);
        Assert.AreEqual(1, library.Modules.Count(row => row.IsDifferent));
        Assert.AreEqual(1, library.Modules.Count(row => row.IsIdentical));
        Assert.IsTrue(viewModel.ConflictProblems.Any(row => PathsEqual(row.Path, badPackage) && row.IsWholeFile));

        viewModel.SelectConflictFilterCommand.Execute("different");
        Assert.AreEqual(1, viewModel.VisibleConflictResources.Count);
        Assert.AreEqual(1, viewModel.VisibleConflictScripts.Single().Modules.Count);
        viewModel.SelectConflictFilterCommand.Execute("same");
        Assert.AreEqual(1, viewModel.VisibleConflictResources.Count);
        Assert.AreEqual(1, viewModel.VisibleConflictScripts.Single().Modules.Count);
    }

    [TestMethod]
    public async Task SourcesShareSettingsAndOnlyRemovingTheLastSourceResetsResults()
    {
        using var files = new TemporaryFiles();
        var mods = files.Directory("Mods");
        var extra = files.Directory("Extra");
        WritePackage(mods, "a.package", 1, "first");
        WritePackage(mods, "b.package", 1, "second");
        var settings = new FakeSettingsStore(new DuplicateSettings(mods,
            [new SavedScanSource(mods, true)]));
        var viewModel = CreateViewModel(settings);
        viewModel.OpenConflictCommand.Execute(null);
        viewModel.OpenConflictSectionCommand.Execute("basic");
        await viewModel.StartConflictScanCommand.ExecuteAsync();
        Assert.AreEqual(1, viewModel.ConflictResources.Count, Diagnose(viewModel));

        viewModel.GoHomeCommand.Execute(null);
        viewModel.OpenConflictCommand.Execute(null);
        viewModel.OpenConflictSectionCommand.Execute("basic");
        Assert.AreEqual(1, viewModel.ConflictResources.Count);
        viewModel.Sources.Add(new ScanSourceViewModel(extra));
        viewModel.Sources[0].IsEnabled = false;
        Assert.AreEqual(1, viewModel.ConflictResources.Count);
        Assert.IsTrue(viewModel.StartConflictScanCommand.CanExecute(null));
        viewModel.RemoveSourceCommand.Execute(viewModel.Sources[0]);
        Assert.AreEqual(1, viewModel.ConflictResources.Count);
        viewModel.RemoveSourceCommand.Execute(viewModel.Sources[0]);
        Assert.AreEqual("initial", viewModel.ConflictRunState);
        Assert.AreEqual(0, viewModel.ConflictResources.Count);
        Assert.AreEqual(0, viewModel.ConflictProblems.Count);
    }

    [TestMethod]
    public async Task FailedPackageWithoutCandidatesIsNotReportedAsCleanScan()
    {
        using var files = new TemporaryFiles();
        var mods = files.Directory("Mods");
        var badPackage = Path.Combine(mods, "broken.package");
        File.WriteAllText(badPackage, "bad");
        var viewModel = CreateViewModel(mods);

        await viewModel.StartConflictScanCommand.ExecuteAsync();

        Assert.AreEqual("complete", viewModel.ConflictRunState);
        Assert.IsFalse(viewModel.HasConflictMatches);
        Assert.IsTrue(viewModel.HasConflictProblems);
        Assert.AreEqual("已分析范围未发现冲突", viewModel.ConflictStatus);
        Assert.IsTrue(viewModel.ConflictProblems.Any(row => PathsEqual(row.Path, badPackage)));
    }

    [TestMethod]
    public async Task CancellationUnlocksSourcesAndNavigationWithoutPublishingSuccess()
    {
        using var files = new TemporaryFiles();
        var mods = files.Directory("Mods");
        var scanner = new WaitingPackageScanner();
        var viewModel = CreateViewModel(new FakeSettingsStore(new DuplicateSettings(null,
            [new SavedScanSource(mods, true)])), scanner);
        var scan = viewModel.StartConflictScanCommand.ExecuteAsync();
        await scanner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(viewModel.IsBusy);
        Assert.IsFalse(viewModel.CanEditSources);
        Assert.IsFalse(viewModel.GoHomeCommand.CanExecute(null));
        Assert.IsFalse(viewModel.OpenDuplicateScannerCommand.CanExecute(null));
        Assert.IsTrue(viewModel.CancelConflictScanCommand.CanExecute(null));
        viewModel.CancelConflictScanCommand.Execute(null);
        await scan.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual("cancelled", viewModel.ConflictRunState);
        Assert.IsFalse(viewModel.IsConflictComplete);
        Assert.IsFalse(viewModel.IsBusy);
        Assert.IsTrue(viewModel.CanEditSources);
        Assert.IsTrue(viewModel.GoHomeCommand.CanExecute(null));
        Assert.IsTrue(viewModel.StartConflictScanCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task ClearingSourcesDuringScanReleasesBusyStateAndAllowsAnotherScan()
    {
        using var files = new TemporaryFiles();
        var mods = files.Directory("Mods");
        var scanner = new WaitingPackageScanner();
        var viewModel = CreateViewModel(new FakeSettingsStore(new DuplicateSettings(null,
            [new SavedScanSource(mods, true)])), scanner);
        var scan = viewModel.StartConflictScanCommand.ExecuteAsync();
        await scanner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.Sources.Clear();
        await scan.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual("initial", viewModel.ConflictRunState);
        Assert.IsFalse(viewModel.IsBusy);
        Assert.IsFalse(viewModel.StartConflictScanCommand.CanExecute(null));
        viewModel.Sources.Add(new ScanSourceViewModel(mods));
        Assert.IsTrue(viewModel.StartConflictScanCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task ScannerFailureAllowsRetryWithoutPublishingCompleteResults()
    {
        using var files = new TemporaryFiles();
        var mods = files.Directory("Mods");
        var viewModel = CreateViewModel(new FakeSettingsStore(new DuplicateSettings(null,
            [new SavedScanSource(mods, true)])), new FailingPackageScanner());

        await viewModel.StartConflictScanCommand.ExecuteAsync();

        Assert.AreEqual("failed", viewModel.ConflictRunState);
        Assert.IsFalse(viewModel.IsConflictComplete);
        Assert.IsFalse(viewModel.IsBusy);
        Assert.AreEqual(0, viewModel.ConflictResources.Count);
        Assert.IsTrue(viewModel.StartConflictScanCommand.CanExecute(null));
    }

    private static MainWindowViewModel CreateViewModel(string source) =>
        CreateViewModel(new FakeSettingsStore(new DuplicateSettings(source,
            [new SavedScanSource(source, true)])));

    private static MainWindowViewModel CreateViewModel(FakeSettingsStore settings,
        IPackageConflictScanner? scanner = null) => new(
            new DuplicateRunService(DuplicateScanner.CreateDefault()),
            new FakeFolderPicker(), settings, packageConflictScanner: scanner);

    private static bool PathsEqual(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Equals(left, right);

    private static string Diagnose(MainWindowViewModel viewModel) =>
        $"state={viewModel.ConflictRunState}; status={viewModel.ConflictStatus}; " +
        $"problems={string.Join(" | ", viewModel.ConflictProblems.Select(problem =>
            $"{problem.Path}: {string.Join(", ", problem.Reasons.Select(reason => $"{reason.Code} {reason.Detail}"))}"))}; " +
        $"range={string.Join(" | ", viewModel.ConflictRangeProblems.Select(problem =>
            $"{problem.Path}: {problem.Code} {problem.Detail}"))}";

    private static void WritePackage(string directory, string name, uint instance, string payload)
    {
        var content = Encoding.UTF8.GetBytes(payload);
        const int headerLength = 96;
        var indexOffset = headerLength + content.Length;
        var bytes = new byte[indexOffset + 36];
        "DBPF"u8.CopyTo(bytes);
        Put(bytes, 0x04, 2);
        Put(bytes, 0x08, 1);
        Put(bytes, 0x24, 1);
        Put(bytes, 0x2C, 36);
        Put(bytes, 0x3C, 3);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x40), (ulong)indexOffset);
        content.CopyTo(bytes.AsSpan(headerLength));
        Put(bytes, indexOffset + 0, 0);                 // index type
        Put(bytes, indexOffset + 4, 0x0904DF10);        // type
        Put(bytes, indexOffset + 8, 0);                 // group
        Put(bytes, indexOffset + 12, 0);                // instance high
        Put(bytes, indexOffset + 16, instance);         // instance low
        Put(bytes, indexOffset + 20, headerLength);     // data offset
        Put(bytes, indexOffset + 24, (uint)content.Length | 0x8000_0000);
        Put(bytes, indexOffset + 28, (uint)content.Length);
        Put(bytes, indexOffset + 32, 0x0001_0000);      // no compression
        File.WriteAllBytes(Path.Combine(directory, name), bytes);
    }

    private static void Put(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private static void WriteScript(string directory, string name, string first, string second)
    {
        using var file = File.Create(Path.Combine(directory, name));
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("library/alpha.py").Open())) writer.Write(first);
        using (var writer = new StreamWriter(zip.CreateEntry("library/beta.py").Open())) writer.Write(second);
    }

    private sealed class WaitingPackageScanner : IPackageConflictScanner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PackageConflictScanReport> ScanAsync(PackageConflictScanRequest request,
            IProgress<PackageConflictScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class FailingPackageScanner : IPackageConflictScanner
    {
        public Task<PackageConflictScanReport> ScanAsync(PackageConflictScanRequest request,
            IProgress<PackageConflictScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromException<PackageConflictScanReport>(new IOException("fixture read failure"));
    }

    private sealed class FakeSettingsStore(DuplicateSettings? value) : IDuplicateSettingsStore
    {
        public DuplicateSettings? Load() => value;
        public void Save(DuplicateSettings settings) => value = settings;
    }

    private sealed class FakeFolderPicker : IFolderPickerService
    {
        public string? PickFolder(string title, string? initialDirectory = null) => null;
    }

    private sealed class TemporaryFiles : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"s4ms-conflict-flow-{Guid.NewGuid():N}");

        public string Directory(string name)
        {
            var path = Path.Combine(root, name);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
        }
    }
}
