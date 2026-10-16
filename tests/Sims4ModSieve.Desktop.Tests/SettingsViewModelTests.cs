using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictResultSessionTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class SettingsViewModelTests
{
    [TestMethod]
    public Task ChangingDefaultModsPreservesBothDetectionSessions() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        var next = files.Folder("新默认目录");
        foreach (var name in new[] { "a", "b", "c" }) File.WriteAllText(Path.Combine(mods, name + ".package"), "same");
        files.Store.Save(new(mods, []));
        var actions = new MemoryFileActions();
        var model = files.Model(new Picker(next), actions: actions);
        await model.StartScanCommand.ExecuteAsync();
        model.ApplySuggestionsCommand.Execute(null);
        model.Groups.Single().Files.Last().IsSelected = false;
        await model.DeleteSelectedCommand.ExecuteAsync();
        Assert.IsTrue(model.CanUndoLastDelete);
        Assert.IsTrue(model.HasResults);
        model.Groups.Single().IsExpanded = true;
        model.Groups.Single().Files.Last().IsSelected = true;
        await PopulateConflictAsync(model);
        model.Sources[0].IsEnabled = false;
        model.ConflictSources[0].IsEnabled = false;
        var groups = model.Groups;
        var selected = model.SelectedFileCount;
        var rows = model.ConflictRows;
        var sources = model.Sources.ToArray();
        var conflictSources = model.ConflictSources.ToArray();
        var status = model.FileActionStatusText;
        model.OpenSettingsCommand.Execute(null);
        model.ChangeDefaultModsRootCommand.Execute(null);
        Assert.AreEqual(next, model.ModsRoot);
        CollectionAssert.AreEqual(sources, model.Sources.ToArray());
        CollectionAssert.AreEqual(conflictSources, model.ConflictSources.ToArray());
        Assert.IsFalse(model.Sources[0].IsEnabled || model.ConflictSources[0].IsEnabled);
        Assert.AreSame(groups, model.Groups);
        Assert.AreSame(rows, model.ConflictRows);
        Assert.AreEqual(selected, model.SelectedFileCount);
        Assert.IsTrue(model.Groups.Single().IsExpanded);
        Assert.IsTrue(model.CanUndoLastDelete);
        Assert.AreEqual(status, model.FileActionStatusText);
        await model.UndoLastDeleteCommand.ExecuteAsync();
        Assert.AreEqual(3, model.Groups.Single().Files.Count);
        Assert.IsFalse(model.CanUndoLastDelete);
    });

    [TestMethod]
    public void AddSourceUsesUpdatedDefaultModsOnBothPages()
    {
        using var files = new Scenario();
        var old = files.Folder("Old");
        var updated = files.Folder("Updated");
        files.Store.Save(new(old, []));
        var picker = new Picker(updated, null, null);
        var model = files.Model(picker);
        model.ChangeDefaultModsRootCommand.Execute(null);
        model.OpenDuplicateScannerCommand.Execute(null);
        model.AddSourceCommand.Execute(null);
        model.OpenConflictCommand.Execute(null);
        model.AddSourceCommand.Execute(null);
        CollectionAssert.AreEqual(new string?[] { old, updated, updated }, picker.InitialDirectories.ToArray());
        Assert.AreEqual(old, model.Sources.Single().Path);
        Assert.AreEqual(old, model.ConflictSources.Single().Path);
    }

    [TestMethod]
    public void RestartUsesUpdatedModsWithoutRestoringSources()
    {
        using var files = new Scenario();
        var old = files.Folder("Old");
        var updated = files.Folder("Updated");
        files.Store.Save(new(old, [new(files.Root, false)]));
        var model = files.Model(new Picker(updated));
        model.Sources.Add(new(files.Root));
        model.ConflictSources.Clear();
        model.ChangeDefaultModsRootCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("about");
        var reopened = files.Model();
        Assert.AreEqual(updated, reopened.Sources.Single().Path);
        Assert.AreEqual(updated, reopened.ConflictSources.Single().Path);
        Assert.IsTrue(reopened.Sources[0].IsEnabled && reopened.ConflictSources[0].IsEnabled);
        Assert.AreEqual("folders", reopened.SettingsSection);
        Assert.IsFalse(reopened.HasResults);
        Assert.AreEqual(0, files.Store.Load()!.Sources.Count);
    }

    [TestMethod]
    public void TrayConfigurationSurvivesModsChanges()
    {
        using var files = new Scenario();
        var old = files.Folder("Old");
        var tray = files.Folder("Tray");
        var updated = files.Folder("Updated");
        files.Store.Save(new(old, []));
        var model = files.Model(new Picker(tray, null, updated));
        model.ChangeTrayRootCommand.Execute(null);
        model.ChangeTrayRootCommand.Execute(null);
        Assert.AreEqual(tray, model.TrayRoot);
        Assert.AreEqual(old, model.Sources.Single().Path);
        model.OpenConflictCommand.Execute(null);
        model.ChangeModsRootCommand.Execute(null);
        Assert.AreEqual(tray, files.Model().TrayRoot);
        CollectionAssert.AreEqual(new[] { old, updated }, model.ConflictSources.Select(source => source.Path).ToArray());
        Assert.AreEqual(old, model.Sources.Single().Path);
        model.ClearTrayRootCommand.Execute(null);
        Assert.IsNull(files.Model().TrayRoot);
        Assert.IsTrue(Directory.Exists(tray));
    }

    [TestMethod]
    public void FailedSettingsSaveKeepsPreviousConfiguration()
    {
        using var files = new Scenario();
        var old = files.Folder("Old");
        var tray = files.Folder("Tray");
        var updated = files.Folder("Updated");
        files.Store.Save(new(old, [], tray));
        var dialogs = new Dialogs();
        var model = files.Model(new Picker(updated, updated, updated), dialogs: dialogs);
        files.Folder("Documents", "Electronic Arts", "The Sims 4", "Mods");
        model.FindDefaultFolderCommand.Execute("Mods");
        var path = Path.Combine(files.Root, "Sims4ModSieve", "duplicate-settings.json");
        var before = File.ReadAllText(path);
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            model.ChangeDefaultModsRootCommand.Execute(null);
            Assert.AreEqual(old, model.ModsRoot);
            model.ChangeTrayRootCommand.Execute(null);
            Assert.AreEqual(tray, model.TrayRoot);
            model.ClearTrayRootCommand.Execute(null);
            Assert.AreEqual(tray, model.TrayRoot);
            model.ChangeModsRootCommand.Execute(null);
            Assert.AreEqual(old, model.ModsRoot);
            model.ApplyDefaultFolderCandidateCommand.Execute(null);
            Assert.AreEqual(old, model.ModsRoot);
            Assert.IsTrue(model.HasModsFolderCandidate);
            StringAssert.Contains(model.SettingsFeedback, "未保存");
        }
        Assert.AreEqual(before, File.ReadAllText(path));
        Assert.AreEqual(old, model.Sources.Single().Path);
        Assert.AreEqual(1, dialogs.Problems.Count);
        Assert.IsFalse(model.IsBusy);
    }

    [TestMethod]
    public void DefaultFolderCandidateRequiresExplicitApplication()
    {
        using var files = new Scenario();
        var old = files.Folder("Old");
        var defaultMods = files.Folder("Documents", "Electronic Arts", "The Sims 4", "Mods");
        var defaultTray = files.Folder("Documents", "Electronic Arts", "The Sims 4", "Tray");
        files.Store.Save(new(old, []));
        var model = files.Model();
        model.FindDefaultFolderCommand.Execute("Mods");
        Assert.AreEqual(defaultMods, model.FolderCandidate!.Path);
        Assert.AreEqual(old, files.Store.Load()!.ModsRoot);
        model.CancelDefaultFolderCandidateCommand.Execute(null);
        Assert.IsNull(model.FolderCandidate);
        model.FindDefaultFolderCommand.Execute("Tray");
        Assert.AreEqual("Tray", model.FolderCandidate!.Kind);
        model.ApplyDefaultFolderCandidateCommand.Execute(null);
        Assert.AreEqual(defaultTray, files.Store.Load()!.TrayRoot);
        model.FindDefaultFolderCommand.Execute("Mods");
        model.ApplyDefaultFolderCandidateCommand.Execute(null);
        Assert.AreEqual(defaultMods, files.Store.Load()!.ModsRoot);
        Assert.AreEqual(old, model.Sources.Single().Path);
        Assert.AreEqual(old, model.ConflictSources.Single().Path);
        Directory.Delete(defaultMods);
        model.FindDefaultFolderCommand.Execute("Mods");
        Assert.IsNull(model.FolderCandidate);
        StringAssert.Contains(model.ModsFolderFeedback, "未找到");
        Assert.AreEqual(defaultMods, model.ModsRoot);
        Assert.IsFalse(Directory.Exists(defaultMods));
    }

    [TestMethod]
    public void MissingConfiguredFolderIsRetained()
    {
        using var files = new Scenario();
        var missing = Path.Combine(files.Root, "Missing");
        files.Store.Save(new(missing, [], missing));
        var model = files.Model();
        model.OpenSettingsCommand.Execute(null);
        Assert.IsTrue(model.HasModsRootAvailabilityWarning && model.HasTrayRootAvailabilityWarning);
        model.OpenModsFolderCommand.Execute(null);
        model.OpenTrayFolderCommand.Execute(null);
        Assert.IsFalse(string.IsNullOrEmpty(model.ModsFolderFeedback));
        Assert.IsFalse(string.IsNullOrEmpty(model.TrayFolderFeedback));
        Assert.AreEqual(missing, files.Store.Load()!.ModsRoot);
        Assert.AreEqual(missing, model.TrayRoot);
        Assert.IsFalse(Directory.Exists(missing));
    }

    [TestMethod]
    public Task SettingsNavigationPreservesDetectionState() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        File.WriteAllText(Path.Combine(mods, "a.package"), "same");
        File.WriteAllText(Path.Combine(mods, "b.package"), "same");
        files.Store.Save(new(mods, []));
        var model = files.Model();
        await model.StartScanCommand.ExecuteAsync();
        model.ApplySuggestionsCommand.Execute(null);
        model.Groups.Single().IsExpanded = true;
        await PopulateConflictAsync(model);
        var rows = model.ConflictRows;
        var groups = model.Groups;
        foreach (var command in new[] { model.OpenSettingsCommand, model.GoHomeCommand, model.OpenDuplicateScannerCommand,
            model.OpenSettingsCommand, model.OpenConflictCommand, model.OpenSettingsCommand })
        {
            command.Execute(null);
            Assert.AreEqual(1, new[] { model.IsHomeVisible, model.IsScannerVisible, model.IsConflictVisible, model.IsSettingsVisible }.Count(value => value));
            Assert.AreSame(groups, model.Groups);
            Assert.AreSame(rows, model.ConflictRows);
            Assert.AreEqual(1, model.SelectedFileCount);
            Assert.IsTrue(model.Groups.Single().IsExpanded);
        }
        model.SelectSettingsSectionCommand.Execute("about");
        model.GoHomeCommand.Execute(null);
        model.OpenSettingsCommand.Execute(null);
        Assert.IsTrue(model.IsAboutSettings);
        Assert.AreEqual("different", model.ConflictFilter);
        Assert.IsTrue(model.ConflictRows[0].IsExpanded);
    });

    [TestMethod]
    public async Task BusyStateBlocksSettingsMutation()
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        files.Store.Save(new(mods, []));
        var runner = new WaitingRun();
        var picker = new Picker(files.Folder("Other"));
        var model = files.Model(picker, run: runner);
        var task = model.StartScanCommand.ExecuteAsync();
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var command in new[] { model.OpenSettingsCommand, model.ChangeDefaultModsRootCommand, model.ChangeTrayRootCommand,
            model.ClearTrayRootCommand, model.FindDefaultFolderCommand, model.ApplyDefaultFolderCandidateCommand })
        {
            Assert.IsFalse(command.CanExecute(null));
            command.Execute("Mods");
        }
        Assert.AreEqual(0, picker.InitialDirectories.Count);
        Assert.AreEqual(mods, model.ModsRoot);
        Assert.IsTrue(model.IsBusy);
        model.CancelScanCommand.Execute(null);
        await task;
        Assert.IsTrue(model.OpenSettingsCommand.CanExecute(null));
        Assert.IsTrue(model.ChangeTrayRootCommand.CanExecute(null));
    }

    [TestMethod]
    public Task FileOperationBusyStateBlocksSettingsMutation() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        File.WriteAllText(Path.Combine(mods, "a.package"), "same");
        File.WriteAllText(Path.Combine(mods, "b.package"), "same");
        files.Store.Save(new(mods, []));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = new MemoryFileActions { DeleteRelease = release.Task };
        var model = files.Model(actions: actions);
        await model.StartScanCommand.ExecuteAsync();
        model.ApplySuggestionsCommand.Execute(null);
        var deletion = model.DeleteSelectedCommand.ExecuteAsync();
        Assert.IsTrue(model.IsBusy);
        Assert.IsFalse(model.OpenSettingsCommand.CanExecute(null));
        Assert.IsFalse(model.ChangeDefaultModsRootCommand.CanExecute(null));
        model.ChangeTrayRootCommand.Execute(null);
        Assert.IsNull(model.TrayRoot);
        release.SetResult();
        await deletion;
        Assert.IsTrue(model.OpenSettingsCommand.CanExecute(null));
    });

    [TestMethod]
    public Task ExternalOperationFailuresProvideLocalFeedback() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var support = new SettingsSupportService(files.Root, files.Folder("Documents"),
            openTarget: _ => throw new InvalidOperationException(), copyText: _ => throw new InvalidOperationException());
        var model = files.Model(support: support);
        await model.CopyDiagnosticsCommand.ExecuteAsync();
        StringAssert.Contains(model.DiagnosticsFeedback, "无法复制");
        model.OpenFeedbackCommand.Execute(null);
        StringAssert.Contains(model.DiagnosticsFeedback, "无法打开");
        model.OpenProjectCommand.Execute(null);
        StringAssert.Contains(model.AboutFeedback, "无法打开");
        Assert.IsFalse(model.IsBusy);
        Assert.AreEqual(string.Empty, model.StatusText);
    });

    private static async Task PopulateConflictAsync(MainWindowViewModel model)
    {
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var packages = new PackageConflictScanReport(DateTime.UnixEpoch, DateTime.UnixEpoch, [], 2, 2, [], [], [])
        {
            ResourceInventories = new[] { A, B }.Select(path => new PackageResourceInventory(path, Stamp, 10)
            { ResourceTypeCounts = new Dictionary<uint, int> { [0x034AEECB] = 10 } }).ToArray(),
        };
        var session = new ConflictResultSession();
        session.Replace(packages, Contents(Resource(1, (A, "a"), (B, "b"))), Scripts());
        await ConflictPageRenderTests.SetSessionAsync(model, session, "all");
        model.SelectConflictFilterCommand.Execute("different");
        await model.ConflictRowsRefreshTask;
        model.ToggleConflictExpandedCommand.Execute(model.ConflictRows.First());
        await model.ConflictRowsRefreshTask;
    }

    internal sealed class Scenario : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "s4ms-settings-tests-" + Guid.NewGuid().ToString("N"));
        public DuplicateSettingsStore Store { get; }
        public Scenario() { Directory.CreateDirectory(Root); Store = new(Root); }
        public string Folder(params string[] parts)
        {
            var path = parts.Aggregate(Root, Path.Combine);
            Directory.CreateDirectory(path);
            return path;
        }
        public MainWindowViewModel Model(Picker? picker = null, IDuplicateRunService? run = null,
            IFileActionService? actions = null, Dialogs? dialogs = null, SettingsSupportService? support = null) => new(
                run ?? new DuplicateRunService(DuplicateScanner.CreateDefault()), picker ?? new Picker(), Store,
                fileActionService: actions, fileActionDialogs: dialogs ?? new Dialogs(),
                settingsSupport: support ?? new SettingsSupportService(Root, Folder("Documents")));
        public void Dispose() { Directory.Delete(Root, recursive: true); }
    }

    internal sealed class Picker(params string?[] paths) : IFolderPickerService
    {
        private readonly Queue<string?> paths = new(paths);
        public List<string?> InitialDirectories { get; } = [];
        public string? PickFolder(string title, string? initialDirectory = null)
        {
            InitialDirectories.Add(initialDirectory);
            return paths.Count > 0 ? paths.Dequeue() : null;
        }
    }

    internal sealed class Dialogs : IFileActionDialogService
    {
        public List<FileActionFailure> Problems { get; } = [];
        public bool ConfirmDeletion(FileDeletionPreflight preflight) => true;
        public void ShowProblems(string title, IReadOnlyList<FileActionFailure> failures) => Problems.AddRange(failures);
    }

    private sealed class WaitingRun : IDuplicateRunService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DuplicateReport> RunAsync(DuplicateRunInput input, IProgress<DuplicateScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class MemoryFileActions : IFileActionService
    {
        private string[] deleted = [];
        public Task DeleteRelease { get; init; } = Task.CompletedTask;
        public bool CanUndoLastDelete => deleted.Length > 0;
        public void ClearLastDelete() => deleted = [];
        public FileDeletionPreflight Preflight(IReadOnlyList<FileDeletionCandidate> candidates, IReadOnlyList<string> fullySelectedGroups) => new(candidates, [], fullySelectedGroups);
        public async Task<FileActionBatchResult> DeleteAsync(FileDeletionPreflight preflight, CancellationToken cancellationToken = default)
        {
            await DeleteRelease;
            deleted = preflight.Candidates.Select(file => file.Path).ToArray();
            return new FileActionBatchResult(deleted, [], true);
        }
        public Task<FileActionBatchResult> UndoLastDeleteAsync(CancellationToken cancellationToken = default)
        {
            var paths = deleted;
            deleted = [];
            return Task.FromResult(new FileActionBatchResult(paths, [], false));
        }
    }
}
