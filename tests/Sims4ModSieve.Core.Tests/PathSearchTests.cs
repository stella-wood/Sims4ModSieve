using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Reporting;

namespace Sims4ModSieve.Core.Tests;

[TestClass]
public sealed class PathSearchTests
{
    [TestMethod]
    [DataRow(@"Mods\Creator\Slider.package", @"cReAtOr\sLi")]
    [DataRow("ＡＢＣ　１２！.package", "abc 12!")]
    [DataRow("abc 12!.package", "ＡＢＣ　１２！")]
    [DataRow("Cafe\u0301.package", "CAFÉ")]
    [DataRow("Café.package", "cafe\u0301")]
    [DataRow("a.package", "a")]
    [DataRow("abc.package", "")]
    public void MatchesPathSubstrings(string path, string query) => Assert.IsTrue(PathSearch.Matches(path, query));

    [TestMethod]
    [DataRow("a.package", "aa")]
    [DataRow("ab.package", "a b")]
    [DataRow("a b.package", "a  b")]
    [DataRow("①.package", "1")]
    public void RejectsAbsentSubstrings(string path, string query) => Assert.IsFalse(PathSearch.Matches(path, query));

    [TestMethod]
    public void RanksSamePathBeforeSplitGroupAndPartialMatches()
    {
        var search = new PathSearch("ＡＬＰＨＡ　beta alpha");
        Assert.AreEqual(2, search.Rank([@"C:\Alpha\Beta.package", "peer.package"]));
        Assert.AreEqual(1, search.Rank(["alpha.package", "BETA.package"]));
        Assert.AreEqual(0, search.Rank(["alpha.package"]));
        Assert.AreEqual(-1, search.Rank(["peer.package"]));
        Assert.AreEqual(0, new PathSearch(" 　").Rank([]));
    }

    [TestMethod]
    public void HighlightRangesPreserveOriginalTextAndCombineOverlaps()
    {
        const string original = "Cafe\u0301　ＡＢＣ.txt";
        var portions = new PathSearch("CAFÉ ab BC").FindMatches(original).Select(range => original[range]).ToArray();
        CollectionAssert.AreEqual(new[] { "Cafe\u0301", "ＡＢＣ" }, portions);
    }
}
