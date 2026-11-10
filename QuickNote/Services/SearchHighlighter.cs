namespace QuickNote.Services;

/// <summary>
/// Finds where the search terms sit inside a row of text, so the list can
/// highlight them. Kept free of WPF types (and unit-tested on its own): the
/// rules mirror the search itself - every whitespace-separated term must be
/// found, case is ignored, and overlapping matches resolve to the longest term.
/// </summary>
internal static class SearchHighlighter
{
    /// <summary>
    /// Non-overlapping ranges of <paramref name="text"/> that match one of the
    /// query terms, in left-to-right order.
    /// </summary>
    public static List<(int Start, int Length)> FindRanges(string? text, string? query)
    {
        var ranges = new List<(int Start, int Length)>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query))
            return ranges;

        var terms = query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            // Longest first, so "退款流程" wins over "退款" at the same offset.
            .OrderByDescending(term => term.Length)
            .ToArray();

        var index = 0;
        while (index < text.Length)
        {
            var bestStart = -1;
            var bestLength = 0;

            foreach (var term in terms)
            {
                var hit = text.IndexOf(term, index, StringComparison.CurrentCultureIgnoreCase);
                if (hit < 0)
                    continue;

                if (bestStart < 0 || hit < bestStart || (hit == bestStart && term.Length > bestLength))
                {
                    bestStart = hit;
                    bestLength = term.Length;
                }
            }

            if (bestStart < 0)
                break;

            ranges.Add((bestStart, bestLength));
            index = bestStart + bestLength;
        }

        return ranges;
    }
}
