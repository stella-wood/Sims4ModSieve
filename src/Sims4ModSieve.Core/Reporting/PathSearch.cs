using System.Globalization;
using System.Text;

namespace Sims4ModSieve.Core.Reporting;

public sealed class PathSearch(string query)
{
    public static PathSearch Empty { get; } = new("");
    private readonly string[] terms = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public bool IsActive => terms.Length > 0;

    public static bool Matches(string path, string query) => query.Length == 0
        || Normalize(path).Contains(Normalize(query), StringComparison.OrdinalIgnoreCase);

    public int Rank(IEnumerable<string> paths)
    {
        if (!IsActive) return 0;
        var covered = new bool[terms.Length];
        var coveredCount = 0;
        foreach (var path in paths)
        {
            var normalized = Normalize(path);
            var pathCount = 0;
            for (var i = 0; i < terms.Length; i++)
            {
                if (!normalized.Contains(terms[i], StringComparison.OrdinalIgnoreCase)) continue;
                pathCount++;
                if (covered[i]) continue;
                covered[i] = true;
                coveredCount++;
            }
            if (pathCount == terms.Length) return 2;
        }
        return coveredCount == terms.Length ? 1 : coveredCount > 0 ? 0 : -1;
    }

    public IEnumerable<Range> FindMatches(string value)
    {
        if (!IsActive) yield break;
        var normalized = Normalize(value);
        var hits = new bool[normalized.Length];
        foreach (var term in terms)
            for (var index = normalized.IndexOf(term, StringComparison.OrdinalIgnoreCase); index >= 0;
                index = normalized.IndexOf(term, index + 1, StringComparison.OrdinalIgnoreCase))
                hits.AsSpan(index, term.Length).Fill(true);

        // NFC can shorten or reorder a grapheme. Keep the original displayed text and mark the whole grapheme.
        if (!value.IsNormalized(NormalizationForm.FormC))
        {
            var originalHits = new bool[value.Length];
            var elements = StringInfo.GetTextElementEnumerator(value);
            var offset = 0;
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                var length = Normalize(element).Length;
                if (hits.AsSpan(offset, length).Contains(true))
                    originalHits.AsSpan(elements.ElementIndex, element.Length).Fill(true);
                offset += length;
            }
            hits = originalHits;
        }
        for (var i = 0; i < hits.Length;)
        {
            if (!hits[i]) { i++; continue; }
            var start = i++;
            while (i < hits.Length && hits[i]) i++;
            yield return start..i;
        }
    }

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormC);
        var firstWide = normalized.AsSpan().IndexOfAnyInRange('\uFF01', '\uFF5E');
        if (firstWide < 0 && !normalized.Contains('\u3000')) return normalized;

        return string.Create(normalized.Length, normalized, static (result, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                result[i] = source[i] switch
                {
                    >= '\uFF01' and <= '\uFF5E' => (char)(source[i] - 0xFEE0),
                    '\u3000' => ' ',
                    var character => character,
                };
        });
    }
}
