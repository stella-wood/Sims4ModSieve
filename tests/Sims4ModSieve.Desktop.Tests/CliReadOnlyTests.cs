using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Caching;
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
    public async Task DuplicateCommandBypassesCacheOnRequestAndFallsBackForBadDatabase()
    {
        using var temp = new TempDirectory();
        var first = temp.WriteFile("Mods/a.package", "same");
        temp.WriteFile("Mods/b.package", "same");
        var settings = new DuplicateSettingsStore(temp.Path);
        var args = new[] { "scan-duplicates", "--source", Path.GetDirectoryName(first)!, "--json", "-" };
        var database = Path.Combine(settings.DataDirectory, "cache", "sha256.sqlite3");
        Assert.AreEqual(1, (await Run(settings, [.. args, "--no-cache"])).ExitCode);
        Assert.IsFalse(File.Exists(database));

        Assert.AreEqual(1, (await Run(settings, args)).ExitCode);
        var fileSystem = new PhysicalFileSystemAccess();
        var stamp = fileSystem.GetFileStamp(first);
        var cache = new SqliteSha256Cache(database);
        Assert.IsNotNull(cache.GetSha256(first, stamp));
        File.WriteAllText(first, "diff");
        File.SetLastWriteTimeUtc(first, stamp.LastWriteTimeUtc);
        Assert.AreEqual(stamp, fileSystem.GetFileStamp(first));
        Assert.AreEqual(1, (await Run(settings, args)).ExitCode);
        var uncached = await Run(settings, [.. args, "--no-cache"]);
        Assert.AreEqual(0, uncached.ExitCode);

        File.WriteAllText(database, "corrupt");
        var fallback = await Run(settings, args);
        Assert.AreEqual(uncached.ExitCode, fallback.ExitCode);
        Assert.AreEqual(uncached.Error, fallback.Error);
        var expected = JsonNode.Parse(uncached.Output)!.AsObject();
        var actual = JsonNode.Parse(fallback.Output)!.AsObject();
        foreach (var key in new[] { "startedAtUtc", "completedAtUtc" })
        {
            expected.Remove(key);
            actual.Remove(key);
        }
        Assert.IsTrue(JsonNode.DeepEquals(expected, actual));
        Assert.AreEqual("corrupt", File.ReadAllText(database));
    }

    [TestMethod]
    public async Task DuplicateCommandCommitsSuccessfulHashesDespiteSourceIssue()
    {
        using var temp = new TempDirectory();
        var first = temp.WriteFile("Mods/a.package", "same");
        temp.WriteFile("Mods/b.package", "same");
        var settings = new DuplicateSettingsStore(temp.Path);

        var scan = await Run(settings, "scan-duplicates", "--source", Path.GetDirectoryName(first)!,
            "--source", Path.Combine(temp.Path, "missing"), "--json", "-");

        using var json = JsonDocument.Parse(scan.Output);
        Assert.AreEqual(1, scan.ExitCode);
        Assert.AreEqual(1, json.RootElement.GetProperty("groupCount").GetInt32());
        Assert.IsTrue(json.RootElement.GetProperty("issueCount").GetInt32() > 0);
        var cache = new SqliteSha256Cache(Path.Combine(settings.DataDirectory, "cache", "sha256.sqlite3"));
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first))),
            cache.GetSha256(first, new PhysicalFileSystemAccess().GetFileStamp(first)));
    }

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
            Assert.AreEqual(!hide, root.GetProperty("complete").GetBoolean());
            Assert.AreEqual(0, root.GetProperty("omittedGroupCount").GetInt32());
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

    [TestMethod]
    [DataRow("scan-duplicates", "duplicate", "risk")]
    [DataRow("scan-conflicts", "resource_conflict", "abnormal")]
    [DataRow("scan-scripts", "script_collision", "abnormal")]
    public async Task SeverityAndKindFiltersSelectMatchingDetails(string command, string kind, string severity)
    {
        using var temp = new TempDirectory();
        var source = CreateFilterFixture(temp);
        var settings = new DuplicateSettingsStore(temp.Path);
        var baseline = await Run(settings, command, "--source", source, "--json", "-");
        using var full = JsonDocument.Parse(baseline.Output);
        Assert.IsTrue(full.RootElement.GetProperty("complete").GetBoolean());
        Assert.AreEqual(0, full.RootElement.GetProperty("omittedGroupCount").GetInt32());
        var fullGroups = full.RootElement.GetProperty("groups").EnumerateArray().ToArray();

        var scan = await Run(settings, command, "--source", source, "--json", "-",
            "--severity", severity, "--kind", kind);
        using var filtered = JsonDocument.Parse(scan.Output);
        var root = filtered.RootElement;
        var expected = fullGroups.Where(group => Read(group, "severity") == severity && Read(group, "kind") == kind).ToArray();
        Assert.IsNotEmpty(expected);
        Assert.IsFalse(root.GetProperty("complete").GetBoolean());
        CollectionAssert.AreEqual(expected.Select(GroupKey).ToArray(), root.GetProperty("groups").EnumerateArray().Select(GroupKey).ToArray());
        Assert.AreEqual(fullGroups.Length - expected.Length, root.GetProperty("omittedGroupCount").GetInt32());
        AssertFullSummaryUnchanged(full.RootElement, root);
        Assert.AreEqual(baseline.ExitCode, scan.ExitCode);
        CollectionAssert.AreEqual(new[] { severity }, root.GetProperty("filters").GetProperty("severity").EnumerateArray().Select(item => item.GetString()).ToArray());
        CollectionAssert.AreEqual(new[] { kind }, root.GetProperty("filters").GetProperty("kind").EnumerateArray().Select(item => item.GetString()).ToArray());

        var repeat = await Run(settings, command, "--source", source, "--json", "-",
            "--severity", "abnormal", "--severity", "risk", "--severity", "risk",
            "--kind", kind, "--kind", "read_incomplete", "--limit", "1");
        using var repeated = JsonDocument.Parse(repeat.Output);
        CollectionAssert.AreEqual(new[] { "abnormal", "risk" }, repeated.RootElement.GetProperty("filters").GetProperty("severity")
            .EnumerateArray().Select(item => item.GetString()).ToArray());
        CollectionAssert.AreEqual(fullGroups.Where(group => Read(group, "kind") == kind && Read(group, "severity") is "abnormal" or "risk")
            .Take(1).Select(GroupKey).ToArray(), repeated.RootElement.GetProperty("groups").EnumerateArray().Select(GroupKey).ToArray());
        Assert.AreEqual(fullGroups.Length - 1, repeated.RootElement.GetProperty("omittedGroupCount").GetInt32());
        Assert.AreEqual(1, repeated.RootElement.GetProperty("filters").GetProperty("limit").GetInt32());
        AssertFullSummaryUnchanged(full.RootElement, repeated.RootElement);
    }

    [TestMethod]
    [DataRow("scan-duplicates", "risk", "-ＣＯＰＹ.package", 2)]
    [DataRow("scan-duplicates", "risk", "", 2)]
    [DataRow("scan-conflicts", "risk", "-ＣＯＰＹ.package", 2)]
    [DataRow("scan-conflicts", "abnormal", "-ＣＯＰＹ.package", 1)]
    [DataRow("scan-conflicts", "abnormal", "Ａ.ts4script", 1)]
    [DataRow("scan-conflicts", "risk", "missing", 0)]
    public async Task PathFilterKeepsWholeGroupsAndComposesWithSeverityAndLimit(
        string command, string severity, string query, int matchCount)
    {
        using var temp = new TempDirectory();
        var source = CreateFilterFixture(temp);
        temp.WriteBytes(@"Mods\broken.ts4script", [1, 2, 3]);
        var settings = new DuplicateSettingsStore(temp.Path);
        var args = new[] { command, "--source", source, "--json", "-", "--severity", severity };
        if (query.Length == 0) args = [.. args, "--extension", ".package"];
        var baseline = await Run(settings, args);
        using var full = JsonDocument.Parse(baseline.Output);
        var root = full.RootElement;
        var normalizedQuery = query switch
        {
            "-ＣＯＰＹ.package" => "-copy.package",
            "Ａ.ts4script" => "a.ts4script",
            _ => query,
        };
        var expected = root.GetProperty("groups").EnumerateArray().Where(group => group.GetProperty("files")
            .EnumerateArray().Any(file => Read(file, "path")!.Contains(normalizedQuery, StringComparison.Ordinal))).ToArray();
        Assert.AreEqual(matchCount, expected.Length);

        foreach (var limit in new[] { 1, 20 })
        {
            var scan = await Run(settings, [.. args, "--path-contains", query,
                "--limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            using var document = JsonDocument.Parse(scan.Output);
            var filtered = document.RootElement;
            Assert.AreEqual(baseline.ExitCode, scan.ExitCode);
            AssertFullSummaryUnchanged(root, filtered);
            CollectionAssert.AreEqual(expected.Take(limit).Select(GroupKey).ToArray(),
                filtered.GetProperty("groups").EnumerateArray().Select(GroupKey).ToArray());
            Assert.AreEqual(Math.Min(expected.Length, limit), filtered.GetProperty("visibleGroupCount").GetInt32());
            Assert.AreEqual(root.GetProperty("groupCount").GetInt32() - Math.Min(expected.Length, limit),
                filtered.GetProperty("omittedGroupCount").GetInt32());
            Assert.AreEqual(root.GetProperty("issues").GetRawText(), filtered.GetProperty("issues").GetRawText());
            Assert.AreEqual(query, Read(filtered.GetProperty("filters"), "pathContains"));
            Assert.IsFalse(filtered.GetProperty("complete").GetBoolean());
        }
    }

    [TestMethod]
    public async Task PathKeywordsRankBeforeLimitAndPreserveNonmatchingMembers()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        temp.WriteBytes(@"Mods\a-alpha.package", [1]);
        temp.WriteBytes(@"Mods\a-peer.package", [1]);
        temp.WriteBytes(@"Mods\b-alpha.package", [2, 2]);
        temp.WriteBytes(@"Mods\b-beta.package", [2, 2]);
        temp.WriteBytes(@"Mods\c-alpha-beta.package", [3, 3, 3]);
        temp.WriteBytes(@"Mods\c-peer.package", [3, 3, 3]);
        var scan = await Run(new DuplicateSettingsStore(temp.Path), "scan-duplicates", "--source", source, "--json", "-",
            "--path-contains", "alpha　ＢＥＴＡ", "--severity", "risk", "--limit", "2");
        using var document = JsonDocument.Parse(scan.Output);
        var root = document.RootElement;
        Assert.AreEqual(1, scan.ExitCode);
        Assert.AreEqual(3, root.GetProperty("groupCount").GetInt32());
        Assert.AreEqual(2, root.GetProperty("visibleGroupCount").GetInt32());
        var groups = root.GetProperty("groups").EnumerateArray().ToArray();
        CollectionAssert.AreEquivalent(new[] { "c-alpha-beta.package", "c-peer.package" }, Names(groups[0]));
        CollectionAssert.AreEquivalent(new[] { "b-alpha.package", "b-beta.package" }, Names(groups[1]));

        static string[] Names(JsonElement group) => group.GetProperty("files").EnumerateArray()
            .Select(file => Path.GetFileName(Read(file, "path"))!).ToArray();
    }

    [TestMethod]
    [DataRow("scan-duplicates")]
    [DataRow("scan-conflicts")]
    [DataRow("scan-scripts")]
    public async Task LimitsAndSummaryOnlyReportOmissionsWithoutChangingFindings(string command)
    {
        using var temp = new TempDirectory();
        var source = CreateFilterFixture(temp);
        var settings = new DuplicateSettingsStore(temp.Path);
        var baseline = await Run(settings, command, "--source", source, "--json", "-");
        using var full = JsonDocument.Parse(baseline.Output);
        var total = full.RootElement.GetProperty("groupCount").GetInt32();
        Assert.IsGreaterThan(1, total);
        foreach (var options in new[] { new[] { "--limit", "1" }, new[] { "--limit", "0" },
            new[] { "--summary-only" }, new[] { "--severity", "no_effect" }, new[] { "--limit", "1000" } })
        {
            var scan = await Run(settings, [command, "--source", source, "--json", "-", .. options]);
            using var filtered = JsonDocument.Parse(scan.Output);
            var root = filtered.RootElement;
            var listed = options is ["--limit", "1"] ? 1 : options is ["--limit", "1000"] ? total : 0;
            Assert.AreEqual(1, scan.ExitCode);
            Assert.IsFalse(root.GetProperty("complete").GetBoolean());
            Assert.AreEqual(listed, root.GetProperty("groups").GetArrayLength());
            Assert.AreEqual(listed, root.GetProperty("visibleGroupCount").GetInt32());
            Assert.AreEqual(total - listed, root.GetProperty("omittedGroupCount").GetInt32());
            StringAssert.Contains(scan.Error, $"还有 {total - listed} 组未列出");
            AssertFullSummaryUnchanged(full.RootElement, root);
            if (options is ["--summary-only"])
            {
                Assert.IsTrue(root.GetProperty("filters").GetProperty("summaryOnly").GetBoolean());
                Assert.AreEqual(0, root.GetProperty("issues").GetArrayLength());
                Assert.AreEqual(root.GetProperty("issueCount").GetInt32(), root.GetProperty("omittedIssueCount").GetInt32());
            }
        }
    }

    [TestMethod]
    [DataRow("scan-duplicates")]
    [DataRow("scan-conflicts")]
    [DataRow("scan-scripts")]
    public async Task FiltersOnEmptyResultsKeepSuccessfulExitCode(string command)
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        var scan = await Run(new DuplicateSettingsStore(temp.Path), command, "--source", source,
            "--json", "-", "--severity", "abnormal", "--limit", "0", "--summary-only");
        using var document = JsonDocument.Parse(scan.Output);
        var root = document.RootElement;
        Assert.AreEqual(0, scan.ExitCode);
        Assert.IsFalse(root.GetProperty("complete").GetBoolean());
        Assert.AreEqual(0, root.GetProperty("groupCount").GetInt32());
        Assert.AreEqual(0, root.GetProperty("omittedGroupCount").GetInt32());
        Assert.AreEqual(0, root.GetProperty("issueCount").GetInt32());
        Assert.AreEqual(0, root.GetProperty("omittedIssueCount").GetInt32());
    }

    [TestMethod]
    public async Task ConflictJsonAddsTypeNamesAlongsideOriginalResourceKeys()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        DbpfFixtureBuilder Package(byte value) => new DbpfFixtureBuilder()
            .AddContent([value], type: 0x8B18FF6E, instanceLo: 1)
            .AddContent([value], type: 0x8B18FF6E, instanceLo: 2)
            .AddContent([value], type: 0x1CC04273, instanceLo: 3);
        temp.WriteBytes("Mods/a.package", Package(1).Build());
        temp.WriteBytes("Mods/b.package", Package(2).Build());

        var scan = await Run(new DuplicateSettingsStore(temp.Path), "scan-conflicts", "--source", source, "--json", "-");
        Assert.AreEqual(1, scan.ExitCode);
        using var document = JsonDocument.Parse(scan.Output);
        var group = document.RootElement.GetProperty("groups").EnumerateArray().Single();
        var keys = group.GetProperty("resourceKeys").EnumerateArray().Select(key => key.GetString()).ToArray();
        CollectionAssert.AreEquivalent(new[] { "8B18FF6E:00000000:0000000000000001",
            "8B18FF6E:00000000:0000000000000002", "1CC04273:00000000:0000000000000003" }, keys);
        Assert.AreEqual(3, group.GetProperty("resourceKeyCount").GetInt32());
        Assert.AreEqual(2, group.GetProperty("files").GetArrayLength());
        Assert.AreEqual(3, group.GetProperty("relations")[0].GetProperty("differentResourceCount").GetInt32());
        var types = group.GetProperty("resourceTypes").EnumerateArray()
            .ToDictionary(type => type.GetProperty("type").GetString()!, type => type.GetProperty("typeName").GetString());
        Assert.AreEqual(2, types.Count);
        Assert.AreEqual("WorldMap（0x1CC04273）", types["0x1CC04273"]);
        Assert.AreEqual("捏人滑杆热点（SimHotspotControl）", types["0x8B18FF6E"]);
    }

    [TestMethod]
    public async Task ResourceKeyOmissionKeepsCountsAndOtherGroupDetails()
    {
        using var temp = new TempDirectory();
        var source = CreateFilterFixture(temp);
        var settings = new DuplicateSettingsStore(temp.Path);
        var baseline = await Run(settings, "scan-conflicts", "--source", source, "--json", "-");
        var scan = await Run(settings, "scan-conflicts", "--source", source, "--json", "-", "--omit-resource-keys");
        using var full = JsonDocument.Parse(baseline.Output);
        using var filtered = JsonDocument.Parse(scan.Output);
        var root = filtered.RootElement;
        Assert.AreEqual(baseline.ExitCode, scan.ExitCode);
        AssertFullSummaryUnchanged(full.RootElement, root);
        Assert.IsFalse(root.GetProperty("complete").GetBoolean());
        Assert.IsTrue(root.GetProperty("filters").GetProperty("omitResourceKeys").GetBoolean());
        Assert.AreEqual(0, root.GetProperty("omittedGroupCount").GetInt32());
        StringAssert.Contains(scan.Error, "未展开 resourceKeys");
        var groups = root.GetProperty("groups").EnumerateArray().ToArray();
        Assert.IsTrue(full.RootElement.GetProperty("groups").EnumerateArray().Any(group => group.TryGetProperty("resourceKeys", out _)));
        foreach (var original in full.RootElement.GetProperty("groups").EnumerateArray())
        {
            var actual = groups.Single(group => GroupKey(group) == GroupKey(original));
            Assert.IsFalse(actual.TryGetProperty("resourceKeys", out _));
            foreach (var property in original.EnumerateObject().Where(property => property.Name != "resourceKeys"))
                Assert.AreEqual(property.Value.GetRawText(), actual.GetProperty(property.Name).GetRawText());
            if (original.TryGetProperty("resourceKeys", out var keys))
                Assert.AreEqual(keys.GetArrayLength(), actual.GetProperty("resourceKeyCount").GetInt32());
        }
    }

    [TestMethod]
    public async Task IssueFiltersAndSummaryOnlyPreserveReadProblemTotalsAndExitCode()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        temp.WriteFile(@"Mods\broken.ts4script", "not a zip");
        var absent = Path.Combine(temp.Path, "absent");
        var settings = new DuplicateSettingsStore(temp.Path);
        var baseline = await Run(settings, "scan-scripts", "--source", source, "--source", absent, "--json", "-");
        using var full = JsonDocument.Parse(baseline.Output);
        foreach (var options in new[] { new[] { "--kind", "damaged_file" }, new[] { "--severity", "incomplete" },
            new[] { "--kind", "damaged_file", "--kind", "read_incomplete" }, new[] { "--limit", "0" },
            new[] { "--summary-only" }, new[] { "--severity", "no_effect" } })
        {
            var scan = await Run(settings, ["scan-scripts", "--source", source, "--source", absent, "--json", "-", .. options]);
            using var filtered = JsonDocument.Parse(scan.Output);
            var root = filtered.RootElement;
            var expected = full.RootElement.GetProperty("issues").EnumerateArray().Where(issue => options switch
            {
                ["--kind", var kind] => Read(issue, "kind") == kind,
                ["--severity", var severity] => Read(issue, "severity") == severity,
                ["--summary-only"] => false,
                _ => true,
            }).ToArray();
            Assert.AreEqual(1, scan.ExitCode);
            AssertFullSummaryUnchanged(full.RootElement, root);
            CollectionAssert.AreEqual(expected.Select(issue => issue.GetRawText()).ToArray(),
                root.GetProperty("issues").EnumerateArray().Select(issue => issue.GetRawText()).ToArray());
            Assert.AreEqual(full.RootElement.GetProperty("issueCount").GetInt32() - expected.Length,
                root.GetProperty("omittedIssueCount").GetInt32());
        }
    }

    [TestMethod]
    public async Task HiddenIgnoredGroupsAlsoMarkOutputIncompleteAndKeepFullCounts()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        var first = temp.WriteFile(@"Mods\first.package", "same");
        var second = temp.WriteFile(@"Mods\second.package", "same");
        var settings = new DuplicateSettingsStore(temp.Path);
        new FileManagementStore(settings.DataDirectory).Update(data => data with
        {
            Ignored = [new(first, 4, DateTime.UtcNow), new(second, 4, DateTime.UtcNow)],
        });
        var scan = await Run(settings, "scan-duplicates", "--source", source, "--json", "-", "--hide-ignored");
        using var document = JsonDocument.Parse(scan.Output);
        var root = document.RootElement;
        Assert.AreEqual(1, scan.ExitCode);
        Assert.IsFalse(root.GetProperty("complete").GetBoolean());
        Assert.IsTrue(root.GetProperty("filters").GetProperty("hideIgnored").GetBoolean());
        Assert.AreEqual(1, root.GetProperty("groupCount").GetInt32());
        Assert.AreEqual(0, root.GetProperty("groups").GetArrayLength());
        Assert.AreEqual(1, root.GetProperty("omittedGroupCount").GetInt32());
        Assert.AreEqual(1, root.GetProperty("severityCounts").GetProperty("risk").GetInt32());
        StringAssert.Contains(scan.Error, "还有 1 组未列出");
    }

    [TestMethod]
    [DataRow("scan-duplicates", "--omit-resource-keys", null)]
    [DataRow("scan-scripts", "--omit-resource-keys", null)]
    [DataRow("scan-scripts", "--path-contains", "MouthScale")]
    [DataRow("scan-duplicates", "--path-contains", null)]
    [DataRow("scan-duplicates", "--kind", "resource_conflict")]
    [DataRow("scan-duplicates", "--kind", "script_collision")]
    [DataRow("scan-duplicates", "--kind", "damaged_file")]
    [DataRow("scan-scripts", "--kind", "duplicate")]
    [DataRow("scan-scripts", "--kind", "bundle_containment")]
    [DataRow("scan-conflicts", "--kind", "unknown")]
    [DataRow("scan-conflicts", "--severity", "unknown")]
    [DataRow("scan-conflicts", "--severity", null)]
    [DataRow("scan-conflicts", "--kind", null)]
    [DataRow("scan-conflicts", "--limit", null)]
    [DataRow("scan-conflicts", "--limit", "-1")]
    [DataRow("scan-conflicts", "--limit", "1.5")]
    [DataRow("scan-conflicts", "--limit", "2147483648")]
    public async Task InvalidOrInapplicableOutputOptionsReturnParameterError(string command, string option, string? value)
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        var settings = new DuplicateSettingsStore(temp.Path);
        var args = new[] { command, "--source", source, "--json", "-", option };
        var scan = await Run(settings, value is null ? args : [.. args, value]);
        Assert.AreEqual(2, scan.ExitCode);
        Assert.AreEqual(string.Empty, scan.Output);
        StringAssert.Contains(scan.Error, option);
        Assert.IsFalse(scan.Error.Contains("正在扫描", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("--severity", "risk")]
    [DataRow("--kind", "duplicate")]
    [DataRow("--limit", "1")]
    [DataRow("--path-contains", "MouthScale")]
    [DataRow("--summary-only", null)]
    public async Task HtmlAndOutputFiltersAreRejectedBeforeWritingReports(string option, string? value)
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("Mods");
        var json = Path.Combine(temp.Path, "report.json");
        var html = Path.Combine(temp.Path, "report.html");
        var args = new[] { "scan-duplicates", "--source", source, "--json", json, "--html", html, option };
        var scan = await Run(new DuplicateSettingsStore(temp.Path), value is null ? args : [.. args, value]);
        Assert.AreEqual(2, scan.ExitCode);
        StringAssert.Contains(scan.Error, "--html 不能");
        Assert.IsFalse(File.Exists(json));
        Assert.IsFalse(File.Exists(html));
    }

    [TestMethod]
    public async Task FilteredJsonFileUsesStderrSummaryAndDoesNotGenerateHtml()
    {
        using var temp = new TempDirectory();
        var source = CreateFilterFixture(temp);
        var path = Path.Combine(temp.Path, "report.json");
        var scan = await Run(new DuplicateSettingsStore(temp.Path), "scan-duplicates", "--source", source,
            "--json", path, "--limit", "1");
        Assert.AreEqual(1, scan.ExitCode);
        Assert.AreEqual(string.Empty, scan.Output);
        StringAssert.Contains(scan.Error, $"JSON：{path}");
        Assert.IsFalse(scan.Error.Contains("HTML：", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.IsFalse(document.RootElement.GetProperty("complete").GetBoolean());
        Assert.AreEqual(1, document.RootElement.GetProperty("groups").GetArrayLength());
    }

    private static string CreateFilterFixture(TempDirectory temp)
    {
        var source = temp.CreateDirectory("Mods");
        var first = new DbpfFixtureBuilder().AddContent([1, 2], instanceLo: 1).AddContent([3], instanceLo: 2).Build();
        var second = new DbpfFixtureBuilder().AddContent([3, 4], instanceLo: 1)
            .AddContent([5, 6], instanceLo: 3).AddContent([7], instanceLo: 4).Build();
        temp.WriteBytes(@"Mods\a.package", first);
        temp.WriteBytes(@"Mods\a-copy.package", first);
        temp.WriteBytes(@"Mods\b.package", second);
        temp.WriteBytes(@"Mods\b-copy.package", second);
        WriteScript(Path.Combine(source, "a.ts4script"), "x = 1");
        WriteScript(Path.Combine(source, "b.ts4script"), "x = 2");
        WriteScript(Path.Combine(source, "c.ts4script"), "x = 0", "other");
        WriteScript(Path.Combine(source, "d.ts4script"), "x = 0", "other");
        return source;
    }

    private static void AssertFullSummaryUnchanged(JsonElement full, JsonElement filtered)
    {
        foreach (var field in new[] { "discoveredFileCount", "hashedFileCount", "ignoredCount", "incompleteCount",
            "abnormalCount", "groupCount", "severityCounts", "issueCount", "duplicateFileCount", "reclaimableBytes" })
            Assert.AreEqual(full.GetProperty(field).GetRawText(), filtered.GetProperty(field).GetRawText(), field);
    }

    private static void WriteScript(string path, string content, string topLevel = "lib")
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry($"{topLevel}/shared.py").Open()))
            writer.Write(content);
        using var other = new StreamWriter(zip.CreateEntry($"{topLevel}/other.py").Open());
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
