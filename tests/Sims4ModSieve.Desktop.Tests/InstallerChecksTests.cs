using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Desktop.Services;
using static Sims4ModSieve.Desktop.Tests.SettingsViewModelTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class InstallerChecksTests
{
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
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, mods }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode);
        try
        {
            Assert.AreEqual(InstallerChecks.UnsafePath, InstallerChecks.CheckPath(Path.Combine(link, "Tool"),
                Path.Combine(files.Root, "Sims4ModSieve"), files.Folder("Documents")));
        }
        finally { Directory.Delete(link); }
    }

    [TestMethod]
    public void UnreadableSettingsOrRelativePathsStopInstallation()
    {
        using var files = new Scenario();
        var data = files.Folder("Sims4ModSieve");
        var target = files.Folder("Apps");
        var docs = files.Folder("Documents");
        File.WriteAllText(Path.Combine(data, "duplicate-settings.json"), "{");
        Assert.AreEqual(InstallerChecks.CannotCheck, InstallerChecks.CheckPath(target, data, docs));
        File.Delete(Path.Combine(data, "duplicate-settings.json"));
        Assert.AreEqual(InstallerChecks.CannotCheck, InstallerChecks.CheckPath("relative", data, docs));
        Assert.IsTrue(InstallerChecks.TryRun(["--installer-check-path"], out var code));
        Assert.AreEqual(InstallerChecks.CannotCheck, code);
    }

    [TestMethod]
    [DataRow("missing", 0)]
    [DataRow("empty", 0)]
    [DataRow("disabled", InstallerChecks.DisabledMods)]
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
            Recycled: state is "recycled" or "pending", RestorePending: state == "pending");
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
}
