using Sims4ModSieve.Core.Settings;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using static Sims4ModSieve.Desktop.Tests.ConflictPageRenderTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class LinkTargetScanTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public Task RealJunctionButtonAddsOrEnablesTargetOnlyOnCurrentPage(bool conflict, bool alreadyListed) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new LinkFiles();
        var model = files.Model();
        if (conflict) { model.OpenConflictCommand.Execute(null); model.OpenConflictSectionCommand.Execute("basic"); }
        else model.OpenDuplicateScannerCommand.Execute(null);
        var sources = conflict ? model.ConflictSources : model.Sources;
        var otherSources = conflict ? model.Sources : model.ConflictSources;
        if (alreadyListed) sources.Add(new(files.Target, isEnabled: false));
        if (conflict) await model.StartConflictScanCommand.ExecuteAsync();
        else await model.StartScanCommand.ExecuteAsync();
        Assert.IsFalse(model.ScanLinkTargetCommand.CanExecute(null));
        Assert.IsTrue(model.ScanLinkTargetCommand.CanExecute(files.Target));
        if (conflict)
        {
            Assert.AreEqual(1, model.ConflictRangeProblems.Count);
            Assert.IsTrue(model.ConflictRangeProblems.All(problem => problem.Summary == "链接没有跟进去检查"));
            await ExpandAsync(model, ConflictRowKind.RangeProblem);
        }
        else StringAssert.Contains(model.StatusDetail, "真实位置：" + files.Target);

        var window = OpenWindow(model);
        WpfTestHost.PlaceOnScreen(window);
        await WpfTestHost.LayoutAsync(window);
        var button = Descendants<Button>(window).First(button => button.Name == (conflict ? "ConflictScanLinkTarget" : "DuplicateScanLinkTarget") && button.IsVisible);
        Assert.IsTrue(button.IsEnabled);
        Assert.AreEqual(files.Target, button.ToolTip);
        Assert.IsTrue(button.Focus());
        foreach (var fraction in new[] { .1, .5, .9 })
        {
            var hit = button.InputHitTest(new Point(button.ActualWidth * fraction, button.ActualHeight / 2)) as DependencyObject;
            while (hit is not null && hit != button) hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
            Assert.AreSame(button, hit);
        }
        Save(window, $"link-target-{conflict}-{alreadyListed}-before.png");
        await WpfTestHost.ClickAsync(button);
        await WpfTestHost.LayoutAsync(window);
        Assert.AreEqual(2, sources.Count);
        Assert.IsTrue(sources.Single(source => source.Path == files.Target).IsEnabled);
        Assert.AreEqual(files.Link, otherSources.Single().Path);
        Assert.AreEqual(files.Link, model.ModsRoot);
        Assert.IsFalse(model.ScanLinkTargetCommand.CanExecute(files.Target));
        Assert.IsFalse(Descendants<Button>(window).Any(button => button.Name == (conflict ? "ConflictScanLinkTarget" : "DuplicateScanLinkTarget") && button.IsVisible));
        if (conflict) Assert.IsTrue(model.ConflictRangeProblems.Count > 0);
        else Assert.AreEqual(1, model.IssueCount);
        Save(window, $"link-target-{conflict}-{alreadyListed}-added.png");

        if (conflict)
        {
            await model.StartConflictScanCommand.ExecuteAsync();
            Assert.AreEqual(0, model.ConflictRangeProblems.Count);
        }
        else
        {
            await model.StartScanCommand.ExecuteAsync();
            Assert.AreEqual(0, model.IssueCount);
            Assert.IsNull(model.DuplicateLinkTarget);
        }
        sources.Clear();
        Assert.IsNull(model.DuplicateLinkTarget);
        Assert.AreEqual(0, model.ConflictRangeProblems.Count);
    });

    [TestMethod]
    public Task LinkTargetActionTracksSourceCoverageBusyStateAndSessionReset() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new LinkFiles();
        var model = files.Model();
        model.OpenDuplicateScannerCommand.Execute(null);
        await model.StartScanCommand.ExecuteAsync();
        Assert.IsTrue(model.ShowDuplicateScanLinkTarget);
        model.Sources.Add(new(files.Root));
        Assert.IsFalse(model.ShowDuplicateScanLinkTarget);
        model.Sources[^1].IsEnabled = false;
        Assert.IsTrue(model.ShowDuplicateScanLinkTarget);
        SetField(model, "isBusy", true);
        Assert.IsFalse(model.ScanLinkTargetCommand.CanExecute(files.Target));
        model.ScanLinkTargetCommand.Execute(files.Target);
        Assert.AreEqual(2, model.Sources.Count);
        SetField(model, "isBusy", false);
        model.Sources.Clear();
        Assert.IsNull(model.DuplicateLinkTarget);
        Assert.IsFalse(model.ShowDuplicateScanLinkTarget);
    });

    [TestMethod]
    public Task DuplicatePageStillShowsOnlyFirstIssueAndDoesNotOfferFileOrMissingTargets() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new LinkFiles();
        var first = new ScanIssue("reparse-point-skipped", ScanIssueStage.Discovery, files.Link, "第一条") { LinkTarget = files.Target };
        var second = new ScanIssue("source-not-found", ScanIssueStage.SourceValidation, files.Root, "第二条");
        var report = new DuplicateReport(DateTime.UnixEpoch, DateTime.UnixEpoch, [], 0, 0, [], [first, second]);
        var model = files.Model(new ReportRun(report));
        model.OpenDuplicateScannerCommand.Execute(null);
        await model.StartScanCommand.ExecuteAsync();
        Assert.IsTrue(model.ShowDuplicateScanLinkTarget);
        StringAssert.Contains(model.StatusDetail, "第一条");
        Assert.IsFalse(model.StatusDetail.Contains("第二条", StringComparison.Ordinal));
        Assert.IsFalse(model.ScanLinkTargetCommand.CanExecute(files.Link));
        Assert.IsFalse(model.ScanLinkTargetCommand.CanExecute(Path.Combine(files.Root, "missing")));
        var file = Path.Combine(files.Root, "file.package");
        File.WriteAllText(file, "file");
        Assert.IsFalse(model.ScanLinkTargetCommand.CanExecute(file));
    });

    private sealed class ReportRun(DuplicateReport report) : IDuplicateRunService
    {
        public Task<DuplicateReport> RunAsync(DuplicateRunInput input, IProgress<DuplicateScanProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(report);
    }

    [TestMethod]
    public void LinkRangeIsSharedAcrossScanPhasesAndViews()
    {
        const string path = @"C:\Mods\Linked";
        const string target = @"D:\RealMods";
        var packages = ConflictResultSessionTests.Packages(paths: [], issues: [
            new("reparse-point-skipped", PackageConflictScanIssueStage.Discovery, path, "链接") { LinkTarget = target }]);
        var scripts = ConflictResultSessionTests.Scripts(issues: [
            new("reparse-point-skipped", ScriptArchiveIssueStage.Discovery, path.ToLowerInvariant(), "链接") { LinkTarget = target }]);
        var session = new ConflictResultSession();
        session.Replace(packages, ConflictResultSessionTests.Contents(), scripts);
        Assert.AreEqual(1, session.RangeProblems.Count);
        foreach (var view in new[] { "all", "resources", "scripts", "problems" })
        {
            var header = session.BuildVisibleRows(view, "all", new HashSet<string>()).Single(row => row.IsCardStart);
            var rows = session.BuildVisibleRows(view, "all", new HashSet<string> { header.Key });
            Assert.AreEqual(target, rows.Single(row => row.LinkTarget is not null).LinkTarget);
        }
    }

    [TestMethod]
    public void StorageProbeResolvesDirectoryUnderAJunctionAndHandlesMissingPaths()
    {
        using var files = new LinkFiles();
        var target = Directory.CreateDirectory(Path.Combine(files.Target, "Nested")).FullName;
        var profile = new WindowsStorageProfile();
        var direct = profile.GetVolume(target);
        var viaJunction = profile.GetVolume(Path.Combine(files.Link, "Nested"));
        Assert.IsNotNull(direct.VolumeId);
        Assert.AreEqual(direct, viaJunction);
        var missing = profile.GetVolume(Path.Combine(files.Target, "Missing"));
        Assert.IsNull(missing.IncursSeekPenalty);
        Assert.IsFalse(string.IsNullOrWhiteSpace(missing.UnknownReason));
    }

    [TestMethod]
    [TestCategory("WindowsIntegration")]
    public void StorageProbeReturnsSeekPenaltyOnSupportedVolume()
    {
        var profile = new WindowsStorageProfile().GetVolume(GetStorageProbeTestRoot());

        Assert.IsNotNull(profile.VolumeId, profile.UnknownReason);
        Assert.IsNotNull(profile.IncursSeekPenalty, profile.UnknownReason);
    }

    [TestMethod]
    [TestCategory("WindowsIntegration")]
    public void StorageProbeResolvesCrossVolumeJunctionToTargetVolume()
    {
        var targetRoot = GetStorageProbeTestRoot();
        var profile = new WindowsStorageProfile();
        var sourceVolume = profile.GetVolume(Path.GetFullPath(Path.GetTempPath()));
        var targetVolume = profile.GetVolume(targetRoot);
        Assert.IsNotNull(sourceVolume.VolumeId, sourceVolume.UnknownReason);
        Assert.IsNotNull(targetVolume.VolumeId, targetVolume.UnknownReason);
        Assert.AreNotEqual(sourceVolume.VolumeId, targetVolume.VolumeId,
            "The configured root must be on a different volume from the system temp directory.");

        var source = Directory.CreateTempSubdirectory("s4ms-storage-probe-");
        var link = Path.Combine(source.FullName, "Junction");
        var target = Path.Combine(targetRoot, source.Name);
        var nested = Path.Combine(target, "Nested");
        try
        {
            Directory.CreateDirectory(nested);
            CreateJunction(link, target);

            var direct = profile.GetVolume(nested);
            var viaJunction = profile.GetVolume(Path.Combine(link, "Nested"));
            Assert.AreEqual(targetVolume.VolumeId, direct.VolumeId, direct.UnknownReason);
            Assert.AreEqual(targetVolume.VolumeId, viaJunction.VolumeId, viaJunction.UnknownReason);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(nested)) Directory.Delete(nested);
            if (Directory.Exists(target)) Directory.Delete(target);
            source.Delete();
        }
    }

    private static string GetStorageProbeTestRoot()
    {
        const string variable = "SIMS4_MOD_SIEVE_STORAGE_PROBE_TEST_ROOT";
        var path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(path))
            Assert.Inconclusive($"Set {variable} to an existing writable directory on a local test volume.");
        Assert.IsTrue(Path.IsPathFullyQualified(path) && Directory.Exists(path),
            $"{variable} must be an existing absolute directory path.");
        return Path.GetFullPath(path);
    }

    private static void CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("powershell.exe")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["S4MS_TEST_JUNCTION_LINK"] = link;
        start.Environment["S4MS_TEST_JUNCTION_TARGET"] = target;
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path $env:S4MS_TEST_JUNCTION_LINK -Target $env:S4MS_TEST_JUNCTION_TARGET | Out-Null" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
    }

    private sealed class LinkFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "s4ms-link-" + Guid.NewGuid().ToString("N"));
        public string Target => Path.Combine(Root, "真实 Mods");
        public string Link => Path.Combine(Root, "LinkedMods");

        public LinkFiles()
        {
            Directory.CreateDirectory(Target);
            CreateJunction(Link, Target);
        }

        public MainWindowViewModel Model(IDuplicateRunService? run = null)
        {
            var settings = new DuplicateSettingsStore(Path.Combine(Root, "state"));
            settings.Save(new DuplicateSettings(Link, []));
            return new(run ?? new DuplicateRunService(DuplicateScanner.CreateDefault()), new NoPicker(), settings);
        }

        public void Dispose()
        {
            Directory.Delete(Link);
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class NoPicker : IFolderPickerService
    {
        public string? PickFolder(string title, string? initialDirectory = null) => null;
    }
}
