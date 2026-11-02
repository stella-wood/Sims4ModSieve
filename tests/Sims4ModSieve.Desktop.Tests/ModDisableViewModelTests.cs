using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Infrastructure;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.SettingsViewModelTests;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;
using static Sims4ModSieve.Desktop.Tests.ConflictV2SessionTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ModDisableViewModelTests
{
    [TestMethod]
    public Task CancelledDisableAndRestoreKeepFilesAndLedgerUnchanged() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var path = NewFile(files.Root, "test.package");
        var accepted = false;
        var prompts = new List<string>();
        var model = Model(files, confirm: action => { prompts.Add(action); return accepted; });
        await ((AsyncRelayCommand)model.DisabledFiles.AddCommand!).ExecuteAsync();
        await model.DisableFilesAsync([]);
        Assert.AreEqual(0, prompts.Count);
        await model.DisableFilesAsync([path]);
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(0, model.DisabledFiles.Files.Count);
        accepted = true;
        await model.DisableFilesAsync([path]);
        var ledger = Path.Combine(files.Store.DataDirectory, "disabled-mods.json");
        var saved = File.ReadAllText(ledger);
        accepted = false;
        await RestoreAllSelectedAsync(model);
        Assert.IsTrue(File.Exists(path + ".off"));
        Assert.AreEqual(saved, File.ReadAllText(ledger));
        CollectionAssert.AreEqual(new[] { "禁用", "禁用", "启用" }, prompts);
        Assert.IsFalse(model.IsBusy);
    });

    [TestMethod]
    public Task DisabledFilesPersistAndSingleSelectedAndAllRestoreRemainIndependent() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var paths = Enumerable.Range(0, 4).Select(index => NewFile(files.Root,
            index % 2 == 0 ? $"test-{index}.package" : $"test-{index}.ts4script")).ToArray();
        var model = Model(files);
        await model.DisableFilesAsync(paths);
        StringAssert.Contains(model.FileManagementFeedback, "localthumbcache.package");
        Assert.IsTrue(paths.All(path => File.Exists(path + ".off") && !File.Exists(path)));
        var restarted = Model(files);
        Assert.AreEqual(4, restarted.DisabledFiles.Files.Count);
        await restarted.DisabledFiles.Files[0].RestoreCommand!.ExecuteAsync();
        Assert.AreEqual(1, paths.Count(File.Exists));
        restarted.DisabledFiles.ToggleMultiSelectCommand.Execute(null);
        restarted.DisabledFiles.Files[0].IsSelected = true;
        Assert.IsTrue(restarted.DisabledFiles.BulkRestoreCommand!.CanExecute(null));
        await restarted.DisabledFiles.BulkRestoreCommand!.ExecuteAsync();
        Assert.AreEqual(2, paths.Count(File.Exists));
        Assert.AreEqual(2, restarted.DisabledFiles.Files.Count);
        restarted.DisabledFiles.SelectAllCommand.Execute(null);
        restarted.DisabledFiles.ToggleMultiSelectCommand.Execute(null);
        Assert.IsFalse(restarted.DisabledFiles.HasSelection);
        await RestoreAllSelectedAsync(restarted);
        Assert.IsTrue(paths.All(File.Exists));
        Assert.AreEqual(0, restarted.DisabledFiles.Files.Count);
        Assert.IsFalse(restarted.DisabledFiles.BulkRestoreCommand!.CanExecute(null));
        Assert.IsFalse(restarted.DisabledFiles.IsMultiSelect, "最后一项启用后退出多选");
        Assert.IsFalse(restarted.DisabledFiles.HasMultiSelect);
        Assert.AreEqual(0, Model(files).DisabledFiles.Files.Count);
        StringAssert.Contains(restarted.FileManagementFeedback, "localthumbcache.package");
    });

    [TestMethod]
    public Task DisableAndRestoreUpdateBothCurrentSessionsWithoutRefillingNewScan() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        var paths = Enumerable.Range(0, 3).Select(index => NewFile(mods, $"test-{index}.package")).ToArray();
        files.Store.Save(new(mods, []));
        var model = Model(files);
        await model.StartScanCommand.ExecuteAsync();
        var report = await new DuplicateRunService(DuplicateScanner.CreateDefault()).RunAsync(new([new(mods, "Mods")], mods));
        var session = V2([], [], report);
        session.ToggleExpanded(session.Cards.Single().Key);
        await SetSessionAsync(model, session, "all");
        await model.DisableFilesAsync([paths[0]]);
        Assert.AreEqual(2, model.Groups.Single().Files.Count);
        Assert.IsTrue(session.Cards.SelectMany(card => card.Files).Any(file => file.Path == paths[0]));
        Assert.IsTrue(model.ConflictRows.Single(row => row.File?.Path == paths[0]).IsDisabled);
        await RestoreAllSelectedAsync(model);
        Assert.AreEqual(3, model.Groups.Single().Files.Count);
        Assert.IsTrue(session.Cards.SelectMany(card => card.Files).Any(file => file.Path == paths[0]));
        await model.DisableFilesAsync([paths[0]]);
        await model.StartScanCommand.ExecuteAsync();
        var next = V2([], [], report with { Groups = [] });
        await SetSessionAsync(model, next, "all");
        await RestoreAllSelectedAsync(model);
        Assert.IsTrue(File.Exists(paths[0]));
        Assert.AreEqual(2, model.Groups.Single().Files.Count, "恢复不能给新扫描补入旧成员");
        Assert.IsFalse(next.Cards.SelectMany(card => card.Files).Any(file => file.Path == paths[0]));
    });

    [TestMethod]
    public Task DeleteUndoDoesNotRefillStillDisabledMembers() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        var paths = Enumerable.Range(0, 4).Select(index => NewFile(mods, $"test-{index}.package")).ToArray();
        files.Store.Save(new(mods, []));
        var service = new FileActionService(new TemporaryRecycleBin(files.Folder("Recycle")), new PhysicalFileMetadataAccess(),
            new FileManagementStore(files.Store.DataDirectory));
        var model = Model(files, actions: service);
        await model.StartScanCommand.ExecuteAsync();
        await model.DisableFilesAsync([paths[0]]);
        model.Groups.Single().Files.Single(file => file.Path == paths[1]).IsSelected = true;
        await model.DeleteSelectedCommand.ExecuteAsync();
        Assert.IsTrue(model.CanUndoLastDelete);
        await model.UndoLastDeleteCommand.ExecuteAsync();
        Assert.AreEqual(3, model.Groups.Single().Files.Count);
        Assert.IsFalse(model.Groups.SelectMany(group => group.Files).Any(file => file.Path == paths[0]));
        Assert.IsTrue(File.Exists(paths[0] + ".off"));
        await RestoreAllSelectedAsync(model);
        Assert.AreEqual(4, model.Groups.Single().Files.Count);
        Assert.IsFalse(model.CanUndoLastDelete);
    });

    [TestMethod]
    public Task PartialFailureChangesOnlySuccessfulFilesAndBusyLocksCommands() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var a = NewFile(files.Root, "a.package");
        var b = NewFile(files.Root, "b.package");
        File.WriteAllText(b + ".off", "occupied");
        var dialogs = new Dialogs();
        var model = Model(files, dialogs: dialogs);
        var observedBusy = false;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(model.IsBusy) || !model.IsBusy) return;
            observedBusy = true;
            Assert.IsFalse(model.DisabledFiles.AddCommand!.CanExecute(null));
            Assert.IsFalse(model.OpenSettingsCommand.CanExecute(null));
            Assert.IsFalse(model.CanEditSources);
        };
        await model.DisableFilesAsync([a, b]);
        Assert.IsTrue(observedBusy);
        Assert.IsTrue(File.Exists(a + ".off"));
        Assert.IsTrue(File.Exists(b));
        Assert.AreEqual("occupied", File.ReadAllText(b + ".off"));
        Assert.IsTrue(dialogs.Problems.Any(problem => problem.Path == b));
        Assert.AreEqual(a, model.DisabledFiles.Files.Single().Path);
        File.WriteAllText(a, "replacement");
        await RestoreAllSelectedAsync(model);
        Assert.AreEqual("replacement", File.ReadAllText(a));
        Assert.IsTrue(File.Exists(a + ".off"));
        Assert.AreEqual(1, model.DisabledFiles.Files.Count);
        Assert.IsFalse(model.IsBusy);
        Assert.IsTrue(model.DisabledFiles.AddCommand!.CanExecute(null));
    });

    [TestMethod]
    public Task RestoreSuccessSurvivesLedgerCleanupAndRefreshFailure() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var path = NewFile(files.Root, "test.package");
        var dialogs = new Dialogs();
        var model = Model(files, dialogs: dialogs);
        await model.DisableFilesAsync([path]);
        var ledger = Path.Combine(files.Store.DataDirectory, "disabled-mods.json");
        using (File.Open(ledger, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await RestoreAllSelectedAsync(model);
            Assert.IsTrue(File.Exists(path));
            Assert.IsFalse(File.Exists(path + ".off"));
            Assert.AreEqual(0, model.DisabledFiles.Files.Count);
            StringAssert.Contains(model.FileManagementFeedback, "已启用 1 个文件");
            StringAssert.Contains(model.FileManagementFeedback, "localthumbcache.package");
            Assert.IsTrue(dialogs.Problems.Count > 0, "清单清理失败需单独报告");
        }
        await model.RefreshFileManagementAsync();
        Assert.AreEqual(0, model.DisabledFiles.Files.Count);
    });

    [TestMethod]
    public Task DisabledRowDeleteAndHistoryRestoreKeepReplacementAndScanResultsUnchanged() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        var paths = new[] { "SpringGarden.package", "CozyLighting.package", "TownStories.package" }
            .Select(name => NewFile(mods, name)).ToArray();
        files.Store.Save(new(mods, []));
        var disabled = new ModDisableService(files.Store.DataDirectory);
        var recycle = new TemporaryRecycleBin(files.Folder("Recycle"));
        var actions = new FileActionService(recycle, new PhysicalFileMetadataAccess(), new FileManagementStore(files.Store.DataDirectory), disabled);
        var model = Model(files, actions: actions, disableService: disabled);
        await model.StartScanCommand.ExecuteAsync();
        await model.DisableFilesAsync([paths[0]]);
        File.WriteAllText(paths[0], "replacement stays here");
        var window = new MainWindow(model) { Width = 1180, Height = 900 };
        window.Show();
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("files");
        await model.FileManagementRefreshTask;
        await WpfTestHost.LayoutAsync(window);
        var page = Descendants<SettingsPage>(window).Single();
        var panel = (FrameworkElement)page.FindName("FileManagementPanel");
        var row = model.DisabledFiles.Files.Single();
        var delete = Descendants<Button>(panel).Single(button => button.IsVisible && button.Command == row.DeleteCommand);
        Assert.AreEqual(paths[0], delete.ToolTip as string);
        Assert.IsNotNull(delete.InputHitTest(new Point(delete.ActualWidth * .1, delete.ActualHeight * .1)));
        ((IInvokeProvider)new ButtonAutomationPeer(delete).GetPattern(PatternInterface.Invoke)).Invoke();
        await WaitForAsync(() => !model.IsBusy && model.DisabledFiles.Files.Count == 0 && model.RecentDeletedFiles.Files.Count == 1);
        await WpfTestHost.LayoutAsync(window);
        CollectionAssert.AreEqual(new[] { paths[0] + ".off" }, recycle.Deleted.ToArray());
        Assert.AreEqual("replacement stays here", File.ReadAllText(paths[0]));
        Assert.IsFalse(File.Exists(paths[0] + ".off"));
        Assert.AreEqual(2, model.Groups.Single().Files.Count);
        SaveReview(window, "disabled-actions-deleted.png");
        var returned = model.RecentDeletedFiles.Files.Single();
        Assert.AreEqual(paths[0] + ".off", returned.Path);
        await returned.RestoreCommand!.ExecuteAsync();
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(1, model.DisabledFiles.Files.Count);
        Assert.AreEqual(paths[0], model.DisabledFiles.Files.Single().Path);
        Assert.AreEqual(0, model.RecentDeletedFiles.Files.Count);
        Assert.IsTrue(File.Exists(paths[0] + ".off"));
        Assert.AreEqual("replacement stays here", File.ReadAllText(paths[0]));
        Assert.AreEqual(2, model.Groups.Single().Files.Count, "恢复到.off不能回填启用状态的扫描成员");
        Assert.IsFalse(model.CanUndoLastDelete);
        SaveReview(window, "disabled-actions-delete-restored.png");
        var restarted = Model(files, disableService: new ModDisableService(files.Store.DataDirectory));
        Assert.AreEqual(paths[0], restarted.DisabledFiles.Files.Single().Path);
        window.Close();
    });

    [TestMethod]
    public Task DisabledDeleteCancellationAndPreflightFailuresKeepFilesAndLedger() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var path = NewFile(files.Root, "test.package");
        var disabled = new ModDisableService(files.Store.DataDirectory);
        var recycle = new TemporaryRecycleBin(files.Folder("Recycle"));
        var actions = new FileActionService(recycle, new PhysicalFileMetadataAccess(), new FileManagementStore(files.Store.DataDirectory), disabled);
        var dialogs = new DeleteDialogs { Confirm = _ => false };
        var model = Model(files, actions: actions, dialogs: dialogs, disableService: disabled);
        await model.DisableFilesAsync([path]);
        var ledger = Path.Combine(files.Store.DataDirectory, "disabled-mods.json");
        var before = File.ReadAllText(ledger);
        var row = model.DisabledFiles.Files.Single();
        await row.DeleteCommand!.ExecuteAsync();
        Assert.AreEqual(1, dialogs.Confirmations.Count);
        Assert.AreEqual(path + ".off", dialogs.Confirmations.Single().Candidates.Single().Path);
        Assert.AreEqual(before, File.ReadAllText(ledger));
        recycle.CanRecycleFiles = false;
        await row.DeleteCommand!.ExecuteAsync();
        Assert.AreEqual(1, dialogs.Confirmations.Count, "不支持回收站时预检失败，不再确认");
        Assert.IsTrue(dialogs.Problems.Count > 0);
        recycle.CanRecycleFiles = true;
        dialogs.Confirm = _ => { File.WriteAllText(path + ".off", "changed after confirmation"); return true; };
        await row.DeleteCommand!.ExecuteAsync();
        Assert.AreEqual(2, dialogs.Confirmations.Count);
        Assert.AreEqual(0, recycle.Deleted.Count, "确认后再次复核文件戳");
        Assert.IsTrue(File.Exists(path + ".off"));
        Assert.AreEqual(before, File.ReadAllText(ledger));
        Assert.AreEqual(1, model.DisabledFiles.Files.Count);
        Assert.IsFalse(model.IsBusy);
    });

    [TestMethod]
    public Task DisabledDeleteUndoReplacesPreviousBatchWithoutRestoringScanMembers() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        var paths = Enumerable.Range(0, 4).Select(index => NewFile(mods, $"test-{index}.package")).ToArray();
        files.Store.Save(new(mods, []));
        var disabled = new ModDisableService(files.Store.DataDirectory);
        var actions = new FileActionService(new TemporaryRecycleBin(files.Folder("Recycle")), new PhysicalFileMetadataAccess(),
            new FileManagementStore(files.Store.DataDirectory), disabled);
        var model = Model(files, actions: actions, disableService: disabled);
        await model.StartScanCommand.ExecuteAsync();
        model.Groups.Single().Files.Single(file => file.Path == paths[1]).IsSelected = true;
        await model.DeleteSelectedCommand.ExecuteAsync();
        await model.DisableFilesAsync([paths[0]]);
        await model.DisabledFiles.Files.Single().DeleteCommand!.ExecuteAsync();
        Assert.IsTrue(model.CanUndoLastDelete);
        Assert.AreEqual(2, model.RecentDeletedFiles.Files.Count);
        await model.UndoLastDeleteCommand.ExecuteAsync();
        Assert.IsTrue(File.Exists(paths[0] + ".off"));
        Assert.IsFalse(File.Exists(paths[0]));
        Assert.IsFalse(File.Exists(paths[1]), "撤回只恢复最新的.off删除批次");
        Assert.AreEqual(2, model.Groups.Single().Files.Count);
        Assert.AreEqual(paths[0], model.DisabledFiles.Files.Single().Path);
        Assert.AreEqual(paths[1], model.RecentDeletedFiles.Files.Single().Path);
        Assert.IsFalse(model.CanUndoLastDelete);
    });

    [TestMethod]
    public Task DisabledSectionButtonsRenderAndInvokeAtMinimumWindowSize() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        var paths = new[] { "SpringGarden.package", "TownStories.ts4script", "TownStories.package" }
            .Select(name => NewFile(mods, name)).ToArray();
        var dialogs = new Dialogs();
        var model = Model(files, pick: () => paths.Take(2).ToArray(), dialogs: dialogs);
        var window = new MainWindow(model) { Width = 1180, Height = 780 };
        window.Show();
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("files");
        await model.FileManagementRefreshTask;
        await WpfTestHost.LayoutAsync(window);
        var page = Descendants<SettingsPage>(window).Single();
        var panel = (FrameworkElement)page.FindName("FileManagementPanel");
        Assert.IsTrue(Descendants<TextBlock>(panel).Any(text => text.IsVisible && text.Text == "已禁用"));
        Assert.IsFalse(Descendants<TextBlock>(panel).Any(text => text.IsVisible && text.Text == "隔离区"));
        var add = Descendants<Button>(panel).Single(button => button.IsVisible && button.Command == model.DisabledFiles.AddCommand);
        var multi = Descendants<Button>(panel).Single(button => button.Command == model.DisabledFiles.ToggleMultiSelectCommand);
        var refresh = Descendants<Button>(panel).Single(button => button.IsVisible && button.Command == model.RefreshFileManagementCommand);
        Assert.IsFalse(multi.IsVisible, "已禁用空态只保留添加入口");
        Assert.IsFalse(Descendants<Button>(panel).Any(button => button.IsVisible && ReferenceEquals(button.DataContext, model.DisabledFiles)
            && button.Content as string == "恢复全部"));
        Assert.IsFalse(Descendants<Button>(panel).Any(button => button.IsVisible && button.Command == model.RecentDeletedFiles.ToggleMultiSelectCommand));
        Assert.IsFalse(Descendants<Button>(panel).Any(button => button.IsVisible && button.Command == model.IgnoredFiles.ToggleMultiSelectCommand));
        Assert.AreEqual(1, Descendants<Button>(panel).Count(button => button.IsVisible && button.Content as string == "刷新"));
        Assert.AreEqual("刷新删除与忽略", System.Windows.Automation.AutomationProperties.GetName(refresh));
        SaveReview(window, "disabled-actions-empty.png");
        window.Width = 920; window.Height = 640;
        await WpfTestHost.LayoutAsync(window);
        foreach (var button in Descendants<Button>(panel).Where(button => button.IsVisible
            && (ReferenceEquals(button.DataContext, model.DisabledFiles) || ReferenceEquals(button, refresh))))
        {
            Assert.IsTrue(new Rect(page.RenderSize).Contains(button.TransformToAncestor(page).TransformBounds(new Rect(button.RenderSize))),
                $"{button.Content} 不应挤出页面");
            if (button.IsEnabled) Assert.IsNotNull(button.InputHitTest(new Point(button.ActualWidth * .1, button.ActualHeight * .1)));
        }
        ((IInvokeProvider)new ButtonAutomationPeer(add).GetPattern(PatternInterface.Invoke)).Invoke();
        await WaitForAsync(() => !model.IsBusy && model.DisabledFiles.Files.Count == 2);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(multi.IsVisible);
        foreach (var point in new[] { new Point(.1, .1), new Point(.9, .1), new Point(.1, .9), new Point(.9, .9) })
        {
            var hit = refresh.InputHitTest(new Point(refresh.ActualWidth * point.X, refresh.ActualHeight * point.Y)) as DependencyObject;
            while (hit is not null && !ReferenceEquals(hit, refresh)) hit = VisualTreeHelper.GetParent(hit);
            Assert.AreSame(refresh, hit, "刷新按钮的矩形留白应参与命中");
        }
        new ModDisableService(files.Store.DataDirectory).Disable([paths[2]]);
        WpfTestHost.PlaceOnScreen(window, 920, 640);
        await WpfTestHost.LayoutAsync(window);
        await WpfTestHost.ClickAsync(refresh, new Point(refresh.ActualWidth * .1, refresh.ActualHeight * .1));
        await WaitForAsync(() => !model.IsBusy && model.DisabledFiles.Files.Count == 3);
        window.Width = 1180; window.Height = 780;
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(paths.All(path => File.Exists(path + ".off")));
        Assert.IsTrue(Descendants<TextBlock>(panel).Any(text => text.IsVisible && text.Text == paths[0] && text.ToolTip as string == paths[0]));
        Assert.IsTrue(model.DisabledFiles.Files.All(row => row.CanDelete && row.DeleteCommand is not null));
        Assert.IsTrue(model.DisabledFiles.Files.All(row => row.RestoreText == "启用"));
        Assert.IsTrue(Descendants<Button>(panel).Any(button => button.IsVisible && button.Content as string == "启用"
            && button.DataContext is ManagedFileRow row && model.DisabledFiles.Files.Contains(row)));
        Assert.IsTrue(Descendants<Button>(panel).Any(button => button.IsVisible && button.Content as string == "删除"
            && button.DataContext is ManagedFileRow row && model.DisabledFiles.Files.Contains(row)));
        SaveReview(window, "disabled-actions-populated.png");
        ((IInvokeProvider)new ButtonAutomationPeer(multi).GetPattern(PatternInterface.Invoke)).Invoke();
        await WpfTestHost.LayoutAsync(window);
        var selectAll = Descendants<Button>(panel).Single(button => button.IsVisible && button.Command == model.DisabledFiles.SelectAllCommand);
        var clearSelection = Descendants<Button>(panel).Single(button => button.IsVisible && button.Command == model.DisabledFiles.ClearSelectionCommand);
        var bottom = Descendants<DockPanel>(panel).Single(control => ReferenceEquals(control.DataContext, model.DisabledFiles));
        var bulkDelete = Descendants<Button>(bottom).Single(button => button.Command == model.DisabledFiles.BulkDeleteCommand);
        Assert.IsTrue(bulkDelete.IsVisible);
        Assert.AreEqual("删除", bulkDelete.Content);
        Assert.IsFalse(bulkDelete.IsEnabled, "未勾选文件时不可批量删除");
        Assert.IsFalse(add.IsVisible, "已禁用多选时隐藏添加入口");
        Assert.IsFalse(Descendants<Button>(bottom).Any(button => button.IsVisible && (button.Command == model.DisabledFiles.SelectAllCommand
            || button.Command == model.DisabledFiles.ClearSelectionCommand)), "全选和清空勾选不应留在底部");
        Assert.AreSame(VisualTreeHelper.GetParent(selectAll), VisualTreeHelper.GetParent(clearSelection));
        var disabledList = Descendants<ListBox>(panel).Single(control => ReferenceEquals(control.DataContext, model.DisabledFiles));
        Assert.IsLessThan(disabledList.TranslatePoint(new Point(), page).Y, selectAll.TranslatePoint(new Point(), page).Y,
            "全选入口应在文件列表上方");
        window.Width = 920; window.Height = 640;
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(selectAll.TranslatePoint(new Point(), page).X < clearSelection.TranslatePoint(new Point(), page).X,
            "全选应在清空勾选左侧");
        Assert.IsTrue(clearSelection.TranslatePoint(new Point(), page).X < multi.TranslatePoint(new Point(), page).X,
            "取消多选应在清空勾选右侧");
        foreach (var button in new[] { multi, selectAll, clearSelection })
        {
            Assert.IsTrue(new Rect(page.RenderSize).Contains(button.TransformToAncestor(page).TransformBounds(new Rect(button.RenderSize))),
                $"{button.Content} 不应挤出页面");
            Assert.IsNotNull(button.InputHitTest(new Point(button.ActualWidth * .1, button.ActualHeight * .1)));
        }
        ((IInvokeProvider)new ButtonAutomationPeer(selectAll).GetPattern(PatternInterface.Invoke)).Invoke();
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(3, model.DisabledFiles.Files.Count(row => row.IsSelected));
        ((IInvokeProvider)new ButtonAutomationPeer(clearSelection).GetPattern(PatternInterface.Invoke)).Invoke();
        await WpfTestHost.LayoutAsync(window);
        Assert.IsFalse(model.DisabledFiles.HasSelection);
        ((IInvokeProvider)new ButtonAutomationPeer(selectAll).GetPattern(PatternInterface.Invoke)).Invoke();
        await WpfTestHost.LayoutAsync(window);
        ((IInvokeProvider)new ButtonAutomationPeer(multi).GetPattern(PatternInterface.Invoke)).Invoke();
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(add.IsVisible, "退出已禁用多选后恢复添加入口");
        Assert.IsFalse(selectAll.IsVisible);
        Assert.IsFalse(clearSelection.IsVisible);
        Assert.IsFalse(model.DisabledFiles.HasSelection, "退出多选应清空选择");
        ((IInvokeProvider)new ButtonAutomationPeer(multi).GetPattern(PatternInterface.Invoke)).Invoke();
        window.Width = 1180; window.Height = 780;
        model.DisabledFiles.Files.Single(row => row.Path == paths[0]).IsSelected = true;
        model.DisabledFiles.Files.Single(row => row.Path == paths[1]).IsSelected = true;
        await WpfTestHost.LayoutAsync(window);
        var bulk = Descendants<Button>(panel).Single(button => button.IsVisible && button.Command == model.DisabledFiles.BulkRestoreCommand);
        Assert.AreEqual("启用所选", bulk.Content);
        Assert.IsTrue(Descendants<Button>(bottom).Contains(bulk), "启用所选仍位于列表下方");
        Assert.IsTrue(Descendants<TextBlock>(bottom).Any(text => text.IsVisible && text.Text == model.DisabledFiles.SelectionText));
        Assert.IsTrue(bulk.IsEnabled);
        Assert.IsTrue(bulkDelete.IsEnabled);
        Assert.IsTrue(bulk.TranslatePoint(new Point(), page).X < bulkDelete.TranslatePoint(new Point(), page).X,
            "启用所选在删除左侧");
        Assert.IsNotNull(bulkDelete.InputHitTest(new Point(bulkDelete.ActualWidth * .1, bulkDelete.ActualHeight * .1)));
        Assert.AreEqual(2, model.DisabledFiles.Files.Count(row => row.IsSelected));
        SaveReview(window, "disabled-actions-multiselect.png");
        File.WriteAllText(paths[1], "same-name placeholder");
        ((IInvokeProvider)new ButtonAutomationPeer(bulk).GetPattern(PatternInterface.Invoke)).Invoke();
        await WaitForAsync(() => !model.IsBusy && model.DisabledFiles.Files.Count == 2);
        Assert.IsTrue(File.Exists(paths[0]));
        Assert.IsFalse(File.Exists(paths[0] + ".off"));
        Assert.IsTrue(File.Exists(paths[1] + ".off"));
        Assert.AreEqual("same-name placeholder", File.ReadAllText(paths[1]));
        Assert.IsTrue(dialogs.Problems.Any(problem => problem.Path == paths[1]));
        StringAssert.Contains(model.FileManagementFeedback, "已启用 1 个文件");
        window.Height = 900;
        await WpfTestHost.LayoutAsync(window);
        var feedback = Descendants<TextBlock>(panel).Single(text => text.IsVisible && text.Text == model.FileManagementFeedback);
        feedback.BringIntoView();
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(new Rect(page.RenderSize).Contains(feedback.TransformToAncestor(page).TransformBounds(new Rect(feedback.RenderSize))),
            "部分启用截图应显示页面反馈");
        var blockedName = Descendants<TextBlock>(panel).Single(text => text.IsVisible && text.Text == Path.GetFileName(paths[1]));
        Assert.IsTrue(new Rect(page.RenderSize).Contains(blockedName.TransformToAncestor(page).TransformBounds(new Rect(blockedName.RenderSize))),
            "部分启用截图应显示保留的失败条目");
        SaveReview(window, "disabled-actions-partial-restore.png");
        File.Delete(paths[1]);
        ((IInvokeProvider)new ButtonAutomationPeer(selectAll).GetPattern(PatternInterface.Invoke)).Invoke();
        await WpfTestHost.LayoutAsync(window);
        ((IInvokeProvider)new ButtonAutomationPeer(bulk).GetPattern(PatternInterface.Invoke)).Invoke();
        await WaitForAsync(() => !model.IsBusy && model.DisabledFiles.Files.Count == 0);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(paths.All(File.Exists));
        Assert.IsFalse(model.DisabledFiles.IsMultiSelect);
        Assert.IsFalse(multi.IsVisible);
        Assert.IsFalse(bulk.IsVisible, "最后一项启用后隐藏批量操作区");
        Assert.IsTrue(add.IsVisible);
        Assert.IsFalse(selectAll.IsVisible);
        Assert.IsFalse(clearSelection.IsVisible);
        window.Close();
    });

    [TestMethod]
    public Task DisabledBulkDeleteUsesOneBatchAndUndoKeepsOffSuffix() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var paths = new[] { "SpringGarden.package", "TownStories.ts4script", "TownStories.package" }
            .Select(name => NewFile(files.Root, name)).ToArray();
        var disabled = new ModDisableService(files.Store.DataDirectory);
        var recycle = new TemporaryRecycleBin(files.Folder("Recycle"));
        var actions = new FileActionService(recycle, new PhysicalFileMetadataAccess(), new FileManagementStore(files.Store.DataDirectory), disabled);
        var dialogs = new DeleteDialogs();
        var model = Model(files, actions: actions, dialogs: dialogs, disableService: disabled);
        await model.DisableFilesAsync(paths);
        File.WriteAllText(paths[0], "replacement remains");
        Assert.IsFalse(model.DisabledFiles.BulkDeleteCommand!.CanExecute(null));
        await model.DisabledFiles.BulkDeleteCommand.ExecuteAsync();
        Assert.AreEqual(0, dialogs.Confirmations.Count);
        model.DisabledFiles.ToggleMultiSelectCommand.Execute(null);
        foreach (var row in model.DisabledFiles.Files.Where(row => paths.Take(2).Contains(row.Path))) row.IsSelected = true;
        var window = new MainWindow(model) { Width = 1180, Height = 900 };
        window.Show();
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("files");
        await model.FileManagementRefreshTask;
        await WpfTestHost.LayoutAsync(window);
        var page = Descendants<SettingsPage>(window).Single();
        var panel = (FrameworkElement)page.FindName("FileManagementPanel");
        var delete = Descendants<Button>(panel).Single(button => button.IsVisible && button.Command == model.DisabledFiles.BulkDeleteCommand);
        Assert.IsTrue(delete.IsEnabled);
        ((IInvokeProvider)new ButtonAutomationPeer(delete).GetPattern(PatternInterface.Invoke)).Invoke();
        await WaitForAsync(() => !model.IsBusy && model.RecentDeletedFiles.Files.Count == 2);
        Assert.AreEqual(1, dialogs.Confirmations.Count, "一次多选只确认整个批次");
        CollectionAssert.AreEquivalent(paths.Take(2).Select(path => path + ".off").ToArray(),
            dialogs.Confirmations.Single().Candidates.Select(candidate => candidate.Path).ToArray());
        CollectionAssert.AreEquivalent(paths.Take(2).Select(path => path + ".off").ToArray(), recycle.Deleted);
        Assert.AreEqual(paths[2], model.DisabledFiles.Files.Single().Path);
        Assert.IsTrue(File.Exists(paths[2] + ".off"), "未勾选文件保留");
        Assert.AreEqual("replacement remains", File.ReadAllText(paths[0]));
        Assert.IsTrue(model.CanUndoLastDelete);
        Assert.IsFalse(model.DisabledFiles.BulkDeleteCommand.CanExecute(null));
        await WpfTestHost.LayoutAsync(window);
        SaveReview(window, "disabled-bulk-deleted.png");
        await model.UndoLastDeleteCommand.ExecuteAsync();
        Assert.IsTrue(paths.All(path => File.Exists(path + ".off")));
        Assert.AreEqual(3, model.DisabledFiles.Files.Count);
        Assert.AreEqual(0, model.RecentDeletedFiles.Files.Count);
        Assert.IsFalse(model.CanUndoLastDelete);
        Assert.IsFalse(File.Exists(paths[1]), "撤回只还原.off，不启用文件");
        Assert.AreEqual("replacement remains", File.ReadAllText(paths[0]));
        model.DisabledFiles.SelectAllCommand.Execute(null);
        await model.DisabledFiles.BulkDeleteCommand.ExecuteAsync();
        Assert.AreEqual(0, model.DisabledFiles.Files.Count);
        Assert.IsFalse(model.DisabledFiles.IsMultiSelect);
        model.RecentDeletedFiles.ToggleMultiSelectCommand.Execute(null);
        model.RecentDeletedFiles.SelectAllCommand.Execute(null);
        await model.RecentDeletedFiles.BulkRestoreCommand!.ExecuteAsync();
        Assert.IsTrue(paths.All(path => File.Exists(path + ".off")));
        Assert.AreEqual(3, model.DisabledFiles.Files.Count);
        Assert.IsFalse(model.RecentDeletedFiles.IsMultiSelect);
        Assert.IsFalse(model.RecentDeletedFiles.HasMultiSelect);
        Assert.AreEqual("replacement remains", File.ReadAllText(paths[0]));
        window.Close();
    });

    [TestMethod]
    public Task DisabledBulkDeleteCancellationAndPartialFailureKeepRemainingSelection() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var paths = Enumerable.Range(0, 3).Select(index => NewFile(files.Root, $"test-{index}.package")).ToArray();
        var disabled = new ModDisableService(files.Store.DataDirectory);
        var recycle = new TemporaryRecycleBin(files.Folder("Recycle"));
        var actions = new FileActionService(recycle, new PhysicalFileMetadataAccess(), new FileManagementStore(files.Store.DataDirectory), disabled);
        var dialogs = new DeleteDialogs { Confirm = _ => false };
        var model = Model(files, actions: actions, dialogs: dialogs, disableService: disabled);
        await model.DisableFilesAsync(paths);
        model.DisabledFiles.ToggleMultiSelectCommand.Execute(null);
        foreach (var row in model.DisabledFiles.Files.Where(row => paths.Take(2).Contains(row.Path))) row.IsSelected = true;
        var ledger = Path.Combine(files.Store.DataDirectory, "disabled-mods.json");
        var before = File.ReadAllText(ledger);
        await model.DisabledFiles.BulkDeleteCommand!.ExecuteAsync();
        Assert.AreEqual(1, dialogs.Confirmations.Count);
        Assert.AreEqual(2, model.DisabledFiles.Files.Count(row => row.IsSelected));
        Assert.AreEqual(before, File.ReadAllText(ledger));
        Assert.AreEqual(0, recycle.Deleted.Count);
        var observedBusy = false;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(model.IsBusy) || !model.IsBusy) return;
            observedBusy = true;
            Assert.IsFalse(model.DisabledFiles.BulkDeleteCommand.CanExecute(null));
            Assert.IsFalse(model.DisabledFiles.BulkRestoreCommand!.CanExecute(null));
            Assert.IsFalse(model.DisabledFiles.AddCommand!.CanExecute(null));
            Assert.IsTrue(model.DisabledFiles.Files.All(row => !row.DeleteCommand!.CanExecute(null)));
        };
        dialogs.Confirm = _ => true;
        recycle.FailDeletePath = paths[1] + ".off";
        await model.DisabledFiles.BulkDeleteCommand.ExecuteAsync();
        Assert.IsTrue(observedBusy);
        Assert.AreEqual(2, dialogs.Confirmations.Count);
        CollectionAssert.AreEquivalent(paths.Skip(1).ToArray(), model.DisabledFiles.Files.Select(row => row.Path).ToArray());
        Assert.AreEqual(paths[1], model.DisabledFiles.Files.Single(row => row.IsSelected).Path);
        Assert.AreEqual(paths[0] + ".off", model.RecentDeletedFiles.Files.Single().Path);
        Assert.IsTrue(paths.Skip(1).All(path => File.Exists(path + ".off")));
        Assert.IsTrue(dialogs.Problems.Any(problem => problem.Path == paths[1] + ".off"));
        Assert.IsTrue(model.DisabledFiles.IsMultiSelect);
        Assert.IsTrue(model.DisabledFiles.BulkDeleteCommand.CanExecute(null));
        await model.UndoLastDeleteCommand.ExecuteAsync();
        Assert.IsTrue(paths.All(path => File.Exists(path + ".off")));
        Assert.AreEqual(0, model.RecentDeletedFiles.Files.Count);
    });

    [TestMethod]
    public Task FileManagementSectionsShareMultiSelectAndAddVisibility() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var paths = new[] { "SpringGarden.package", "TownStories.ts4script", "CozyLighting.package", "FreshWalls.package" }
            .Select(name => NewFile(files.Root, name)).ToArray();
        var deletedPaths = new[] { "OldGarden.package", "OldStories.ts4script" }.Select(name => NewFile(files.Root, name)).ToArray();
        var disabled = new ModDisableService(files.Store.DataDirectory);
        var actions = new FileActionService(new TemporaryRecycleBin(files.Folder("Recycle")), new PhysicalFileMetadataAccess(),
            new FileManagementStore(files.Store.DataDirectory), disabled);
        await actions.DeleteAsync(actions.Preflight(deletedPaths.Select(path => new FileDeletionCandidate(path, 4,
            File.GetLastWriteTimeUtc(path), "", ExpectedIdentity: PhysicalFileIdentity.Read(path))).ToArray(), []));
        var model = Model(files, actions: actions, disableService: disabled);
        await model.DisableFilesAsync(paths.Take(2).ToArray());
        model.AddIgnoredFiles(paths.Skip(2).ToArray());
        var window = new MainWindow(model) { Width = 1180, Height = 1100 };
        window.Show();
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("files");
        await model.FileManagementRefreshTask;
        await WpfTestHost.LayoutAsync(window);
        var page = Descendants<SettingsPage>(window).Single();
        var panel = (FrameworkElement)page.FindName("FileManagementPanel");
        foreach (var section in new[] { model.DisabledFiles, model.RecentDeletedFiles, model.IgnoredFiles })
        {
            var list = Descendants<ListBox>(panel).Single(control => ReferenceEquals(control.DataContext, section));
            foreach (var row in section.Files)
            {
                if (section.HasRestore)
                {
                    var restore = Descendants<Button>(list).Single(button => button.IsVisible && button.Command == row.RestoreCommand);
                    Assert.AreEqual(section.IsDisabled ? "启用" : "恢复", restore.Content);
                }
                if (section.IsIgnored)
                {
                    var remove = Descendants<Button>(list).Single(button => button.IsVisible && button.Command == row.RemoveCommand);
                    var delete = Descendants<Button>(list).Single(button => button.IsVisible && button.Command == row.DeleteCommand);
                    Assert.AreEqual("取消忽略", remove.Content);
                    Assert.AreEqual("删除", delete.Content);
                    Assert.IsTrue(remove.TranslatePoint(new Point(), page).X < delete.TranslatePoint(new Point(), page).X,
                        "已忽略行的取消忽略应在删除左侧");
                    Assert.IsNotNull(delete.InputHitTest(new Point(delete.ActualWidth * .1, delete.ActualHeight * .1)));
                }
            }
            var multi = Descendants<Button>(panel).Single(button => button.Command == section.ToggleMultiSelectCommand);
            Assert.IsTrue(multi.IsVisible);
            ((IInvokeProvider)new ButtonAutomationPeer(multi).GetPattern(PatternInterface.Invoke)).Invoke();
            await WpfTestHost.LayoutAsync(window);
            var selectAll = Descendants<Button>(panel).Single(button => button.IsVisible && button.Command == section.SelectAllCommand);
            var clear = Descendants<Button>(panel).Single(button => button.IsVisible && button.Command == section.ClearSelectionCommand);
            var bottom = Descendants<DockPanel>(panel).Single(control => ReferenceEquals(control.DataContext, section));
            var primary = Descendants<Button>(bottom).Single(button => button.IsVisible && (section.IsIgnored
                ? button.Command == section.BulkRemoveCommand : button.Command == section.BulkRestoreCommand));
            Assert.AreEqual(section.IsDisabled ? "启用所选" : section.IsIgnored ? "取消忽略" : "恢复所选", primary.Content);
            if (section.HasDelete)
            {
                var delete = Descendants<Button>(bottom).Single(button => button.IsVisible && button.Command == section.BulkDeleteCommand);
                Assert.AreEqual("删除", delete.Content);
                Assert.IsFalse(delete.IsEnabled, "空选时不可批量删除");
                Assert.IsTrue(primary.TranslatePoint(new Point(), page).X < delete.TranslatePoint(new Point(), page).X);
            }
            else Assert.IsFalse(Descendants<Button>(bottom).Any(button => button.IsVisible && button.Content as string == "删除"));
            Assert.IsTrue(selectAll.TranslatePoint(new Point(), page).Y < list.TranslatePoint(new Point(), page).Y);
            Assert.IsTrue(selectAll.TranslatePoint(new Point(), page).X < clear.TranslatePoint(new Point(), page).X);
            Assert.IsTrue(clear.TranslatePoint(new Point(), page).X < multi.TranslatePoint(new Point(), page).X);
            Assert.IsFalse(Descendants<Button>(bottom).Any(button => button.Command == section.SelectAllCommand || button.Command == section.ClearSelectionCommand));
            if (section.HasAdd)
                Assert.IsFalse(Descendants<Button>(panel).Single(button => button.Command == section.AddCommand).IsVisible);
            foreach (var button in new[] { multi, selectAll, clear })
                Assert.IsNotNull(button.InputHitTest(new Point(button.ActualWidth * .1, button.ActualHeight * .1)));
            ((IInvokeProvider)new ButtonAutomationPeer(selectAll).GetPattern(PatternInterface.Invoke)).Invoke();
            await WpfTestHost.LayoutAsync(window);
            Assert.AreEqual(section.Files.Count, section.Files.Count(row => row.IsSelected));
            foreach (var button in Descendants<Button>(bottom).Where(button => button.IsVisible))
            {
                Assert.IsTrue(button.IsEnabled);
                Assert.IsNotNull(button.InputHitTest(new Point(button.ActualWidth * .1, button.ActualHeight * .1)));
            }
            ((IInvokeProvider)new ButtonAutomationPeer(clear).GetPattern(PatternInterface.Invoke)).Invoke();
            await WpfTestHost.LayoutAsync(window);
            Assert.IsFalse(section.HasSelection);
            Assert.IsTrue(Descendants<Button>(bottom).Where(button => button.IsVisible).All(button => !button.IsEnabled));
            ((IInvokeProvider)new ButtonAutomationPeer(selectAll).GetPattern(PatternInterface.Invoke)).Invoke();
            await WpfTestHost.LayoutAsync(window);
        }
        await WpfTestHost.LayoutAsync(window);
        SaveReview(window, "file-sections-multiselect.png");
        window.Width = 920; window.Height = 640;
        await WpfTestHost.LayoutAsync(window);
        foreach (var section in new[] { model.RecentDeletedFiles, model.IgnoredFiles })
        {
            var multi = Descendants<Button>(panel).Single(button => button.Command == section.ToggleMultiSelectCommand);
            multi.BringIntoView();
            await WpfTestHost.LayoutAsync(window);
            foreach (var button in Descendants<Button>(panel).Where(button => ReferenceEquals(button.DataContext, section) && button.IsVisible))
            {
                var bounds = button.TransformToAncestor(page).TransformBounds(new Rect(button.RenderSize));
                Assert.IsTrue(bounds.Left >= 0 && bounds.Right <= page.ActualWidth, "下方多选操作不应横向溢出");
            }
        }
        window.Width = 1180; window.Height = 1100;
        await WpfTestHost.LayoutAsync(window);
        var ignoredBottom = Descendants<DockPanel>(panel).Single(control => ReferenceEquals(control.DataContext, model.IgnoredFiles));
        ignoredBottom.BringIntoView();
        await WpfTestHost.LayoutAsync(window);
        SaveReview(window, "file-sections-multiselect-lower.png");
        model.IgnoredFiles.ClearSelectionCommand.Execute(null);
        model.IgnoredFiles.Files.Single(row => row.Path == paths[2]).IsSelected = true;
        await WpfTestHost.LayoutAsync(window);
        var ignoredDelete = Descendants<Button>(ignoredBottom).Single(button => button.IsVisible && button.Command == model.IgnoredFiles.BulkDeleteCommand);
        ((IInvokeProvider)new ButtonAutomationPeer(ignoredDelete).GetPattern(PatternInterface.Invoke)).Invoke();
        await WaitForAsync(() => !model.IsBusy && model.RecentDeletedFiles.Files.Count == 3);
        Assert.IsFalse(File.Exists(paths[2]));
        Assert.IsTrue(File.Exists(paths[3]), "未勾选的已忽略文件不得删除");
        Assert.AreEqual(2, model.IgnoredFiles.Files.Count, "删除实体文件不移除长期忽略规则");
        Assert.IsFalse(model.IgnoredFiles.HasSelection, "成功删除项清空勾选，忽略规则继续保留");
        await model.RecentDeletedFiles.Files.Single(row => row.Path == paths[2]).RestoreCommand!.ExecuteAsync();
        Assert.IsTrue(File.Exists(paths[2]));
        Assert.AreEqual(2, model.RecentDeletedFiles.Files.Count);
        foreach (var section in new[] { model.DisabledFiles, model.RecentDeletedFiles, model.IgnoredFiles })
        {
            section.ToggleMultiSelectCommand.Execute(null);
            Assert.IsFalse(section.HasSelection);
            await WpfTestHost.LayoutAsync(window);
            if (section.HasAdd)
                Assert.IsTrue(Descendants<Button>(panel).Single(button => button.Command == section.AddCommand).IsVisible);
        }
        model.RecentDeletedFiles.ToggleMultiSelectCommand.Execute(null);
        model.RecentDeletedFiles.SelectAllCommand.Execute(null);
        await model.RecentDeletedFiles.BulkRestoreCommand!.ExecuteAsync();
        Assert.IsTrue(deletedPaths.All(File.Exists));
        Assert.IsFalse(model.RecentDeletedFiles.HasMultiSelect);
        Assert.IsFalse(model.RecentDeletedFiles.IsMultiSelect);
        model.IgnoredFiles.ToggleMultiSelectCommand.Execute(null);
        model.IgnoredFiles.SelectAllCommand.Execute(null);
        model.IgnoredFiles.BulkRemoveCommand!.Execute(null);
        Assert.IsFalse(model.IgnoredFiles.HasMultiSelect);
        Assert.IsFalse(model.IgnoredFiles.IsMultiSelect);
        Assert.IsTrue(paths.Skip(2).All(File.Exists), "取消忽略不删除真实文件");
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(Descendants<Button>(panel).Single(button => button.Command == model.IgnoredFiles.AddCommand).IsVisible);
        Assert.IsFalse(Descendants<Button>(panel).Single(button => button.Command == model.IgnoredFiles.ToggleMultiSelectCommand).IsVisible);
        window.Close();
    });

    [TestMethod]
    public Task FileManagementHeaderRefreshUpdatesAllSections() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var disabledPaths = new[] { "SpringGarden.package", "TownStories.ts4script" }.Select(name => NewFile(files.Root, name)).ToArray();
        var ignoredPaths = new[] { "CozyLighting.package", "FreshWalls.package" }.Select(name => NewFile(files.Root, name)).ToArray();
        var deletedPaths = new[] { "OldGarden.package", "OldStories.ts4script" }.Select(name => NewFile(files.Root, name)).ToArray();
        var store = new FileManagementStore(files.Store.DataDirectory);
        var disabled = new ModDisableService(files.Store.DataDirectory);
        var actions = new FileActionService(new TemporaryRecycleBin(files.Folder("Recycle")), new PhysicalFileMetadataAccess(), store, disabled);
        await actions.DeleteAsync(actions.Preflight([new(deletedPaths[0], 4, File.GetLastWriteTimeUtc(deletedPaths[0]), "",
            ExpectedIdentity: PhysicalFileIdentity.Read(deletedPaths[0]))], []));
        var model = Model(files, actions: actions, disableService: disabled);
        await model.DisableFilesAsync([disabledPaths[0]]);
        model.AddIgnoredFiles([ignoredPaths[0]]);
        var window = new MainWindow(model) { Width = 1180, Height = 1100 };
        window.Show();
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("files");
        await model.FileManagementRefreshTask;
        await WpfTestHost.LayoutAsync(window);
        var page = Descendants<SettingsPage>(window).Single();
        var panel = (FrameworkElement)page.FindName("FileManagementPanel");
        var title = (TextBlock)page.FindName("FileManagementTitle");
        var refresh = Descendants<Button>(panel).Single(button => button.IsVisible && button.Content as string == "刷新");
        Assert.AreSame(page.FindName("FileManagementRefreshButton"), refresh);
        Assert.AreSame(model.RefreshFileManagementCommand, refresh.Command);
        Assert.AreSame(VisualTreeHelper.GetParent(title), VisualTreeHelper.GetParent(refresh));
        var titleBounds = title.TransformToAncestor(page).TransformBounds(new Rect(title.RenderSize));
        var refreshBounds = refresh.TransformToAncestor(page).TransformBounds(new Rect(refresh.RenderSize));
        Assert.IsTrue(titleBounds.Right <= refreshBounds.Left, "刷新应位于大标题右侧");
        Assert.IsTrue(titleBounds.Top < refreshBounds.Bottom && refreshBounds.Top < titleBounds.Bottom, "刷新与大标题应在同一行");
        Assert.IsFalse(Descendants<Button>(panel).Any(button => button.IsVisible && button.Command == model.RefreshFileManagementCommand
            && button.DataContext is FileManagementSectionViewModel), "各列表不再保留独立刷新入口");
        foreach (var section in new[] { model.DisabledFiles, model.RecentDeletedFiles, model.IgnoredFiles })
        {
            section.ToggleMultiSelectCommand.Execute(null);
            section.Files.Single().IsSelected = true;
        }
        new ModDisableService(files.Store.DataDirectory).Disable([disabledPaths[1]]);
        new FileManagementStore(files.Store.DataDirectory).Update(data => data with
        { Ignored = data.Ignored.Append(new IgnoredFileRecord(ignoredPaths[1], 4, DateTime.UtcNow)).ToArray() });
        await actions.DeleteAsync(actions.Preflight([new(deletedPaths[1], 4, File.GetLastWriteTimeUtc(deletedPaths[1]), "",
            ExpectedIdentity: PhysicalFileIdentity.Read(deletedPaths[1]))], []));
        Assert.AreEqual(1, model.DisabledFiles.Files.Count);
        Assert.AreEqual(1, model.RecentDeletedFiles.Files.Count);
        Assert.AreEqual(1, model.IgnoredFiles.Files.Count);
        var busyStarts = 0;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(model.IsBusy) || !model.IsBusy) return;
            busyStarts++;
            Assert.IsFalse(model.RefreshFileManagementCommand.CanExecute(null));
            Assert.IsTrue(model.RefreshFileManagementCommand.ExecuteAsync().IsCompletedSuccessfully,
                "忙碌时重复刷新应立即返回");
        };
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void RefreshStateChanged(object? sender, EventArgs args)
        {
            if (busyStarts > 0 && model.RefreshFileManagementCommand.CanExecute(null)) completed.TrySetResult();
        }
        model.RefreshFileManagementCommand.CanExecuteChanged += RefreshStateChanged;
        try
        {
            ((IInvokeProvider)new ButtonAutomationPeer(refresh).GetPattern(PatternInterface.Invoke)).Invoke();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { model.RefreshFileManagementCommand.CanExecuteChanged -= RefreshStateChanged; }
        Assert.AreEqual(1, busyStarts);
        Assert.IsTrue(model.RefreshFileManagementCommand.CanExecute(null));
        Assert.AreEqual(2, model.DisabledFiles.Files.Count);
        Assert.AreEqual(2, model.RecentDeletedFiles.Files.Count);
        Assert.AreEqual(2, model.IgnoredFiles.Files.Count);
        Assert.AreEqual(disabledPaths[0], model.DisabledFiles.Files.Single(row => row.IsSelected).Path);
        Assert.AreEqual(deletedPaths[0], model.RecentDeletedFiles.Files.Single(row => row.IsSelected).Path);
        Assert.AreEqual(ignoredPaths[0], model.IgnoredFiles.Files.Single(row => row.IsSelected).Path);
        Assert.IsTrue(new[] { model.DisabledFiles, model.RecentDeletedFiles, model.IgnoredFiles }.All(section => section.IsMultiSelect));
        await WpfTestHost.LayoutAsync(window);
        SaveReview(window, "file-management-header-refresh.png");
        model.DisabledFiles.ClearSelectionCommand.Execute(null);
        Assert.IsFalse(model.DisabledFiles.HasSelection);
        Assert.IsTrue(model.RecentDeletedFiles.HasSelection);
        Assert.IsTrue(model.IgnoredFiles.HasSelection);
        window.Width = 920; window.Height = 640;
        await WpfTestHost.LayoutAsync(window);
        refreshBounds = refresh.TransformToAncestor(page).TransformBounds(new Rect(refresh.RenderSize));
        Assert.IsTrue(new Rect(page.RenderSize).Contains(refreshBounds), "窄窗页头刷新不应溢出");
        Assert.IsNotNull(refresh.InputHitTest(new Point(refresh.ActualWidth * .1, refresh.ActualHeight * .1)));
        window.Close();
    });

    [TestMethod]
    public Task FileMenuDisableEnableAndSettingsUseSameLedger() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        var paths = Enumerable.Range(0, 3).Select(index => NewFile(mods, $"test-{index}.package")).ToArray();
        files.Store.Save(new(mods, []));
        var model = Model(files);
        await model.StartScanCommand.ExecuteAsync();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        var report = await new DuplicateRunService(DuplicateScanner.CreateDefault()).RunAsync(new([new(mods, "Mods")], mods));
        var session = V2([], [], report);
        session.ToggleExpanded(session.Cards.Single().Key);
        await SetSessionAsync(model, session, "all");
        var row = model.ConflictRows.Single(row => row.File?.Path == paths[0]);
        model.ToggleConflictFileEnabledCommand.Execute(row);
        await model.ConflictFileActionTask;
        row = model.ConflictRows.Single(row => row.File?.Path == paths[0]);
        Assert.IsTrue(row.IsDisabled);
        Assert.AreEqual("启用", row.DisableMenuText);
        Assert.AreEqual(paths[0] + ".off", row.FilePath);
        Assert.AreEqual(paths[0], model.DisabledFiles.Files.Single().Path);
        Assert.IsTrue(File.Exists(row.FilePath));
        await WpfTestHost.LayoutAsync(window);
        Save(window, "file-disabled-row.png");
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("files");
        await model.FileManagementRefreshTask;
        await WpfTestHost.LayoutAsync(window);
        Save(window, "file-disabled-settings.png");
        await model.DisabledFiles.Files.Single().RestoreCommand!.ExecuteAsync();
        row = model.ConflictRows.Single(row => row.File?.Path == paths[0]);
        Assert.IsFalse(row.IsDisabled);
        Assert.AreEqual("禁用", row.DisableMenuText);
        await model.DisableFilesAsync([paths[0]]);
        row = model.ConflictRows.Single(row => row.File?.Path == paths[0]);
        Assert.IsTrue(row.IsDisabled);
        model.ToggleConflictFileEnabledCommand.Execute(row);
        await model.ConflictFileActionTask;
        Assert.AreEqual(0, model.DisabledFiles.Files.Count);
        Assert.IsTrue(File.Exists(paths[0]));
        Assert.IsFalse(model.ConflictRows.Single(row => row.File?.Path == paths[0]).IsDisabled);
        window.Close();
    });

    [TestMethod]
    public Task FileMenuDeleteDisabledFileAndRestoreKeepItDisabled() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        var paths = Enumerable.Range(0, 3).Select(index => NewFile(mods, $"test-{index}.package")).ToArray();
        files.Store.Save(new(mods, []));
        var disabled = new ModDisableService(files.Store.DataDirectory);
        var service = new FileActionService(new TemporaryRecycleBin(files.Folder("Recycle")), new PhysicalFileMetadataAccess(),
            new FileManagementStore(files.Store.DataDirectory), disabled);
        var model = Model(files, actions: service, disableService: disabled);
        var report = await new DuplicateRunService(DuplicateScanner.CreateDefault()).RunAsync(new([new(mods, "Mods")], mods));
        var session = V2([], [], report);
        session.ToggleExpanded(session.Cards.Single().Key);
        await SetSessionAsync(model, session, "all");
        await model.DisableFilesAsync([paths[0]]);
        var row = model.ConflictRows.Single(row => row.File?.Path == paths[0]);
        await model.DeleteConflictFileAsync(row);
        Assert.IsFalse(File.Exists(paths[0] + ".off"));
        Assert.IsFalse(model.ConflictRows.Any(row => row.File?.Path == paths[0]));
        Assert.AreEqual(0, model.DisabledFiles.Files.Count);
        await model.RefreshFileManagementAsync();
        await model.RecentDeletedFiles.Files.Single().RestoreCommand!.ExecuteAsync();
        Assert.IsFalse(File.Exists(paths[0]));
        Assert.IsTrue(File.Exists(paths[0] + ".off"));
        Assert.AreEqual(1, model.DisabledFiles.Files.Count);
        Assert.IsTrue(model.ConflictRows.Single(row => row.File?.Path == paths[0]).IsDisabled);
    });

    private static async Task RestoreAllSelectedAsync(MainWindowViewModel model)
    {
        if (!model.DisabledFiles.IsMultiSelect) model.DisabledFiles.ToggleMultiSelectCommand.Execute(null);
        model.DisabledFiles.SelectAllCommand.Execute(null);
        await model.DisabledFiles.BulkRestoreCommand!.ExecuteAsync();
    }

    private static MainWindowViewModel Model(Scenario files, Func<string, bool>? confirm = null,
        Func<IReadOnlyList<string>>? pick = null, IFileActionService? actions = null, IFileActionDialogService? dialogs = null,
        ModDisableService? disableService = null) =>
        new(new DuplicateRunService(DuplicateScanner.CreateDefault()), new Picker(), files.Store,
            fileActionService: actions, fileActionDialogs: dialogs ?? new Dialogs(),
            settingsSupport: new SettingsSupportService(files.Root), modDisableService: disableService ?? new ModDisableService(files.Store.DataDirectory),
            pickDisabledFiles: pick ?? (() => []), confirmModChange: confirm ?? (_ => true));

    private static string NewFile(string directory, string name)
    { var path = Path.Combine(directory, name); File.WriteAllText(path, "same"); return path; }

    private static async Task WaitForAsync(Func<bool> done)
    {
        var limit = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < limit) await Task.Delay(20);
        Assert.IsTrue(done(), "文件操作未在等待时间内完成");
    }

    private static void SaveReview(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SIMS4_SETTINGS_REVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }

    private sealed class TemporaryRecycleBin(string directory) : IRecycleBinAdapter
    {
        public bool CanRecycleFiles { get; set; } = true;
        public string? FailDeletePath { get; set; }
        public List<string> Deleted { get; } = [];
        public bool CanRecycle(string path) => CanRecycleFiles;
        public Task<RecycleBinPresence> QueryAsync(IReadOnlyList<RecycleBinItem> items, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecycleBinPresence(items.Where(item => File.Exists(item.RecycledPath)).ToArray(),
                items.Where(item => !File.Exists(item.RecycledPath)).ToArray(), []));
        public Task<RecycleBinDeleteResult> MoveToRecycleBinAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            var items = paths.Where(path => path != FailDeletePath).Select(path =>
            {
                var identity = Guid.NewGuid();
                return new RecycleBinItem(path, [18, 0, .. identity.ToByteArray(), 0, 0], Path.Combine(directory, identity.ToString("N")));
            }).ToArray();
            foreach (var item in items) File.Move(item.OriginalPath, item.RecycledPath!);
            Deleted.AddRange(items.Select(item => item.OriginalPath));
            return Task.FromResult(new RecycleBinDeleteResult(items,
                paths.Where(path => path == FailDeletePath).Select(path => new FileActionFailure(path, "模拟回收失败")).ToArray()));
        }
        public Task<FileActionBatchResult> RestoreAsync(IReadOnlyList<RecycleBinItem> items, CancellationToken cancellationToken = default)
        {
            foreach (var item in items) File.Move(item.RecycledPath!, item.OriginalPath);
            return Task.FromResult(new FileActionBatchResult(items.Select(item => item.OriginalPath).ToArray(), [], false));
        }
    }

    private sealed class DeleteDialogs : IFileActionDialogService
    {
        public Func<FileDeletionPreflight, bool> Confirm { get; set; } = _ => true;
        public List<FileDeletionPreflight> Confirmations { get; } = [];
        public List<FileActionFailure> Problems { get; } = [];
        public bool ConfirmDeletion(FileDeletionPreflight preflight)
        { Confirmations.Add(preflight); return Confirm(preflight); }
        public void ShowProblems(string title, IReadOnlyList<FileActionFailure> failures) => Problems.AddRange(failures);
    }
}
