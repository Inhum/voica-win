using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Voica.UI;

/// <summary>
/// Paints the search query inside the history window (spec §7). A row that answers a query is only
/// half the answer on a long dictation — the place inside it has to be visible too.
///
/// The list cells use the attached properties (a converter cannot: the runs depend on the text and
/// the query at once, and both change as the list is filtered); the detail pane uses
/// <see cref="RecordDocument"/>, which also carries the engine's raw text when that is what matched.
/// </summary>
public static class Highlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Highlight), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(Highlight), new PropertyMetadata(null, OnChanged));

    public static void SetText(DependencyObject o, string? v) => o.SetValue(TextProperty, v);
    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);
    public static void SetQuery(DependencyObject o, string? v) => o.SetValue(QueryProperty, v);
    public static string? GetQuery(DependencyObject o) => (string?)o.GetValue(QueryProperty);

    private static readonly Brush MatchBrush =
        Frozen(new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xE0, 0x00)));

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBlock block) return;

        var text = GetText(block) ?? "";
        block.Inlines.Clear();
        if (Runs(text, GetQuery(block)) is { Count: > 0 } runs)
            foreach (var run in runs) block.Inlines.Add(run);
        else
            block.Text = text;
    }

    /// <summary>
    /// The record's text with the query highlighted, plus the engine's own words underneath when
    /// <paramref name="raw"/> is given — that is the case where the record was found through the raw
    /// text and the shown text contains nothing of the query. Returns the document, how many times
    /// the query was found in the shown text, and the first highlighted run, to scroll it into view.
    /// </summary>
    public static (FlowDocument Document, int Matches, Inline? First) RecordDocument(
        string text, string? query, string? raw, string rawCaption)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        Inline? first = null;
        int matches = 0;

        foreach (var run in Runs(text, query) ?? new List<Run> { new(text) })
        {
            if (run.Background is not null) { matches++; first ??= run; }
            paragraph.Inlines.Add(run);
        }

        if (!string.IsNullOrEmpty(raw))
        {
            paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run(rawCaption) { FontSize = 11, Foreground = SystemColors.GrayTextBrush });
            paragraph.Inlines.Add(new LineBreak());
            foreach (var run in Runs(raw, query) ?? new List<Run> { new(raw) })
            {
                run.FontSize = 13;
                run.Foreground = SystemColors.GrayTextBrush;
                if (run.Background is not null) first ??= run;
                paragraph.Inlines.Add(run);
            }
        }

        return (new FlowDocument(paragraph) { PagePadding = new Thickness(2) }, matches, first);
    }

    /// <summary>A plain muted line — the detail pane's message when several records are selected.</summary>
    public static FlowDocument MutedDocument(string text) =>
        new(new Paragraph(new Run(text) { Foreground = SystemColors.GrayTextBrush }) { Margin = new Thickness(0) })
        {
            PagePadding = new Thickness(2),
        };

    /// <summary>
    /// Splits a text into plain and highlighted runs, or null when there is nothing to highlight.
    /// </summary>
    private static List<Run>? Runs(string text, string? query)
    {
        var ranges = HistorySearch.MatchRanges(text, query);
        if (ranges.Count == 0) return null;

        var runs = new List<Run>();
        int at = 0;
        foreach (var (start, length) in ranges)
        {
            if (start > at) runs.Add(new Run(text[at..start]));
            runs.Add(new Run(text.Substring(start, length)) { Background = MatchBrush });
            at = start + length;
        }
        if (at < text.Length) runs.Add(new Run(text[at..]));
        return runs;
    }
}
