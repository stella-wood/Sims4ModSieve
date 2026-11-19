using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Settings;
using Sims4ModSieve.Core.Tests;
using Sims4ModSieve.Core.Tests.Packages;
using Sims4ModSieve.Desktop.Components;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ConflictGroupingSizeTests
{
    [TestMethod]
    [DataRow(3, false)]
    [DataRow(3, true)]
    [DataRow(4, false)]
    public Task OversizeScanFailsAndDuplicateNavigationOnlyChangesPage(int originals, bool navigate) => WpfTestHost.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        for (var index = 0; index < originals; index++)
        {
            var bytes = new DbpfFixtureBuilder().AddContent([1], instanceLo: 1).AddContent([2], instanceLo: 2)
                .AddContent([3], instanceLo: (uint)(100 + index)).Build();
            for (var copy = 0; copy < 2; copy++) temp.WriteBytes(Path.Combine("Mods", $"{index}-{copy}.package"), bytes);
        }
        var settings = new DuplicateSettingsStore(temp.CreateDirectory("settings"));
        settings.Save(new(mods, []));
        var duplicateRuns = new CountingDuplicateRuns();
        var dialogs = new Dialogs(navigate);
        var actions = new NoFileActions();
        var model = new MainWindowViewModel(duplicateRuns, new Picker(), settings, actions, dialogs,
            settingsSupport: new SettingsSupportService(localApplicationData: temp.Path), conflictGroupingMemoryBudgetBytes: 4_000);
        await model.StartScanCommand.ExecuteAsync();
        model.ApplySuggestionsCommand.Execute(null);
        var groups = model.Groups.ToArray();
        var selectedCount = model.SelectedFileCount;
        var checkedSources = model.Sources.Select(source => (source.Path, source.IsEnabled)).ToArray();
        var hashes = Directory.GetFiles(mods).ToDictionary(path => path, File.ReadAllBytes);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        await model.StartConflictScanCommand.ExecuteAsync();

        Assert.AreEqual("failed", model.ConflictRunState);
        Assert.AreEqual("扫描没有完成", model.ConflictStatus);
        Assert.IsFalse(model.IsBusy);
        Assert.AreEqual(0, model.ConflictRows.Count);
        Assert.AreEqual(0, model.ConflictResources.Count);
        Assert.AreEqual(0, model.ConflictScripts.Count);
        Assert.IsFalse(model.HasConflictMatches);
        Assert.AreEqual(1, dialogs.Calls);
        Assert.AreEqual(originals == 3 ? "duplicate-copies" : "too-many-shared-resources", dialogs.Size!.Reason);
        Assert.AreEqual(originals == 3
            ? "⚠️文件夹内存在大量相同副本（3份），建议优先进行重复检测"
            : "⚠️所选文件冲突资源数过多，无法完整处理，请分批检测", dialogs.Size.WarningMessage);
        Assert.AreEqual(navigate, model.IsScannerVisible);
        Assert.AreEqual(!navigate, model.IsConflictVisible);
        Assert.AreEqual(2, duplicateRuns.Calls, "冲突扫描后只应切页，不能再次开始重复检测。");
        CollectionAssert.AreEqual(groups, model.Groups.ToArray());
        Assert.AreEqual(selectedCount, model.SelectedFileCount);
        CollectionAssert.AreEqual(checkedSources, model.Sources.Select(source => (source.Path, source.IsEnabled)).ToArray());
        Assert.AreEqual(0, actions.FileOperations);
        foreach (var (path, bytes) in hashes) CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        StringAssert.Contains(model.LatestScanTiming, originals == 3
            ? "资源匹配估算=33；合并副本后估算=6；上限=0"
            : "资源匹配估算=60；合并副本后估算=12；上限=0");
    });

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, false)]
    public Task WarningUsesExistingCustomDialogWithExactTextAndWorkingButtons(bool duplicateCopies, bool navigate) => WpfTestHost.RunAsync(() =>
    {
        var size = new ConflictGroupingSize(33, duplicateCopies ? 6 : 20, 15, duplicateCopies ? 3 : 10, 4_000, 3);
        var owner = new MainWindow(new MainWindowViewModel(new CountingDuplicateRuns(), new Picker(), new MemorySettings()));
        WpfTestHost.ShowOffScreen(owner);
        Application.Current.MainWindow = owner;
        Exception? inspectionFailure = null;
        var checkedDialog = false;
        owner.Dispatcher.BeginInvoke(() =>
        {
            var dialog = Application.Current.Windows.OfType<WarningDialog>().Single();
            try
            {
                dialog.UpdateLayout();
                Assert.AreSame(owner, dialog.Owner);
                Assert.AreEqual("扫描未完成", dialog.Title);
                var message = (TextBlock)dialog.FindName("MessageText");
                Assert.AreEqual(duplicateCopies
                    ? "文件夹内存在大量相同副本（3\u2060份），建议优先进行重复检测"
                    : "所选文件冲突资源数过多，无法完整处理，请分批检测", message.Text);
                var buttons = ConflictPageRenderTests.Descendants<Button>(dialog).Where(button => button.IsVisible).ToArray();
                CollectionAssert.AreEquivalent(duplicateCopies ? new[] { "关闭", "前往重复检测" } : new[] { "关闭" },
                    buttons.Select(button => (string)button.Content).ToArray());
                Assert.IsTrue(buttons.Single(button => (string)button.Content == "关闭").IsCancel);
                var button = buttons.Single(button => (string)button.Content == (navigate ? "前往重复检测" : "关闭"));
                var point = button.TranslatePoint(new Point(button.ActualWidth / 10, button.ActualHeight / 2), dialog);
                var hit = dialog.InputHitTest(point) as DependencyObject;
                Assert.IsTrue(Contains(button, hit), "按钮边缘的留白应命中按钮及其子控件。");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                checkedDialog = true;
            }
            catch (Exception exception)
            {
                inspectionFailure = exception;
                dialog.Close();
            }
        }, DispatcherPriority.ApplicationIdle);
        var result = new FileActionDialogService().ShowConflictGroupingWarning(size);
        if (inspectionFailure is not null) throw inspectionFailure;
        Assert.IsTrue(checkedDialog);
        Assert.AreEqual(navigate, result);
    });

    private static bool Contains(DependencyObject parent, DependencyObject? child)
    {
        while (child is not null)
        {
            if (ReferenceEquals(parent, child)) return true;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return false;
    }

    private sealed class Dialogs(bool navigate) : IFileActionDialogService
    {
        public int Calls { get; private set; }
        public ConflictGroupingSize? Size { get; private set; }
        public bool ShowConflictGroupingWarning(ConflictGroupingSize size) { Calls++; Size = size; return navigate; }
        public bool ConfirmDeletion(FileDeletionPreflight preflight) => throw new AssertFailedException("不应确认删除");
        public bool ConfirmFiles(string title, string message) => throw new AssertFailedException("不应确认文件操作");
        public void ShowProblems(string title, IReadOnlyList<FileActionFailure> failures) => throw new AssertFailedException("应使用专用规模警告");
    }

    private sealed class CountingDuplicateRuns : IDuplicateRunService
    {
        public int Calls { get; private set; }
        public Task<DuplicateReport> RunAsync(DuplicateRunInput input, IProgress<DuplicateScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return new DuplicateRunService(DuplicateScanner.CreateDefault()).RunAsync(input, progress, cancellationToken);
        }
    }

    private sealed class Picker : IFolderPickerService
    {
        public string? PickFolder(string title, string? initialDirectory = null) => throw new AssertFailedException("不应选取文件夹");
    }

    private sealed class MemorySettings : IDuplicateSettingsStore
    {
        public DuplicateSettings? Load() => null;
        public void Save(DuplicateSettings settings) => throw new AssertFailedException("不应保存设置");
    }

    private sealed class NoFileActions : IFileActionService
    {
        public int FileOperations { get; private set; }
        public bool CanUndoLastDelete => false;
        public void ClearLastDelete() { }
        public FileDeletionPreflight Preflight(IReadOnlyList<FileDeletionCandidate> candidates, IReadOnlyList<string> fullySelectedGroups)
        { FileOperations++; throw new AssertFailedException("不应执行文件操作"); }
        public Task<FileActionBatchResult> DeleteAsync(FileDeletionPreflight preflight, CancellationToken cancellationToken = default)
        { FileOperations++; throw new AssertFailedException("不应删除文件"); }
        public Task<FileActionBatchResult> UndoLastDeleteAsync(CancellationToken cancellationToken = default)
        { FileOperations++; throw new AssertFailedException("不应恢复文件"); }
    }
}
