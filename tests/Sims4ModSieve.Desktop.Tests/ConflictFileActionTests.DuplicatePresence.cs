using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Sims4ModSieve.Desktop.Components;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;

namespace Sims4ModSieve.Desktop.Tests;

public sealed partial class ConflictFileActionTests
{
    [TestMethod]
    [DataRow("duplicates")]
    [DataRow("delete")]
    [DataRow("disable")]
    [DataRow("bulk-delete")]
    [DataRow("bulk-disable")]
    public Task MissingKeepRefreshesBothSessionsAndSkipsEmptyAction(string entry) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var report = Duplicates(files.A, files.B);
        var dialogs = new PresenceDialogs();
        var model = PresenceModel(files, report, dialogs, out var adapter);
        var session = await PresenceSessionAsync(model, report);
        SelectPresenceFiles(model, files.B);
        File.Delete(files.A);
        await RunPresenceActionAsync(model, entry, files.B);
        CollectionAssert.AreEqual(new[] { "refresh:1" }, dialogs.Events);
        Assert.AreEqual(0, model.Groups.Count);
        Assert.AreEqual(0, session.Cards.Count);
        Assert.AreEqual(0, model.ConflictSelectedCount);
        Assert.AreEqual(0, adapter.Deleted.Count);
        Assert.IsTrue(File.Exists(files.B));
        Assert.IsFalse(File.Exists(files.B + ".off"));
        session.ApplyDeletedPaths([]);
        Assert.AreEqual(0, session.Cards.Count, "后续重建不能带回已失去重复身份的卡");
    });

    [TestMethod]
    [DataRow("duplicates")]
    [DataRow("delete")]
    [DataRow("disable")]
    [DataRow("bulk-delete")]
    [DataRow("bulk-disable")]
    public Task MissingMemberRefreshesBeforeConfirmingRemainingAction(string entry) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var report = Duplicates(files.A, files.B, files.C);
        var dialogs = new PresenceDialogs();
        var model = PresenceModel(files, report, dialogs, out var adapter);
        var session = await PresenceSessionAsync(model, report);
        SelectPresenceFiles(model, files.A, files.B);
        File.Delete(files.A);
        dialogs.OnConfirm = () =>
        {
            CollectionAssert.AreEquivalent(new[] { files.B, files.C }, model.Groups.Single().Files.Select(file => file.Path).ToArray());
            Assert.AreEqual(1, model.SelectedFileCount);
            Assert.AreEqual(1, model.ConflictSelectedCount);
            Assert.AreEqual(2, session.Cards.Single().Files.Count);
        };
        await RunPresenceActionAsync(model, entry, files.B);
        CollectionAssert.AreEqual(new[] { "refresh:1", "confirm" }, dialogs.Events);
        if (entry.Contains("disable", StringComparison.Ordinal)) Assert.IsTrue(File.Exists(files.B + ".off"));
        else CollectionAssert.AreEqual(new[] { files.B }, adapter.Deleted);
    });

    [TestMethod]
    [DataRow("duplicates")]
    [DataRow("delete")]
    [DataRow("disable")]
    [DataRow("bulk-delete")]
    [DataRow("bulk-disable")]
    public Task ExistingMembersDoNotWarnAndWholeGroupSelectionRemainsAllowed(string entry) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var report = Duplicates(files.A, files.B);
        var dialogs = new PresenceDialogs();
        var model = PresenceModel(files, report, dialogs, out var adapter);
        await PresenceSessionAsync(model, report);
        SelectPresenceFiles(model, files.A, files.B);
        Assert.IsTrue(model.Groups.Single().IsSuggestionSelected);
        await RunPresenceActionAsync(model, entry, files.B);
        CollectionAssert.AreEqual(new[] { "confirm" }, dialogs.Events);
        var expected = entry.StartsWith("bulk", StringComparison.Ordinal) || entry == "duplicates" ? 2 : 1;
        if (entry.Contains("disable", StringComparison.Ordinal)) Assert.AreEqual(expected, model.DisabledFiles.Files.Count);
        else Assert.AreEqual(expected, adapter.Deleted.Count);
    });

    [TestMethod]
    [DataRow("duplicates")]
    [DataRow("bulk-delete")]
    [DataRow("bulk-disable")]
    public Task MixedGroupsRefreshOnlyInvolvedGroupsAndContinueOtherSelections(string entry) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var paths = Enumerable.Range(0, 4).Select(index => Path.Combine(files.Root, $"extra-{index}.package")).ToArray();
        foreach (var path in paths) File.WriteAllText(path, Array.IndexOf(paths, path) < 2 ? "other content" : "third content");
        var first = Duplicates(files.A, files.B);
        var second = Duplicates(paths[0], paths[1]);
        var third = Duplicates(paths[2], paths[3]);
        var report = first with { Groups = [first.Groups.Single(), second.Groups.Single(), third.Groups.Single()] };
        var dialogs = new PresenceDialogs();
        var model = PresenceModel(files, report, dialogs, out var adapter);
        var session = await PresenceSessionAsync(model, report);
        SelectPresenceFiles(model, files.A, files.B, paths[0]);
        File.Delete(files.A);
        File.Delete(paths[2]);
        // 隐藏结果行，仍须使用会话里的完整组和选择。
        model.SelectConflictViewCommand.Execute("resources");
        await model.ConflictRowsRefreshTask;
        await RunPresenceActionAsync(model, entry, files.B);
        CollectionAssert.AreEqual(new[] { "refresh:1", "confirm" }, dialogs.Events);
        Assert.IsTrue(session.Cards.Any(card => card.Files.Any(file => file.Path == paths[2])), "未涉及的组不刷新");
        if (entry.Contains("disable", StringComparison.Ordinal)) Assert.IsTrue(File.Exists(paths[0] + ".off"));
        else CollectionAssert.AreEqual(new[] { paths[0] }, adapter.Deleted);
        Assert.IsTrue(File.Exists(files.B), "失去重复身份的选择不继续操作");
    });

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, false, true)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public Task DisabledMemberUsesRecordedOffPathBeforeConflictDeletion(bool missing, bool bulk, bool selectDisabled) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var report = Duplicates(files.A, files.B);
        var disable = new ModDisableService(Path.Combine(files.Root, "data"));
        Assert.AreEqual(1, disable.Disable([files.A]).CompletedPaths.Count);
        var dialogs = new PresenceDialogs();
        var model = PresenceModel(files, report, dialogs, out var adapter, disable);
        var session = await PresenceSessionAsync(model, report);
        var selected = selectDisabled ? files.A : files.B;
        SelectPresenceFiles(model, selected);
        if (missing) File.Delete(files.A + ".off");
        await RunPresenceActionAsync(model, bulk ? "bulk-delete" : "delete", selected);
        CollectionAssert.AreEqual(missing ? new[] { "refresh:1" } : new[] { "confirm" }, dialogs.Events);
        if (missing) { Assert.AreEqual(0, adapter.Deleted.Count); Assert.AreEqual(0, session.Cards.Count); }
        else CollectionAssert.AreEqual(new[] { selectDisabled ? files.A + ".off" : files.B }, adapter.Deleted);
    });

    [TestMethod]
    public Task ClosingRefreshWarningLeavesResultsAndSelectionUnchanged() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var report = Duplicates(files.A, files.B);
        var dialogs = new PresenceDialogs { AcceptRefresh = false };
        var model = PresenceModel(files, report, dialogs, out var adapter);
        var session = await PresenceSessionAsync(model, report);
        SelectPresenceFiles(model, files.B);
        File.Delete(files.A);
        await model.DeleteSelectedCommand.ExecuteAsync();
        CollectionAssert.AreEqual(new[] { "refresh:1" }, dialogs.Events);
        Assert.AreEqual(2, model.Groups.Single().Files.Count);
        Assert.AreEqual(2, session.Cards.Single().Files.Count);
        Assert.AreEqual(1, model.SelectedFileCount);
        Assert.AreEqual(0, adapter.Deleted.Count);
    });

    [TestMethod]
    public Task ExistingKeepContentIsNotReadOrCompared() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var report = Duplicates(files.A, files.B);
        var dialogs = new PresenceDialogs();
        var model = PresenceModel(files, report, dialogs, out var adapter);
        SelectPresenceFiles(model, files.B);
        File.AppendAllText(files.A, "changed keep content and size");
        using var lockedKeep = File.Open(files.A, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await model.DeleteSelectedCommand.ExecuteAsync();
        CollectionAssert.AreEqual(new[] { "confirm" }, dialogs.Events);
        CollectionAssert.AreEqual(new[] { files.B }, adapter.Deleted);
    });

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public Task RefreshWarningUsesWarningDialogAndWorkingButtons(bool refresh) => WpfTestHost.RunAsync(() =>
    {
        var owner = new Window();
        WpfTestHost.ShowOffScreen(owner);
        Application.Current.MainWindow = owner;
        Exception? failure = null;
        var inspected = false;
        owner.Dispatcher.BeginInvoke(() =>
        {
            var dialog = Application.Current.Windows.OfType<WarningDialog>().Single();
            try
            {
                dialog.UpdateLayout();
                Assert.AreSame(owner, dialog.Owner);
                Assert.AreEqual("文件有变动", dialog.Title);
                Assert.AreEqual("有 2 组结果里的部分文件已不存在。刷新结果后，再继续确认剩下的文件。",
                    ((TextBlock)dialog.FindName("MessageText")).Text);
                Assert.AreEqual("刷新结果", ((Button)dialog.FindName("ActionButton")).Content);
                Assert.AreEqual("关闭", Descendants<Button>(dialog).Single(item => item.IsCancel).Content);
                var button = refresh ? (Button)dialog.FindName("ActionButton")
                    : Descendants<Button>(dialog).Single(item => item.IsCancel);
                Assert.IsTrue(button.IsVisible && button.IsEnabled);
                var hit = dialog.InputHitTest(button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), dialog));
                Assert.IsNotNull(hit);
                Assert.AreSame(button, AncestorButton(hit as DependencyObject));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                inspected = true;
            }
            catch (Exception exception) { failure = exception; dialog.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        var result = new FileActionDialogService().ConfirmResultRefresh(2);
        if (failure is not null) throw failure;
        Assert.IsTrue(inspected);
        Assert.AreEqual(refresh, result);
    });

    private static Button? AncestorButton(DependencyObject? node)
    {
        while (node is not null && node is not Button) node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        return node as Button;
    }

    private static MainWindowViewModel PresenceModel(TemporaryFiles files, DuplicateReport report, PresenceDialogs dialogs,
        out TestRecycleBin adapter, ModDisableService? disable = null)
    {
        var duplicates = new DuplicateResultSession();
        duplicates.Replace(duplicates.PrepareGroups(report.Groups));
        disable ??= new ModDisableService(Path.Combine(files.Root, "data"));
        var store = new FileManagementStore(Path.Combine(files.Root, "data"));
        adapter = new TestRecycleBin();
        return new(new RecordingDuplicateRun(report), new NoPicker(), new Settings(files.Root),
            new FileActionService(adapter, new PhysicalFileMetadataAccess(), store, disable), dialogs,
            resultSession: duplicates, modDisableService: disable, fileManagementStore: store);
    }

    private static async Task<ConflictResultSession> PresenceSessionAsync(MainWindowViewModel model, DuplicateReport report)
    {
        var session = V2([], [], report);
        foreach (var card in session.Cards) session.ToggleExpanded(card.Key);
        await SetSessionAsync(model, session, "all");
        return session;
    }

    private static void SelectPresenceFiles(MainWindowViewModel model, params string[] paths)
    {
        foreach (var file in model.Groups.SelectMany(group => group.Files)) file.IsSelected = paths.Contains(file.Path);
        model.IsConflictMultiSelect = true;
        foreach (var row in model.ConflictRows.Where(row => row.IsSelectableFile && paths.Contains(row.File!.Path)))
            model.SetConflictFileSelected(row, true);
    }

    private static Task RunPresenceActionAsync(MainWindowViewModel model, string entry, string path) => entry switch
    {
        "duplicates" => model.DeleteSelectedCommand.ExecuteAsync(),
        "bulk-delete" => model.DeleteSelectedConflictFilesCommand.ExecuteAsync(),
        "bulk-disable" => model.DisableSelectedConflictFilesCommand.ExecuteAsync(),
        "delete" => model.DeleteConflictFileAsync(model.ConflictRows.Single(row => row.IsSelectableFile && row.File!.Path == path)),
        _ => model.ToggleConflictFileEnabledAsync(model.ConflictRows.Single(row => row.IsSelectableFile && row.File!.Path == path)),
    };

    private sealed class PresenceDialogs : IFileActionDialogService
    {
        public List<string> Events { get; } = [];
        public bool AcceptRefresh { get; init; } = true;
        public Action? OnConfirm { get; set; }
        public bool ConfirmResultRefresh(int count) { Events.Add($"refresh:{count}"); return AcceptRefresh; }
        public bool ConfirmDeletion(FileDeletionPreflight preflight) => ConfirmFiles("", "");
        public bool ConfirmFiles(string title, string message) { Events.Add("confirm"); OnConfirm?.Invoke(); return true; }
        public void ShowProblems(string title, IReadOnlyList<FileActionFailure> failures) => Assert.Fail(title);
    }
}
