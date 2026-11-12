using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Core.Tests.Conflicts;

namespace Sims4ModSieve.Core.Tests;

[TestClass]
public sealed class LinkTargetDiscoveryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealDirectoryLinkIsSkippedAndCarriesTargetAcrossScanners(bool asSource)
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var target = temp.CreateDirectory("Real", "Content");
        var link = Path.Combine(mods, "Linked");
        CreateDirectoryLink(link, target);
        var fileSystem = new PhysicalFileSystemAccess();
        var sources = new[] { new ScanSource("mods", asSource ? link : mods) };
        var duplicates = await DuplicateScanner.CreateDefault().ScanAsync(new(sources));
        var packages = await new PackageConflictScanner(fileSystem, new FakePackageIndexReader()).ScanAsync(new(sources));
        var scripts = await new ScriptModuleScanner(fileSystem).ScanAsync(new(sources));

        Assert.AreEqual(target, fileSystem.GetLinkTarget(link));
        Assert.AreEqual(target, duplicates.Issues.Single().LinkTarget);
        Assert.AreEqual(target, packages.Issues.Single().LinkTarget);
        Assert.AreEqual(target, scripts.Issues.Single().LinkTarget);
        Assert.AreEqual(asSource ? "source-is-reparse-point" : "reparse-point-skipped", duplicates.Issues.Single().Code);
        StringAssert.Contains(duplicates.Issues.Single().Message, "没有跟进去扫描");
        Assert.IsNull(fileSystem.GetLinkTarget(target));
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, false, true)]
    [DataRow(true, true, true)]
    public async Task CoveredRealLocationDoesNotReportLink(bool asSource, bool parent, bool enabled)
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var real = temp.CreateDirectory("Real");
        var target = temp.CreateDirectory("Real", "Content");
        var link = Path.Combine(mods, "Linked");
        CreateDirectoryLink(link, target);
        temp.WriteFile("Real/Content/a.package", "content");
        var report = await DuplicateScanner.CreateDefault().ScanAsync(new([
            new("mods", asSource ? link : mods), new("real", parent ? real : target, Enabled: enabled)]));

        Assert.AreEqual(enabled ? 0 : 1, report.Issues.Count);
        Assert.AreEqual(enabled ? 1 : 0, report.DiscoveredFileCount);
    }

    [TestMethod]
    public async Task CoveredPathBehindAnotherLinkStillReportsSkippedLink()
    {
        using var temp = new TempDirectory();
        var mods = temp.CreateDirectory("Mods");
        var real = temp.CreateDirectory("Real");
        var target = temp.CreateDirectory("Target", "Content");
        var outer = Path.Combine(real, "Outer");
        CreateDirectoryLink(outer, Path.GetDirectoryName(target)!);
        var link = temp.CreateDirectory("Mods", "Linked");
        var physical = new PhysicalFileSystemAccess();
        var scanner = new DuplicateScanner(new ReparsePointFileSystemAccess(physical, link, Path.Combine(outer, "Content")),
            new StableFileHasher(physical, TimeSpan.Zero), new DuplicateSelectionService());
        var report = await scanner.ScanAsync(new([new("mods", mods), new("real", real)]));
        Assert.IsTrue(report.Issues.Any(issue => issue.Path == link && issue.Code == "reparse-point-skipped"));
    }

    [TestMethod]
    public async Task MissingSourceExplainsMissingFolderInChinese()
    {
        using var temp = new TempDirectory();
        var report = await DuplicateScanner.CreateDefault().ScanAsync(new([new("missing", Path.Combine(temp.Path, "missing"))]));
        Assert.AreEqual("source-not-found", report.Issues.Single().Code);
        StringAssert.Contains(report.Issues.Single().Message, "找不到这个文件夹");
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
    }
}
