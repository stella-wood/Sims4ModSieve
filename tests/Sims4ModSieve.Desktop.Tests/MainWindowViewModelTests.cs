using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Desktop.Infrastructure;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class MainWindowViewModelTests
{
    [TestMethod]
    [DataRow("basic")]
    [DataRow("logs")]
    [DataRow("health")]
    [DataRow("bisect")]
    public void ConflictNavigationRestoresLastSectionUntilExplicitlyReturningToOverview(string section)
    {
        var model = CreateViewModel(new FakeSettingsStore(new DuplicateSettings(Path.GetTempPath(), [])));
        model.OpenConflictCommand.Execute(null);
        Assert.IsTrue(model.IsConflictOverview);
        model.OpenConflictSectionCommand.Execute(section);

        model.GoHomeCommand.Execute(null);
        model.OpenConflictCommand.Execute(null);
        Assert.AreEqual(section, model.ConflictSection);
        Assert.IsFalse(model.IsConflictOverview);
        Assert.IsTrue(section == "basic" ? model.IsConflictBasic : model.IsConflictUnavailable);
        model.OpenDuplicateScannerCommand.Execute(null);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictCommand.Execute(null);
        Assert.AreEqual(section, model.ConflictSection);
        Assert.IsTrue(section == "basic" ? model.IsConflictBasic : model.IsConflictUnavailable);

        model.ConflictBackCommand.Execute(null);
        Assert.IsTrue(model.IsConflictOverview);
        model.GoHomeCommand.Execute(null);
        model.OpenConflictCommand.Execute(null);
        Assert.IsTrue(model.IsConflictOverview);
    }

    [TestMethod]
    public void SourcesAndChecksStayIndependentAcrossPageNavigation()
    {
        using var temporary = new TemporaryDirectory();
        var mods = temporary.CreateDirectory("Mods");
        var duplicates = temporary.CreateDirectory("Duplicates");
        var conflicts = temporary.CreateDirectory("Conflicts");
        var settings = new FakeSettingsStore(new DuplicateSettings(mods, []));
        var model = CreateViewModel(settings, new FakeFolderPicker(duplicates, conflicts));
        Assert.AreNotSame(model.Sources[0], model.ConflictSources[0]);

        model.OpenDuplicateScannerCommand.Execute(null);
        model.AddSourceCommand.Execute(null);
        model.Sources[0].IsEnabled = false;
        Assert.AreEqual(1, model.ConflictSources.Count);
        Assert.IsTrue(model.ConflictSources[0].IsEnabled);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        model.AddSourceCommand.Execute(null);
        model.ConflictSources[0].IsEnabled = false;
        Assert.IsTrue(model.StartConflictScanCommand.CanExecute(null));
        model.RemoveSourceCommand.Execute(model.ConflictSources[1]);
        Assert.IsFalse(model.StartConflictScanCommand.CanExecute(null));
        Assert.IsTrue(model.StartScanCommand.CanExecute(null));
        Assert.AreEqual(2, model.Sources.Count);

        model.GoHomeCommand.Execute(null);
        model.OpenDuplicateScannerCommand.Execute(null);
        Assert.IsFalse(model.Sources[0].IsEnabled);
        Assert.AreEqual(duplicates, model.Sources[1].Path);
        model.RemoveSourceCommand.Execute(model.Sources[0]);
        Assert.AreEqual(mods, model.ConflictSources.Single().Path);
        model.OpenConflictCommand.Execute(null);
        Assert.IsFalse(model.ConflictSources.Single().IsEnabled);
        Assert.AreEqual(0, settings.SaveCalls);
    }

    [TestMethod]
    public void RestartRestoresDefaultsInsteadOfLegacyOrCurrentSessionSources()
    {
        using var temporary = new TemporaryDirectory();
        var mods = temporary.CreateDirectory("Mods");
        var extra = temporary.CreateDirectory("Extra");
        var newMods = temporary.CreateDirectory("NewMods");
        var settings = new DuplicateSettingsStore(temporary.Path);
        settings.Save(new DuplicateSettings(mods, [new(mods, false), new(extra, true)]));
        var model = new MainWindowViewModel(new DuplicateRunService(DuplicateScanner.CreateDefault()),
            new FakeFolderPicker(extra, newMods), settings);
        Assert.AreEqual(mods, model.Sources.Single().Path);
        Assert.AreEqual(mods, model.ConflictSources.Single().Path);
        Assert.IsTrue(model.Sources[0].IsEnabled && model.ConflictSources[0].IsEnabled);

        model.OpenDuplicateScannerCommand.Execute(null);
        model.AddSourceCommand.Execute(null);
        model.Sources[0].IsEnabled = false;
        model.OpenConflictCommand.Execute(null);
        model.ChangeModsRootCommand.Execute(null);
        model.ConflictSources.Clear();
        Assert.AreEqual(newMods, model.ModsRoot);
        Assert.AreEqual(mods, model.Sources[0].Path);
        Assert.AreEqual(0, settings.Load()!.Sources.Count);

        var reopened = new MainWindowViewModel(new DuplicateRunService(DuplicateScanner.CreateDefault()),
            new FakeFolderPicker(), settings);
        Assert.AreEqual(newMods, reopened.Sources.Single().Path);
        Assert.AreEqual(newMods, reopened.ConflictSources.Single().Path);
        Assert.IsTrue(reopened.Sources[0].IsEnabled && reopened.ConflictSources[0].IsEnabled);
        Assert.IsFalse(reopened.HasResults);
        Assert.AreEqual("initial", reopened.ConflictRunState);
    }

    [TestMethod]
    public async Task ClearingDuplicateSourcesRejectsLateScanResultsAndProgress()
    {
        using var temporary = new TemporaryDirectory();
        var mods = temporary.CreateDirectory("Mods");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new FakeDuplicateRunService(async (input, progress, _) =>
        {
            started.SetResult();
            await release.Task;
            progress!.Report(new DuplicateScanProgress(DuplicateScanPhase.Hashing, "迟到的进度", 1, 1));
            return await new DuplicateRunService(DuplicateScanner.CreateDefault()).RunAsync(input);
        });
        var model = CreateViewModel(new FakeSettingsStore(new DuplicateSettings(mods, [])), runService: run);
        var scan = model.StartScanCommand.ExecuteAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.Sources.Clear();
        release.SetResult();
        await scan.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.IsFalse(model.IsBusy);
        Assert.IsFalse(model.IsStatusVisible);
        Assert.AreEqual(string.Empty, model.StatusText);
        Assert.AreEqual(0, model.DiscoveredFileCount);
        Assert.AreEqual(1, model.ConflictSources.Count);
    }

    [TestMethod]
    public async Task ClearingSourcesCancelsConflictScanAndUnlocksNextSession()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("Mods");
        var scanner = new WaitingPackageScanner();
        var viewModel = new MainWindowViewModel(
            new DuplicateRunService(DuplicateScanner.CreateDefault()),
            new FakeFolderPicker(),
            new FakeSettingsStore(new DuplicateSettings(source, [])),
            packageConflictScanner: scanner);
        viewModel.OpenConflictCommand.Execute(null);
        viewModel.OpenConflictSectionCommand.Execute("basic");
        var scan = viewModel.StartConflictScanCommand.ExecuteAsync();
        await scanner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(viewModel.IsBusy);
        viewModel.ConflictSources.Clear();

        Assert.AreEqual("initial", viewModel.ConflictRunState);
        Assert.IsFalse(viewModel.StartConflictScanCommand.CanExecute(null));
        viewModel.ConflictSources.Add(new ScanSourceViewModel(source));
        await scan;
        Assert.IsFalse(viewModel.IsBusy);
        Assert.IsTrue(viewModel.StartConflictScanCommand.CanExecute(null));
        Assert.AreEqual("initial", viewModel.ConflictRunState);
    }

    [TestMethod]
    public async Task LateConflictProgressCannotOverwriteCancelledSession()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("Mods");
        var scanner = new WaitingPackageScanner();
        var viewModel = new MainWindowViewModel(
            new DuplicateRunService(DuplicateScanner.CreateDefault()),
            new FakeFolderPicker(),
            new FakeSettingsStore(new DuplicateSettings(source, [])),
            packageConflictScanner: scanner);
        var scan = viewModel.StartConflictScanCommand.ExecuteAsync();
        await scanner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.CancelConflictScanCommand.Execute(null);
        await scan;

        scanner.Progress!.Report(new PackageConflictScanProgress(PackageConflictScanPhase.ReadingIndexes,
            "迟到的进度", 1, 1));
        await Task.Delay(50);

        Assert.AreEqual("cancelled", viewModel.ConflictRunState);
        Assert.AreEqual("扫描已取消", viewModel.ConflictStatus);
    }

    private sealed class WaitingPackageScanner : IPackageConflictScanner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<PackageConflictScanProgress>? Progress { get; private set; }

        public async Task<PackageConflictScanReport> ScanAsync(PackageConflictScanRequest request,
            IProgress<PackageConflictScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Progress = progress;
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    [TestMethod]
    public void SavedUnavailablePathsAreRetainedWithoutChangingTheirEnabledState()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"Sims4ModSieve-Missing-{Guid.NewGuid():N}");
        var settings = new FakeSettingsStore(new DuplicateSettings(
            missing,
            [new SavedScanSource(missing, true)]));

        var viewModel = CreateViewModel(settings);

        Assert.AreEqual(missing, viewModel.ModsRoot);
        Assert.AreEqual(1, viewModel.Sources.Count);
        Assert.IsTrue(viewModel.Sources[0].IsEnabled);
        Assert.IsFalse(viewModel.Sources[0].IsAvailable);
        Assert.IsTrue(viewModel.Sources[0].HasAvailabilityWarning);
        Assert.AreEqual("不可访问", viewModel.Sources[0].AvailabilityText);
        Assert.IsTrue(viewModel.HasModsRootAvailabilityWarning);
        Assert.AreEqual(0, settings.SaveCalls);
    }

    [TestMethod]
    public void AddingAFolderNamedModsDoesNotSetModsRoot()
    {
        using var temporary = new TemporaryDirectory();
        var existing = temporary.CreateDirectory("existing");
        var namedMods = temporary.CreateDirectory("Mods");
        var settings = new FakeSettingsStore(new DuplicateSettings(
            existing,
            []));
        var picker = new FakeFolderPicker(namedMods);
        var viewModel = CreateViewModel(settings, picker);

        viewModel.AddSourceCommand.Execute(null);

        Assert.AreEqual(existing, viewModel.ModsRoot);
        Assert.IsTrue(viewModel.Sources.Any(source => PathsEqual(source.Path, namedMods)));
    }

    [TestMethod]
    public void ChoosingModsRootAddsItAsAnEnabledSourceAndSavesImmediately()
    {
        using var temporary = new TemporaryDirectory();
        var existing = temporary.CreateDirectory("existing");
        var mods = temporary.CreateDirectory("My Mods");
        var settings = new FakeSettingsStore(new DuplicateSettings(
            existing,
            []));
        var viewModel = CreateViewModel(settings, new FakeFolderPicker(mods));

        viewModel.ChangeModsRootCommand.Execute(null);

        Assert.AreEqual(mods, viewModel.ModsRoot);
        var source = viewModel.Sources.Single(item => PathsEqual(item.Path, mods));
        Assert.IsTrue(source.IsEnabled);
        Assert.IsTrue(source.IsAvailable);
        Assert.AreEqual("更改", viewModel.ModsRootActionText);
        Assert.IsNotNull(settings.LastSaved);
        Assert.AreEqual(mods, settings.LastSaved.ModsRoot);
        Assert.AreEqual(0, settings.LastSaved.Sources.Count);
    }

    [TestMethod]
    public void ChangingModsRootRetainsTheOldRootAsAnOrdinarySource()
    {
        using var temporary = new TemporaryDirectory();
        var existing = temporary.CreateDirectory("existing");
        var first = temporary.CreateDirectory("first");
        var second = temporary.CreateDirectory("second");
        var settings = new FakeSettingsStore(new DuplicateSettings(
            existing,
            []));
        var viewModel = CreateViewModel(settings, new FakeFolderPicker(first, second));

        viewModel.ChangeModsRootCommand.Execute(null);
        viewModel.ChangeModsRootCommand.Execute(null);

        Assert.AreEqual(second, viewModel.ModsRoot);
        Assert.IsTrue(viewModel.Sources.Any(item => PathsEqual(item.Path, first)));
        Assert.IsTrue(viewModel.Sources.Any(item => PathsEqual(item.Path, second)));
    }

    [TestMethod]
    public async Task ReconnectedSourceRefreshesBeforeScanAndProducesResults()
    {
        using var temporary = new TemporaryDirectory();
        var recovered = Path.Combine(temporary.Path, "recovered");
        var settings = new FakeSettingsStore(new DuplicateSettings(
            recovered,
            []));
        var viewModel = CreateViewModel(settings);
        Assert.IsFalse(viewModel.Sources.Single().IsAvailable);

        Directory.CreateDirectory(recovered);
        await File.WriteAllTextAsync(Path.Combine(recovered, "one.package"), "same");
        await File.WriteAllTextAsync(Path.Combine(recovered, "two.package"), "same");

        await viewModel.StartScanCommand.ExecuteAsync();

        Assert.IsTrue(viewModel.Sources.Single().IsAvailable);
        Assert.AreEqual(1, viewModel.DuplicateGroupCount);
        Assert.AreEqual(0, viewModel.IssueCount);
    }

    [TestMethod]
    public async Task UnavailableEnabledSourceIsReportedWithoutBlockingAvailableSources()
    {
        using var temporary = new TemporaryDirectory();
        var available = temporary.CreateDirectory("available");
        var missing = Path.Combine(temporary.Path, "missing");
        await File.WriteAllTextAsync(Path.Combine(available, "one.package"), "same");
        await File.WriteAllTextAsync(Path.Combine(available, "two.package"), "same");
        var settings = new FakeSettingsStore(new DuplicateSettings(
            available,
            [
                new SavedScanSource(available, true),
                new SavedScanSource(missing, true),
            ]));
        var viewModel = CreateViewModel(settings);
        viewModel.Sources.Add(new ScanSourceViewModel(missing));

        await viewModel.StartScanCommand.ExecuteAsync();

        Assert.AreEqual(1, viewModel.DuplicateGroupCount);
        Assert.AreEqual(1, viewModel.IssueCount);
    }

    [TestMethod]
    public async Task CancelledRunUpdatesStatusWithoutReportingAnUnexpectedError()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        var settings = new FakeSettingsStore(new DuplicateSettings(
            source,
            []));
        var errors = new FakeUnexpectedErrorHandler();
        var runService = new FakeDuplicateRunService((_, _, _) =>
            Task.FromCanceled<DuplicateReport>(new CancellationToken(canceled: true)));
        var viewModel = CreateViewModel(settings, runService: runService, errorHandler: errors);

        await viewModel.StartScanCommand.ExecuteAsync();

        Assert.AreEqual("扫描已取消", viewModel.StatusText);
        Assert.IsFalse(viewModel.IsBusy);
        Assert.AreEqual(0, errors.Errors.Count);
    }

    [TestMethod]
    public async Task UnexpectedRunFailureUsesTheInjectedErrorHandler()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        var settings = new FakeSettingsStore(new DuplicateSettings(
            source,
            []));
        var errors = new FakeUnexpectedErrorHandler();
        var expected = new InvalidOperationException("unexpected");
        var runService = new FakeDuplicateRunService((_, _, _) => Task.FromException<DuplicateReport>(expected));
        var viewModel = CreateViewModel(settings, runService: runService, errorHandler: errors);

        await viewModel.StartScanCommand.ExecuteAsync();

        Assert.IsFalse(viewModel.IsBusy);
        Assert.AreEqual(1, errors.Errors.Count);
        Assert.AreEqual("扫描没有完成", errors.Errors[0].Title);
        Assert.AreSame(expected, errors.Errors[0].Exception);
    }

    [TestMethod]
    public async Task AsyncRelayCommandReportsFailuresAndAlwaysBecomesExecutableAgain()
    {
        var failures = new List<Exception>();
        var command = new AsyncRelayCommand(
            () => Task.FromException(new InvalidOperationException("boom")),
            failures.Add);

        await command.ExecuteAsync();

        Assert.AreEqual(1, failures.Count);
        Assert.IsInstanceOfType<InvalidOperationException>(failures[0]);
        Assert.IsTrue(command.CanExecute(null));
    }

    [TestMethod]
    public async Task AsyncRelayCommandDoesNotReportCancellationAndDoesNotRunTwice()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var failures = new List<Exception>();
        var command = new AsyncRelayCommand(
            async () =>
            {
                calls++;
                started.SetResult();
                await release.Task;
            },
            failures.Add);

        var first = command.ExecuteAsync();
        await started.Task;
        await command.ExecuteAsync();
        release.SetResult();
        await first;

        Assert.AreEqual(1, calls);
        Assert.AreEqual(0, failures.Count);
        Assert.IsTrue(command.CanExecute(null));

        var cancelled = new AsyncRelayCommand(
            () => Task.FromCanceled(new CancellationToken(canceled: true)),
            failures.Add);
        await cancelled.ExecuteAsync();
        Assert.AreEqual(0, failures.Count);
        Assert.IsTrue(cancelled.CanExecute(null));
    }

    private static MainWindowViewModel CreateViewModel(
        FakeSettingsStore settings,
        FakeFolderPicker? picker = null,
        IDuplicateRunService? runService = null,
        FakeUnexpectedErrorHandler? errorHandler = null) => new(
        runService ?? new DuplicateRunService(DuplicateScanner.CreateDefault()),
        picker ?? new FakeFolderPicker(),
        settings,
        unexpectedErrorHandler: errorHandler ?? new FakeUnexpectedErrorHandler());

    private static bool PathsEqual(string first, string second) =>
        StringComparer.OrdinalIgnoreCase.Equals(first, second);

    private sealed class FakeSettingsStore(DuplicateSettings? settings) : IDuplicateSettingsStore
    {
        public int SaveCalls { get; private set; }

        public DuplicateSettings? LastSaved { get; private set; }

        public DuplicateSettings? Load() => LastSaved ?? settings;

        public void Save(DuplicateSettings value)
        {
            SaveCalls++;
            LastSaved = value;
        }
    }

    private sealed class FakeFolderPicker(params string[] paths) : IFolderPickerService
    {
        private readonly Queue<string> paths = new(paths);

        public string? PickFolder(string title, string? initialDirectory = null) =>
            paths.Count == 0 ? null : paths.Dequeue();
    }

    private sealed class FakeDuplicateRunService(
        Func<DuplicateRunInput, IProgress<DuplicateScanProgress>?, CancellationToken, Task<DuplicateReport>> run)
        : IDuplicateRunService
    {
        public Task<DuplicateReport> RunAsync(
            DuplicateRunInput input,
            IProgress<DuplicateScanProgress>? progress = null,
            CancellationToken cancellationToken = default) => run(input, progress, cancellationToken);
    }

    private sealed class FakeUnexpectedErrorHandler : IUnexpectedErrorHandler
    {
        public List<(string Title, Exception Exception)> Errors { get; } = [];

        public void Report(string title, Exception exception)
        {
            Errors.Add((title, exception));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s4ms-desktop-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateDirectory(params string[] parts)
        {
            var path = parts.Aggregate(Path, System.IO.Path.Combine);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
