using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Services;
using static Sims4ModSieve.Desktop.Tests.SettingsViewModelTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class ApplicationDirectorySafetyTests
{
    [TestMethod]
    public async Task DeleteRejectsProgramPayloadButAllowsNeighbouringMods()
    {
        using var files = new Scenario();
        var app = files.Folder("Mods", "Sieve");
        var protectedFile = Path.Combine(app, "Sims4ModSieve.runtimeconfig.json");
        var allowedFile = Path.Combine(app, "other.package");
        File.WriteAllText(protectedFile, "protected");
        File.WriteAllText(allowedFile, "allowed");
        var metadata = new PhysicalFileMetadataAccess();
        var candidates = new[] { protectedFile, allowedFile }.Select(path =>
        {
            var stamp = metadata.GetFileStamp(path);
            return new FileDeletionCandidate(path, stamp.Length, stamp.LastWriteTimeUtc, "", ExpectedIdentity: stamp.Identity);
        }).ToArray();
        var adapter = new RecordingRecycleBin();
        var refused = new List<string>();
        var service = new FileActionService(adapter, metadata, new FileManagementStore(files.Root),
            applicationDirectory: app, logProtectedPath: refused.Add);
        Assert.IsFalse(service.Preflight(candidates, []).CanExecute);
        var result = await service.DeleteAsync(new(candidates, [], []));
        CollectionAssert.AreEquivalent(new[] { allowedFile }, adapter.Deleted.ToArray());
        Assert.AreEqual(1, result.Failures.Count);
        Assert.IsTrue(refused.Contains(protectedFile));
        Assert.AreEqual("protected", File.ReadAllText(protectedFile));
    }

    [TestMethod]
    public async Task RestoreRejectsProgramDestinationAndRetainsHistory()
    {
        using var files = new Scenario();
        var app = files.Folder("Sieve");
        var path = Path.Combine(app, "Sims4ModSieve.runtimeconfig.json");
        var record = new DeletedFileRecord("protected", new(path, [0, 0]), 1, DateTime.UtcNow);
        var history = new FileManagementStore(files.Root);
        history.Update(_ => new([], [record]));
        var adapter = new RecordingRecycleBin();
        var refused = new List<string>();
        var service = new FileActionService(adapter, new PhysicalFileMetadataAccess(), history,
            applicationDirectory: app, logProtectedPath: refused.Add);
        var result = await service.RestoreDeletedAsync([record.Id]);
        Assert.AreEqual(0, adapter.RestoreCalls);
        Assert.AreEqual(1, result.Failures.Count);
        Assert.AreEqual(record.Id, history.Load().Deleted.Single().Id);
        CollectionAssert.AreEqual(new[] { path }, refused);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void DisableAndEnableRejectProgramFilesAndKeepRecoveryRecords()
    {
        using var files = new Scenario();
        var app = files.Folder("Mods", "Sieve");
        var assets = files.Folder("Mods", "Sieve", "Assets");
        var path = Path.Combine(assets, "old.package");
        var next = Path.Combine(assets, "new.package");
        File.WriteAllText(path, "old");
        File.WriteAllText(next, "new");
        var data = files.Folder("data");
        var previous = new ModDisableService(data, files.Folder("other-program"));
        Assert.AreEqual(1, previous.Disable([path]).CompletedPaths.Count);
        var record = previous.Load().Records.Single();
        var refused = new List<string>();
        var service = new ModDisableService(data, app, refused.Add);
        Assert.AreEqual(1, service.Disable([next]).Failures.Count);
        Assert.AreEqual(1, service.Restore([record.Id]).Failures.Count);
        Assert.IsTrue(File.Exists(path + ".off"));
        Assert.IsTrue(File.Exists(next));
        Assert.AreEqual(record.Id, service.Load().Records.Single().Id);
        Assert.AreEqual(2, refused.Count);
    }

    [TestMethod]
    public void LocationWarningCoversConfiguredAndDetectedFoldersWithoutPrefixConfusion()
    {
        using var files = new Scenario();
        var mods = files.Folder("Documents", "Electronic Arts", "The Sims 4", "Mods");
        var tray = files.Folder("Tray");
        var support = new SettingsSupportService(files.Root, Path.Combine(files.Root, "Documents"), mods);
        StringAssert.Contains(support.GetApplicationLocationWarning(null, null), "单独的文件夹");
        support = new(files.Root, files.Root, tray);
        StringAssert.Contains(support.GetApplicationLocationWarning(null, tray), "单独的文件夹");
        StringAssert.Contains(support.GetApplicationLocationWarning(tray, null), "单独的文件夹");
        Assert.AreEqual("", new SettingsSupportService(files.Root, files.Root, Path.Combine(tray, "Sieve")).GetApplicationLocationWarning(null, tray));
        support = new(files.Root, files.Root, tray + "-backup");
        Assert.AreEqual("", support.GetApplicationLocationWarning(null, tray));
    }

    [TestMethod]
    public Task LocationNoticeRendersAcrossNavigationAndUpdatesAfterFolderChange() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        var app = mods;
        var safe = files.Folder("OtherMods");
        files.Store.Save(new(mods, []));
        var support = new SettingsSupportService(files.Root, files.Root, app);
        var model = files.Model(new Picker(safe), support: support);
        var window = new MainWindow(model);
        try
        {
            WpfTestHost.ShowOffScreen(window);
            await WpfTestHost.LayoutAsync(window);
            var notice = (Border)window.FindName("ApplicationLocationNotice");
            Assert.IsTrue(notice.IsVisible);
            StringAssert.Contains(((TextBlock)notice.Child).Text, "单独的文件夹");
            var imageDirectory = Environment.GetEnvironmentVariable("SIMS4_APPLICATION_GUARD_REVIEW_DIR");
            if (!string.IsNullOrEmpty(imageDirectory))
            {
                Directory.CreateDirectory(imageDirectory);
                var content = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(imageDirectory, "application-location-warning.png"));
                encoder.Save(stream);
            }
            model.OpenSettingsCommand.Execute(null);
            await WpfTestHost.LayoutAsync(window);
            Assert.IsTrue(notice.IsVisible);
            model.ChangeDefaultModsRootCommand.Execute(null);
            model.Sources.Clear();
            model.ConflictSources.Clear();
            await WpfTestHost.LayoutAsync(window);
            Assert.IsFalse(notice.IsVisible);
            Assert.IsFalse(model.HasApplicationLocationWarning);
        }
        finally { window.Close(); }
    });

    private sealed class RecordingRecycleBin : IRecycleBinAdapter
    {
        public List<string> Deleted { get; } = [];
        public int RestoreCalls { get; private set; }
        public bool CanRecycle(string path) => true;
        public Task<RecycleBinPresence> QueryAsync(IReadOnlyList<RecycleBinItem> items, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecycleBinPresence(items, [], []));
        public Task<RecycleBinDeleteResult> MoveToRecycleBinAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            Deleted.AddRange(paths);
            return Task.FromResult(new RecycleBinDeleteResult(paths.Select(path => new RecycleBinItem(path, [0, 0])).ToArray(), []));
        }
        public Task<FileActionBatchResult> RestoreAsync(IReadOnlyList<RecycleBinItem> items, CancellationToken cancellationToken = default)
        {
            RestoreCalls++;
            return Task.FromResult(new FileActionBatchResult(items.Select(item => item.OriginalPath).ToArray(), [], false));
        }
    }
}
