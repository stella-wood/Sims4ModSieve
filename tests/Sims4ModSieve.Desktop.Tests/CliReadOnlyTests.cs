using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Cli;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Scripts;
using Sims4ModSieve.Core.Settings;
using Sims4ModSieve.Core.Tests;
using Sims4ModSieve.Core.Tests.Packages;
using Sims4ModSieve.Desktop.Services;
using Sims4ModSieve.Desktop.ViewModels;
using Sims4ModSieve.Packages;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class CliReadOnlyTests
{
    [TestMethod]
    public async Task ConflictAndScriptCommandsMatchDesktopCardsAndListWholeFileHashes()
    {
        using var temp = new TempDirectory();
        var packagesRoot = temp.CreateDirectory("Mods", "Packages");
        var scriptsRoot = temp.CreateDirectory("Mods", "Scripts");
        var a = new DbpfFixtureBuilder().AddContent([1, 2], instanceLo: 1).AddContent([3], instanceLo: 2).Build();
        var b = new DbpfFixtureBuilder().AddContent([3, 4], instanceLo: 1)
            .AddContent([5, 6], instanceLo: 3).AddContent([7], instanceLo: 4).Build();
        var packagePath = temp.WriteBytes(@"Mods\Packages\a.package", a);
        var copyPath = temp.WriteBytes(@"Mods\Packages\a-copy.package", a);
        temp.WriteBytes(@"Mods\Packages\b.package", b);
        WriteScript(Path.Combine(scriptsRoot, "a.ts4script"), "x = 1");
        WriteScript(Path.Combine(scriptsRoot, "b.ts4script"), "x = 2");

        var settings = new DuplicateSettingsStore(temp.Path);
        var sources = new[] { new ScanSource("packages", packagesRoot), new ScanSource("scripts", scriptsRoot, Order: 1) };
        var physical = new PhysicalFileSystemAccess();
        var packages = await new PackageConflictScanner(physical, LlamaLogicPackageIndexReader.CreateDefault()).ScanAsync(new(sources));
        var contents = await new ResourceContentComparer(DbpfResourceContentHasher.CreateDefault()).CompareAsync(new(packages.Candidates));
        var scripts = await new ScriptModuleScanner(physical).ScanAsync(new(sources));
        var duplicates = await new DuplicateRunService(DuplicateScanner.CreateDefault())
            .RunAsync(new(sources.Select(source => new DuplicateRunSource(source.Path, source.Id)).ToArray()));
        var desktop = new ConflictResultSession();
        desktop.Replace(packages, contents, scripts, duplicates: duplicates);

        var conflicts = await Run(settings, "scan-conflicts", "--source", packagesRoot, "--source", scriptsRoot, "--json", "-");
        Assert.AreEqual(1, conflicts.ExitCode);
        StringAssert.Contains(conflicts.Error, "--source");
        using var conflictJson = JsonDocument.Parse(conflicts.Output);
        var root = conflictJson.RootElement;
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.IsFalse(string.IsNullOrEmpty(root.GetProperty("version").GetString()));
        Assert.AreEqual("argument", root.GetProperty("sourceOrigin").GetString());
        Assert.HasCount(2, root.GetProperty("sources").EnumerateArray().ToArray());
        CollectionAssert.AreEquivalent(desktop.Cards.Select(CardKey).ToArray(), root.GetProperty("groups").EnumerateArray().Select(GroupKey).ToArray());
        var resource = root.GetProperty("groups").EnumerateArray().Single(group => Read(group, "kind") == "resource_conflict");
        var resourceFiles = resource.GetProperty("files").EnumerateArray().ToArray();
        Assert.IsTrue(resourceFiles.Any(file => Read(file, "path") == packagePath));
        Assert.IsTrue(resourceFiles.Any(file => Read(file, "path") == copyPath));
        Assert.AreEqual(2, resourceFiles.Count(file => Read(file, "duplicateSha256") is not null));
        foreach (var file in root.GetProperty("groups").EnumerateArray().SelectMany(group => group.GetProperty("files").EnumerateArray()))
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Read(file, "path")!))), Read(file, "sha256"));

        var scriptScan = await Run(settings, "scan-scripts", "--source", scriptsRoot, "--json", "-");
        Assert.AreEqual(1, scriptScan.ExitCode);
        StringAssert.Contains(scriptScan.Error, "1 项异常");
        using var scriptJson = JsonDocument.Parse(scriptScan.Output);
        var scriptGroups = scriptJson.RootElement.GetProperty("groups").EnumerateArray().ToArray();
        CollectionAssert.AreEquivalent(desktop.Cards.Where(card => card.Modules is not null).Select(CardKey).ToArray(),
            scriptGroups.Select(GroupKey).ToArray());
        CollectionAssert.AreEqual(new[] { "lib.other", "lib.shared" },
            scriptGroups.Single().GetProperty("modules").EnumerateArray().Select(module => Read(module, "moduleName")).ToArray());
    }

    [TestMethod]
    public async Task DuplicateCommandKeepsFixtureResultsAndReadsDesktopIgnoredFiles()
    {
        using var temp = new TempDirectory();
        var mods = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Duplicates", "Mods");
        var downloads = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Duplicates", "Downloads");
        var ignoredPath = Path.Combine(downloads, "椅子-copy.package");
        var settings = new DuplicateSettingsStore(temp.Path);
        new FileManagementStore(settings.DataDirectory).Update(data => data with { Ignored = [new(ignoredPath, 0, DateTime.UtcNow)] });
        var args = new[] { "scan-duplicates", "--source", mods, "--source", downloads, "--mods-root", mods,
            "--extension", ".package", "--json", "-" };

        foreach (var hide in new[] { false, true })
        {
            var scan = await Run(settings, hide ? [.. args, "--hide-ignored"] : args);
            Assert.AreEqual(1, scan.ExitCode);
            using var document = JsonDocument.Parse(scan.Output);
            var root = document.RootElement;
            Assert.AreEqual(1, root.GetProperty("ignoredCount").GetInt32());
            Assert.AreEqual(3, root.GetProperty("discoveredFileCount").GetInt32());
            var group = root.GetProperty("groups").EnumerateArray().Single();
            Assert.AreEqual("risk", Read(group, "severity"));
            var files = group.GetProperty("files").EnumerateArray().ToArray();
            Assert.HasCount(hide ? 1 : 2, files);
            Assert.AreEqual(hide ? 0 : 1, files.Count(file => file.GetProperty("ignored").GetBoolean()));
            Assert.AreEqual(hide ? 0 : 1, group.GetProperty("suggestedDeletePaths").GetArrayLength());
            Assert.IsTrue(files.All(file => Read(file, "sha256")?.Length == 64));
        }

        var jsonPath = Path.Combine(temp.Path, "report.json");
        var htmlPath = Path.Combine(temp.Path, "report.html");
        var fileArgs = args[..^2].Concat(["--json", jsonPath, "--html", htmlPath]).ToArray();
        var report = await Run(settings, fileArgs);
        Assert.AreEqual(1, report.ExitCode);
        using var writtenJson = JsonDocument.Parse(File.ReadAllText(jsonPath));
        Assert.AreEqual(1, writtenJson.RootElement.GetProperty("groupCount").GetInt32());
        StringAssert.Contains(File.ReadAllText(htmlPath), "<!doctype html>");
        Assert.IsNull(settings.Load());
    }

    [TestMethod]
    public async Task ConfigAndFindModsUseDesktopRulesAndPreserveEditsFromAnOpenWindow()
    {
        using var temp = new SettingsViewModelTests.Scenario();
        var documents = temp.Folder("Documents");
        var fallback = temp.Folder("Documents", "Electronic Arts", "The Sims 4", "Mods");
        var next = temp.Folder("新 Mods");
        var tray = temp.Folder("Tray");
        var missing = Path.Combine(temp.Root, "missing");
        var initial = await RunWithDocuments(temp.Store, documents, "find-mods");
        Assert.AreEqual(0, initial.ExitCode);
        StringAssert.Contains(initial.Output, fallback);

        temp.Store.Save(new(fallback, [new(tray, false), new(missing, true)]));
        var model = temp.Model(new SettingsViewModelTests.Picker(tray));
        var settingsPath = Path.Combine(temp.Store.DataDirectory, "duplicate-settings.json");
        var before = File.ReadAllText(settingsPath);
        Assert.AreEqual(2, (await Run(temp.Store, "config", "set-mods", missing)).ExitCode);
        Assert.AreEqual(before, File.ReadAllText(settingsPath));
        var show = await Run(temp.Store, "config", "show");
        StringAssert.Contains(show.Output, $"停用 · {tray}（存在）");
        StringAssert.Contains(show.Output, $"启用 · {missing}（不存在或不可访问）");

        Assert.AreEqual(0, (await Run(temp.Store, "config", "set-mods", next)).ExitCode);
        Assert.HasCount(2, temp.Store.Load()!.Sources);
        var found = await RunWithDocuments(temp.Store, documents, "find-mods");
        StringAssert.Contains(found.Output, $"设置中的 Mods：{next}");
        Assert.AreEqual(fallback, model.ModsRoot);
        model.ChangeTrayRootCommand.Execute(null);
        Assert.AreEqual(next, model.ModsRoot);
        Assert.AreEqual(next, temp.Store.Load()!.ModsRoot);
        Assert.AreEqual(tray, temp.Store.Load()!.TrayRoot);
        Assert.AreEqual(fallback, model.Sources.Single().Path);
        Assert.AreEqual(fallback, model.ConflictSources.Single().Path);
        Assert.AreEqual(0, (await Run(temp.Store, "config", "set-mods", fallback)).ExitCode);
        model.RefreshSavedFolders();
        Assert.AreEqual(fallback, model.ModsRoot);
        Assert.AreEqual(0, (await Run(temp.Store, "config", "set-mods", next)).ExitCode);

        var scan = await RunWithDocuments(temp.Store, documents, "scan-duplicates", "--json", "-");
        Assert.AreEqual(0, scan.ExitCode);
        using var document = JsonDocument.Parse(scan.Output);
        Assert.AreEqual("settings", Read(document.RootElement, "sourceOrigin"));
        StringAssert.Contains(Read(document.RootElement, "notice")!, "请先确认");
        Assert.AreEqual(next, Read(document.RootElement.GetProperty("sources").EnumerateArray().Single(), "path"));
        var saved = File.ReadAllText(settingsPath);
        Assert.AreEqual(0, (await Run(temp.Store, "scan-scripts", "--source", tray, "--json", "-")).ExitCode);
        Assert.AreEqual(saved, File.ReadAllText(settingsPath));
    }

    [TestMethod]
    [DataRow("Sims4ModSieve")]
    [DataRow("Sim4ModSieve")]
    [DataRow("Sims4ModDoctor")]
    public async Task ChangingDefaultModsPreservesSavedHashConcurrency(string settingsDirectory)
    {
        using var temp = new SettingsViewModelTests.Scenario();
        var mods = temp.Folder("Mods");
        var settingsFile = Path.Combine(temp.Folder(settingsDirectory), "duplicate-settings.json");
        const string savedJson = """{"ModsRoot":null,"Sources":[],"HashConcurrency":4}""";
        File.WriteAllText(settingsFile, savedJson);

        var model = temp.Model();
        Assert.AreEqual(DuplicateHashConcurrency.Four, model.DuplicateHashConcurrency);
        Assert.AreEqual(0, (await Run(temp.Store, "config", "set-mods", mods)).ExitCode);

        var saved = temp.Store.Load()!;
        Assert.AreEqual(mods, saved.ModsRoot);
        Assert.AreEqual(DuplicateHashConcurrency.Four, saved.HashConcurrency);
        if (settingsDirectory != "Sims4ModSieve")
            Assert.AreEqual(savedJson, File.ReadAllText(settingsFile));

        model.DuplicateHashConcurrency = DuplicateHashConcurrency.Eight;
        Assert.AreEqual(mods, model.ModsRoot);
        Assert.AreEqual(mods, temp.Store.Load()!.ModsRoot);
        Assert.AreEqual(DuplicateHashConcurrency.Eight, temp.Store.Load()!.HashConcurrency);
    }

    [TestMethod]
    public async Task MissingAndInvalidInputsKeepStdoutCleanAndProtectExistingFiles()
    {
        using var temp = new TempDirectory();
        var settings = new DuplicateSettingsStore(temp.Path);
        var mods = temp.CreateDirectory("Mods");
        var documents = temp.CreateDirectory("Documents");
        var missing = await RunWithDocuments(settings, documents, "find-mods");
        Assert.AreEqual(1, missing.ExitCode);
        StringAssert.Contains(missing.Error, "未找到");
        var noSource = await RunWithDocuments(settings, documents, "scan-scripts", "--json", "-");
        Assert.AreEqual(2, noSource.ExitCode);
        Assert.AreEqual(string.Empty, noSource.Output);
        StringAssert.Contains(noSource.Error, "config set-mods");

        var damaged = temp.WriteFile(@"Sims4ModSieve\duplicate-settings.json", "{invalid");
        foreach (var args in new[] { new[] { "config", "show" }, new[] { "config", "set-mods", mods },
            new[] { "find-mods" }, new[] { "scan-conflicts", "--json", "-" } })
        {
            var result = await Run(settings, args);
            Assert.AreEqual(1, result.ExitCode);
            Assert.AreEqual(string.Empty, result.Output);
            StringAssert.Contains(result.Error, "设置格式无效");
            Assert.AreEqual("{invalid", File.ReadAllText(damaged));
        }

        var badArchive = temp.WriteFile(@"Mods\broken.ts4script", "not a zip");
        var incomplete = await Run(settings, "scan-scripts", "--source", mods, "--source", Path.Combine(temp.Path, "absent"), "--json", "-");
        Assert.AreEqual(1, incomplete.ExitCode);
        using var document = JsonDocument.Parse(incomplete.Output);
        var root = document.RootElement;
        Assert.AreEqual(1, root.GetProperty("sources").GetArrayLength());
        var issues = root.GetProperty("issues").EnumerateArray().ToArray();
        Assert.IsTrue(issues.Any(issue => Read(issue, "severity") == "incomplete"));
        Assert.IsTrue(issues.Any(issue => Read(issue, "severity") == "abnormal" && Read(issue, "path") == badArchive));
        Assert.AreEqual(2, (await Run(settings, "scan-scripts", "--source", mods, "--json", badArchive)).ExitCode);
        Assert.AreEqual("not a zip", File.ReadAllText(badArchive));
    }

    [TestMethod]
    public async Task CommandHelpAndGuideExposeTheReadOnlyWorkflow()
    {
        using var temp = new TempDirectory();
        var settings = new DuplicateSettingsStore(temp.Path);
        foreach (var args in new[] { new[] { "--help" }, new[] { "scan-conflicts", "--help" }, new[] { "config", "set-mods", "--help" } })
        {
            var help = await Run(settings, args);
            Assert.AreEqual(0, help.ExitCode);
            StringAssert.StartsWith(help.Output, "AI 助手请先运行 `guide`");
            StringAssert.Contains(help.Output, "退出码");
        }
        var guide = await Run(settings, "guide");
        Assert.AreEqual(0, guide.ExitCode);
        StringAssert.Contains(guide.Output, "同一次对话里同一批目录确认一次就够");
    }

    private static void WriteScript(string path, string content)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("lib/shared.py").Open()))
            writer.Write(content);
        using var other = new StreamWriter(zip.CreateEntry("lib/other.py").Open());
        other.Write("x = 0");
    }

    private static string CardKey(ConflictCard card)
    {
        var kind = card.Type switch
        {
            "override" => "resource_conflict",
            "collection" => "bundle_containment",
            "suspected" => "suspected_same_mod",
            "duplicates" => "duplicate",
            "script-duplicate" => "script_duplicate",
            _ => "script_collision",
        };
        var severity = card.Priority switch
        {
            ConflictPairPriority.NeedsReview => "abnormal",
            ConflictPairPriority.CanOrganize => "risk",
            _ => "incomplete",
        };
        var certainty = card.Type == "suspected" || severity == "incomplete" ? "possible" : "confirmed";
        return Key(kind, severity, certainty, card.Files.Select(file => file.Path));
    }

    private static string GroupKey(JsonElement group) => Key(Read(group, "kind")!, Read(group, "severity")!, Read(group, "certainty")!,
        group.GetProperty("files").EnumerateArray().Select(file => Read(file, "path")!));

    private static string Key(string kind, string severity, string certainty, IEnumerable<string> paths) =>
        $"{kind}/{severity}/{certainty}/{string.Join('|', paths.Order(StringComparer.OrdinalIgnoreCase))}";

    private static string? Read(JsonElement element, string key) => element.GetProperty(key).GetString();

    private static Task<(int ExitCode, string Output, string Error)> Run(DuplicateSettingsStore settings, params string[] args) =>
        RunWithDocuments(settings, null, args);

    private static async Task<(int ExitCode, string Output, string Error)> RunWithDocuments(DuplicateSettingsStore settings,
        string? documents, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliCommands.RunAsync(args, settings, output, error, documents);
        return (exit, output.ToString(), error.ToString());
    }
}
