using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Cli;
using Sims4ModSieve.Core.Settings;
using Sims4ModSieve.Core.Tests;
using Sims4ModSieve.Core.Tests.Packages;

namespace Sims4ModSieve.Packages.Tests;

[TestClass]
public sealed class CliConflictGroupingSizeTests
{
    [TestMethod]
    [DataRow(3, "duplicate-copies", 33L, 6L)]
    [DataRow(4, "too-many-shared-resources", 60L, 12L)]
    public async Task OversizeScanReturnsStructuredIssueFailureCodeAndChineseStderrWithoutPartialGroups(
        int originals, string reason, long expectedMatches, long expectedCollapsedMatches)
    {
        using var temp = new TempDirectory();
        var mods = WriteFanout(temp, originals);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CliCommands.RunAsync(["scan-conflicts", "--source", mods, "--json", "-"],
            new DuplicateSettingsStore(temp.CreateDirectory("settings")), output, error, conflictResourceMatchLimit: 10);
        Assert.AreEqual(1, exit);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.IsFalse(root.GetProperty("complete").GetBoolean());
        Assert.AreEqual(0, root.GetProperty("groupCount").GetInt32());
        Assert.AreEqual(0, root.GetProperty("groups").GetArrayLength());
        Assert.AreEqual(1, root.GetProperty("issueCount").GetInt32());
        var issue = root.GetProperty("issues").EnumerateArray().Single();
        Assert.AreEqual("conflict-grouping-too-large", issue.GetProperty("code").GetString());
        Assert.AreEqual(reason, issue.GetProperty("reason").GetString());
        Assert.AreEqual(expectedMatches, issue.GetProperty("resourceMatchCount").GetInt64());
        Assert.AreEqual(expectedCollapsedMatches, issue.GetProperty("collapsedResourceMatchCount").GetInt64());
        Assert.AreEqual(10L, issue.GetProperty("resourceMatchLimit").GetInt64());
        Assert.AreEqual("incomplete", issue.GetProperty("severity").GetString());
        var message = reason == "duplicate-copies"
            ? "⚠️文件夹内存在大量相同副本（3份），建议优先进行重复检测"
            : "⚠️所选文件冲突资源数过多，无法完整处理，请分批检测";
        Assert.AreEqual(message, issue.GetProperty("message").GetString());
        StringAssert.Contains(error.ToString(), message);
        StringAssert.Contains(error.ToString(), $"资源匹配估算={expectedMatches}；合并副本后估算={expectedCollapsedMatches}；上限=10");
        if (reason == "duplicate-copies") Assert.AreEqual(3, issue.GetProperty("redundantCopyCount").GetInt32());
        else Assert.IsFalse(issue.TryGetProperty("redundantCopyCount", out _));
    }

    [TestMethod]
    [DataRow("--summary-only", "", false)]
    [DataRow("--severity", "abnormal", false)]
    [DataRow("--severity", "incomplete", true)]
    [DataRow("--kind", "read_incomplete", true)]
    [DataRow("--kind", "resource_conflict", false)]
    [DataRow("--limit", "1", true)]
    [DataRow("--omit-resource-keys", "", true)]
    [DataRow("--hide-ignored", "", true)]
    public async Task OutputFiltersPreserveFailureCountsAndIncompleteStatus(string flag, string value, bool visibleIssue)
    {
        using var temp = new TempDirectory();
        var mods = WriteFanout(temp, 3);
        var args = new List<string> { "scan-conflicts", "--source", mods, "--json", "-", flag };
        if (value.Length > 0) args.Add(value);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CliCommands.RunAsync(args.ToArray(), new DuplicateSettingsStore(temp.CreateDirectory("settings")),
            output, error, conflictResourceMatchLimit: 10);
        Assert.AreEqual(1, exit);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.IsFalse(root.GetProperty("complete").GetBoolean());
        Assert.AreEqual(1, root.GetProperty("incompleteCount").GetInt32());
        Assert.AreEqual(1, root.GetProperty("issueCount").GetInt32());
        Assert.AreEqual(visibleIssue ? 1 : 0, root.GetProperty("issues").GetArrayLength());
        Assert.AreEqual(visibleIssue ? 0 : 1, root.GetProperty("omittedIssueCount").GetInt32());
        Assert.AreEqual(0, root.GetProperty("groups").GetArrayLength());
        StringAssert.Contains(error.ToString(), "建议优先进行重复检测");
    }

    [TestMethod]
    public async Task ExactLimitCompletesAndStillWritesEstimatesToStderr()
    {
        using var temp = new TempDirectory();
        var mods = WriteFanout(temp, 3);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CliCommands.RunAsync(["scan-conflicts", "--source", mods, "--json", "-"],
            new DuplicateSettingsStore(temp.CreateDirectory("settings")), output, error, conflictResourceMatchLimit: 33);
        Assert.AreEqual(1, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.IsTrue(json.RootElement.GetProperty("complete").GetBoolean());
        Assert.AreEqual(0, json.RootElement.GetProperty("issues").GetArrayLength());
        Assert.IsTrue(json.RootElement.GetProperty("groups").GetArrayLength() > 0);
        StringAssert.Contains(error.ToString(), "资源匹配估算=33；合并副本后估算=6；上限=33");
    }

    private static string WriteFanout(TempDirectory temp, int originals)
    {
        var mods = temp.CreateDirectory("Mods");
        for (var index = 0; index < originals; index++)
        {
            var bytes = new DbpfFixtureBuilder().AddContent([1], instanceLo: 1).AddContent([2], instanceLo: 2)
                .AddContent([3], instanceLo: (uint)(100 + index)).Build();
            for (var copy = 0; copy < 2; copy++) temp.WriteBytes(Path.Combine("Mods", $"{index}-{copy}.package"), bytes);
        }
        return mods;
    }
}
