using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Caching;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;
using static Sims4ModSieve.Desktop.Tests.SettingsViewModelTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class MainWindowLifecycleTests
{
    [TestMethod]
    public Task ClosingWaitsForBusyOperationAndClosesOnceAfterCompletion() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        var window = OpenWindow(model);
        var closed = 0;
        window.Closed += (_, _) => closed++;
        var busy = typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.IsBusy))!;
        busy.SetValue(model, true);
        window.Close();
        window.Close();
        Assert.IsTrue(window.IsVisible);
        Assert.AreEqual(0, closed);
        busy.SetValue(model, false);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsFalse(window.IsVisible);
        Assert.AreEqual(1, closed);
    });

    [TestMethod]
    public Task ClosingDuringScanCancelsAndDiscardsStagedHashes() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        var path = Path.Combine(mods, "a.package");
        File.WriteAllText(path, "same");
        files.Store.Save(new(mods, []));
        var run = new WaitingScan(path);
        var model = new MainWindowViewModel(run, new Picker(), files.Store, scanCacheDirectory: files.Store.DataDirectory);
        var window = OpenWindow(model);
        var scan = model.StartScanCommand.ExecuteAsync();
        await run.Staged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        window.Close();
        await scan.WaitAsync(TimeSpan.FromSeconds(5));
        await WpfTestHost.LayoutAsync(window);

        Assert.IsFalse(window.IsVisible);
        Assert.AreEqual("扫描已取消", model.StatusText);
        var cache = new SqliteSha256Cache(Path.Combine(files.Store.DataDirectory, "cache", "sha256.sqlite3"));
        Assert.IsTrue(cache.IsAvailable);
        Assert.IsNull(cache.GetSha256(path, new PhysicalFileSystemAccess().GetFileStamp(path)));
    });

    [TestMethod]
    public Task ClosingDuringDeleteWaitsForHistoryAndResultBeforeExit() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        File.WriteAllText(Path.Combine(mods, "a.package"), "same");
        File.WriteAllText(Path.Combine(mods, "b.package"), "same");
        files.Store.Save(new(mods, []));
        var history = new FileManagementStore(files.Folder("Data"));
        var recycle = new WaitingRecycleBin(files.Folder("Recycle"));
        var actions = new FileActionService(recycle, new PhysicalFileMetadataAccess(), history);
        var model = files.Model(actions: actions);
        var window = OpenWindow(model);
        await model.StartScanCommand.ExecuteAsync();
        model.ApplySuggestionsCommand.Execute(null);
        var deletion = model.DeleteSelectedCommand.ExecuteAsync();
        await recycle.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        window.Close();
        Assert.IsTrue(window.IsVisible);
        Assert.IsTrue(model.IsBusy);
        Assert.AreEqual(0, history.Load().Deleted.Count);
        recycle.Release.SetResult();
        await deletion;
        await WpfTestHost.LayoutAsync(window);
        Assert.IsFalse(window.IsVisible);
        Assert.AreEqual(1, history.Load().Deleted.Count);
        Assert.IsFalse(File.Exists(history.Load().Deleted.Single().Item.OriginalPath));
        StringAssert.Contains(model.FileActionStatusText, "已移入回收站 1 个文件");
        Assert.IsFalse(model.IsBusy);
    });

    private sealed class WaitingScan(string path) : IDuplicateRunService
    {
        public TaskCompletionSource Staged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DuplicateReport> RunAsync(DuplicateRunInput input, IProgress<DuplicateScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            await input.FileHasher!.HashAsync(path, cancellationToken);
            Staged.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class WaitingRecycleBin(string directory) : IRecycleBinAdapter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanRecycle(string path) => true;

        public async Task<RecycleBinDeleteResult> MoveToRecycleBinAsync(IReadOnlyList<string> paths,
            CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            var items = paths.Select(path =>
            {
                var recycled = Path.Combine(directory, Path.GetFileName(path));
                File.Move(path, recycled);
                return new RecycleBinItem(path, [1, 2, 0, 0], recycled);
            }).ToArray();
            return new RecycleBinDeleteResult(items, []);
        }

        public Task<FileActionBatchResult> RestoreAsync(IReadOnlyList<RecycleBinItem> items,
            CancellationToken cancellationToken = default) => Task.FromResult(new FileActionBatchResult([], [], false));
    }
}
