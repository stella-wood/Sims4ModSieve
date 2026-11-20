using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictResultSessionTests;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;

namespace Sims4ModSieve.Desktop.Tests;

public sealed partial class ConflictFileActionTests
{
    [TestMethod]
    [DataRow("resource", "delete")]
    [DataRow("resource", "disable")]
    [DataRow("resource", "bulk-delete")]
    [DataRow("resource", "bulk-disable")]
    [DataRow("script", "delete")]
    [DataRow("script", "disable")]
    [DataRow("script", "bulk-delete")]
    [DataRow("script", "bulk-disable")]
    public Task MissingConflictPeerRemovesCardAndSelectionWithoutConfirmation(string kind, string entry) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var dialogs = new PresenceDialogs();
        var model = PresenceModel(files, Duplicates(files.A, files.B), dialogs, out var adapter);
        var session = ConflictPresenceSession(files, kind, 2);
        var paths = session.Cards.Single().Files.Select(file => file.Path).ToArray();
        await PreparePresenceSessionAsync(model, session);
        SelectPresenceFiles(model, paths[1]);
        File.Delete(paths[0]);
        await RunPresenceActionAsync(model, entry, paths[1]);
        CollectionAssert.AreEqual(new[] { "refresh:1" }, dialogs.Events);
        Assert.AreEqual(0, session.Cards.Count);
        Assert.AreEqual(0, model.ConflictSelectedCount);
        Assert.AreEqual(0, adapter.Deleted.Count);
        Assert.IsTrue(File.Exists(paths[1]));
        Assert.IsFalse(File.Exists(paths[1] + ".off"));
        session.ApplyDeletedPaths([]);
        Assert.AreEqual(0, session.Cards.Count, "后续重建不能带回失效卡片");
    });

    [TestMethod]
    [DataRow("resource", "delete")]
    [DataRow("resource", "disable")]
    [DataRow("resource", "bulk-delete")]
    [DataRow("resource", "bulk-disable")]
    [DataRow("script", "delete")]
    [DataRow("script", "disable")]
    [DataRow("script", "bulk-delete")]
    [DataRow("script", "bulk-disable")]
    public Task ThreeFileConflictRefreshesToTwoBeforeConfirmation(string kind, string entry) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var dialogs = new PresenceDialogs();
        var model = PresenceModel(files, Duplicates(files.A, files.B), dialogs, out var adapter);
        var session = ConflictPresenceSession(files, kind, 3);
        var paths = session.Cards.Single().Files.Select(file => file.Path).ToArray();
        await PreparePresenceSessionAsync(model, session);
        SelectPresenceFiles(model, paths[0], paths[1]);
        File.Delete(paths[0]);
        dialogs.OnConfirm = () =>
        {
            CollectionAssert.AreEquivalent(paths[1..], session.Cards.Single().Files.Select(file => file.Path).ToArray());
            Assert.AreEqual(1, model.ConflictSelectedCount);
        };
        await RunPresenceActionAsync(model, entry, paths[1]);
        CollectionAssert.AreEqual(new[] { "refresh:1", "confirm" }, dialogs.Events);
        if (entry.Contains("disable", StringComparison.Ordinal)) Assert.IsTrue(File.Exists(paths[1] + ".off"));
        else CollectionAssert.AreEqual(new[] { paths[1] }, adapter.Deleted);
    });

    [TestMethod]
    [DataRow("resource", "delete")]
    [DataRow("resource", "disable")]
    [DataRow("resource", "bulk-delete")]
    [DataRow("resource", "bulk-disable")]
    [DataRow("script", "delete")]
    [DataRow("script", "disable")]
    [DataRow("script", "bulk-delete")]
    [DataRow("script", "bulk-disable")]
    public Task ExistingConflictMembersKeepOriginalAction(string kind, string entry) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var dialogs = new PresenceDialogs();
        var model = PresenceModel(files, Duplicates(files.A, files.B), dialogs, out var adapter);
        var session = ConflictPresenceSession(files, kind, 2);
        var paths = session.Cards.Single().Files.Select(file => file.Path).ToArray();
        await PreparePresenceSessionAsync(model, session);
        SelectPresenceFiles(model, paths);
        await RunPresenceActionAsync(model, entry, paths[1]);
        CollectionAssert.AreEqual(new[] { "confirm" }, dialogs.Events);
        var expected = entry.StartsWith("bulk", StringComparison.Ordinal) ? 2 : 1;
        if (entry.Contains("disable", StringComparison.Ordinal)) Assert.AreEqual(expected, model.DisabledFiles.Files.Count);
        else Assert.AreEqual(expected, adapter.Deleted.Count);
    });

    [TestMethod]
    public Task OverlappingCardsKeepSelectionThatStillBelongsToAnotherCard() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var dialogs = new PresenceDialogs();
        var model = PresenceModel(files, Duplicates(files.A, files.B), dialogs, out var adapter);
        var session = V2([Resource(1, (files.A, "x"), (files.B, "y")), Resource(2, (files.A, "x"), (files.C, "y"))],
            [(files.A, 100), (files.B, 100), (files.C, 100)]);
        Assert.AreEqual(2, session.Cards.Count);
        await PreparePresenceSessionAsync(model, session);
        SelectPresenceFiles(model, files.A, files.B);
        File.Delete(files.B);
        dialogs.OnConfirm = () =>
        {
            CollectionAssert.AreEquivalent(new[] { files.A, files.C }, session.Cards.Single().Files.Select(file => file.Path).ToArray());
            Assert.AreEqual(1, model.ConflictSelectedCount);
            Assert.IsTrue(model.ConflictRows.Where(row => row.File?.Path == files.A).All(row => row.Selection.IsSelected == true));
        };
        await model.DeleteSelectedConflictFilesCommand.ExecuteAsync();
        CollectionAssert.AreEqual(new[] { "refresh:1", "confirm" }, dialogs.Events);
        CollectionAssert.AreEqual(new[] { files.A }, adapter.Deleted);
    });

    [TestMethod]
    public Task MissingSharedMemberExpiresUnselectedCardGlobally() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var dialogs = new PresenceDialogs();
        var model = PresenceModel(files, Duplicates(files.A, files.B), dialogs, out var adapter);
        var session = V2([Resource(1, (files.A, "x"), (files.B, "y")), Resource(2, (files.B, "x"), (files.C, "y"))],
            [(files.A, 100), (files.B, 100), (files.C, 100)]);
        Assert.AreEqual(2, session.Cards.Count);
        await PreparePresenceSessionAsync(model, session);
        SelectPresenceFiles(model, files.A);
        File.Delete(files.B);
        await model.DeleteSelectedConflictFilesCommand.ExecuteAsync();
        CollectionAssert.AreEqual(new[] { "refresh:1" }, dialogs.Events);
        Assert.AreEqual(0, session.Cards.Count);
        Assert.AreEqual(0, model.ConflictSelectedCount);
        Assert.AreEqual(0, adapter.Deleted.Count);
    });

    [TestMethod]
    [DataRow("bulk-delete")]
    [DataRow("bulk-disable")]
    public Task MixedHiddenCardsRefreshTogetherAndConfirmOnlyRemainingFiles(string entry) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var dialogs = new PresenceDialogs();
        var report = Duplicates(files.A, files.B);
        var model = PresenceModel(files, report, dialogs, out var adapter);
        var scripts = ConflictPresenceSession(files, "script", 3).Cards.Single().Files.Select(file => file.Path).ToArray();
        var session = V2([Resource(1, (files.A, "x"), (files.C, "y"))], [(files.A, 100), (files.C, 100)], report,
            Scripts([Collision("kit.common", scripts.Select((path, index) => (path, (string?)index.ToString())).ToArray())]));
        Assert.AreEqual(3, session.Cards.Count);
        await PreparePresenceSessionAsync(model, session);
        SelectPresenceFiles(model, files.B, files.C, scripts[0], scripts[1]);
        model.IncludeAllConflictTypes = false;
        await model.ConflictRowsRefreshTask;
        Assert.AreEqual(0, model.ConflictRows.Count);
        File.Delete(files.A);
        File.Delete(scripts[0]);
        dialogs.OnConfirm = () =>
        {
            Assert.AreEqual(1, session.Cards.Count);
            CollectionAssert.AreEquivalent(scripts[1..], session.Cards.Single().Files.Select(file => file.Path).ToArray());
            Assert.AreEqual(1, model.ConflictSelectedCount);
        };
        await RunPresenceActionAsync(model, entry, scripts[1]);
        CollectionAssert.AreEqual(new[] { "refresh:3", "confirm" }, dialogs.Events);
        if (entry.Contains("disable", StringComparison.Ordinal)) Assert.IsTrue(File.Exists(scripts[1] + ".off"));
        else CollectionAssert.AreEqual(new[] { scripts[1] }, adapter.Deleted);
    });

    [TestMethod]
    [DataRow("resource", true)]
    [DataRow("resource", false)]
    [DataRow("script", true)]
    [DataRow("script", false)]
    public Task DisabledConflictPeerUsesRecordedOffPath(string kind, bool missing) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var dialogs = new PresenceDialogs();
        var disable = new ModDisableService(Path.Combine(files.Root, "data"));
        var session = ConflictPresenceSession(files, kind, 2);
        var paths = session.Cards.Single().Files.Select(file => file.Path).ToArray();
        var report = Duplicates(files.A, files.B);
        disable.Disable([paths[0]]);
        var model = PresenceModel(files, report, dialogs, out var adapter, disable);
        await PreparePresenceSessionAsync(model, session);
        SelectPresenceFiles(model, paths[1]);
        if (missing) File.Delete(paths[0] + ".off");
        await RunPresenceActionAsync(model, "delete", paths[1]);
        CollectionAssert.AreEqual(missing ? new[] { "refresh:1" } : new[] { "confirm" }, dialogs.Events);
        Assert.AreEqual(missing ? 0 : 1, adapter.Deleted.Count);
        if (missing) Assert.AreEqual(0, session.Cards.Count);
    });

    [TestMethod]
    [DataRow("resource")]
    [DataRow("script")]
    public Task ClosingConflictRefreshKeepsResultsAndSelection(string kind) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new TemporaryFiles();
        var dialogs = new PresenceDialogs { AcceptRefresh = false };
        var model = PresenceModel(files, Duplicates(files.A, files.B), dialogs, out var adapter);
        var session = ConflictPresenceSession(files, kind, 2);
        var paths = session.Cards.Single().Files.Select(file => file.Path).ToArray();
        await PreparePresenceSessionAsync(model, session);
        SelectPresenceFiles(model, paths[1]);
        File.Delete(paths[0]);
        await RunPresenceActionAsync(model, "delete", paths[1]);
        CollectionAssert.AreEqual(new[] { "refresh:1" }, dialogs.Events);
        Assert.AreEqual(2, session.Cards.Single().Files.Count);
        Assert.AreEqual(1, model.ConflictSelectedCount);
        Assert.AreEqual(0, adapter.Deleted.Count);
    });

    private static ConflictResultSession ConflictPresenceSession(TemporaryFiles files, string kind, int count)
    {
        var paths = new[] { files.A, files.B, files.C }.Take(count).ToArray();
        if (kind == "script")
        {
            paths = paths.Select(path => path + ".ts4script").ToArray();
            foreach (var path in paths)
            {
                File.WriteAllBytes(path, new byte[10]);
                File.SetLastWriteTimeUtc(path, Stamp.LastWriteTimeUtc);
            }
            return V2([], [], scripts: Scripts([Collision("kit.common", paths.Select((path, index) => (path, (string?)index.ToString())).ToArray())]));
        }
        return V2([Resource(1, paths.Select((path, index) => (path, (string?)index.ToString())).ToArray())], paths.Select(path => (path, 100)).ToArray());
    }

    private static async Task PreparePresenceSessionAsync(MainWindowViewModel model, ConflictResultSession session)
    {
        foreach (var card in session.Cards) session.ToggleExpanded(card.Key);
        await SetSessionAsync(model, session, "all");
    }
}
