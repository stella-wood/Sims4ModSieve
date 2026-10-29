using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.SettingsViewModelTests;
using static Sims4ModSieve.Desktop.Tests.ConflictResultSessionTests;
using static Sims4ModSieve.Desktop.Tests.ConflictV2SessionTests;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class FileManagementTests
{
    [TestMethod]
    public void GlobalIgnorePreservesOriginalGroupsNumbersAndFacts()
    {
        var session = ContainmentSession();
        foreach (var card in session.Cards) session.ToggleExpanded(card.Key);
        var cards = session.Cards;
        var original = Rows(session).Where(row => row.File?.Path == B).ToArray();
        Assert.AreEqual(2, original.Length);
        session.ToggleIgnored(original[0]);
        Assert.AreSame(cards, session.Cards);
        Assert.IsFalse(Rows(session).Any(row => row.File?.Path == B));
        var shown = session.BuildVisibleRows("all", "all", session.GetExpandedKeysSnapshot(), showIgnored: true)
            .Where(row => row.File?.Path == B).ToArray();
        CollectionAssert.AreEqual(original.Select(row => (row.Key, row.GroupIndex, row.Annotation)).ToArray(),
            shown.Select(row => (row.Key, row.GroupIndex, row.Annotation)).ToArray());
        Assert.IsTrue(shown.All(row => row.IsIgnored));
        session.ToggleIgnored(shown[1]);
        Assert.AreEqual(2, Rows(session).Count(row => row.File?.Path == B));
        Assert.IsFalse(Rows(session).Any(row => row.File?.Path == B && row.IsIgnored));
    }

    [TestMethod]
    public void IgnoredNonRepresentativeCopyReturnsInEveryOriginalCard()
    {
        var session = V2([Resource(1, (A, "same"), (B, "same"), (C, "other"))], [(A, 1), (B, 1), (C, 1)], Duplicates(A, B));
        foreach (var card in session.Cards) session.ToggleExpanded(card.Key);
        var originals = session.Cards.Select(card => (card.Key, card.GroupIndex)).ToArray();
        session.SetIgnoredFiles([B.ToUpperInvariant()]);
        Assert.IsFalse(Rows(session).Any(row => row.File?.Path == B));
        var shown = session.BuildVisibleRows("all", "all", session.GetExpandedKeysSnapshot(), showIgnored: true);
        var copies = shown.Where(row => row.File?.Path == B).ToArray();
        Assert.AreEqual(2, copies.Length);
        Assert.IsTrue(copies.All(row => row.IsIgnored));
        Assert.AreEqual(copies.Length, copies.Select(row => row.Key).Distinct().Count());
        CollectionAssert.AreEqual(originals, session.Cards.Select(card => (card.Key, card.GroupIndex)).ToArray());
        session.SetIgnoredFiles([]);
        Assert.IsFalse(session.IsFileIgnored(B));
        Assert.IsFalse(Rows(session).Any(row => row.IsIgnored));
    }

    [TestMethod]
    public void FileIgnoreIncludesProblemRowsAndSnapshotAndSurvivesSessionReset()
    {
        var report = Duplicates(A, B) with { Groups = [], Issues = [new("hash-failed", ScanIssueStage.Hashing, C, "无法读取")] };
        var session = V2([], [], report);
        var row = Rows(session).Single(row => row.File?.Path == C);
        session.ToggleIgnored(row);
        Assert.IsFalse(Rows(session).Any(row => row.File?.Path == C));
        Assert.IsTrue(session.ProjectionSnapshot().IsFileIgnored(C));
        session.Reset();
        Assert.IsTrue(session.IsFileIgnored(C));
        Assert.IsTrue(session.BuildVisibleRows("all", "all", Closed, showIgnored: true).Count == 0);
    }

    [TestMethod]
    public Task PersistentIgnoredFilesDeduplicateAndSurviveRestartScanAndSourceClear() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        var a = Path.Combine(mods, "a.package");
        var b = Path.Combine(mods, "b.package");
        File.WriteAllText(a, "same"); File.WriteAllText(b, "same");
        files.Store.Save(new(mods, []));
        var model = files.Model();
        model.AddIgnoredFiles([a, a.ToUpperInvariant()]);
        Assert.AreEqual(1, model.IgnoredFiles.Files.Count);
        await model.StartScanCommand.ExecuteAsync();
        Assert.AreEqual(2, model.Groups.Single().Files.Count, "独立重复检测不受忽略影响");
        model.Sources.Clear();
        model.ConflictSources.Clear();
        Assert.AreEqual(1, files.Model().IgnoredFiles.Files.Count);
        File.Delete(a);
        await model.RefreshFileManagementAsync();
        Assert.AreEqual(1, model.IgnoredFiles.Files.Count, "暂时不存在的文件规则不能清理");
        model.RemoveIgnoredFiles([a]);
        Assert.AreEqual(0, files.Model().IgnoredFiles.Files.Count);
    });

    [TestMethod]
    public Task FailedIgnoreSaveKeepsPreviousStateAndCancelledPickerDoesNothing() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var a = Path.Combine(files.Root, "a.package");
        File.WriteAllText(a, "same");
        var model = Model(files, pick: () => []);
        model.AddIgnoredFiles([a]);
        var saved = Path.Combine(files.Root, "Sims4ModSieve", "file-management.json");
        var before = File.ReadAllText(saved);
        model.IgnoredFiles.AddCommand!.Execute(null);
        Assert.AreEqual(before, File.ReadAllText(saved));
        using (File.Open(saved, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            model.RemoveIgnoredFiles([a]);
            Assert.AreEqual(1, model.IgnoredFiles.Files.Count);
            StringAssert.Contains(model.FileManagementFeedback, "未保存");
        }
        Assert.AreEqual(before, File.ReadAllText(saved));
        await Task.CompletedTask;
    });

    [TestMethod]
    public async Task MultipleDeletionBatchesSurviveRestartAndClearOnlyUndo()
    {
        using var files = new Scenario();
        var store = new FileManagementStore(files.Folder("Data"));
        var adapter = new TemporaryRecycleBin(files.Folder("Recycle"));
        var service = new FileActionService(adapter, new PhysicalFileMetadataAccess(), store);
        var a = NewFile(files.Root, "a.package");
        var b = NewFile(files.Root, "b.package");
        await Delete(service, a); await Delete(service, b);
        Assert.AreEqual(2, (await service.GetRecentlyDeletedAsync()).Files.Count);
        service.ClearLastDelete();
        var restarted = new FileActionService(adapter, new PhysicalFileMetadataAccess(), new FileManagementStore(files.Folder("Data")));
        var history = await restarted.GetRecentlyDeletedAsync();
        Assert.AreEqual(2, history.Files.Count);
        Assert.IsFalse(restarted.CanUndoLastDelete);
        var restore = await restarted.RestoreDeletedAsync([history.Files.Single(record => record.Item.OriginalPath == a).Id]);
        Assert.AreEqual(1, restore.CompletedPaths.Count);
        Assert.IsTrue(File.Exists(a));
        Assert.IsFalse(File.Exists(b));
        Assert.AreEqual(1, (await restarted.GetRecentlyDeletedAsync()).Files.Count);
    }

    [TestMethod]
    public async Task RefreshRemovesOnlyConfirmedMissingAndQueryFailureKeepsRecords()
    {
        using var files = new Scenario();
        var adapter = new TemporaryRecycleBin(files.Folder("Recycle"));
        var service = new FileActionService(adapter, new PhysicalFileMetadataAccess());
        var a = NewFile(files.Root, "a.package");
        await Delete(service, a);
        adapter.QueryFails = true;
        var uncertain = await service.GetRecentlyDeletedAsync();
        Assert.AreEqual(1, uncertain.Files.Count);
        Assert.AreEqual(1, uncertain.Failures.Count);
        adapter.QueryFails = false;
        File.Delete(uncertain.Files[0].Item.RecycledPath!);
        var missing = await service.RestoreDeletedAsync([uncertain.Files[0].Id]);
        StringAssert.Contains(missing.Failures.Single().Message, "找不到");
        Assert.AreEqual(0, (await service.GetRecentlyDeletedAsync()).Files.Count);
        Assert.IsFalse(service.CanUndoLastDelete);
    }

    [TestMethod]
    public async Task SamePathHistoryRestoresExactEntryWithoutLosingLatestUndo()
    {
        using var files = new Scenario();
        var adapter = new TemporaryRecycleBin(files.Folder("Recycle"));
        var service = new FileActionService(adapter, new PhysicalFileMetadataAccess());
        var path = NewFile(files.Root, "same.package", "old");
        await Delete(service, path);
        var first = (await service.GetRecentlyDeletedAsync()).Files.Single();
        NewFile(files.Root, "same.package", "new-content");
        await Delete(service, path);
        var restore = await service.RestoreDeletedAsync([first.Id]);
        Assert.AreEqual("old", File.ReadAllText(path));
        Assert.AreEqual(0, restore.RestoredUndoPaths!.Count);
        Assert.IsTrue(service.CanUndoLastDelete);
        Assert.AreEqual(1, (await service.GetRecentlyDeletedAsync()).Files.Count);
        var blocked = await service.UndoLastDeleteAsync();
        Assert.AreEqual(0, blocked.CompletedPaths.Count);
        Assert.AreEqual("old", File.ReadAllText(path));
        Assert.IsTrue(service.CanUndoLastDelete);
    }

    [TestMethod]
    public Task SettingsRestoreAcrossBatchesThenUndoPreservesCurrentSession() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        var paths = Enumerable.Range(1, 4).Select(index => NewFile(mods, $"{index}.package")).ToArray();
        files.Store.Save(new(mods, []));
        var store = new FileManagementStore(files.Folder("Sims4ModSieve"));
        var actions = new FileActionService(new TemporaryRecycleBin(files.Folder("Recycle")), new PhysicalFileMetadataAccess(), store);
        var model = Model(files, actions, store);
        await model.StartScanCommand.ExecuteAsync();
        model.Groups.Single().Files.Single(file => file.Path == paths[3]).IsSelected = true;
        await model.DeleteSelectedCommand.ExecuteAsync();
        model.Groups.Single().Files.Single(file => file.Path == paths[2]).IsSelected = true;
        await model.DeleteSelectedCommand.ExecuteAsync();
        await model.RefreshFileManagementAsync();
        var first = model.RecentDeletedFiles.Files.Single(row => row.Path == paths[3]);
        await model.RestoreManagedFilesAsync([first.Id]);
        Assert.AreEqual(3, model.Groups.Single().Files.Count);
        await model.UndoLastDeleteCommand.ExecuteAsync();
        Assert.AreEqual(4, model.Groups.Single().Files.Count);
        Assert.IsFalse(model.CanUndoLastDelete);
        model.Groups.Single().Files.Where(file => file.Path != paths[0]).ToList().ForEach(file => file.IsSelected = true);
        await model.DeleteSelectedCommand.ExecuteAsync();
        await model.RefreshFileManagementAsync();
        await model.RestoreManagedFilesAsync([model.RecentDeletedFiles.Files[0].Id]);
        await model.UndoLastDeleteCommand.ExecuteAsync();
        Assert.AreEqual(4, model.Groups.Single().Files.Count);
        Assert.IsFalse(model.CanUndoLastDelete);
    });

    [TestMethod]
    public Task OldVersionRestoreDoesNotRepopulateNewScanOrConsumeNewUndo() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        var a = NewFile(mods, "a.package", "old");
        NewFile(mods, "b.package", "old");
        files.Store.Save(new(mods, []));
        var store = new FileManagementStore(files.Folder("Sims4ModSieve"));
        var actions = new FileActionService(new TemporaryRecycleBin(files.Folder("Recycle")), new PhysicalFileMetadataAccess(), store);
        var model = Model(files, actions, store);
        await model.StartScanCommand.ExecuteAsync();
        model.Groups.Single().Files.Single(file => file.Path == a).IsSelected = true;
        await model.DeleteSelectedCommand.ExecuteAsync();
        var old = (await actions.GetRecentlyDeletedAsync()).Files.Single();
        NewFile(mods, "a.package", "new-version");
        NewFile(mods, "b.package", "new-version");
        await model.StartScanCommand.ExecuteAsync();
        model.Groups.Single().Files.Single(file => file.Path == a).IsSelected = true;
        await model.DeleteSelectedCommand.ExecuteAsync();
        await model.RestoreManagedFilesAsync([old.Id]);
        Assert.AreEqual(0, model.Groups.Count, "旧版本不能塞入新扫描的结果组");
        Assert.IsTrue(model.CanUndoLastDelete);
        Assert.AreEqual("old", File.ReadAllText(a));
    });

    [TestMethod]
    public Task AppliedPageSupportsSelectionIndependentScrollingAndFilePicker() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var sample = NewFile(files.Root, "new.package");
        var store = new FileManagementStore(files.Folder("Sims4ModSieve"));
        store.Update(data => data with { Ignored = Enumerable.Range(0, 10).Select(index => new IgnoredFileRecord(
            Path.Combine(files.Root, "Mods", $"VeryLongExampleFile-{index}.package"), 1024 * (index + 1), new DateTime(2026, 10, 1, 13, 50, 0, DateTimeKind.Utc))).ToArray() });
        var adapter = new TemporaryRecycleBin(files.Folder("Recycle"));
        var originalFiles = Enumerable.Range(0, 10).Select(index => NewFile(files.Root, $"deleted-{index}.package")).ToArray();
        var stamps = originalFiles.ToDictionary(path => path, path => File.GetLastWriteTimeUtc(path));
        var recycled = await adapter.MoveToRecycleBinAsync(originalFiles);
        store.Update(data => data with { Deleted = recycled.CompletedItems.Select(item => new DeletedFileRecord(
            Guid.NewGuid().ToString("N"), item, 4, new DateTime(2026, 10, 1, 13, 50, 0, DateTimeKind.Utc), stamps[item.OriginalPath])).ToArray() });
        var actions = new FileActionService(adapter, new PhysicalFileMetadataAccess(), store);
        var model = Model(files, actions, store, pick: () => [sample, sample.ToUpperInvariant()]);
        var window = new MainWindow(model) { Width = 1280, Height = 1100 };
        window.Show();
        model.OpenSettingsCommand.Execute(null);
        model.SelectSettingsSectionCommand.Execute("files");
        await model.FileManagementRefreshTask;
        await WpfTestHost.LayoutAsync(window);
        var page = Descendants<SettingsPage>(window).Single();
        var panel = (FrameworkElement)page.FindName("FileManagementPanel");
        var list = Descendants<ListBox>(panel).Single(control => ReferenceEquals(control.DataContext, model.IgnoredFiles));
        Assert.IsTrue(Descendants<CheckBox>(list).All(check => !check.IsVisible));
        Assert.AreEqual(10, model.RecentDeletedFiles.Files.Count);
        model.RecentDeletedFiles.ToggleMultiSelectCommand.Execute(null);
        model.RecentDeletedFiles.Files[0].IsSelected = true;
        model.RecentDeletedFiles.Files[1].IsSelected = true;
        await model.RecentDeletedFiles.BulkRestoreCommand!.ExecuteAsync();
        Assert.AreEqual(8, model.RecentDeletedFiles.Files.Count);
        Assert.AreEqual(2, originalFiles.Count(File.Exists));
        model.RecentDeletedFiles.ToggleMultiSelectCommand.Execute(null);
        await model.RecentDeletedFiles.Files[0].RestoreCommand!.ExecuteAsync();
        Assert.AreEqual(7, model.RecentDeletedFiles.Files.Count);
        Assert.AreEqual(3, originalFiles.Count(File.Exists));
        var recentList = Descendants<ListBox>(panel).Single(control => ReferenceEquals(control.DataContext, model.RecentDeletedFiles));
        var recentScroll = Descendants<ScrollViewer>(recentList).Single();
        var header = Descendants<TextBlock>(panel).Single(text => text.IsVisible && text.Text == "删除时间");
        var headerOrigin = header.TranslatePoint(new Point(), page);
        recentScroll.ScrollToEnd();
        await WpfTestHost.LayoutAsync(window);
        Assert.IsGreaterThan(0, recentScroll.VerticalOffset);
        Assert.AreEqual(headerOrigin, header.TranslatePoint(new Point(), page), "文件内部滚动时表头应保持原位");
        var add = Descendants<Button>(panel).Single(button => button.IsVisible && button.Content as string == "＋添加"
            && ReferenceEquals(button.DataContext, model.IgnoredFiles));
        ((IInvokeProvider)new ButtonAutomationPeer(add).GetPattern(PatternInterface.Invoke)).Invoke();
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(11, model.IgnoredFiles.Files.Count);
        Assert.IsFalse(Descendants<Button>(panel).Any(button => button.IsVisible && button.Content as string == "刷新"
            && ReferenceEquals(button.DataContext, model.IgnoredFiles)));
        var multi = Descendants<Button>(panel).Single(button => ReferenceEquals(button.DataContext, model.IgnoredFiles) && button.Content as string == "多选");
        ((IInvokeProvider)new ButtonAutomationPeer(multi).GetPattern(PatternInterface.Invoke)).Invoke();
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(Descendants<CheckBox>(list).Any(check => check.IsVisible));
        model.IgnoredFiles.Files[0].IsSelected = true;
        Assert.IsTrue(model.IgnoredFiles.BulkRemoveCommand!.CanExecute(null));
        model.IgnoredFiles.BulkRemoveCommand.Execute(null);
        Assert.IsFalse(model.IgnoredFiles.BulkRemoveCommand.CanExecute(null));
        model.IgnoredFiles.ToggleMultiSelectCommand.Execute(null);
        await WpfTestHost.LayoutAsync(window);
        var outer = (ScrollViewer)page.FindName("SettingsContentScroll");
        var inner = Descendants<ScrollViewer>(list).Single();
        var outerOffset = outer.VerticalOffset;
        inner.ScrollToEnd();
        await WpfTestHost.LayoutAsync(window);
        Assert.IsGreaterThan(0, inner.VerticalOffset);
        Assert.AreEqual(outerOffset, outer.VerticalOffset);
        var evt = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        list.RaiseEvent(evt);
        Assert.IsTrue(evt.Handled);
        Assert.AreEqual(outerOffset, outer.VerticalOffset, "列表边界滚轮不能带动整页");
        var icon = Descendants<Button>(panel).First(button => button.Command == model.RecentDeletedFiles.OpenFolderCommand);
        Assert.IsNotNull(icon.InputHitTest(new Point(icon.ActualWidth * .1, icon.ActualHeight * .1)));
        SaveReview(window, "deletion-ignore-1280.png");
        window.Width = 920; window.Height = 640;
        await WpfTestHost.LayoutAsync(window);
        outer.ScrollToTop();
        await WpfTestHost.LayoutAsync(window);
        var disabledAdd = Descendants<Button>(panel).Single(button => button.Command == model.DisabledFiles.AddCommand && button.IsVisible);
        var addBounds = disabledAdd.TransformToAncestor(page).TransformBounds(new Rect(disabledAdd.RenderSize));
        Assert.IsTrue(new Rect(page.RenderSize).Contains(addBounds), "空态的添加入口应在页面可见区域内");
        Assert.IsFalse(Descendants<Button>(panel).Any(button => button.IsVisible && button.Command == model.DisabledFiles.ToggleMultiSelectCommand),
            "空的已禁用列表不显示多选入口");
        SaveReview(window, "deletion-ignore-920-top.png");
        outer.ScrollToEnd();
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(0d, outer.HorizontalOffset);
        foreach (var button in Descendants<Button>(list).Where(button => button.IsVisible))
            Assert.IsNotNull(button.InputHitTest(new Point(button.ActualWidth * .5, button.ActualHeight * .5)));
        SaveReview(window, "deletion-ignore-920-scrolled.png");
        window.Close();
    });

    [TestMethod]
    public async Task MalformedPidlFailsManagedValidationBeforeShellBinding()
    {
        var adapter = new WindowsRecycleBinAdapter();
        foreach (var pidl in new byte[][] { [], [1, 0], [8, 0, 1, 0], [0, 0, 1, 0] })
        {
            var result = await adapter.QueryAsync([new(Path.Combine(Path.GetTempPath(), "invalid.package"), pidl)]);
            Assert.AreEqual(0, result.MissingItems.Count);
            Assert.AreEqual(1, result.Failures.Count);
        }
    }

    [TestMethod]
    public Task InvalidSchemaReportsErrorWithoutOverwritingConfig() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var directory = files.Folder("Sims4ModSieve");
        var saved = Path.Combine(directory, "file-management.json");
        const string malformed = "{}";
        File.WriteAllText(saved, malformed);
        var model = files.Model();
        StringAssert.Contains(model.FileManagementFeedback, "无法读取");
        await model.RefreshFileManagementAsync();
        StringAssert.Contains(model.FileManagementFeedback, "无法刷新");
        var path = NewFile(files.Root, "a.package");
        model.AddIgnoredFiles([path]);
        Assert.AreEqual(0, model.IgnoredFiles.Files.Count);
        Assert.AreEqual(malformed, File.ReadAllText(saved));
    });

    [TestMethod]
    public Task ProblemFileReturnsAfterRestoreOnlyWithOriginalStamp() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var path = NewFile(files.Root, "damaged.package");
        var info = new FileInfo(path);
        var stamp = new FileStamp(info.Length, info.LastWriteTimeUtc);
        var report = ConflictResultSessionTests.Packages([(path, 1)]) with
        { ResourceInventories = [new PackageResourceInventory(path, stamp, 1)] };
        var session = new ConflictResultSession();
        session.Replace(report, Contents(), Scripts(), duplicates: Duplicates(path, path) with
        { Groups = [], Issues = [new("hash-failed", ScanIssueStage.Hashing, path, "无法读取")] });
        var store = new FileManagementStore(files.Folder("Sims4ModSieve"));
        var service = new FileActionService(new TemporaryRecycleBin(files.Folder("Recycle")), new PhysicalFileMetadataAccess(), store);
        var model = Model(files, service, store);
        await SetSessionAsync(model, session, "all");
        var row = model.ConflictRows.Single(row => row.File?.Path == path);
        await model.DeleteConflictFileAsync(row);
        Assert.IsFalse(model.ConflictRows.Any(row => row.File?.Path == path));
        await model.RefreshFileManagementAsync();
        await model.RestoreManagedFilesAsync([model.RecentDeletedFiles.Files.Single().Id]);
        Assert.IsTrue(model.ConflictRows.Any(row => row.File?.Path == path));
        Assert.IsFalse(session.MatchesOriginalFile(path, info.Length + 1, stamp.LastWriteTimeUtc));
    });

    [TestMethod]
    public Task IgnoredDeletionPreservesRulesAndRestoresOnlyCurrentOriginalMembers() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        var paths = Enumerable.Range(0, 4).Select(index => NewFile(mods, $"ignored-{index}.package")).ToArray();
        files.Store.Save(new(mods, []));
        var store = new FileManagementStore(files.Folder("Sims4ModSieve"));
        var recycle = new TemporaryRecycleBin(files.Folder("Recycle"));
        var service = new FileActionService(recycle, new PhysicalFileMetadataAccess(), store);
        var dialogs = new DeleteDialogs();
        var model = Model(files, service, store, dialogs: dialogs);
        await model.StartScanCommand.ExecuteAsync();
        var report = await new DuplicateRunService(DuplicateScanner.CreateDefault()).RunAsync(new([new(mods, "Mods")], mods));
        var session = V2([], [], report);
        await SetSessionAsync(model, session, "all");
        model.AddIgnoredFiles(paths.Take(3).ToArray());
        model.IgnoredFiles.ToggleMultiSelectCommand.Execute(null);
        foreach (var row in model.IgnoredFiles.Files.Where(row => paths.Take(2).Contains(row.Path))) row.IsSelected = true;
        await model.IgnoredFiles.BulkDeleteCommand!.ExecuteAsync();
        Assert.AreEqual(1, dialogs.Confirmations.Count);
        CollectionAssert.AreEquivalent(paths.Take(2).ToArray(), dialogs.Confirmations.Single().Candidates.Select(candidate => candidate.Path).ToArray());
        Assert.AreEqual(1, recycle.DeleteBatches.Count);
        Assert.IsTrue(paths.Take(2).All(path => !File.Exists(path)));
        Assert.IsTrue(paths.Skip(2).All(File.Exists), "未选中和未忽略的文件保留");
        Assert.AreEqual(3, model.IgnoredFiles.Files.Count);
        Assert.AreEqual(3, store.Load().Ignored.Count);
        Assert.AreEqual(2, model.Groups.Single().Files.Count);
        Assert.IsFalse(session.Cards.SelectMany(card => card.Files).Any(file => paths.Take(2).Contains(file.Path)));
        var first = model.RecentDeletedFiles.Files.Single(row => row.Path == paths[0]);
        await first.RestoreCommand!.ExecuteAsync();
        Assert.AreEqual(3, model.Groups.Single().Files.Count);
        Assert.IsTrue(session.Cards.SelectMany(card => card.Files).Any(file => file.Path == paths[0]));
        Assert.IsTrue(session.IsFileIgnored(paths[0]));
        await model.UndoLastDeleteCommand.ExecuteAsync();
        Assert.IsTrue(paths.All(File.Exists));
        Assert.AreEqual(4, model.Groups.Single().Files.Count);
        Assert.IsFalse(model.CanUndoLastDelete);
        Assert.AreEqual(3, model.IgnoredFiles.Files.Count);

        File.WriteAllText(paths[0], "new version at the ignored path");
        await model.IgnoredFiles.Files.Single(row => row.Path == paths[0]).DeleteCommand!.ExecuteAsync();
        Assert.AreEqual(3, model.Groups.Single().Files.Count);
        await model.UndoLastDeleteCommand.ExecuteAsync();
        Assert.AreEqual("new version at the ignored path", File.ReadAllText(paths[0]));
        Assert.AreEqual(3, model.Groups.Single().Files.Count, "实时删除的新版本不能回填旧扫描成员");
        Assert.IsFalse(session.Cards.SelectMany(card => card.Files).Any(file => file.Path == paths[0]));

        await model.DeleteIgnoredFilesAsync([paths[1]]);
        await model.StartScanCommand.ExecuteAsync();
        var nextReport = await new DuplicateRunService(DuplicateScanner.CreateDefault()).RunAsync(new([new(mods, "Mods")], mods));
        var next = V2([], [], nextReport);
        await SetSessionAsync(model, next, "all");
        await model.RefreshFileManagementAsync();
        await model.RestoreManagedFilesAsync([model.RecentDeletedFiles.Files.Single(row => row.Path == paths[1]).Id]);
        Assert.IsTrue(File.Exists(paths[1]));
        Assert.AreEqual(2, model.Groups.Single().Files.Count, "历史恢复不能给新扫描补入旧成员");
        Assert.IsFalse(next.Cards.SelectMany(card => card.Files).Any(file => file.Path == paths[1]));
        Assert.AreEqual(3, store.Load().Ignored.Count);
        Assert.IsTrue(next.IsFileIgnored(paths[1]));
    });

    [TestMethod]
    public Task IgnoredDeletionCancellationAndPreflightFailuresProtectFiles() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var path = NewFile(files.Root, "ignored.package");
        var other = NewFile(files.Root, "other.package");
        var store = new FileManagementStore(files.Folder("Sims4ModSieve"));
        var recycle = new TemporaryRecycleBin(files.Folder("Recycle"));
        var service = new FileActionService(recycle, new PhysicalFileMetadataAccess(), store);
        var dialogs = new DeleteDialogs { Confirm = _ => false };
        var model = Model(files, service, store, dialogs: dialogs);
        model.AddIgnoredFiles([path]);
        Assert.IsFalse(model.IgnoredFiles.BulkDeleteCommand!.CanExecute(null));
        await model.DeleteIgnoredFilesAsync([]);
        await model.DeleteIgnoredFilesAsync([other]);
        Assert.AreEqual(0, dialogs.Confirmations.Count);
        File.WriteAllText(path, "current file is larger than the saved ignore entry");
        var current = new FileInfo(path);
        var expectedSize = current.Length;
        var expectedLastWriteTimeUtc = current.LastWriteTimeUtc;
        await model.IgnoredFiles.Files.Single().DeleteCommand!.ExecuteAsync();
        var candidate = dialogs.Confirmations.Single().Candidates.Single();
        Assert.AreEqual(expectedSize, candidate.ExpectedSize);
        Assert.AreEqual(expectedLastWriteTimeUtc, candidate.ExpectedLastWriteTimeUtc);
        Assert.AreEqual(0, recycle.DeleteBatches.Count);
        File.Move(path, path + ".off");
        await model.DeleteIgnoredFilesAsync([path]);
        Assert.IsTrue(File.Exists(path + ".off"), "忽略的原路径缺失时不得改删.off");
        Assert.AreEqual(1, dialogs.Confirmations.Count);
        File.Move(path + ".off", path);
        recycle.CanRecycleFiles = false;
        await model.DeleteIgnoredFilesAsync([path]);
        Assert.AreEqual(1, dialogs.Confirmations.Count);
        recycle.CanRecycleFiles = true;
        dialogs.Confirm = _ => { File.WriteAllText(path, "changed after confirmation"); return true; };
        await model.DeleteIgnoredFilesAsync([path]);
        Assert.AreEqual(2, dialogs.Confirmations.Count);
        Assert.AreEqual(0, recycle.DeleteBatches.Count);
        Assert.AreEqual("changed after confirmation", File.ReadAllText(path));
        Assert.IsTrue(File.Exists(other));
        Assert.AreEqual(1, store.Load().Ignored.Count);
        Assert.AreEqual(0, model.RecentDeletedFiles.Files.Count);
        Assert.IsTrue(dialogs.Problems.Count >= 3);
        Assert.IsFalse(model.IsBusy);
    });

    [TestMethod]
    public Task IgnoredDeletionPartialFailureLocksCommandsAndKeepsDisabledProvenance() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var a = NewFile(files.Root, "a.package");
        var b = NewFile(files.Root, "b.package");
        var disabledPath = NewFile(files.Root, "disabled.package");
        var disabled = new ModDisableService(files.Store.DataDirectory);
        disabled.Disable([disabledPath]);
        var off = disabledPath + ".off";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FileManagementStore(files.Folder("Sims4ModSieve"));
        var recycle = new TemporaryRecycleBin(files.Folder("Recycle")) { FailDeletePath = b, DeleteRelease = release.Task };
        var service = new FileActionService(recycle, new PhysicalFileMetadataAccess(), store, disabled);
        var dialogs = new DeleteDialogs();
        var model = Model(files, service, store, dialogs: dialogs, disabled: disabled);
        model.AddIgnoredFiles([a, b, off]);
        model.IgnoredFiles.ToggleMultiSelectCommand.Execute(null);
        model.IgnoredFiles.SelectAllCommand.Execute(null);
        var deleting = model.IgnoredFiles.BulkDeleteCommand!.ExecuteAsync();
        await recycle.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(model.IsBusy);
        Assert.IsFalse(model.IgnoredFiles.BulkDeleteCommand.CanExecute(null));
        Assert.IsFalse(model.IgnoredFiles.BulkRemoveCommand!.CanExecute(null));
        Assert.IsFalse(model.IgnoredFiles.AddCommand!.CanExecute(null));
        Assert.IsTrue(model.IgnoredFiles.Files.All(row => !row.DeleteCommand!.CanExecute(null)));
        await model.DeleteIgnoredFilesAsync([a]);
        release.SetResult();
        await deleting;
        Assert.AreEqual(1, dialogs.Confirmations.Count);
        Assert.AreEqual(1, recycle.DeleteBatches.Count);
        Assert.IsFalse(File.Exists(a));
        Assert.IsTrue(File.Exists(b));
        Assert.IsFalse(File.Exists(off));
        Assert.IsFalse(File.Exists(disabledPath));
        Assert.AreEqual(0, model.DisabledFiles.Files.Count);
        Assert.AreEqual(2, model.RecentDeletedFiles.Files.Count);
        Assert.IsTrue(dialogs.Problems.Any(problem => problem.Path == b));
        Assert.AreEqual(3, model.IgnoredFiles.Files.Count);
        Assert.IsTrue(model.IgnoredFiles.Files.Single(row => row.Path == b).IsSelected, "失败项保留勾选供重试");
        Assert.IsTrue(model.IgnoredFiles.Files.Where(row => row.Path != b).All(row => !row.IsSelected), "成功项清空勾选，避免重试被缺失路径阻断");
        Assert.IsTrue(model.IgnoredFiles.BulkDeleteCommand.CanExecute(null));
        await model.UndoLastDeleteCommand.ExecuteAsync();
        Assert.IsTrue(File.Exists(a));
        Assert.IsTrue(File.Exists(b));
        Assert.IsTrue(File.Exists(off));
        Assert.IsFalse(File.Exists(disabledPath));
        Assert.AreEqual(disabledPath, model.DisabledFiles.Files.Single().Path);
        Assert.AreEqual(0, model.RecentDeletedFiles.Files.Count);
        Assert.AreEqual(3, store.Load().Ignored.Count);
        Assert.IsFalse(model.CanUndoLastDelete);
    });

    private static MainWindowViewModel Model(Scenario files, IFileActionService? actions = null, FileManagementStore? store = null,
        Func<IReadOnlyList<string>>? pick = null, IFileActionDialogService? dialogs = null, ModDisableService? disabled = null) =>
        new(new DuplicateRunService(DuplicateScanner.CreateDefault()), new Picker(), files.Store,
            actions, dialogs ?? new Dialogs(), settingsSupport: new SettingsSupportService(files.Root), fileManagementStore: store,
            pickIgnoredFiles: pick, modDisableService: disabled);

    private static string NewFile(string directory, string name, string contents = "same")
    { var path = Path.Combine(directory, name); File.WriteAllText(path, contents); return path; }

    private static Task<FileActionBatchResult> Delete(FileActionService service, string path)
    {
        var info = new FileInfo(path);
        return service.DeleteAsync(service.Preflight([new(path, info.Length, info.LastWriteTimeUtc, "第1组")], []));
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
        private int sequence;
        public bool QueryFails { get; set; }
        public bool CanRecycleFiles { get; set; } = true;
        public string? FailDeletePath { get; set; }
        public Task? DeleteRelease { get; set; }
        public TaskCompletionSource DeleteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string[]> DeleteBatches { get; } = [];
        public bool CanRecycle(string path) => CanRecycleFiles;
        public async Task<RecycleBinDeleteResult> MoveToRecycleBinAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            DeleteBatches.Add(paths.ToArray());
            DeleteStarted.TrySetResult();
            if (DeleteRelease is not null) await DeleteRelease;
            var items = new List<RecycleBinItem>();
            var failures = new List<FileActionFailure>();
            foreach (var path in paths)
            {
                if (path == FailDeletePath) { failures.Add(new(path, "模拟回收失败")); continue; }
                sequence++;
                var target = Path.Combine(directory, sequence.ToString());
                File.Move(path, target);
                items.Add(new(path, [4, 0, (byte)sequence, 0, 0, 0], target));
            }
            return new RecycleBinDeleteResult(items, failures);
        }
        public Task<RecycleBinPresence> QueryAsync(IReadOnlyList<RecycleBinItem> items, CancellationToken cancellationToken = default) =>
            Task.FromResult(QueryFails ? new RecycleBinPresence([], [], [new("", "无法访问回收站")]) :
                new(items.Where(item => File.Exists(item.RecycledPath)).ToArray(), items.Where(item => !File.Exists(item.RecycledPath)).ToArray(), []));
        public Task<FileActionBatchResult> RestoreAsync(IReadOnlyList<RecycleBinItem> items, CancellationToken cancellationToken = default)
        {
            var completed = new List<string>(); var failed = new List<FileActionFailure>();
            foreach (var item in items)
            {
                if (File.Exists(item.OriginalPath)) { failed.Add(new(item.OriginalPath, "原位置已有同名文件，未覆盖")); continue; }
                File.Move(item.RecycledPath!, item.OriginalPath);
                completed.Add(item.OriginalPath);
            }
            return Task.FromResult(new FileActionBatchResult(completed, failed, false));
        }
    }

    private sealed class DeleteDialogs : IFileActionDialogService
    {
        public Func<FileDeletionPreflight, bool> Confirm { get; set; } = _ => true;
        public List<FileDeletionPreflight> Confirmations { get; } = [];
        public List<FileActionFailure> Problems { get; } = [];
        public bool ConfirmDeletion(FileDeletionPreflight preflight) { Confirmations.Add(preflight); return Confirm(preflight); }
        public void ShowProblems(string title, IReadOnlyList<FileActionFailure> failures) => Problems.AddRange(failures);
    }
}
