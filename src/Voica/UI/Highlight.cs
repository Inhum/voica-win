using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Voica.UI;

/// <summary>
/// Paints the search query inside a <see cref="TextBlock"/> (spec §7). A row that answers a query is
/// only half the answer on a long dictation — the place inside it has to be visible too. Attached
/// properties rather than a converter, because the runs depend on two values at once (the text and
/// the query) and both change as the list is filtered.
/// </summary>
public static class Highlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Highlight),
        new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(Highlight),
        new PropertyMetadata(null, OnChanged));

    public static void SetText(DependencyObject o, string? v) => o.SetValue(TextProperty, v);
    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);
    public static void SetQuery(DependencyObject o, string? v) => o.SetValue(QueryProperty, v);
    public static string? GetQuery(DependencyObject o) => (string?)o.GetValue(QueryProperty);

    private static readonly Brush MatchBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xE0, 0x00)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBlock block) return;

        var text = GetText(block) ?? "";
        var ranges = HistorySearch.MatchRanges(text, GetQuery(block));

        block.Inlines.Clear();
        if (ranges.Count == 0)
        {
            block.Text = text;
            return;
        }

        int at = 0;
        foreach (var (start, length) in ranges)
        {
            if (start > at) block.Inlines.Add(new Run(text[at..start]));
            block.Inlines.Add(new Run(text.Substring(start, length)) { Background = MatchBrush });
            at = start + length;
        }
        if (at < text.Length) block.Inlines.Add(new Run(text[at..]));
    }
}
