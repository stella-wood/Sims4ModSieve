using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Settings;
using Sims4ModSieve.Desktop.Services;
using static Sims4ModSieve.Desktop.Tests.SettingsViewModelTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class InstallerChecksTests
{
    [TestMethod]
    public void InstallerPathEntryReturnsBeforeCreatingWpfApplication()
    {
        using var files = new Scenario();
        var application = System.Windows.Application.Current;
        var data = files.Folder("Sims4ModSieve");
        var documents = files.Folder("Documents");
        Assert.AreEqual(0, Program.Main(["--installer-check-path", files.Folder("App"), data, documents]));
        var report = Path.Combine(files.Root, "path.ini");
        Assert.AreEqual(InstallerChecks.UnsafePath, Program.Main(["--installer-check-path", data, data, documents, report]));
        StringAssert.Contains(File.ReadAllText(report), "命中受保护目录：工具数据目录");
        Assert.AreSame(application, System.Windows.Application.Current);
    }

    [TestMethod]
    public void InstallerUninstallEntryReturnsBeforeCreatingWpfApplication()
    {
        using var files = new Scenario();
        var application = System.Windows.Application.Current;
        var data = files.Folder("Sims4ModSieve");
        var report = Path.Combine(files.Root, "uninstall.ini");
        Assert.AreEqual(0, Program.Main(["--installer-check-uninstall", data, report]));
        StringAssert.Contains(File.ReadAllText(report), "DisabledCount=0");
        File.WriteAllText(Path.Combine(data, "disabled-mods.json"), "{");
        Assert.AreEqual(InstallerChecks.CannotCheck, Program.Main(["--installer-check-uninstall", data]));
        Assert.AreSame(application, System.Windows.Application.Current);
    }

    [TestMethod]
    [DataRow("--installer-check-path")]
    [DataRow("--installer-check-uninstall")]
    [DataRow("--installer-unknown")]
    public void MalformedInstallerEntryReturnsBeforeCreatingWpfApplication(string argument)
    {
        var application = System.Windows.Application.Current;
        Assert.AreEqual(InstallerChecks.CannotCheck, Program.Main([argument]));
        Assert.AreSame(application, System.Windows.Application.Current);
    }

    [TestMethod]
    public void RejectsGameAndConfiguredFoldersUsingDirectoryBoundaries()
    {
        using var files = new Scenario();
        var data = files.Folder("Sims4ModSieve");
        var documents = files.Folder("Documents");
        var mods = files.Folder("CustomMods");
        var tray = files.Folder("CustomTray");
        files.Store.Save(new(mods, [], tray));
        foreach (var path in new[] { mods, Path.Combine(mods, "Tool"), tray, data,
            Path.Combine(documents, "Electronic Arts", "The Sims 4"),
            Path.Combine(files.Root, "Other", "Electronic Arts", "The Sims 4", "Tool"),
            @"\\?\" + mods + @"\Tool",
            Path.Combine(mods, "..", "CustomMods", "Tool").ToUpperInvariant() })
            Assert.AreEqual(InstallerChecks.UnsafePath, InstallerChecks.CheckPath(path, data, documents), path);
        foreach (var path in new[] { mods + "Other", Path.Combine(files.Root, "Apps", "Tool") })
            Assert.AreEqual(0, InstallerChecks.CheckPath(path, data, documents), path);
    }

    [TestMethod]
    public void JunctionCannotBypassConfiguredFolderProtection()
    {
        using var files = new Scenario();
        var mods = files.Folder("Mods");
        files.Store.Save(new(mods, []));
        var link = Path.Combine(files.Root, "Alias");
        CreateJunction(link, mods);
        try
        {
            Assert.AreEqual(InstallerChecks.UnsafePath, InstallerChecks.CheckPath(Path.Combine(link, "Tool"),
                Path.Combine(files.Root, "Sims4ModSieve"), files.Folder("Documents")));
            files.Store.Save(new(link, []));
            Assert.AreEqual(InstallerChecks.UnsafePath, InstallerChecks.CheckPath(Path.Combine(mods, "Tool"),
                Path.Combine(files.Root, "Sims4ModSieve"), files.Folder("Documents")));
        }
        finally { Directory.Delete(link); }
    }

    private static void CreateJunction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode);
    }

    [TestMethod]
    [DataRow("malformed")]
    [DataRow("missing-sources")]
    [DataRow("locked")]
    public void SkipsUnreadableSettingsAndUsesNextReadableFile(string failure)
    {
        using var files = new Scenario();
        var data = files.Folder("Sims4ModSieve");
        var target = files.Folder("Apps");
        var docs = files.Folder("Documents");
        var report = Path.Combine(files.Root, "path.ini");
        var primary = Path.Combine(data, "duplicate-settings.json");
        var older = Path.Combine(files.Folder("Sim4ModSieve"), "duplicate-settings.json");
        var oldest = Path.Combine(files.Folder("Sims4ModDoctor"), "duplicate-settings.json");
        var mods = files.Folder("OldMods");
        var tray = files.Folder("OldTray");
        var invalidSettings = failure == "malformed" ? "{" : "{}";
        File.WriteAllText(primary, invalidSettings);
        File.WriteAllText(older, "null");
        using (var locked = failure == "locked" ? File.Open(primary, FileMode.Open, FileAccess.Read, FileShare.None) : null)
        {
            Assert.AreEqual(0, InstallerChecks.CheckPath(target, data, docs, report));
            StringAssert.Contains(File.ReadAllText(report), $"跳过设置：{primary}");
            StringAssert.Contains(File.ReadAllText(report), $"跳过设置：{older}");
            Assert.AreEqual(InstallerChecks.UnsafePath, InstallerChecks.CheckPath(data, data, docs));
            Assert.AreEqual(InstallerChecks.UnsafePath, InstallerChecks.CheckPath(Path.Combine(docs, "Electronic Arts", "The Sims 4"), data, docs));
            File.WriteAllText(oldest, JsonSerializer.Serialize(new DuplicateSettings(mods, [], tray)));
            foreach (var path in new[] { mods, tray })
                Assert.AreEqual(InstallerChecks.UnsafePath, InstallerChecks.CheckPath(Path.Combine(path, "Tool"), data, docs, report));
            StringAssert.Contains(File.ReadAllText(report), $"读取设置：{oldest}");
        }

        Assert.AreEqual(invalidSettings, File.ReadAllText(primary));
        Assert.AreEqual("null", File.ReadAllText(older));
        var current = files.Folder("CurrentMods");
        File.WriteAllText(primary, JsonSerializer.Serialize(new DuplicateSettings(current, [])));
        Assert.AreEqual(0, InstallerChecks.CheckPath(mods, data, docs));
        Assert.AreEqual(InstallerChecks.UnsafePath, InstallerChecks.CheckPath(current, data, docs));
    }

    [TestMethod]
    public void UnresolvedGameDirectoryDoesNotHideOtherMatchesOrDirectGamePath()
    {
        using var files = new Scenario();
        var data = files.Folder("Sims4ModSieve");
        var documents = Path.Combine(files.Root, "Documents");
        File.WriteAllText(documents, "模拟文档路径中无法解析的目录项");
        var report = Path.Combine(files.Root, "path.ini");
        var tray = files.Folder("Tray");
        files.Store.Save(new(null, [], tray));
        Assert.AreEqual(0, InstallerChecks.CheckPath(files.Folder("App"), data, documents, report));
        StringAssert.Contains(File.ReadAllText(report), "文档中的《模拟人生 4》文件夹：");
        StringAssert.Contains(File.ReadAllText(report), "解析失败");
        Assert.AreEqual(InstallerChecks.UnsafePath, InstallerChecks.CheckPath(tray, data, documents));
        var game = Path.Combine(documents, "Electronic Arts", "The Sims 4", "Tool");
        Assert.AreEqual(InstallerChecks.UnsafePath, InstallerChecks.CheckPath(game, data, documents));
    }

    [TestMethod]
    public void BrokenModsJunctionUsesFullPathAndKeepsCheckingTray()
    {
        using var files = new Scenario();
        var data = files.Folder("Sims4ModSieve");
        var documents = files.Folder("Documents");
        var tray = files.Folder("Tray");
        var mods = files.Folder("ModsTarget");
        var link = Path.Combine(files.Root, "ModsLink");
        var report = Path.Combine(files.Root, "path.ini");
        CreateJunction(link, mods);
        try
        {
            Directory.Delete(mods);
            files.Store.Save(new(link, [], tray));
            Assert.AreEqual(0, InstallerChecks.CheckPath(files.Folder("App"), data, documents, report));
            StringAssert.Contains(File.ReadAllText(report), "目录链接指向的位置当前无法访问");
            StringAssert.Contains(File.ReadAllText(report), $"使用完整路径 {link}");
            Assert.AreEqual(InstallerChecks.UnsafePath, InstallerChecks.CheckPath(Path.Combine(link, "Tool"), data, documents));
            Assert.AreEqual(InstallerChecks.UnsafePath, InstallerChecks.CheckPath(tray, data, documents));
        }
        finally { Directory.Delete(link); }
    }

    [TestMethod]
    public void OfflineModsDriveIsLoggedAndUnresolvedInstallTargetReturns11()
    {
        using var files = new Scenario();
        var data = files.Folder("Sims4ModSieve");
        var documents = files.Folder("Documents");
        var report = Path.Combine(files.Root, "path.ini");
        var drives = DriveInfo.GetDrives();
        var drive = Enumerable.Range('D', 23).Select(letter => $"{(char)letter}:\\")
            .First(root => !drives.Any(existing => existing.Name == root));
        files.Store.Save(new(Path.Combine(drive, "Mods"), []));
        Assert.AreEqual(0, InstallerChecks.CheckPath(files.Folder("App"), data, documents, report));
        StringAssert.Contains(File.ReadAllText(report), "所在的盘当前无法访问");
        Assert.AreEqual(InstallerChecks.CannotCheck, InstallerChecks.CheckPath("relative", data, documents, report));
        StringAssert.Contains(File.ReadAllText(report), "Reason=无法解析所选安装位置。路径必须为绝对路径。");
    }

    [TestMethod]
    public void ReportWriteFailureDoesNotOverridePathDecision()
    {
        using var files = new Scenario();
        var data = files.Folder("Sims4ModSieve");
        var documents = files.Folder("Documents");
        var unwritableReport = files.Folder("ReportIsADirectory");
        Assert.AreEqual(InstallerChecks.UnsafePath, InstallerChecks.CheckPath(data, data, documents, unwritableReport));
        Assert.AreEqual(0, InstallerChecks.CheckPath(files.Folder("App"), data, documents, unwritableReport));
    }

    [TestMethod]
    [DataRow("missing", 0)]
    [DataRow("empty", 0)]
    [DataRow("disabled", InstallerChecks.DisabledMods)]
    [DataRow("external", 0)]
    [DataRow("recycled", 0)]
    [DataRow("pending", InstallerChecks.DisabledMods)]
    [DataRow("invalid", InstallerChecks.CannotCheck)]
    [DataRow("null", InstallerChecks.CannotCheck)]
    [DataRow("malformed", InstallerChecks.CannotCheck)]
    public void UninstallChecksLedgerWithoutChangingFiles(string state, int expected)
    {
        using var files = new Scenario();
        var data = files.Folder("Sims4ModSieve");
        var ledger = Path.Combine(data, "disabled-mods.json");
        var original = Path.Combine(files.Root, "test.package");
        var disabled = original + ".off";
        File.WriteAllText(disabled, "user mod");
        var record = new DisabledModRecord(Guid.NewGuid().ToString("N"), original, 8, DateTime.UtcNow, DateTime.UtcNow,
            Recycled: state is "recycled" or "pending", RestorePending: state == "pending",
            Source: state == "external" ? DisabledModRecord.ExternalSource : DisabledModRecord.SieveSource,
            DisabledSuffix: state == "external" ? ".off" : null);
        string? json = state switch
        {
            "missing" => null, "empty" => "[]", "invalid" => "[{}]", "null" => "null", "malformed" => "{",
            _ => JsonSerializer.Serialize(new[] { record }),
        };
        if (json is not null) File.WriteAllText(ledger, json);
        Assert.AreEqual(expected, InstallerChecks.CheckUninstall(data));
        Assert.AreEqual(json, File.Exists(ledger) ? File.ReadAllText(ledger) : null);
        Assert.AreEqual("user mod", File.ReadAllText(disabled));
        Assert.IsFalse(File.Exists(original));
    }

    [TestMethod]
    public void UninstallReportCountsPendingModsAndListsDistinctCurrentFolders()
    {
        using var files = new Scenario();
        var data = files.Folder("Sims4ModSieve");
        var first = files.Folder("测试 Mods", "作者一");
        var second = files.Folder("测试 Mods", "作者二");
        DisabledModRecord Record(string folder, string name, bool recycled = false, bool pending = false) =>
            new(Guid.NewGuid().ToString("N"), Path.Combine(folder, name + ".package"), 0, DateTime.UtcNow, DateTime.UtcNow,
                Recycled: recycled, RestorePending: pending);
        var json = JsonSerializer.Serialize(new[] { Record(first, "a"), Record(first, "b"),
            Record(second, "c", recycled: true, pending: true), Record(second, "removed", recycled: true) });
        var ledger = Path.Combine(data, "disabled-mods.json");
        File.WriteAllText(ledger, json);
        var report = Path.Combine(files.Root, "uninstall.ini");
        Assert.IsTrue(InstallerChecks.TryRun(["--installer-check-uninstall", data, report], out var result));
        Assert.AreEqual(InstallerChecks.DisabledMods, result);
        CollectionAssert.AreEqual(new[] { "[Uninstall]", "DisabledCount=3", "FolderCount=2", $"Folder0={first}", $"Folder1={second}" }, File.ReadAllLines(report));
        Assert.AreEqual(json, File.ReadAllText(ledger));
    }

    [TestMethod]
    public void MissingLedgerProducesEmptyReportButUnreadableLedgerDoesNotProduceOne()
    {
        using var files = new Scenario();
        var data = files.Folder("Sims4ModSieve");
        var report = Path.Combine(files.Root, "uninstall.ini");
        Assert.AreEqual(0, InstallerChecks.CheckUninstall(data, report));
        StringAssert.Contains(File.ReadAllText(report), "DisabledCount=0");
        File.Delete(report);
        File.WriteAllText(Path.Combine(data, "disabled-mods.json"), "{");
        Assert.AreEqual(InstallerChecks.CannotCheck, InstallerChecks.CheckUninstall(data, report));
        Assert.IsFalse(File.Exists(report));
    }
}
