using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Scripts;
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
        Assert.AreEqual(1, viewModel.ConflictResources.Count(row => row.HasConfirmedDifference));
        Assert.AreEqual(1, viewModel.ConflictResources.Count(row => row.HasConfirmedIdentical));
        Assert.AreEqual(1, viewModel.ConflictScripts.Count);
        var library = viewModel.ConflictScripts.Single();
        Assert.AreEqual(2, library.Modules.Count);
        Assert.AreEqual(1, library.Modules.Count(row => row.HasConfirmedDifference));
        Assert.AreEqual(1, library.Modules.Count(row => row.HasConfirmedIdentical));
        Assert.IsTrue(viewModel.ConflictProblems.Any(row => PathsEqual(row.Path, badPackage) && row.IsWholeFile));

        viewModel.SelectConflictFilterCommand.Execute("different");
        await viewModel.ConflictRowsRefreshTask;
        Assert.AreEqual(1, viewModel.VisibleConflictParentCount);
        viewModel.SelectConflictViewCommand.Execute("scripts");
        await viewModel.ConflictRowsRefreshTask;
        var pair = viewModel.ConflictRows.Single();
        viewModel.ToggleConflictExpandedCommand.Execute(pair);
        await viewModel.ConflictRowsRefreshTask;
        Assert.AreEqual(2, viewModel.ConflictRows.Count(row => row.Kind == ConflictRowKind.Module));
        viewModel.SelectConflictFilterCommand.Execute("same");
        await viewModel.ConflictRowsRefreshTask;
        Assert.AreEqual(1, viewModel.VisibleConflictParentCount);
        Assert.AreEqual(2, viewModel.ConflictRows.Count(row => row.Kind == ConflictRowKind.Module));
        Assert.IsFalse(viewModel.ShowConflictWarning);
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
        Assert.IsFalse(viewModel.ShowConflictWarning);
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
        Assert.IsFalse(viewModel.ShowConflictWarning);
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
        Assert.IsFalse(viewModel.ShowConflictWarning);
        Assert.IsFalse(viewModel.IsBusy);
        Assert.AreEqual(0, viewModel.ConflictResources.Count);
        Assert.IsTrue(viewModel.StartConflictScanCommand.CanExecute(null));
    }

    [TestMethod]
    public Task LateProjectionCannotOverwriteFilterCollapseClearOrRescan() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var mods = files.Directory("Projection");
        WritePackage(mods, "new-a.package", 1, "first");
        WritePackage(mods, "new-b.package", 1, "second");
        foreach (var action in new[] { "filter", "view", "collapse", "clear", "rescan" })
        {
            var model = CreateViewModel(mods);
            model.OpenConflictCommand.Execute(null);
            model.OpenConflictSectionCommand.Execute("basic");
            var session = ConflictResultSessionTests.Session([
                ConflictResultSessionTests.Resource(1, (ConflictResultSessionTests.A, "a"), (ConflictResultSessionTests.B, "b"))]);
            await ConflictPageRenderTests.SetSessionAsync(model, session);
            var parent = model.ConflictRows.Single();
            using var gate = new GateList<PackageConflictPairSummary>(session.Resources);
            typeof(ConflictResultSession).GetField("<Resources>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(session, gate);
            model.ToggleConflictExpandedCommand.Execute(parent);
            var old = model.ConflictRowsRefreshTask;
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                switch (action)
                {
                    case "filter":
                        model.SelectConflictFilterCommand.Execute("same");
                        await model.ConflictRowsRefreshTask;
                        Assert.AreEqual(0, model.VisibleConflictParentCount);
                        Assert.IsFalse(model.ShowConflictWarning);
                        break;
                    case "view":
                        model.SelectConflictViewCommand.Execute("scripts");
                        model.SelectConflictViewCommand.Execute("resources");
                        model.SelectConflictViewCommand.Execute("problems");
                        await model.ConflictRowsRefreshTask;
                        Assert.AreEqual("problems", model.ConflictView);
                        Assert.AreEqual(0, model.ConflictRows.Count);
                        break;
                    case "collapse":
                        model.ToggleConflictExpandedCommand.Execute(parent);
                        await model.ConflictRowsRefreshTask;
                        Assert.AreEqual(1, model.ConflictRows.Count);
                        Assert.IsFalse(model.ConflictRows[0].IsExpanded);
                        break;
                    case "clear":
                        model.Sources.Clear();
                        Assert.AreEqual("initial", model.ConflictRunState);
                        break;
                    case "rescan":
                        await model.StartConflictScanCommand.ExecuteAsync();
                        Assert.AreEqual(1, model.VisibleConflictParentCount);
                        break;
                }
            }
            finally { gate.Release.Set(); }
            await old.WaitAsync(TimeSpan.FromSeconds(10));
            if (action is "filter" or "view") Assert.AreEqual(0, model.ConflictRows.Count);
            if (action == "collapse") Assert.IsTrue(model.ConflictRows.All(row => row.Depth == 0 && !row.IsExpanded));
            if (action == "clear") Assert.AreEqual(0, model.ConflictRows.Count);
            if (action == "rescan") StringAssert.Contains(model.ConflictRows[0].Tooltip, "new-a.package");
        }
    });

    [TestMethod]
    public async Task WarningOnlyAppearsForEntirelyEmptyCompletedSession()
    {
        using var files = new TemporaryFiles();
        var mods = files.Directory("Empty");
        var model = CreateViewModel(mods);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        Assert.IsFalse(model.ShowConflictWarning);
        await model.StartConflictScanCommand.ExecuteAsync();
        Assert.IsTrue(model.ShowConflictWarning);
        WritePackage(mods, "a.package", 1, "a");
        WritePackage(mods, "b.package", 1, "b");
        await model.StartConflictScanCommand.ExecuteAsync();
        model.SelectConflictFilterCommand.Execute("same");
        await model.ConflictRowsRefreshTask;
        Assert.IsTrue(model.ShowEmptyConflictRows);
        Assert.IsFalse(model.ShowConflictWarning);
        File.WriteAllText(Path.Combine(mods, "failed.package"), "not DBPF");
        await model.StartConflictScanCommand.ExecuteAsync();
        Assert.IsFalse(model.ShowConflictWarning);
    }

    [TestMethod]
    public Task ProgressNumbersUseStructuredTotalsAndClearPerStage() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var mods = files.Directory("Progress");
        var scanner = new SignalingScanner();
        var comparer = new SignalingComparer();
        var viewModel = CreateViewModel(new FakeSettingsStore(new DuplicateSettings(mods,
            [new SavedScanSource(mods, true)])), scanner, comparer, new ScriptModuleScanner(new PhysicalFileSystemAccess()));
        viewModel.OpenConflictCommand.Execute(null);        viewModel.OpenConflictSectionCommand.Execute("basic");
        Assert.IsTrue(viewModel.ConflictIndeterminate);
        Assert.AreEqual(string.Empty, viewModel.ConflictProgressText);
        Assert.AreEqual(1, viewModel.ConflictProgressMaximum);

        var scan = viewModel.StartConflictScanCommand.ExecuteAsync();
        await scanner.Reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        scanner.Progress.Report(new(PackageConflictScanPhase.ReadingIndexes, "底层消息不该被解析", 42, 180));
        await PumpAsync();
        Assert.AreEqual("正在读取资源索引…", viewModel.ConflictStatus);
        Assert.AreEqual("42 / 180", viewModel.ConflictProgressText);
        Assert.AreEqual(42, viewModel.ConflictCompleted);
        Assert.AreEqual(180, viewModel.ConflictProgressMaximum);
        Assert.IsFalse(viewModel.ConflictIndeterminate);

        scanner.Progress.Report(new(PackageConflictScanPhase.Grouping, "ignored", 180, null));
        await PumpAsync();
        Assert.IsTrue(viewModel.ConflictIndeterminate);
        Assert.AreEqual(string.Empty, viewModel.ConflictProgressText);
        Assert.AreEqual(1, viewModel.ConflictProgressMaximum);

        scanner.Release.SetResult();
        await comparer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await PumpAsync();
        // 比较阶段的第一条结构化进度：42 / 180 已经被阶段切换清掉。
        Assert.AreEqual("3 / 7", viewModel.ConflictProgressText);
        Assert.AreEqual(7, viewModel.ConflictProgressMaximum);
        Assert.IsFalse(viewModel.ConflictIndeterminate);
        Assert.AreEqual("正在比较资源…", viewModel.ConflictStatus);

        comparer.Progress.Report(new(ResourceContentComparisonPhase.ReadingContents, "ignored", 0, 0, 0));
        await PumpAsync();
        Assert.AreEqual(string.Empty, viewModel.ConflictProgressText);
        Assert.AreEqual(1, viewModel.ConflictProgressMaximum);
        Assert.IsTrue(viewModel.ConflictIndeterminate);

        comparer.Done.SetResult();
        await scan.WaitAsync(TimeSpan.FromSeconds(10));
        await PumpAsync();
        Assert.AreEqual("complete", viewModel.ConflictRunState);
        Assert.AreEqual(string.Empty, viewModel.ConflictProgressText);
        Assert.AreEqual(1, viewModel.ConflictProgressMaximum);
        Assert.IsTrue(viewModel.ConflictIndeterminate);
    });

    [TestMethod]
    public Task CancellationAndRescanClearProgressNumbers() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var mods = files.Directory("ProgressReset");
        var scanner = new SignalingScanner();
        var viewModel = CreateViewModel(new FakeSettingsStore(new DuplicateSettings(mods,
            [new SavedScanSource(mods, true)])), scanner, new EmptyComparer(), new EmptyScriptScanner());
        viewModel.OpenConflictCommand.Execute(null);
        viewModel.OpenConflictSectionCommand.Execute("basic");
        var scan = viewModel.StartConflictScanCommand.ExecuteAsync();
        await scanner.Reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        scanner.Progress.Report(new(PackageConflictScanPhase.ReadingIndexes, "ignored", 5, 9));
        await PumpAsync();
        Assert.AreEqual("5 / 9", viewModel.ConflictProgressText);

        viewModel.CancelConflictScanCommand.Execute(null);
        await scan.WaitAsync(TimeSpan.FromSeconds(10));
        await PumpAsync();
        Assert.AreEqual("cancelled", viewModel.ConflictRunState);
        Assert.AreEqual(string.Empty, viewModel.ConflictProgressText);
        Assert.AreEqual(1, viewModel.ConflictProgressMaximum);
        Assert.IsTrue(viewModel.ConflictIndeterminate);

        viewModel.StartConflictScanCommand.Execute(null);
        await PumpAsync();
        Assert.AreEqual("scanning", viewModel.ConflictRunState);
        Assert.AreEqual(string.Empty, viewModel.ConflictProgressText);

        var failing = CreateViewModel(new FakeSettingsStore(new DuplicateSettings(mods,
            [new SavedScanSource(mods, true)])), new FailingPackageScanner(), new EmptyComparer(), new EmptyScriptScanner());
        failing.OpenConflictCommand.Execute(null);
        failing.OpenConflictSectionCommand.Execute("basic");
        await failing.StartConflictScanCommand.ExecuteAsync();
        await PumpAsync();
        Assert.AreEqual("failed", failing.ConflictRunState);
        Assert.AreEqual(string.Empty, failing.ConflictProgressText);
        Assert.AreEqual(1, failing.ConflictProgressMaximum);
        Assert.IsTrue(failing.ConflictIndeterminate);

        viewModel.Sources.Clear();
        Assert.AreEqual("initial", viewModel.ConflictRunState);
        Assert.AreEqual(string.Empty, viewModel.ConflictProgressText);
        Assert.AreEqual(1, viewModel.ConflictProgressMaximum);
    });

    [TestMethod]
    public Task ProgressNumbersAreBoundToTheVisibleStatusArea() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateViewModel(Path.GetTempPath());
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = ConflictPageRenderTests.OpenWindow(model);
        SetConflictField(model, "conflictRunState", "scanning");
        SetConflictField(model, "conflictStatus", "正在读取资源索引…");
        SetConflictField(model, "conflictCompleted", 42);
        SetConflictField(model, "conflictProgressMaximum", 180);
        SetConflictField(model, "conflictProgressTotal", 180);
        SetConflictField(model, "conflictIndeterminate", false);
        ConflictPageRenderTests.Refresh(model);
        await WpfTestHost.LayoutAsync(window);
        var progress = ConflictPageRenderTests.Descendants<TextBlock>(window).Single(text => text.Name == "ConflictProgressText");
        Assert.IsTrue(progress.IsVisible);
        Assert.AreEqual("42 / 180", progress.Text);
        var bar = ConflictPageRenderTests.Descendants<ProgressBar>(window).Single(bar => bar.IsVisible);
        Assert.AreEqual(180, bar.Maximum);
        Assert.AreEqual(42, bar.Value);
        Assert.IsFalse(bar.IsIndeterminate);
    });

    private static void SetConflictField(MainWindowViewModel model, string name, object value) =>
        typeof(MainWindowViewModel).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(model, value);

    // Progress<T> 把回调投递到创建时的同步上下文；等派发器处理完已排队的回调再看界面状态。
    private static async Task PumpAsync()
    {
        if (Application.Current is null) return;
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private sealed class GateList<T>(IReadOnlyList<T> items) : IReadOnlyList<T>, IDisposable
    {
        private int entered;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public int Count => items.Count;
        public T this[int index] => items[index];
        public IEnumerator<T> GetEnumerator()
        {
            if (Interlocked.Increment(ref entered) == 1)
            {
                Started.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Projection gate");
            }
            return items.GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => Release.Dispose();
    }

    private static MainWindowViewModel CreateViewModel(string source) =>
        CreateViewModel(new FakeSettingsStore(new DuplicateSettings(source,
            [new SavedScanSource(source, true)])));

    private static MainWindowViewModel CreateViewModel(FakeSettingsStore settings,
        IPackageConflictScanner? scanner = null) => new(
            new DuplicateRunService(DuplicateScanner.CreateDefault()),
            new FakeFolderPicker(), settings, packageConflictScanner: scanner);

    private static MainWindowViewModel CreateViewModel(FakeSettingsStore settings, IPackageConflictScanner scanner,
        IResourceContentComparer comparer, IScriptModuleScanner scriptScanner) => new(
            new DuplicateRunService(DuplicateScanner.CreateDefault()),
            new FakeFolderPicker(), settings, packageConflictScanner: scanner,
            resourceContentComparer: comparer, scriptModuleScanner: scriptScanner);

    private sealed class SignalingScanner : IPackageConflictScanner
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InlineProgress<PackageConflictScanProgress> Progress { get; } = new();

        public async Task<PackageConflictScanReport> ScanAsync(PackageConflictScanRequest request,
            IProgress<PackageConflictScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Progress.Target = progress;
            Reading.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            // 报告完成后才放行，测试可以等这之后读取界面状态。
            Reported.TrySetResult();
            return new PackageConflictScanReport(DateTime.UnixEpoch, DateTime.UnixEpoch, [], 0, 0, [], [], []);
        }
    }

    private sealed class SignalingComparer : IResourceContentComparer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InlineProgress<ResourceContentComparisonProgress> Progress { get; } = new();

        public async Task<ResourceContentComparisonReport> CompareAsync(ResourceContentComparisonRequest request,
            IProgress<ResourceContentComparisonProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Progress.Target = progress;
            // 上一阶段的迟到进度校验在本方法之前完成，放行后报告一定属于比较阶段。
            progress?.Report(new(ResourceContentComparisonPhase.ReadingContents, "ignored", 3, 7, 0));
            Started.TrySetResult();
            await Done.Task.WaitAsync(cancellationToken);
            return new ResourceContentComparisonReport(DateTime.UnixEpoch, DateTime.UnixEpoch, new(), [], [], 0);
        }
    }

    // 同步调用目标，测试自己决定何时报告，不依赖线程池排程。
    private sealed class InlineProgress<T> : IProgress<T>
    {
        public IProgress<T>? Target { get; set; }
        public void Report(T value) => Target?.Report(value);
    }

    private sealed class EmptyComparer : IResourceContentComparer
    {
        public Task<ResourceContentComparisonReport> CompareAsync(ResourceContentComparisonRequest request,
            IProgress<ResourceContentComparisonProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResourceContentComparisonReport(DateTime.UnixEpoch, DateTime.UnixEpoch, new(), [], [], 0));
    }

    private sealed class EmptyScriptScanner : IScriptModuleScanner
    {
        public Task<ScriptModuleScanReport> ScanAsync(ScriptModuleScanRequest request,
            IProgress<ScriptModuleScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScriptModuleScanReport(DateTime.UnixEpoch, DateTime.UnixEpoch, [], new(), 0, 0, [], 0, [], [], 0));
    }

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
