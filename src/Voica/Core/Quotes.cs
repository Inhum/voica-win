using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Voica;

/// <summary>
/// Unpaired quotation marks (spec §6.4). The last check before the text is delivered, in the single
/// delivery point, AFTER both mechanisms (§6.2 and §6.1) — either can leave a mess and there is no
/// telling which did:
///
/// <list type="bullet">
/// <item>the engine decodes greedily, frame by frame, with no state saying "a quote is open"
/// (§2.5), so an unclosed guillemet is a normal outcome for it, not a failure;</item>
/// <item>the model, substituting a Latin term into Russian text, likes to wrap it in quotes against
/// the explicit ban in the prompt — and does it asymmetrically.</item>
/// </list>
/// </summary>
public static class Quotes
{
    /// <summary>
    /// Straight quotes become guillemets, by position. The engine writes whichever it feels like —
    /// its vocabulary holds `«`, `»` and `"`, each predicted separately, and one sentence can carry
    /// all three. The side is decided by the neighbours, the way text editors do it: after a space,
    /// a colon or a bracket and before a letter it opens, otherwise it closes.
    ///
    /// ⚠️ Only when the text contains Cyrillic. Guillemets are Russian typography; in an English
    /// dictation (§2 — language auto-detect) straight quotes are correct and must be left alone.
    /// </summary>
    public static string Smart(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (!text.Any(TermFix.IsCyrillicLetter)) return text;

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch != '"') { sb.Append(ch); continue; }

            char before = i > 0 ? text[i - 1] : ' ';
            char after = i + 1 < text.Length ? text[i + 1] : ' ';
            bool opening = (before == ' ' || before == ':' || before == '(' || before == '\n')
                && (char.IsLetter(after) || char.IsDigit(after));
            // The engine often forgets the space after a colon before a quote: `в ответ:"Давай`.
            if (opening && before == ':') sb.Append(' ');
            sb.Append(opening ? '«' : '»');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Straightens the quotes and drops the unpaired ones.
    ///
    /// Every surplus closing quote costs something, and which one to drop depends on the meaning.
    /// If one of the ALREADY PAIRED closers looks premature — a comma behind it and the sentence
    /// carrying on in lower case — that one goes: the quotation is in fact longer, and the
    /// "surplus" mark is what really closes it. Live case: `«Да", это стопроцентный вариант"` — the
    /// speaker quoted the whole phrase and the engine closed after the first word, so the right
    /// answer is `«Да, это стопроцентный вариант»`, not `«Да», это стопроцентный вариант`. With no
    /// premature closer around, the surplus one is itself the mistake and it goes — otherwise
    /// `Он сказал «да». Потом ушёл»` would lose the correct closer after «да».
    ///
    /// Unpaired openers are removed rather than closed: in practice it is direct speech left open
    /// («…сказал: «На, занимайся»), and only the speaker knows where the quotation ended. Guessing
    /// the boundary would add a mark that reads as a typo; without it the phrase looks fine.
    /// </summary>
    public static string Balance(string text)
    {
        var chars = Smart(text).ToCharArray();
        var open = new List<int>();
        var matched = new List<int>();
        var unmatched = new List<int>();
        var strays = new HashSet<int>();

        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '«') open.Add(i);
            else if (chars[i] == '»')
            {
                if (open.Count == 0) unmatched.Add(i);      // closed what was never opened
                else { open.RemoveAt(open.Count - 1); matched.Add(i); }
            }
        }

        foreach (int extra in unmatched)
        {
            int? premature = matched
                .Where(m => !strays.Contains(m) && IsPremature(chars, m))
                .Select(m => (int?)m)
                .FirstOrDefault();
            strays.Add(premature ?? extra);
        }
        foreach (int stillOpen in open) strays.Add(stillOpen);   // opened and never closed

        if (strays.Count == 0) return new string(chars);
        var sb = new StringBuilder(chars.Length);
        for (int i = 0; i < chars.Length; i++)
            if (!strays.Contains(i)) sb.Append(chars[i]);
        return sb.ToString();
    }

    /// <summary>
    /// A closing quote looks premature when a comma follows it and the sentence carries on in lower
    /// case: the phrase has not ended, so the quotation probably has not either.
    /// </summary>
    private static bool IsPremature(char[] chars, int at)
    {
        int j = at + 1;
        if (j >= chars.Length || chars[j] != ',') return false;
        j++;
        while (j < chars.Length && chars[j] == ' ') j++;
        return j < chars.Length && char.IsLower(chars[j]);
    }
}
