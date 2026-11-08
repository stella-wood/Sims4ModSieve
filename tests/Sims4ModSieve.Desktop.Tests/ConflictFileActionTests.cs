using System.IO;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Packages;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using Sims4ModSieve.Packages;
using static Sims4ModSieve.Desktop.Tests.ConflictResultSessionTests;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;
using static Sims4ModSieve.Desktop.Tests.ConflictV2SessionTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ConflictFileActionTests
{
    [TestMethod]
    [DataRow(".ts4script", "stable")]
    [DataRow(".package", "stable")]
    [DataRow(".ts4script", "changed")]
    [DataRow(".package", "changed")]
    [DataRow(".ts4script", "missing")]
    [DataRow(".package", "missing")]
    public Task BrokenFilesKeepMetadataAndAllowOnlyUnchangedFileDeletion(string extension, string state) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var source = Directory.CreateDirectory(Path.Combine(files.Root, "broken")).FullName;
        var path = Path.Combine(source, "损坏_不是有效格式" + extension);
        File.WriteAllText(path, "not a ZIP or DBPF file");
        var fileSystem = new PhysicalFileSystemAccess();
        var expected = fileSystem.GetFileStamp(path);
        var packages = await new PackageConflictScanner(fileSystem,
            new LlamaLogicPackageIndexReader(new PackagePrechecker(fileSystem), fileSystem))
            .ScanAsync(new([new("broken", source)]));
        var scripts = await new ScriptModuleScanner(fileSystem).ScanAsync(new([new("broken", source)]));
        if (state == "missing") File.Delete(path);
        var session = new ConflictResultSession();
        session.Replace(packages, Contents(), scripts);
        Assert.AreEqual("文件损坏，建议重新下载", session.Problems.Single().Summary);
        var adapter = new TestRecycleBin();
        var model = Model(files.Root, new RecordingDuplicateRun(Duplicates(files.A, files.B)), adapter: adapter);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        await SetSessionAsync(model, session, "all");
        var window = OpenWindow(model);
        await WpfTestHost.LayoutAsync(window);
        var row = model.ConflictRows.Single(item => item.File is not null);
        var header = model.ConflictRows.Single(item => item.IsCardStart);
        Assert.IsFalse(model.ConflictRows.Any(item => item.Kind == ConflictRowKind.Explanation && item.Title == "整个文件未分析"));
        var button = Descendants<Button>(window).Single(item => ReferenceEquals(item.Command, model.DeleteConflictFileCommand));
        if (state == "missing")
        {
            Assert.IsNull(row.File!.Stamp);
            Assert.IsFalse(button.IsEnabled);
            await model.DeleteConflictFileAsync(row);
            Assert.AreEqual(0, adapter.Deleted.Count);
            return;
        }
        Assert.AreEqual(expected, row.File!.Stamp);
        Assert.IsTrue(button.IsEnabled);
        Assert.IsFalse(row.File.Metadata.Contains("未知", StringComparison.Ordinal));
        Assert.AreEqual(expected, session.Relationships(path)!.File.Stamp);
        Assert.IsTrue(session.MatchesOriginalFile(path, expected.Length, expected.LastWriteTimeUtc));
        if (state == "changed") File.AppendAllText(path, "changed after results");
        model.SelectConflictFilterCommand.Execute("incomplete");
        await model.ConflictRowsRefreshTask;
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(expected, model.ConflictRows.Single(item => item.File is not null).File!.Stamp);
        Save(window, $"broken-file-{extension[1..]}-{state}.png");
        button = Descendants<Button>(window).Single(item => ReferenceEquals(item.Command, model.DeleteConflictFileCommand));
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
        await WpfTestHost.LayoutAsync(window);
        await model.ConflictFileActionTask;
        if (state == "changed")
        {
            Assert.AreEqual(0, adapter.Deleted.Count);
            Assert.IsTrue(model.ConflictRows.Any(item => item.File?.Path == path));
            return;
        }
        CollectionAssert.AreEqual(new[] { path }, adapter.Deleted.ToArray());
        Assert.AreEqual(0, model.ConflictRows.Count);
        Assert.IsTrue(model.CanUndoConflictDelete);
        await model.UndoLastDeleteCommand.ExecuteAsync();
        Assert.AreEqual(header.Key, model.ConflictRows.Single(item => item.IsCardStart).Key);
        Assert.AreEqual(header.GroupIndex, model.ConflictRows.Single(item => item.IsCardStart).GroupIndex);
        Assert.AreEqual(expected, model.ConflictRows.Single(item => item.File is not null).File!.Stamp);
    });

    private static DuplicateReport Duplicates(params string[] paths)
    {
        var files = paths.Select(path =>
        {
            var info = new FileInfo(path);
            return new DuplicateFile(path, info.Length, info.LastWriteTimeUtc, [], [], true,
                path.Count(character => character == '\\'), PhysicalFileIdentity.Read(path));
        }).ToArray();
        var selection = new DuplicateSelectionService().Select(files);
        var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths[0])));
        return new(DateTime.UnixEpoch, DateTime.UnixEpoch, [], paths.Length, paths.Length,
            [new(sha256, files[0].Size, DuplicateSection.Ordinary, files, selection.KeepPath, selection.DeletePaths)], []);
    }

    private static ConflictResultSession V2(ResourceContentComparison[] comparisons,
        (string Path, int Count)[] inventories, DuplicateReport? duplicates = null, ScriptModuleScanReport? scripts = null)
    {
        var session = new ConflictResultSession();
        var stamps = inventories.ToDictionary(item => item.Path, item => File.Exists(item.Path)
            ? new FileStamp(new FileInfo(item.Path).Length, File.GetLastWriteTimeUtc(item.Path), PhysicalFileIdentity.Read(item.Path))
            : Stamp, StringComparer.OrdinalIgnoreCase);
        var report = new PackageConflictScanReport(DateTime.UnixEpoch, DateTime.UnixEpoch, [], inventories.Length,
            inventories.Length, [], [], [])
        {
            ResourceInventories = inventories.Select(item => new PackageResourceInventory(item.Path, stamps[item.Path], item.Count)
            { ResourceTypeCounts = new Dictionary<uint, int> { [0x034AEECB] = item.Count } }).ToArray(),
        };
        var updatedComparisons = comparisons.Select(comparison => comparison with
        {
            ContentGroups = comparison.ContentGroups.Select(group => group with
            {
                Occurrences = group.Occurrences.Select(item => item with { PackageStamp = stamps[item.PackagePath] }).ToArray(),
            }).ToArray(),
        }).ToArray();
        var contents = Contents(updatedComparisons);
        contents = contents with { Packages = contents.Packages.Select(item => item with
        { ScannedStamp = stamps[item.Path], StampAfterRead = stamps[item.Path] }).ToArray() };
        session.Replace(report, contents, scripts ?? Scripts(), duplicates: duplicates);
        return session;
    }

    [TestMethod]
    public Task DuplicateNavigationDoesNotScanOrChangeSourcesResultsOrSelection() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var report = Duplicates(files.A, files.B);
        var duplicateSession = new DuplicateResultSession();
        duplicateSession.Replace(duplicateSession.PrepareGroups(report.Groups));
        duplicateSession.Groups[0].Files[1].IsSelected = true;
        var scanner = new RecordingDuplicateRun(report);
        var model = Model(files.Root, scanner, duplicateSession);
        model.ConflictSources.Add(new ScanSourceViewModel(files.Root + "-extra"));
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var session = V2([], [], report);
        var card = session.Cards.Single();
        session.ToggleExpanded(card.Key);
        await SetSessionAsync(model, session, "all");
        model.ShowIgnoredConflicts = true;
        await model.ConflictRowsRefreshTask;
        var groups = model.Groups;
        var sources = model.Sources.ToArray();
        var conflictSources = model.ConflictSources.ToArray();
        var locations = 0;
        model.DuplicateGroupLocationRequested += _ => locations++;
        model.OpenConflictDuplicatesCommand.Execute(new ConflictGroupLink(card.Key, card.GroupIndex));
        Assert.IsTrue(model.IsScannerVisible);
        Assert.AreEqual(0, scanner.Calls);
        Assert.AreSame(groups, model.Groups);
        Assert.AreEqual(1, model.SelectedFileCount);
        CollectionAssert.AreEqual(sources, model.Sources.ToArray());
        CollectionAssert.AreEqual(conflictSources, model.ConflictSources.ToArray());
        Assert.AreEqual(1, locations);
        model.OpenConflictCommand.Execute(null);
        Assert.IsTrue(model.ConflictRows.Single(row => row.Depth == 0).IsExpanded);
        Assert.IsTrue(model.ShowIgnoredConflicts);
        model.OpenConflictDuplicatesCommand.Execute(new ConflictGroupLink("absent", 999));
        Assert.AreEqual(1, locations);
        Assert.AreEqual(0, scanner.Calls);
    });

    [TestMethod]
    public Task RelationshipJumpUsesCardIdentityAndRevealsIgnoredTarget() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel();
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var session = ContainmentSession();
        await SetSessionAsync(model, session, "all");
        var target = session.Cards.Single(card => card.Type == "collection");
        model.IgnoreConflictCommand.Execute(model.ConflictRows.Single(row => row.Key == target.Key));
        await model.ConflictRowsRefreshTask;
        model.SelectConflictFilterCommand.Execute("different");
        model.IncludeConflictCollections = false;
        await model.ConflictRowsRefreshTask;
        Assert.IsFalse(model.ConflictRows.Any(row => row.Key == target.Key));
        string? located = null;
        model.ConflictGroupLocationRequested += key => located = key;
        model.JumpToConflictGroupCommand.Execute(new ConflictGroupLink(target.Key, target.GroupIndex));
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(target.Key, located);
        Assert.IsTrue(model.ShowIgnoredConflicts);
        Assert.IsTrue(model.IncludeConflictCollections);
        Assert.IsTrue(model.ConflictRows.Single(row => row.Key == target.Key).IsExpanded);
        Assert.AreEqual(target.GroupIndex, model.ConflictRows.Single(row => row.Key == target.Key).GroupIndex);
    });

    [TestMethod]
    public Task CopyFailureKeepsResultsAndReportsTheFailure() => WpfTestHost.RunAsync(async () =>
    {
        var model = CreateModel(_ => throw new UnauthorizedAccessException());
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var session = ContainmentSession();
        session.ToggleExpanded(session.Cards[0].Key);
        session.ToggleExpanded(session.Cards[0].Key + "/technical");
        await SetSessionAsync(model, session, "all");
        var rows = model.ConflictRows;
        model.CopyConflictDetailsCommand.Execute(rows.Single(row => row.Kind == ConflictRowKind.CopyDetails));
        Assert.AreEqual("复制未完成", model.ConflictFileActionStatusText);
        Assert.AreSame(rows, model.ConflictRows);
    });

    [TestMethod]
    public async Task ConflictScanUsesOnlyEnabledConflictSourcesForWholeFileHashing()
    {
        using var files = new TemporaryFiles();
        var report = Duplicates(files.A, files.B);
        var scanner = new RecordingDuplicateRun(report);
        var duplicates = new DuplicateResultSession();
        duplicates.Replace(duplicates.PrepareGroups(report.Groups));
        duplicates.Groups[0].Files[0].IsSelected = true;
        var model = Model(files.Root, scanner, duplicates);
        model.ConflictSources[0].IsEnabled = false;
        model.ConflictSources.Add(new ScanSourceViewModel(files.Root + "-conflict-only"));
        var original = model.Groups;
        await model.StartConflictScanCommand.ExecuteAsync();
        Assert.AreEqual("complete", model.ConflictRunState);
        Assert.AreEqual(1, scanner.Calls);
        Assert.AreEqual(files.Root + "-conflict-only", scanner.Input!.Sources.Single().Path);
        Assert.AreSame(original, model.Groups);
        Assert.AreEqual(1, model.SelectedFileCount);
        Assert.AreEqual(1, model.ConflictRows.Count(row => row.Kind == ConflictRowKind.Duplicate));
    }

    [TestMethod]
    public async Task CancelledDuplicatePhaseCannotPublishIntoClearedConflictSession()
    {
        using var files = new TemporaryFiles();
        var scanner = new RecordingDuplicateRun(Duplicates(files.A, files.B)) { Wait = true };
        var model = Model(files.Root, scanner);
        var scan = model.StartConflictScanCommand.ExecuteAsync();
        await scanner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        model.ConflictSources.Clear();
        Assert.IsTrue(scanner.Token.IsCancellationRequested);
        scanner.Release.SetResult();
        await scan;
        Assert.AreEqual("initial", model.ConflictRunState);
        Assert.AreEqual(0, model.ConflictRows.Count);
        Assert.IsFalse(model.IsBusy);
    }

    [TestMethod]
    public Task SingleRowDeleteTargetsOnePathAndUndoSynchronizesBothExistingSessions() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var report = Duplicates(files.A, files.B, files.C);
        var duplicates = new DuplicateResultSession();
        duplicates.Replace(duplicates.PrepareGroups(report.Groups));
        var adapter = new TestRecycleBin();
        var model = Model(files.Root, new RecordingDuplicateRun(report), duplicates, adapter);
        var session = V2([Resource(1, (files.A, "same"), (files.B, "same"), (files.C, "same"), (files.C + ".other", "other"))],
            [(files.A, 1), (files.B, 1), (files.C, 1), (files.C + ".other", 1)], report);
        var card = session.Cards.Single(card => card.Type == "suspected");
        session.ToggleExpanded(card.Key);
        await SetSessionAsync(model, session, "all");
        var row = model.ConflictRows.Single(row => row.OwnerKey == card.Key && row.File is not null && row.CopyCount > 0);
        Assert.AreEqual(2, row.CopyCount);
        await model.DeleteConflictFileAsync(row);
        CollectionAssert.AreEqual(new[] { row.File!.Path }, adapter.Deleted.ToArray());
        Assert.AreEqual(2, model.Groups.Single().Files.Count);
        Assert.IsTrue(session.Cards.All(card => card.Files.All(file => file.Path != row.File.Path)));
        Assert.IsTrue(model.CanUndoConflictDelete);
        await model.UndoLastDeleteCommand.ExecuteAsync();
        Assert.AreEqual(3, model.Groups.Single().Files.Count);
        Assert.AreEqual(3, session.Cards.Single(card => card.Duplicate is not null).Files.Count);
        Assert.IsFalse(model.CanUndoConflictDelete);
    });

    [TestMethod]
    public Task PartialDeleteAndPartialUndoOnlyChangeSuccessfulPaths() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var report = Duplicates(files.A, files.B, files.C);
        var duplicates = new DuplicateResultSession();
        duplicates.Replace(duplicates.PrepareGroups(report.Groups));
        var adapter = new TestRecycleBin { DeleteFailure = files.C };
        var model = Model(files.Root, new RecordingDuplicateRun(report), duplicates, adapter);
        var session = V2([], [], report);
        await SetSessionAsync(model, session, "all");
        foreach (var file in model.Groups.Single().Files) file.IsSelected = file.Path != files.A;
        await model.DeleteSelectedCommand.ExecuteAsync();
        Assert.IsTrue(model.Groups.Single().Files.Any(file => file.Path == files.C));
        Assert.IsFalse(session.Cards.Single().Files.Any(file => file.Path == files.B));
        await model.UndoLastDeleteCommand.ExecuteAsync();
        Assert.AreEqual(3, model.Groups.Single().Files.Count);
        adapter.DeleteFailure = null;
        adapter.RestoreFailure = files.C;
        foreach (var file in model.Groups.Single().Files) file.IsSelected = file.Path != files.A;
        await model.DeleteSelectedCommand.ExecuteAsync();
        Assert.AreEqual(0, model.Groups.Count);
        await model.UndoLastDeleteCommand.ExecuteAsync();
        CollectionAssert.AreEquivalent(new[] { files.A, files.B }, model.Groups.Single().Files.Select(file => file.Path).ToArray());
        CollectionAssert.AreEquivalent(new[] { files.A, files.B }, session.Cards.Single().Files.Select(file => file.Path).ToArray());
        Assert.IsTrue(model.CanUndoLastDelete);
        adapter.RestoreFailure = null;
        await model.UndoLastDeleteCommand.ExecuteAsync();
        Assert.AreEqual(3, model.Groups.Single().Files.Count);
        Assert.AreEqual(3, session.Cards.Single().Files.Count);
    });

    [TestMethod]
    public Task DeletingOneFileKeepsCardVisibleAndRejectsDeletedRowUntilUndo() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var report = Duplicates(files.A, files.B);
        var adapter = new TestRecycleBin();
        var model = Model(files.Root, new RecordingDuplicateRun(report), adapter: adapter);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        var session = V2([], [], report);
        var card = session.Cards.Single();
        session.ToggleExpanded(card.Key);
        await SetSessionAsync(model, session, "all");
        var removedRow = model.ConflictRows.Single(row => row.File?.Path == files.A);
        await model.DeleteConflictFileAsync(removedRow);
        await WpfTestHost.LayoutAsync(window);
        var header = model.ConflictRows.Single(row => row.IsCardStart);
        Assert.AreEqual(card.Key, header.Key);
        Assert.AreEqual(card.GroupIndex, header.GroupIndex);
        Assert.IsTrue(header.IsExpanded);
        Assert.AreEqual(files.B, model.ConflictRows.Single(row => row.File is not null).File!.Path);
        Assert.IsTrue(model.CanUndoConflictDelete);
        Assert.AreEqual("本次删除 1 个文件，已移入回收站", model.ConflictFileActionStatusText);
        await model.DeleteConflictFileAsync(removedRow);
        Assert.AreEqual(1, adapter.Deleted.Count);
        var undo = Descendants<Button>(window).Single(button => button.Name == "UndoConflictDeleteButton");
        Assert.IsTrue(undo.IsVisible);
        Assert.IsTrue(undo.IsEnabled);
        var hit = window.InputHitTest(undo.TranslatePoint(new Point(undo.ActualWidth / 2, undo.ActualHeight / 2), window)) as DependencyObject;
        Assert.IsNotNull(hit);
        Assert.IsTrue(ReferenceEquals(hit, undo) || Descendants<DependencyObject>(undo).Contains(hit));
        Save(window, "conflict-delete-footer.png");
        var undoCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void UndoStateChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(model.IsBusy) && !model.IsBusy) undoCompleted.TrySetResult();
        }
        model.PropertyChanged += UndoStateChanged;
        try
        {
            ((IInvokeProvider)new ButtonAutomationPeer(undo).GetPattern(PatternInterface.Invoke)!).Invoke();
            await undoCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await model.ConflictRowsRefreshTask;
            await WpfTestHost.LayoutAsync(window);
        }
        finally { model.PropertyChanged -= UndoStateChanged; }
        CollectionAssert.AreEquivalent(new[] { files.A, files.B }, model.ConflictRows.Where(row => row.File is not null).Select(row => row.File!.Path).ToArray());
        Assert.AreEqual(card.Key, model.ConflictRows.Single(row => row.IsCardStart).Key);
        Assert.IsFalse(model.CanUndoConflictDelete);
        Assert.IsFalse(undo.IsVisible);
        Save(window, "conflict-delete-footer-restored.png");
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task FileChangesPreserveVisibleRowPositionAfterLayoutAndUndo(bool deleteBeforeViewport) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var paths = Enumerable.Range(0, 36).Select(index => new[] { "a", "b", "c" }
            .Select(name => Path.Combine(files.Root, $"group{index:D2}-{name}.package")).ToArray()).ToArray();
        foreach (var path in paths.SelectMany(group => group))
        {
            File.WriteAllBytes(path, new byte[10]);
            File.SetLastWriteTimeUtc(path, Stamp.LastWriteTimeUtc);
        }
        var session = V2(paths.Select((group, index) => Resource((ulong)index + 1,
            (group[0], "one"), (group[1], "two"), (group[2], "three"))).ToArray(),
            paths.SelectMany(group => group).Select(path => (path, 10)).ToArray());
        foreach (var card in session.Cards) session.ToggleExpanded(card.Key);
        var adapter = new TestRecycleBin();
        var model = Model(files.Root, new RecordingDuplicateRun(Duplicates(files.A, files.B)), adapter: adapter);
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        var window = OpenWindow(model);
        await SetSessionAsync(model, session, "all");
        await SettleAsync();
        var list = Descendants<ListBox>(window).Single(element => element.Name == "ConflictRowsList");
        var viewer = Descendants<ScrollViewer>(list).First();
        var presenter = Descendants<ScrollContentPresenter>(viewer).First();
        var viewportCard = session.Cards[18];
        var visibleFile = model.ConflictRows.Single(row => row.OwnerKey == viewportCard.Key && row.File?.Path == viewportCard.Files[0].Path);
        list.ScrollIntoView(visibleFile);
        await SettleAsync();
        var visibleItem = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(visibleFile);
        viewer.ScrollToVerticalOffset(viewer.VerticalOffset + visibleItem.TranslatePoint(new Point(), presenter).Y + visibleItem.ActualHeight / 2);
        await SettleAsync();
        var deletedCard = deleteBeforeViewport ? session.Cards[4] : viewportCard;
        var deletedRow = model.ConflictRows.Single(row => row.OwnerKey == deletedCard.Key && row.File?.Path == deletedCard.Files[0].Path);
        Button? deleteButton = null;
        if (!deleteBeforeViewport)
        {
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(deletedRow);
            deleteButton = Descendants<Button>(item).Single(button => ReferenceEquals(button.Command, model.DeleteConflictFileCommand));
            deleteButton.Focus();
            await SettleAsync();
        }
        var anchor = CaptureAnchor(deletedRow.Key);
        if (deleteButton is not null)
        {
            ((IInvokeProvider)new ButtonAutomationPeer(deleteButton).GetPattern(PatternInterface.Invoke)!).Invoke();
            await WpfTestHost.LayoutAsync(window);
            await model.ConflictFileActionTask;
        }
        else await model.DeleteConflictFileAsync(deletedRow);
        await SettleAsync();
        AssertAnchor(anchor);
        Assert.AreEqual(deletedCard.GroupIndex, model.ConflictRows.Single(row => row.Key == deletedCard.Key).GroupIndex);
        Assert.IsFalse(model.ConflictRows.Any(row => row.File?.Path == deletedRow.File!.Path));
        foreach (var row in model.ConflictRows.Where(row => row.OwnerKey == deletedCard.Key && row.File is not null))
        {
            Assert.AreEqual("撞 1 样 · 独有 9 样", row.Annotation);
            Assert.AreEqual("相同TGI编号1个，其中0个内容相同，独有编号9个", row.AnnotationTooltip);
        }
        Save(window, $"conflict-delete-anchor-{(deleteBeforeViewport ? "before-viewport" : "visible-file")}.png");
        var undoAnchor = CaptureAnchor();
        var undo = Descendants<Button>(window).Single(button => button.Name == "UndoConflictDeleteButton");
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void UndoStateChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(model.IsBusy) && !model.IsBusy) completed.TrySetResult();
        }
        model.PropertyChanged += UndoStateChanged;
        try
        {
            ((IInvokeProvider)new ButtonAutomationPeer(undo).GetPattern(PatternInterface.Invoke)!).Invoke();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await SettleAsync();
        }
        finally { model.PropertyChanged -= UndoStateChanged; }
        AssertAnchor(undoAnchor);
        Assert.IsTrue(model.ConflictRows.Any(row => row.File?.Path == deletedRow.File!.Path));
        Save(window, $"conflict-undo-anchor-{(deleteBeforeViewport ? "before-viewport" : "visible-file")}.png");

        async Task SettleAsync()
        {
            await WpfTestHost.LayoutAsync(window);
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
            await Task.Delay(60);
            await WpfTestHost.LayoutAsync(window);
        }
        (string Key, double Top) CaptureAnchor(string? excluded = null)
        {
            var first = Descendants<ListBoxItem>(list).Where(item => item.DataContext is ConflictResultRow row && row.Key != excluded)
                .Select(item => (Item: item, Row: (ConflictResultRow)item.DataContext, Top: item.TranslatePoint(new Point(), presenter).Y))
                .Where(item => item.Top + item.Item.ActualHeight > 0 && item.Top < presenter.ActualHeight)
                .OrderBy(item => item.Top).First();
            return (first.Row.Key, first.Top);
        }
        void AssertAnchor((string Key, double Top) saved)
        {
            var row = model.ConflictRows.Single(row => row.Key == saved.Key);
            var item = list.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
            Assert.IsNotNull(item, "原视口锚点应保持实现且可见");
            Assert.AreEqual(saved.Top, item.TranslatePoint(new Point(), presenter).Y, 1d,
                $"删除或撤回后，存活行应留在视口中的原相对位置；操作前锚点={saved.Key}, Y={saved.Top:F3}；offset={viewer.VerticalOffset:F3}");
        }
    });

    [TestMethod]
    public Task DeleteFooterDescribesLatestSuccessfulBatchAndUndoOnlyRestoresThatBatch() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var report = Duplicates(files.A, files.B, files.C);
        var adapter = new TestRecycleBin();
        var model = Model(files.Root, new RecordingDuplicateRun(report), adapter: adapter);
        var session = V2([], [], report);
        session.ToggleExpanded(session.Cards.Single().Key);
        await SetSessionAsync(model, session, "all");
        await model.DeleteConflictFileAsync(model.ConflictRows.Single(row => row.File?.Path == files.A));
        Assert.AreEqual("本次删除 1 个文件，已移入回收站", model.ConflictFileActionStatusText);
        adapter.DeleteFailure = files.B;
        await model.DeleteConflictFileAsync(model.ConflictRows.Single(row => row.File?.Path == files.B));
        Assert.AreEqual("本次删除 1 个文件，已移入回收站", model.ConflictFileActionStatusText);
        Assert.IsTrue(session.Cards.Single().Files.Any(file => file.Path == files.B));
        adapter.DeleteFailure = null;
        await model.DeleteConflictFileAsync(model.ConflictRows.Single(row => row.File?.Path == files.B));
        Assert.AreEqual("本次删除 1 个文件，已移入回收站", model.ConflictFileActionStatusText, "连续两次单文件删除仍描述最近一次");
        await model.UndoLastDeleteCommand.ExecuteAsync();
        CollectionAssert.AreEquivalent(new[] { files.B, files.C }, session.Cards.Single().Files.Select(file => file.Path).ToArray());
        Assert.AreEqual("已撤回，恢复 1 个文件", model.ConflictFileActionStatusText);
        Assert.IsFalse(model.CanUndoConflictDelete);
    });

    [TestMethod]
    public Task ChangedFileStampPreventsConflictDeletion() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var report = Duplicates(files.A, files.B);
        var adapter = new TestRecycleBin();
        var model = Model(files.Root, new RecordingDuplicateRun(report), adapter: adapter);
        var session = V2([], [], report);
        session.ToggleExpanded(session.Cards.Single().Key);
        await SetSessionAsync(model, session, "all");
        File.AppendAllText(files.A, "changed");
        await model.DeleteConflictFileAsync(model.ConflictRows.Single(row => row.File?.Path == files.A));
        Assert.AreEqual(0, adapter.Deleted.Count);
        Assert.AreEqual(2, session.Cards.Single().Files.Count);
        Assert.IsFalse(model.CanUndoConflictDelete);
    });

    [TestMethod]
    public Task LateDeletionCannotPublishIntoResetOwnerSession() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var report = Duplicates(files.A, files.B);
        var adapter = new TestRecycleBin { WaitDelete = true };
        var model = Model(files.Root, new RecordingDuplicateRun(report), adapter: adapter);
        var session = V2([], [], report);
        session.ToggleExpanded(session.Cards.Single().Key);
        await SetSessionAsync(model, session, "all");
        var deletion = model.DeleteConflictFileAsync(model.ConflictRows.First(row => row.File is not null));
        await adapter.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        model.ConflictSources.Clear();
        adapter.DeleteRelease.SetResult();
        await deletion;
        Assert.AreEqual("initial", model.ConflictRunState);
        Assert.AreEqual(0, model.ConflictRows.Count);
        Assert.AreEqual("", model.ConflictFileActionStatusText);
        Assert.IsFalse(model.CanUndoConflictDelete);
        Assert.IsFalse(model.IsBusy);
    });

    [TestMethod]
    public Task ClearingOnePageOnlyClearsItsOwnDeletionHistory() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var report = Duplicates(files.A, files.B);
        var model = Model(files.Root, new RecordingDuplicateRun(report), adapter: new TestRecycleBin());
        var session = V2([], [], report);
        session.ToggleExpanded(session.Cards.Single().Key);
        await SetSessionAsync(model, session, "all");
        await model.DeleteConflictFileAsync(model.ConflictRows.First(row => row.File is not null));
        model.Sources.Clear();
        Assert.IsTrue(model.CanUndoConflictDelete);
        await model.UndoLastDeleteCommand.ExecuteAsync();
        Assert.AreEqual(1, session.Cards.Count);
        Assert.AreEqual(0, model.Groups.Count);
        await model.DeleteConflictFileAsync(model.ConflictRows.First(row => row.File is not null));
        model.ConflictSources.Clear();
        Assert.IsFalse(model.CanUndoLastDelete);
        Assert.AreEqual(0, model.ConflictRows.Count);
    });

    private static MainWindowViewModel Model(string root, IDuplicateRunService run, DuplicateResultSession? session = null,
        TestRecycleBin? adapter = null) => new(run, new NoPicker(), new Settings(root),
            new FileActionService(adapter ?? new TestRecycleBin(), new PhysicalFileMetadataAccess()), new Dialogs(),
            resultSession: session, packageConflictScanner: new EmptyPackages(), resourceContentComparer: new EmptyContents(), scriptModuleScanner: new EmptyScripts());

    private sealed class RecordingDuplicateRun(DuplicateReport report) : IDuplicateRunService
    {
        public int Calls { get; private set; }
        public DuplicateRunInput? Input { get; private set; }
        public bool Wait { get; init; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DuplicateReport> RunAsync(DuplicateRunInput input, IProgress<DuplicateScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            Input = input;
            Token = cancellationToken;
            Started.TrySetResult();
            if (Wait) await Release.Task;
            return report;
        }
    }
    private sealed class EmptyPackages : IPackageConflictScanner
    {
        public Task<PackageConflictScanReport> ScanAsync(PackageConflictScanRequest request, IProgress<PackageConflictScanProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(ConflictResultSessionTests.Packages());
    }
    private sealed class EmptyContents : IResourceContentComparer
    {
        public Task<ResourceContentComparisonReport> CompareAsync(ResourceContentComparisonRequest request, IProgress<ResourceContentComparisonProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(Contents());
    }
    private sealed class EmptyScripts : IScriptModuleScanner
    {
        public Task<ScriptModuleScanReport> ScanAsync(ScriptModuleScanRequest request, IProgress<ScriptModuleScanProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(Scripts());
    }
    private sealed class NoPicker : IFolderPickerService { public string? PickFolder(string title, string? initialDirectory = null) => null; }
    private sealed class Settings(string root) : IDuplicateSettingsStore
    {
        public DuplicateSettings? Load() => new(root, []);
        public void Save(DuplicateSettings settings) { }
    }
    private sealed class Dialogs : IFileActionDialogService
    {
        public bool ConfirmDeletion(FileDeletionPreflight preflight) => true;
        public void ShowProblems(string title, IReadOnlyList<FileActionFailure> problems) { }
    }
    private sealed class TestRecycleBin : IRecycleBinAdapter
    {
        private int nextPidl;
        public List<string> Deleted { get; } = [];
        public string? DeleteFailure { get; set; }
        public string? RestoreFailure { get; set; }
        public bool WaitDelete { get; init; }
        public TaskCompletionSource DeleteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeleteRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CanRecycle(string path) => true;
        public async Task<RecycleBinDeleteResult> MoveToRecycleBinAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            Deleted.AddRange(paths);
            DeleteStarted.TrySetResult();
            if (WaitDelete) await DeleteRelease.Task;
            return new RecycleBinDeleteResult(paths.Where(path => path != DeleteFailure)
                .Select(path => new RecycleBinItem(path, [1, 2, (byte)++nextPidl, 0])).ToArray(),
                paths.Where(path => path == DeleteFailure).Select(path => new FileActionFailure(path, "模拟删除失败")).ToArray());
        }
        public Task<FileActionBatchResult> RestoreAsync(IReadOnlyList<RecycleBinItem> items, CancellationToken cancellationToken = default) => Task.FromResult(new FileActionBatchResult(
            items.Where(item => item.OriginalPath != RestoreFailure).Select(item => item.OriginalPath).ToArray(),
            items.Where(item => item.OriginalPath == RestoreFailure).Select(item => new FileActionFailure(item.OriginalPath, "模拟恢复失败")).ToArray(), false));
    }
    private sealed class TemporaryFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "s4ms-conflict-actions-" + Guid.NewGuid().ToString("N"));
        public string A => Path.Combine(Root, "a.package");
        public string B => Path.Combine(Root, "b.package");
        public string C => Path.Combine(Root, "c.package");
        public TemporaryFiles()
        {
            Directory.CreateDirectory(Root);
            foreach (var path in new[] { A, B, C }) { File.WriteAllBytes(path, new byte[10]); File.SetLastWriteTimeUtc(path, Stamp.LastWriteTimeUtc); }
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
