using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using Sims4ModSieve.Packages;
using static Sims4ModSieve.Desktop.Tests.SettingsViewModelTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class PortableScanLocationTests
{
    [TestMethod]
    public Task SharedFolderNoticeTracksSourcesAndDoesNotBlockEitherScan() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var app = files.Folder("Desktop");
        var other = files.Folder("Mods");
        foreach (var name in new[] { "first.ts4script", "second.ts4script", "different.ts4script" })
        {
            using var archive = ZipFile.Open(Path.Combine(app, name), ZipArchiveMode.Create);
            var entry = archive.CreateEntry("shared.py");
            entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(name == "different.ts4script" ? "value = 2\n" : "value = 1\n");
        }
        File.WriteAllText(Path.Combine(app, "Sims4ModSieve.exe"), "not a Mod");
        var model = CreateModel(files, other, app);
        model.Sources.Clear();
        model.ConflictSources.Clear();
        var window = new MainWindow(model);
        window.Show();
        model.OpenDuplicateScannerCommand.Execute(null);
        Assert.IsFalse(model.HasApplicationLocationWarning);
        var source = new ScanSourceViewModel(app);
        model.Sources.Add(source);
        await WpfTestHost.LayoutAsync(window);
        var notice = (Border)window.FindName("ApplicationLocationNotice");
        Assert.IsTrue(notice.IsVisible);
        StringAssert.Contains(((TextBlock)notice.Child).Text, "建议把 Sims4ModSieve 放进单独的文件夹");
        Assert.IsTrue(model.StartScanCommand.CanExecute(null));
        await model.StartScanCommand.ExecuteAsync();
        Assert.AreEqual(3, model.DiscoveredFileCount);
        Assert.AreEqual(1, model.DuplicateGroupCount);
        Assert.AreEqual(0, model.IssueCount);
        await WpfTestHost.LayoutAsync(window);
        ConflictPageRenderTests.Save(window, "shared-folder-duplicates.png");

        source.IsEnabled = false;
        await WpfTestHost.LayoutAsync(window);
        Assert.IsFalse(notice.IsVisible);
        model.ConflictSources.Add(new(app));
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        await WpfTestHost.LayoutAsync(window);
        Assert.IsTrue(notice.IsVisible);
        Assert.IsTrue(model.StartConflictScanCommand.CanExecute(null));
        await model.StartConflictScanCommand.ExecuteAsync();
        Assert.AreEqual("complete", model.ConflictRunState, model.ConflictStatus + " · " + model.ConflictPhase);
        Assert.IsTrue(model.ConflictScripts.Any(pair => pair.HasConfirmedDifference), model.ConflictStatus + " · " + model.ConflictPhase);
        Assert.IsFalse(model.HasConflictProblems);
        await WpfTestHost.LayoutAsync(window);
        ConflictPageRenderTests.Save(window, "shared-folder-conflicts.png");
        model.ConflictSources.Clear();
        await WpfTestHost.LayoutAsync(window);
        Assert.IsFalse(notice.IsVisible);
        window.Close();
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task EmptyFolderShowsNoModsOnBothPages(bool programFilesOnly) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var app = files.Folder("Desktop");
        if (programFilesOnly) File.WriteAllText(Path.Combine(app, "Sims4ModSieve.exe"), "program");
        var model = CreateModel(files, app, app);
        var window = new MainWindow(model);
        window.Show();
        model.OpenDuplicateScannerCommand.Execute(null);
        await model.StartScanCommand.ExecuteAsync();
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual("这个文件夹里没有找到 mod 文件", model.DuplicatePlaceholderText);
        Assert.AreEqual(0, model.IssueCount);
        Assert.IsTrue(ConflictPageRenderTests.Descendants<TextBlock>(window).Any(text => text.IsVisible && text.Text == model.DuplicatePlaceholderText));
        ConflictPageRenderTests.Save(window, "empty-folder-duplicates.png");
        model.OpenConflictCommand.Execute(null);
        model.OpenConflictSectionCommand.Execute("basic");
        await model.StartConflictScanCommand.ExecuteAsync();
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual("这个文件夹里没有找到 mod 文件", model.ConflictStatus);
        Assert.AreEqual("这个文件夹里没有找到 mod 文件", model.EmptyConflictText);
        Assert.IsFalse(model.HasConflictProblems);
        Assert.IsTrue(ConflictPageRenderTests.Descendants<TextBlock>(window).Any(text => text.IsVisible && text.Text == model.EmptyConflictText));
        ConflictPageRenderTests.Save(window, "empty-folder-conflicts.png");
        window.Close();
    });

    [TestMethod]
    public async Task ReadFailuresKeepTheirReasonsInsteadOfReportingAnEmptyFolder()
    {
        using var files = new Scenario();
        var root = files.Folder("Unreadable");
        var model = CreateModel(files, root, files.Folder("Program"), new DeniedDirectoryFileSystem());
        await model.StartScanCommand.ExecuteAsync();
        Assert.AreNotEqual("这个文件夹里没有找到 mod 文件", model.StatusText);
        StringAssert.Contains(model.StatusDetail, "fixture permission denied");
        StringAssert.Contains(model.StatusDetail, "无法读取");
        Assert.AreEqual(1, model.IssueCount);
        await model.StartConflictScanCommand.ExecuteAsync();
        Assert.AreEqual("complete", model.ConflictRunState, model.ConflictStatus + " · " + model.ConflictPhase);
        Assert.AreNotEqual("这个文件夹里没有找到 mod 文件", model.ConflictStatus);
        Assert.IsTrue(model.ConflictRangeProblems.Any(problem => problem.Detail?.Contains("fixture permission denied", StringComparison.Ordinal) == true),
            model.ConflictStatus + " · " + model.ConflictPhase);
    }

    [TestMethod]
    public async Task CorruptModsAreReportedAsFailuresRatherThanNoMods()
    {
        using var files = new Scenario();
        var root = files.Folder("Mods");
        File.WriteAllText(Path.Combine(root, "broken.package"), "not DBPF");
        File.WriteAllText(Path.Combine(root, "broken.ts4script"), "not ZIP");
        var model = CreateModel(files, root, root);
        await model.StartConflictScanCommand.ExecuteAsync();
        Assert.AreNotEqual("这个文件夹里没有找到 mod 文件", model.ConflictStatus);
        Assert.AreEqual(2, model.ConflictProblems.Count);
        Assert.IsTrue(model.ConflictProblems.All(problem => problem.Reasons.Any(reason => !string.IsNullOrWhiteSpace(reason.Detail))));
    }

    [TestMethod]
    public void ModBesideProgramCanBeDisabledAndRestored()
    {
        using var files = new Scenario();
        var app = files.Folder("Desktop");
        var path = Path.Combine(app, "user.package");
        File.WriteAllText(path, "Mod content");
        var service = new ModDisableService(files.Folder("data"), app);
        Assert.AreEqual(1, service.Disable([path]).CompletedPaths.Count);
        Assert.AreEqual(1, service.Restore([service.Load().Records.Single().Id]).CompletedPaths.Count);
        Assert.AreEqual("Mod content", File.ReadAllText(path));
    }

    private static MainWindowViewModel CreateModel(Scenario files, string root, string app, IFileSystemAccess? fileSystem = null)
    {
        files.Store.Save(new(root, []));
        fileSystem ??= new PhysicalFileSystemAccess();
        return new(new DuplicateRunService(new DuplicateScanner(fileSystem, new StableFileHasher(fileSystem), new(), app)),
            new Picker(), files.Store,
            packageConflictScanner: new PackageConflictScanner(fileSystem, LlamaLogicPackageIndexReader.CreateDefault(), app),
            scriptModuleScanner: new ScriptModuleScanner(fileSystem, app),
            settingsSupport: new SettingsSupportService(files.Root, files.Folder("Documents"), app));
    }

    private sealed class DeniedDirectoryFileSystem : IFileSystemAccess
    {
        private readonly PhysicalFileSystemAccess inner = new();
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public IReadOnlyList<string> EnumerateFileSystemEntries(string directory) => throw new UnauthorizedAccessException("fixture permission denied");
        public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);
        public FileStamp GetFileStamp(string path) => inner.GetFileStamp(path);
        public Stream OpenRead(string path) => inner.OpenRead(path);
    }
}
