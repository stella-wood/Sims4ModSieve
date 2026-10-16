using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Desktop.Services;
using static Sims4ModSieve.Desktop.Tests.SettingsViewModelTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class SettingsSupportServiceTests
{
    [TestMethod]
    public async Task DiagnosticsHandleMissingUnreadableAndLargeLogs()
    {
        using var files = new Scenario();
        var support = new SettingsSupportService(files.Root);
        var logs = await support.ReadDiagnosticsAsync();
        Assert.AreEqual(2, logs.Count);
        Assert.IsTrue(logs.All(log => !log.HasContent && !log.ReadFailed));
        var directory = files.Folder("Sims4ModSieve");
        var applicationLog = Path.Combine(directory, "application-error.log");
        File.WriteAllText(applicationLog, "busy");
        File.WriteAllText(Path.Combine(directory, "startup-error.log"), new string('界', SettingsSupportService.MaximumLogCharacters + 100));
        using (File.Open(applicationLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            logs = await support.ReadDiagnosticsAsync();
            Assert.IsTrue(logs[0].ReadFailed);
            Assert.IsFalse(logs[1].ReadFailed);
            Assert.IsTrue(logs[1].IsTruncated);
            StringAssert.Contains(logs[1].Content, "仅显示／复制");
            Assert.IsLessThan(SettingsSupportService.MaximumLogCharacters + 100, logs[1].Content.Length);
        }
        File.WriteAllText(applicationLog, "still readable");
        File.Delete(Path.Combine(directory, "startup-error.log"));
        logs = await support.ReadDiagnosticsAsync();
        Assert.AreEqual("still readable", logs[0].Content);
        Assert.IsFalse(logs[1].HasContent);
    }

    [TestMethod]
    public async Task DiagnosticDisplayAndCopyRedactUserPaths()
    {
        using var files = new Scenario();
        var directory = files.Folder("Sims4ModSieve");
        var path = Path.Combine(directory, "application-error.log");
        var raw = "C:\\Users\\TestSecretUser\\Documents\\Mods\n" +
            "d:\\\\Users\\\\TestSecretUser\\\\Desktop\\\\problem\n" +
            "C:/Users/TestSecretUser/Documents/file";
        File.WriteAllText(path, raw);
        string? copied = null;
        var support = new SettingsSupportService(files.Root, copyText: text => copied = text);
        var model = files.Model(support: support);
        await model.RefreshDiagnosticsAsync();
        support.CopyDiagnosticText(model.DiagnosticLogs);
        Assert.IsNotNull(copied);
        Assert.IsFalse(copied.Contains("TestSecretUser", StringComparison.Ordinal));
        Assert.IsFalse(model.DiagnosticLogs[0].Content.Contains("TestSecretUser", StringComparison.Ordinal));
        StringAssert.Contains(copied, model.DiagnosticLogs[0].Content);
        StringAssert.Contains(copied, @"C:\Users\<用户>\Documents");
        StringAssert.Contains(copied, @"d:\\Users\\<用户>\\Desktop");
        Assert.AreEqual(raw, File.ReadAllText(path));
        Assert.AreEqual(typeof(SettingsSupportService).Assembly.GetName().Version!.ToString(), support.ProductVersion);
        Assert.AreEqual(Environment.OSVersion.VersionString, support.SystemVersion);
        var basic = support.BuildDiagnosticText([]);
        StringAssert.Contains(basic, support.ProductVersion);
        StringAssert.Contains(basic, support.SystemVersion);
    }

    [TestMethod]
    public void DefaultFolderLookupAndExternalTargetsStayWithinFixedScope()
    {
        using var files = new Scenario();
        var documents = files.Folder("重定向文档");
        var mods = files.Folder("重定向文档", "Electronic Arts", "The Sims 4", "Mods");
        var tray = files.Folder("重定向文档", "Electronic Arts", "The Sims 4", "Tray");
        var targets = new List<string>();
        var support = new SettingsSupportService(files.Root, documents, openTarget: targets.Add);
        Assert.AreEqual(0, targets.Count);
        Assert.AreEqual(mods, support.FindDefaultFolder("Mods"));
        Assert.AreEqual(tray, support.FindDefaultFolder("Tray"));
        support.OpenFolder(mods);
        support.OpenProjectPage();
        support.OpenProjectPage(feedback: true);
        CollectionAssert.AreEqual(new[] { mods, SettingsSupportService.ProjectUrl, SettingsSupportService.ProjectUrl + "/issues" }, targets);
        Assert.ThrowsExactly<ArgumentException>(() => support.FindDefaultFolder("../Other"));
        Assert.ThrowsExactly<ArgumentException>(() => support.OpenLicenseDocument("../LICENSE"));
        Assert.ThrowsExactly<DirectoryNotFoundException>(() => support.OpenFolder(Path.Combine(files.Root, "Missing")));
        Assert.AreEqual(3, targets.Count);
    }

    [TestMethod]
    public void LicenseDocumentsAvailableInBuildOutput()
    {
        var support = new SettingsSupportService();
        foreach (var name in new[] { "LICENSE", "THIRD-PARTY-NOTICES.md" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, name);
            Assert.IsTrue(File.Exists(path), path);
            Assert.AreEqual(File.ReadAllText(path), support.OpenLicenseDocument(name));
            Assert.IsFalse(string.IsNullOrWhiteSpace(support.OpenLicenseDocument(name)));
        }
    }
}
