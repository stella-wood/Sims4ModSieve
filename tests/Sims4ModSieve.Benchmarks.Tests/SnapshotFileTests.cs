using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Benchmarks;
using Sims4ModSieve.Core.Tests;

namespace Sims4ModSieve.Benchmarks.Tests;

[TestClass]
public sealed class SnapshotFileTests
{
    [TestMethod]
    public async Task LargeSnapshotComparisonCrossesBuffersAndFindsNestedDifferences()
    {
        using var temp = new TempDirectory();
        var left = Path.Combine(temp.Path, "left.json");
        var right = Path.Combine(temp.Path, "right.json");
        var longText = string.Concat(Enumerable.Repeat("中文\\\"", 20000));
        await ResultSnapshot.WriteAsync(left, new
        {
            Items = Enumerable.Range(0, 12000).Select(index => new { Index = index, Facts = new[] { true, false } }),
            Text = longText,
        }, default);
        await ResultSnapshot.WriteAsync(right, new
        {
            Text = longText,
            Items = Enumerable.Range(0, 12000).Select(index => new { Facts = new[] { true, false }, Index = index }),
        }, default);
        Assert.IsNull(ResultSnapshot.FirstDifference(left, right));
        var changed = JsonNode.Parse(await File.ReadAllTextAsync(right))!;
        changed["items"]![11000]!["facts"]![1] = true;
        await File.WriteAllTextAsync(right, changed.ToJsonString());
        Assert.AreEqual("$.items[11000].facts[1]: value differs", ResultSnapshot.FirstDifference(left, right));
    }

    [TestMethod]
    [DataRow("{\"a\":[1,2]}", "{\"a\":[2,1]}", "$.a[0]: value differs")]
    [DataRow("{\"a\":[1,2]}", "{\"a\":[2,1,3]}", "$.a: count 2 != 3")]
    [DataRow("{\"a\":[1,2,3]}", "{\"a\":[1]}", "$.a: count 3 != 1")]
    [DataRow("{\"a\":{\"b\":1}}", "{\"a\":{}}", "$.a.b: missing property")]
    [DataRow("{\"a\":1}", "{\"a\":\"1\"}", "$.a: value differs")]
    [DataRow("{\"a\":1.0}", "{\"a\":10e-1}", null)]
    [DataRow("{\"a\":-0}", "{\"a\":0.0}", null)]
    [DataRow("{\"a\":0.1234567890123456789012345678901}", "{\"a\":0.1234567890123456789012345678902}", "$.a: value differs")]
    [DataRow("{\"a\":1e10000000000000000000}", "{\"a\":10e9999999999999999999}", null)]
    public void SnapshotComparisonPreservesCountsTypesAndExactNumbers(string expected, string actual, string? difference)
    {
        using var temp = new TempDirectory();
        var left = temp.WriteFile("left.json", expected);
        var right = temp.WriteFile("right.json", actual);
        Assert.AreEqual(difference, ResultSnapshot.FirstDifference(left, right));
    }

    [TestMethod]
    [DataRow("{\"a\":[1,2")]
    [DataRow("{\"a\":1} {}")]
    [DataRow("[]")]
    [DataRow("{\"a\":1,\"a\":2}")]
    public void InvalidSnapshotFilesAreRejected(string json)
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("bad.json", json);
        Assert.Throws<JsonException>(() => ResultSnapshot.Validate(path));
    }

    [TestMethod]
    public async Task SnapshotWriteRemovesPartialFilesOnFailureAndCancellation()
    {
        using var temp = new TempDirectory();
        var failed = Path.Combine(temp.Path, "failed.json");
        IEnumerable<int> Fail()
        {
            yield return 1;
            Assert.IsTrue(File.Exists(failed + ".partial"));
            throw new IOException("injected serialization failure");
        }
        await Assert.ThrowsAsync<IOException>(() => ResultSnapshot.WriteAsync(failed, new { Values = Fail() }, default));
        Assert.IsFalse(File.Exists(failed));
        Assert.IsFalse(File.Exists(failed + ".partial"));

        using var cancellation = new CancellationTokenSource();
        var cancelled = Path.Combine(temp.Path, "cancelled.json");
        IEnumerable<int> Cancel()
        {
            yield return 1;
            cancellation.Cancel();
            yield return 2;
        }
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ResultSnapshot.WriteAsync(cancelled, new { Values = Cancel() }, cancellation.Token));
        Assert.IsFalse(File.Exists(cancelled));
        Assert.IsFalse(File.Exists(cancelled + ".partial"));
    }
}
