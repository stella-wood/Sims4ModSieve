using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Sims4ModSieve.Desktop.Components;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Caching;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;

namespace Sims4ModSieve.Desktop.Tests;

public sealed partial class ConflictFileActionTests
{
    [TestMethod]
    public Task BatchDisableUsesDuplicateHashForScriptOnBothCards() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var scriptRoot = Directory.CreateDirectory(Path.Combine(files.Root, "scripts")).FullName;
        var script = Path.Combine(scriptRoot, "first.ts4script");
        var copy = Path.Combine(scriptRoot, "copy.ts4script");
        WriteScript(script, "old");
        File.Copy(script, copy);
        WriteScript(Path.Combine(scriptRoot, "other.ts4script"), "new");
        var fileSystem = new PhysicalFileSystemAccess();
        var stamp = fileSystem.GetFileStamp(script);
        var scripts = await new ScriptModuleScanner(fileSystem).ScanAsync(new([new("scripts", scriptRoot)]));
        var report = Duplicates(script, copy);
        var session = V2([], [], report, scripts);
        Assert.AreEqual("script-conflict", session.Cards.First(card => card.Files.Any(file => file.Path == script)).Type);
        var duplicateCard = session.Cards.Single(card => card.Duplicate is not null);
        var dialogs = new PresenceDialogs { AllowProblems = true };
        var model = PresenceModel(files, report, dialogs, out _);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        await PreparePresenceSessionAsync(model, session);
        model.SelectConflictViewCommand.Execute("duplicates");
        await model.ConflictRowsRefreshTask;
        model.IsConflictMultiSelect = true;
        var window = OpenWindow(model);
        await WpfTestHost.LayoutAsync(window);
        var checkbox = Descendants<CheckBox>(window).Single(box => box.Name == "FileSelection"
            && box.DataContext is ConflictResultRow row && row.OwnerKey == duplicateCard.Key && row.File?.Path == script);
        Assert.IsTrue(checkbox.IsVisible && checkbox.IsEnabled);
        var changed = File.ReadAllBytes(script);
        changed[0] ^= 1;
        File.WriteAllBytes(script, changed);
        File.SetLastWriteTimeUtc(script, stamp.LastWriteTimeUtc);
        Assert.AreEqual(stamp, fileSystem.GetFileStamp(script));

        checkbox.SetCurrentValue(CheckBox.IsCheckedProperty, true);
        checkbox.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Assert.AreEqual(1, model.ConflictSelectedCount);
        var disable = Descendants<Button>(window).Single(button => button.Name == "DisableSelectedConflictFilesButton");
        Assert.IsTrue(disable.IsEnabled);
        Assert.AreSame(model.DisableSelectedConflictFilesCommand, disable.Command);
        await model.DisableSelectedConflictFilesCommand.ExecuteAsync();

        CollectionAssert.AreEqual(new[] { "confirm" }, dialogs.Events);
        Assert.IsTrue(File.Exists(script));
        Assert.IsFalse(File.Exists(script + ".off"));
        Assert.AreEqual(script, dialogs.Failures.Single().Path);
        StringAssert.Contains(dialogs.Failures.Single().Message, "文件内容在扫描后发生变化");

        static void WriteScript(string path, string content)
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            using var writer = new StreamWriter(archive.CreateEntry("kit/common.py").Open());
            writer.Write(content);
        }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task SuggestedDeletionSkipsUnsafeKeepAfterWarmScanAndContinuesOtherGroups(bool unreadable) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var mods = Directory.CreateDirectory(Path.Combine(files.Root, "Mods")).FullName;
        var data = Path.Combine(files.Root, "data");
        var keep = Path.Combine(mods, "a.package");
        var copy = Path.Combine(mods, "b.package");
        var otherCopy = Path.Combine(mods, "d.package");
        File.WriteAllText(keep, "same");
        File.Copy(keep, copy);
        File.WriteAllText(Path.Combine(mods, "c.package"), "other");
        File.Copy(Path.Combine(mods, "c.package"), otherCopy);
        var adapter = new TestRecycleBin();
        var dialogs = new PresenceDialogs { AllowProblems = true };
        var model = new MainWindowViewModel(new DuplicateRunService(DuplicateScanner.CreateDefault()), new NoPicker(), new Settings(mods),
            new FileActionService(adapter, new PhysicalFileMetadataAccess(), new FileManagementStore(data)), dialogs,
            scanCacheDirectory: data);
        await model.StartScanCommand.ExecuteAsync();
        Assert.AreEqual(2, model.Groups.Count);
        var stamp = new PhysicalFileSystemAccess().GetFileStamp(keep);
        using (var warm = CachingStableFileHasher.Open(data))
        {
            await warm.HashAsync(keep);
            Assert.AreEqual(1, warm.Hits);
        }
        if (!unreadable)
        {
            File.WriteAllText(keep, "diff");
            File.SetLastWriteTimeUtc(keep, stamp.LastWriteTimeUtc);
            Assert.AreEqual(stamp, new PhysicalFileSystemAccess().GetFileStamp(keep));
        }
        await model.StartScanCommand.ExecuteAsync();
        Assert.AreEqual(2, model.Groups.Count);
        model.ApplySuggestionsCommand.Execute(null);
        using var keepLock = unreadable ? File.Open(keep, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        await model.DeleteSelectedCommand.ExecuteAsync();

        CollectionAssert.AreEqual(new[] { otherCopy }, adapter.Deleted);
        Assert.IsTrue(File.Exists(copy));
        Assert.AreEqual(keep, dialogs.Failures.Single().Path);
        StringAssert.Contains(dialogs.Failures.Single().Message, "请重新扫描");
    });

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
    public Task RefreshNoticeAlwaysRefreshesResultsAndSelection() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var report = Duplicates(files.A, files.B);
        var dialogs = new PresenceDialogs();
        var model = PresenceModel(files, report, dialogs, out var adapter);
        var session = await PresenceSessionAsync(model, report);
        SelectPresenceFiles(model, files.B);
        File.Delete(files.A);
        await model.DeleteSelectedCommand.ExecuteAsync();
        CollectionAssert.AreEqual(new[] { "refresh:1" }, dialogs.Events);
        Assert.AreEqual(0, model.Groups.Count);
        Assert.AreEqual(0, session.Cards.Count);
        Assert.AreEqual(0, model.SelectedFileCount);
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
    public Task RefreshNoticeShowsOnlyRefreshButtonAndClosesOnClick() => WpfTestHost.RunAsync(() =>
    {
        var owner = new Window();
        WpfTestHost.ShowOffScreen(owner);
        Application.Current.MainWindow = owner;
        Exception? failure = null;
        var inspected = false;
        WarningDialog? notice = null;
        owner.Dispatcher.BeginInvoke(() =>
        {
            var dialog = Application.Current.Windows.OfType<WarningDialog>().Single();
            notice = dialog;
            try
            {
                dialog.UpdateLayout();
                Assert.AreSame(owner, dialog.Owner);
                Assert.AreEqual("提示", dialog.Title);
                Assert.AreEqual("相关组文件有变动，点击刷新",
                    ((TextBlock)dialog.FindName("MessageText")).Text);
                var button = Descendants<Button>(dialog).Single(item => item.IsVisible);
                Assert.AreSame(dialog.FindName("CloseButton"), button);
                Assert.AreEqual("刷新", button.Content);
                Assert.IsTrue(button.IsDefault && button.IsCancel);
                Assert.IsTrue(button.IsVisible && button.IsEnabled);
                var hit = dialog.InputHitTest(button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), dialog));
                Assert.IsNotNull(hit);
                Assert.AreSame(button, AncestorButton(hit as DependencyObject));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                inspected = true;
            }
            catch (Exception exception) { failure = exception; dialog.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        new FileActionDialogService().ShowResultRefreshNotice(2);
        if (failure is not null) throw failure;
        Assert.IsTrue(inspected);
        Assert.IsNotNull(notice);
        Assert.IsFalse(notice.IsVisible);
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
            resultSession: duplicates, modDisableService: disable, fileManagementStore: store, unexpectedErrorHandler: dialogs);
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

    private sealed class PresenceDialogs : IFileActionDialogService, IUnexpectedErrorHandler
    {
        public List<string> Events { get; } = [];
        public List<FileActionFailure> Failures { get; } = [];
        public bool AllowProblems { get; init; }
        public Action? OnConfirm { get; set; }
        public void ShowResultRefreshNotice(int count) => Events.Add($"refresh:{count}");
        public bool ConfirmDeletion(FileDeletionPreflight preflight) => ConfirmFiles("", "");
        public bool ConfirmFiles(string title, string message) { Events.Add("confirm"); OnConfirm?.Invoke(); return true; }
        public void ShowProblems(string title, IReadOnlyList<FileActionFailure> failures)
        {
            if (AllowProblems) Failures.AddRange(failures);
            else Assert.Fail(title + "\n" + string.Join("\n", failures.Select(failure => failure.Message)));
        }
        public void Report(string title, Exception exception) => ExceptionDispatchInfo.Throw(exception);
    }
}
