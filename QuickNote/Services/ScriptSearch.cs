using System.Globalization;
using System.Text;

namespace QuickNote.Services;

/// <summary>
/// Turns script text into the token stream the SQLite full-text index stores.
///
/// SQLite's tokenizers split on whitespace, which never occurs inside Chinese
/// text, so a whole script would collapse into one unsearchable token. This
/// class sidesteps that by indexing every letter on its own ("退款" → "退 款")
/// and turning a query into an FTS5 phrase of the same characters. A phrase
/// only matches where those characters sit next to each other, which gives the
/// same substring match the old LIKE '%keyword%' query had - but it is answered
/// from the index instead of by scanning every row.
/// </summary>
internal static class ScriptSearch
{
    /// <summary>
    /// Indexed form of one script: title, content and category, one token per
    /// letter/digit.
    /// </summary>
    public static string BuildIndexText(string? title, string? content, string? category)
    {
        // Roughly two characters of output per input character (letter + space).
        var capacity = (title?.Length ?? 0) + (content?.Length ?? 0) + (category?.Length ?? 0);
        var sb = new StringBuilder(capacity + capacity / 2 + 16);

        AppendTokens(sb, title);
        AppendTokens(sb, content);
        AppendTokens(sb, category);
        return sb.ToString();
    }

    /// <summary>
    /// FTS5 MATCH expression for a user query: one quoted phrase per
    /// whitespace-separated term, all of them required. Returns null when the
    /// query holds nothing indexable (punctuation only), letting the caller
    /// fall back to the substring scan.
    /// </summary>
    public static string? BuildMatchQuery(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return null;

        var phrases = new List<string>();
        foreach (var part in keyword.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = BuildIndexText(part, null, null);
            if (tokens.Length == 0)
                continue;

            // Tokens are only letters, digits and single spaces, so quoting
            // keeps user punctuation and FTS operators out of the syntax.
            phrases.Add($"\"{tokens}\"");
        }

        return phrases.Count == 0 ? null : string.Join(" AND ", phrases);
    }

    private static void AppendTokens(StringBuilder sb, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        string normalized;
        try
        {
            // Full-width forms become their ASCII equivalent, so "ＡＢＣ" is
            // found by typing "ABC" (and the other way round).
            normalized = text.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Ill-formed UTF-16 from an odd paste still gets indexed as-is.
            normalized = text;
        }

        foreach (var ch in normalized)
        {
            if (!char.IsLetterOrDigit(ch))
                continue;

            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(char.ToLowerInvariant(ch));
        }
    }
}
