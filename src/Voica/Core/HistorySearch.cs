using System;
using System.Collections.Generic;
using System.Linq;

namespace Voica;

/// <summary>
/// Filtering for the history list (spec §7). Records pile up into the thousands and scrolling stops
/// being a way to navigate, so the window filters by a case-insensitive substring.
///
/// The query is matched against the shown text AND the engine's own <c>raw_text</c>: people remember
/// what they SAID, not what the rules of §6.2 and the model of §6.1 turned it into. Searching the
/// final text alone means not finding your own dictation by the word you remember.
/// </summary>
public static class HistorySearch
{
    /// <summary>Whether one record answers the query. An empty query matches everything.</summary>
    public static bool Matches(Transcription record, string? query)
    {
        var q = (query ?? string.Empty).Trim();
        if (q.Length == 0) return true;
        return Contains(record.Text, q) || Contains(record.RawText, q);
    }

    /// <summary>The records to show for a query, in the order they came in (newest first).</summary>
    public static IReadOnlyList<Transcription> Filter(IEnumerable<Transcription> records, string? query)
    {
        var q = (query ?? string.Empty).Trim();
        if (q.Length == 0) return records.ToList();
        return records.Where(r => Matches(r, q)).ToList();
    }

    /// <summary>
    /// Whether the record answers the query ONLY through the raw text. The list then shows a text
    /// that does not contain what was typed, which reads as a bug unless it is said out loud.
    /// </summary>
    public static bool MatchedOnlyInRaw(Transcription record, string? query)
    {
        var q = (query ?? string.Empty).Trim();
        if (q.Length == 0) return false;
        return !Contains(record.Text, q) && Contains(record.RawText, q);
    }

    /// <summary>
    /// Where the query sits inside a text, for highlighting. Finding the record is only half the
    /// job on a five-minute dictation — the place inside it still has to be found by eye.
    /// Lengths come from the comparison itself, not from the query: a culture-aware match can span
    /// a different number of characters than what was typed.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> MatchRanges(string? text, string? query)
    {
        var ranges = new List<(int, int)>();
        var q = (query ?? string.Empty).Trim();
        if (text is null || q.Length == 0) return ranges;

        var compare = System.Globalization.CultureInfo.CurrentCulture.CompareInfo;
        int from = 0;
        while (from < text.Length)
        {
            int at = compare.IndexOf(text.AsSpan(from), q, System.Globalization.CompareOptions.IgnoreCase,
                out int matched);
            if (at < 0) break;
            if (matched <= 0) matched = 1;              // never loop on a zero-width match
            ranges.Add((from + at, matched));
            from += at + matched;
        }
        return ranges;
    }

    // Culture-aware and case-insensitive, so "Гроk" and "гроk" are the same query and Turkish i
    // behaves the way the user's locale expects.
    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) >= 0;
}
