using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;
using static Sims4ModSieve.Desktop.Tests.ConflictResultSessionTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ConflictMultiSelectTests
{
    [TestMethod]
    public Task MultiSelectRevealsCheckboxesActionsAndLiveCount() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        WpfTestHost.PlaceOnScreen(window);
        await SetSessionAsync(model, Session(files.A, files.B, files.C), "all");
        await WpfTestHost.LayoutAsync(window);
        var toggle = Descendants<ToggleButton>(window).Single(button => button.Name == "ConflictMultiSelectButton");
        Assert.IsTrue(toggle.IsVisible);
        Assert.IsFalse(toggle.IsChecked);
        Assert.AreEqual("多选", toggle.Content);
        var checkboxes = Descendants<CheckBox>(window).Where(box => box.Name is "FileSelection" or "GroupSelection").ToArray();
        Assert.AreEqual(4, checkboxes.Length);
        Assert.IsTrue(checkboxes.All(box => !box.IsVisible));
        Assert.IsFalse(Descendants<Button>(window).Single(button => button.Name == "DeleteSelectedConflictFilesButton").IsVisible);
        Assert.IsFalse(Descendants<Button>(window).Single(button => button.Name == "DisableSelectedConflictFilesButton").IsVisible);

        await WpfTestHost.ClickAsync(toggle);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(model.IsConflictMultiSelect);
        Assert.IsTrue(toggle.IsChecked);
        Assert.AreEqual("取消多选", toggle.Content);
        Assert.IsTrue(checkboxes.Where(box => box.Name == "FileSelection").All(box => box.IsVisible));
        Assert.IsTrue(checkboxes.Single(box => box.Name == "GroupSelection").IsVisible);
        var delete = Descendants<Button>(window).Single(button => button.Name == "DeleteSelectedConflictFilesButton");
        var disable = Descendants<Button>(window).Single(button => button.Name == "DisableSelectedConflictFilesButton");
        Assert.IsTrue(delete.IsVisible && !delete.IsEnabled);
        Assert.IsTrue(disable.IsVisible && !disable.IsEnabled);
        Assert.AreEqual("已勾选 0 份文件", model.ConflictSelectionText);
        Assert.AreEqual("已勾选 0 份文件", NoteText(window).Text);
        Save(window, "multi-select-on.png");

        var row = model.ConflictRows.Single(item => item.Kind == ConflictRowKind.File && item.File?.Path == files.A);
        var box = Descendants<CheckBox>(window).Single(item => item.Name == "FileSelection" && ReferenceEquals(item.DataContext, row));
        await WpfTestHost.ClickAsync(box);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(1, model.ConflictSelectedCount);
        Assert.AreEqual("已勾选 1 份文件", NoteText(window).Text);
        Assert.IsTrue(row.Selection.IsSelected);
        Assert.IsTrue(delete.IsEnabled && disable.IsEnabled);
        Save(window, "multi-select-one.png");

        await WpfTestHost.ClickAsync(box);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(0, model.ConflictSelectedCount);
        Assert.IsFalse(row.Selection.IsSelected);

        await WpfTestHost.ClickAsync(toggle);
        await WpfTestHost.LayoutAsync(window);
        Assert.IsFalse(model.IsConflictMultiSelect);
        Assert.AreEqual("多选", toggle.Content);
        Assert.IsTrue(checkboxes.All(box => !box.IsVisible));
        Assert.IsFalse(delete.IsVisible);
    });

    [TestMethod]
    public Task GroupCheckboxSelectsAllFilesAndSyncsPartialState() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        WpfTestHost.PlaceOnScreen(window);
        await SetSessionAsync(model, Session(files.A, files.B, files.C), "all");
        await WpfTestHost.LayoutAsync(window);
        var toggle = Descendants<ToggleButton>(window).Single(button => button.Name == "ConflictMultiSelectButton");
        await WpfTestHost.ClickAsync(toggle);
        await WpfTestHost.LayoutAsync(window);

        var header = model.ConflictRows.Single(item => item.IsCardStart);
        var group = Descendants<CheckBox>(window).Single(box => box.Name == "GroupSelection");
        await WpfTestHost.ClickAsync(group);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(3, model.ConflictSelectedCount);
        Assert.AreEqual(true, header.Selection.IsSelected);
        Assert.AreEqual(true, group.IsChecked);
        Assert.IsTrue(model.ConflictRows.Where(item => item.Kind == ConflictRowKind.File).All(item => item.Selection.IsSelected == true));

        var row = model.ConflictRows.Single(item => item.Kind == ConflictRowKind.File && item.File?.Path == files.B);
        var box = Descendants<CheckBox>(window).Single(item => item.Name == "FileSelection" && ReferenceEquals(item.DataContext, row));
        await WpfTestHost.ClickAsync(box);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(2, model.ConflictSelectedCount);
        Assert.IsNull(header.Selection.IsSelected);
        Assert.IsNull(group.IsChecked);
        group.ApplyTemplate();
        Assert.AreEqual(Visibility.Visible, ((FrameworkElement)group.Template.FindName("Partial", group)!).Visibility);
        Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)group.Template.FindName("Check", group)!).Visibility);
        Save(window, "multi-select-partial.png");

        await WpfTestHost.ClickAsync(group);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(3, model.ConflictSelectedCount);
        Assert.AreEqual(true, header.Selection.IsSelected);
        group.ApplyTemplate();
        Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)group.Template.FindName("Partial", group)!).Visibility);
        Assert.AreEqual(Visibility.Visible, ((FrameworkElement)group.Template.FindName("Check", group)!).Visibility);
        Save(window, "multi-select-group.png");

        await WpfTestHost.ClickAsync(group);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(0, model.ConflictSelectedCount);
        Assert.AreEqual(false, header.Selection.IsSelected);
        Assert.IsTrue(model.ConflictRows.Where(item => item.Kind == ConflictRowKind.File).All(item => item.Selection.IsSelected == false));
    });

    [TestMethod]
    public Task SelectionSurvivesGroupExpansionRefresh() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, Session(files.A, files.B, files.C), "all");
        model.IsConflictMultiSelect = true;
        model.SetConflictFileSelected(model.ConflictRows.Single(row => row.Kind == ConflictRowKind.File && row.File?.Path == files.A), true);
        model.SetConflictFileSelected(model.ConflictRows.Single(row => row.Kind == ConflictRowKind.File && row.File?.Path == files.B), true);
        model.ToggleConflictExpandedCommand.Execute(model.ConflictRows.Single(row => row.IsCardStart));
        await model.ConflictRowsRefreshTask;
        model.ToggleConflictExpandedCommand.Execute(model.ConflictRows.Single(row => row.IsCardStart));
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(2, model.ConflictSelectedCount);
        Assert.IsNull(model.ConflictRows.Single(row => row.IsCardStart).Selection.IsSelected);
        Assert.AreEqual(2, model.ConflictRows.Count(row => row.Kind == ConflictRowKind.File && row.Selection.IsSelected == true));
        model.IsConflictMultiSelect = false;
        Assert.AreEqual(0, model.ConflictSelectedCount);
        Assert.IsTrue(model.ConflictRows.Where(row => row.Kind == ConflictRowKind.File).All(row => row.Selection.IsSelected != true));
    });

    [TestMethod]
    public Task RowDeleteActionAppliesToWholeSelectionWithCountedDialog() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var model = Model(files.Root, out var adapter, out var dialogs);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        await SetSessionAsync(model, Session(files.A, files.B, files.C), "all");
        model.IsConflictMultiSelect = true;
        var rows = model.ConflictRows.Where(row => row.Kind == ConflictRowKind.File).ToArray();
        model.SetConflictFileSelected(rows.Single(row => row.File!.Path == files.A), true);
        model.SetConflictFileSelected(rows.Single(row => row.File!.Path == files.B), true);
        var third = rows.Single(row => row.File!.Path == files.C);
        model.DeleteConflictFileCommand.Execute(third);
        await model.ConflictFileActionTask;
        CollectionAssert.AreEquivalent(new[] { files.A, files.B }, adapter.Deleted.ToArray());
        Assert.AreEqual(1, dialogs.Confirmations.Count);
        Assert.AreEqual("删除文件", dialogs.Confirmations[0].Title);
        Assert.AreEqual("请确认游戏已关闭。是否删除这 2 个文件？", dialogs.Confirmations[0].Message);
        CollectionAssert.AreEqual(new[] { files.C }, model.ConflictRows.Where(row => row.Kind == ConflictRowKind.File)
            .Select(row => row.File!.Path).ToArray());
        Assert.AreEqual(0, model.ConflictSelectedCount);
        Assert.IsTrue(model.CanUndoConflictDelete);
    });

    [TestMethod]
    public Task BatchDisableTurnsOffEveryCheckedFile() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var model = Model(files.Root, out var adapter, out var dialogs);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        await SetSessionAsync(model, Session(files.A, files.B, files.C), "all");
        model.IsConflictMultiSelect = true;
        var rows = model.ConflictRows.Where(row => row.Kind == ConflictRowKind.File).ToArray();
        model.SetConflictFileSelected(rows.Single(row => row.File!.Path == files.A), true);
        model.SetConflictFileSelected(rows.Single(row => row.File!.Path == files.C), true);
        await model.DisableSelectedConflictFilesCommand.ExecuteAsync();
        Assert.IsTrue(File.Exists(files.A + ".off"));
        Assert.IsTrue(File.Exists(files.C + ".off"));
        Assert.IsFalse(File.Exists(files.A));
        Assert.IsFalse(File.Exists(files.C));
        Assert.IsTrue(File.Exists(files.B));
        Assert.AreEqual(0, adapter.Deleted.Count);
        CollectionAssert.AreEqual(new[] { ("禁用文件", "请确认游戏已关闭。是否禁用这 2 个文件？") }, dialogs.Confirmations.ToArray());
        Assert.AreEqual(0, model.ConflictSelectedCount);
        Assert.AreEqual("已禁用 2 个文件，可删除 localthumbcache.package", model.ConflictFileActionStatusText);
    });

    [TestMethod]
    public Task BatchIgnoreConfirmsWithoutGameNoteAndIgnoresEveryCheckedFile() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var model = Model(files.Root, out _, out var dialogs);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        await SetSessionAsync(model, Session(files.A, files.B, files.C), "all");
        model.IsConflictMultiSelect = true;
        var rows = model.ConflictRows.Where(row => row.Kind == ConflictRowKind.File).ToArray();
        model.SetConflictFileSelected(rows.Single(row => row.File!.Path == files.A), true);
        model.SetConflictFileSelected(rows.Single(row => row.File!.Path == files.B), true);
        model.IgnoreConflictCommand.Execute(rows[0]);
        CollectionAssert.AreEqual(new[] { ("忽略文件", "是否忽略这 2 个文件？") }, dialogs.Confirmations.ToArray());
        CollectionAssert.AreEquivalent(new[] { files.A, files.B }, model.IgnoredFiles.Files.Select(row => row.Path).ToArray());
        Assert.AreEqual(0, model.ConflictSelectedCount);
        Assert.AreEqual("已忽略 2 个文件", model.ConflictFileActionStatusText);
    });

    [TestMethod]
    public Task MultiSelectContextMenuNamesSelectionActions() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        WpfTestHost.PlaceOnScreen(window);
        await SetSessionAsync(model, Session(files.A, files.B, files.C), "all");
        await WpfTestHost.LayoutAsync(window);
        model.IsConflictMultiSelect = true;
        model.SetConflictFileSelected(model.ConflictRows.Single(row => row.Kind == ConflictRowKind.File && row.File?.Path == files.A), true);
        var row = model.ConflictRows.Single(item => item.Kind == ConflictRowKind.File && item.File?.Path == files.B);
        var surface = Descendants<Border>(window).Single(border => border.Name == "FileSurface" && ReferenceEquals(border.DataContext, row));
        await WpfTestHost.LayoutAsync(window);
        await WpfTestHost.MoveMouseAsync(surface);
        RightClick();
        await Task.Delay(200);
        var menu = surface.ContextMenu;
        Assert.IsTrue(menu.IsOpen);
        CollectionAssert.AreEqual(new[] { "打开所在文件夹", "复制路径", "禁用所选", "删除所选", "查看文件关系", "忽略所选" },
            menu.Items.OfType<MenuItem>().Select(item => item.Header.ToString()).ToArray());
        menu.IsOpen = false;
    });

    [TestMethod]
    public Task CancelledBatchConfirmationKeepsCheckedFiles() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var model = Model(files.Root, out var adapter, out var dialogs);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        await SetSessionAsync(model, Session(files.A, files.B, files.C), "all");
        model.IsConflictMultiSelect = true;
        var rows = model.ConflictRows.Where(row => row.Kind == ConflictRowKind.File).ToArray();
        model.SetConflictFileSelected(rows.Single(row => row.File!.Path == files.A), true);
        model.SetConflictFileSelected(rows.Single(row => row.File!.Path == files.B), true);
        dialogs.Confirm = false;
        await model.DeleteSelectedConflictFilesCommand.ExecuteAsync();
        Assert.AreEqual(0, adapter.Deleted.Count);
        Assert.AreEqual(2, model.ConflictSelectedCount);
        Assert.AreEqual(3, model.ConflictRows.Count(row => row.Kind == ConflictRowKind.File));
    });

    [TestMethod]
    public Task MultiFileConfirmationRendersOperationTitleMessageAndYesNoButtons() => WpfTestHost.RunAsync(async () =>
    {
        var window = new MultiFileConfirmationWindow("删除文件", "请确认游戏已关闭。是否删除这 3 个文件？")
        {
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
        };
        window.Show();
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual("删除文件", window.Title);
        var message = Descendants<TextBlock>(window).Single(text => text.Name == "MessageText");
        Assert.AreEqual("请确认游戏已关闭。是否删除这 3 个文件？", message.Text);
        Assert.IsTrue(message.IsVisible);
        var buttons = Descendants<Button>(window).ToArray();
        CollectionAssert.AreEqual(new[] { "否", "是" }, buttons.Select(button => button.Content.ToString()).ToArray());
        Assert.AreEqual(2, Descendants<System.Windows.Shapes.Path>(window).Count());
        Save(window, "multi-file-confirmation.png");
        window.Close();
    });

    private static TextBlock NoteText(Window window) => Descendants<TextBlock>(window).Single(text => text.Name == "ConflictSelectionNote");

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    private static void RightClick()
    {
        mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
    }

    private static MainWindowViewModel Model(string root, out TestRecycleBin adapter, out RecordingDialogs dialogs)
    {
        var dataDirectory = Path.Combine(root, "store");
        Directory.CreateDirectory(dataDirectory);
        adapter = new TestRecycleBin();
        dialogs = new RecordingDialogs();
        var store = new FileManagementStore(dataDirectory);
        var disable = new ModDisableService(dataDirectory);
        return new MainWindowViewModel(new EmptyRun(), new NoPicker(), new Settings(root),
            new FileActionService(adapter, new PhysicalFileMetadataAccess(), store, disable), dialogs,
            fileManagementStore: store, modDisableService: disable, confirmModChange: _ => true);
    }

    // 只有真实文件戳才能通过删除前核对；会话直接按磁盘状态构造。
    private static ConflictResultSession Session(params string[] paths)
    {
        var stamps = paths.ToDictionary(path => path, path =>
        {
            var info = new FileInfo(path);
            return new FileStamp(info.Length, info.LastWriteTimeUtc, PhysicalFileIdentity.Read(path));
        }, StringComparer.OrdinalIgnoreCase);
        var key = new Sims4ModSieve.Core.Packages.ResourceKey(0x034AEECB, 0, 1);
        var comparison = new ResourceContentComparison(key,
            paths.Select((path, index) => new ResourceContentSubgroup($"hash{index}", 1,
            [
                new ResourceContentOccurrence(key, path, 0, stamps[path], $"hash{index}", 1, null),
            ])).ToArray(), []);
        var report = new PackageConflictScanReport(DateTime.UnixEpoch, DateTime.UnixEpoch, [], paths.Length,
            paths.Length, [], [], [])
        {
            ResourceInventories = paths.Select(path => new PackageResourceInventory(path, stamps[path], 1)
            {
                ResourceTypeCounts = new Dictionary<uint, int> { [0x034AEECB] = 1 },
            }).ToArray(),
        };
        var contents = new ResourceContentComparisonReport(DateTime.UnixEpoch, DateTime.UnixEpoch, new(),
            [comparison],
            paths.Select(path => new ResourceContentPackageVersion(path, stamps[path], stamps[path], true)).ToArray(), 0);
        var session = new ConflictResultSession();
        session.Replace(report, contents, Scripts());
        foreach (var card in session.Cards) session.ToggleExpanded(card.Key);
        return session;
    }

    private sealed class EmptyRun : IDuplicateRunService
    {
        public Task<DuplicateReport> RunAsync(DuplicateRunInput input, IProgress<DuplicateScanProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DuplicateReport(DateTime.UnixEpoch, DateTime.UnixEpoch, [], 0, 0, [], []));
    }

    private sealed class NoPicker : IFolderPickerService
    {
        public string? PickFolder(string title, string? initialDirectory = null) => null;
    }

    private sealed class Settings(string root) : IDuplicateSettingsStore
    {
        public DuplicateSettings? Load() => new(root, []);
        public void Save(DuplicateSettings settings) { }
    }

    private sealed class RecordingDialogs : IFileActionDialogService
    {
        public List<(string Title, string Message)> Confirmations { get; } = [];
        public List<FileActionFailure> Problems { get; } = [];
        public bool Confirm { get; set; } = true;
        public bool ConfirmDeletion(FileDeletionPreflight preflight) => true;
        public bool ConfirmFiles(string title, string message) { Confirmations.Add((title, message)); return Confirm; }
        public void ShowProblems(string title, IReadOnlyList<FileActionFailure> failures) => Problems.AddRange(failures);
    }

    private sealed class TestRecycleBin : IRecycleBinAdapter
    {
        private int nextPidl;
        public List<string> Deleted { get; } = [];
        public bool CanRecycle(string path) => true;
        public Task<RecycleBinDeleteResult> MoveToRecycleBinAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            Deleted.AddRange(paths);
            return Task.FromResult(new RecycleBinDeleteResult(paths.Select(path =>
                new RecycleBinItem(path, [1, 2, (byte)++nextPidl, 0])).ToArray(), []));
        }
        public Task<FileActionBatchResult> RestoreAsync(IReadOnlyList<RecycleBinItem> items, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FileActionBatchResult(items.Select(item => item.OriginalPath).ToArray(), [], false));
    }

    private sealed class TemporaryFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "s4ms-multi-select-" + Guid.NewGuid().ToString("N"));
        public string A => Path.Combine(Root, "a.package");
        public string B => Path.Combine(Root, "b.package");
        public string C => Path.Combine(Root, "c.package");

        public TemporaryFiles()
        {
            Directory.CreateDirectory(Root);
            foreach (var path in new[] { A, B, C })
            {
                File.WriteAllBytes(path, new byte[10]);
                File.SetLastWriteTimeUtc(path, Stamp.LastWriteTimeUtc);
            }
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
