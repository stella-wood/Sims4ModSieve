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
            new DuplicateSettingsStore(temp.CreateDirectory("settings")), output, error, conflictGroupingMemoryBudgetBytes: 4_000);
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
        Assert.AreEqual(0L, issue.GetProperty("resourceMatchLimit").GetInt64());
        CollectionAssert.AreEquivalent(new[] { "path", "code", "message", "stage", "entry", "kind", "severity",
                "certainty", "file", "ignored", "reason", "resourceMatchCount", "collapsedResourceMatchCount", "resourceMatchLimit" }
            .Concat(reason == "duplicate-copies" ? new[] { "redundantCopyCount" } : []).ToArray(),
            issue.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual("incomplete", issue.GetProperty("severity").GetString());
        var message = reason == "duplicate-copies"
            ? "⚠️文件夹内存在大量相同副本（3份），建议优先进行重复检测"
            : "⚠️所选文件冲突资源数过多，无法完整处理，请分批检测";
        Assert.AreEqual(message, issue.GetProperty("message").GetString());
        StringAssert.Contains(error.ToString(), message);
        StringAssert.Contains(error.ToString(), $"资源匹配估算={expectedMatches}；合并副本后估算={expectedCollapsedMatches}；上限=0");
        StringAssert.Contains(error.ToString(), "可用内存字节=");
        StringAssert.Contains(error.ToString(), "份额=1/4；系数字节每匹配=124；上限来源=注入");
        StringAssert.Contains(error.ToString(), $"文件对上界={originals * 2 * (originals * 2 - 1) / 2}；合并副本后文件对上界={originals * (originals - 1) / 2}");
        StringAssert.Contains(error.ToString(), "系数字节每文件对=492；安全余量=1.4；估算内存字节=");
        StringAssert.Contains(error.ToString(), "内存预算字节=4000");
        Assert.IsFalse(error.ToString().Contains("结束托管内存字节=", StringComparison.Ordinal));
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
            output, error, conflictGroupingMemoryBudgetBytes: 4_000);
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
            new DuplicateSettingsStore(temp.CreateDirectory("settings")), output, error, conflictGroupingMemoryBudgetBytes: 16_061);
        Assert.AreEqual(1, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.IsTrue(json.RootElement.GetProperty("complete").GetBoolean());
        Assert.AreEqual(0, json.RootElement.GetProperty("issues").GetArrayLength());
        Assert.IsTrue(json.RootElement.GetProperty("groups").GetArrayLength() > 0);
        StringAssert.Contains(error.ToString(), "资源匹配估算=33；合并副本后估算=6；上限=33");
        StringAssert.Contains(error.ToString(), "冲突分组内存：阶段=合并副本后；结束托管内存字节=");
        StringAssert.Contains(error.ToString(), "强制GC=false");
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
