using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Voica;

/// <summary>
/// Removal of filler sounds — «э-э-э», «ммм», «хмм» (spec §6.3). They mean nothing in speech and
/// clutter the text. Rules, so no key and no network, on both engines, and before the AI pass.
///
/// The rule looks at the SHAPE of a word, not at a list of spellings: a candidate is a token that
/// shrinks to one or two letters once hyphens and repeats are collapsed (`эээ`→`э`, `хмм`→`хм`).
/// Listing spellings is pointless — recognition writes the same mumble differently every time (a
/// live history held 12 distinct forms across 14 cases).
///
/// Unlike §6.2 this has a setting, on by default: it <b>deletes what was said</b>, which is wrong
/// for anyone transcribing speech verbatim.
/// </summary>
public static class Fillers
{
    /// <summary>
    /// Collapsed forms that are rubbish in full and go even without being drawn out.
    ///
    /// ⚠️ Every entry must be at most two letters — that is all the length gate below lets through,
    /// so anything longer would lie here dead. «мхм», «угу» and «ага» used to (found by comparing
    /// the two platforms, macOS 0.9.18).
    ///
    /// «Угу», «ага» and «мхм» are NOT removed, and that is a decision rather than the gate: they
    /// are agreement, they carry meaning unlike mumbling, and a dictation of a single «Ага.» would
    /// come back empty. Same argument that straightens «ну-у-у» instead of dropping it. «эм» is out
    /// for a different reason: that is how the engine writes GigaAM — «Джига Эм».
    /// </summary>
    private static readonly HashSet<string> FillerWords = new(StringComparer.Ordinal)
        { "хм", "э" };

    /// <summary>
    /// Real words that were merely drawn out: straightened, never dropped. The list is explicit
    /// because straightening everything would turn `PPC` into `Pc` and `All` into `Al`.
    /// </summary>
    private static readonly HashSet<string> Stretchable = new(StringComparer.Ordinal)
        { "ну", "но", "да", "нет", "вот", "так" };

    /// <summary>
    /// Single sounds — dropped ONLY when they were drawn out. On its own «а» is a conjunction,
    /// «у» and «о» are prepositions.
    ///
    /// ⚠️ «и» is deliberately absent. The engine writes the abbreviation «ИИ» in lower case too —
    /// «ии» — which collapses to «и» and would be deleted as a drawn-out sound: «Проверка ии.»
    /// became «Проверка». A drawn-out «и-и-и» is rare, the abbreviation is common.
    /// </summary>
    private static readonly HashSet<string> FillerSounds = new(StringComparer.Ordinal)
        { "э", "а", "ы", "у", "м", "о" };

    /// <summary>
    /// Collapses hyphens and runs of the same letter: «Э-э-э» → «э», «Ну-у-у» → «ну». The drawn-out
    /// sound is spelled differently every time, so the collapsed form is what gets compared.
    /// </summary>
    public static string Collapsed(string word)
    {
        var sb = new StringBuilder(word.Length);
        foreach (char raw in word)
        {
            if (raw == '-' || raw == '‑') continue;
            char c = char.ToLowerInvariant(raw);
            if (sb.Length == 0 || sb[sb.Length - 1] != c) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Removes filler sounds, leaving the rest of the text exactly as it was.</summary>
    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = new StringBuilder(text.Length);
        string word = "", sep = "", prevWord = "", sepAfter = "";
        bool dropped = false, didDrop = false, pendingCapital = false, droppedWasUpper = false;

        void Flush()
        {
            if (word.Length == 0) return;

            // Digits are never touched: collapsing repeats turned `100` into `10`. Nor are
            // all-caps abbreviations: `ИИ` collapsed to `и` and went as a filler.
            bool untouchable = word.Any(char.IsDigit)
                || (word.Length >= 2 && word.All(c => !char.IsLetter(c) || char.IsUpper(c)));
            if (untouchable)
            {
                result.Append(sep).Append(word);
                prevWord = word; word = ""; sep = ""; dropped = false;
                return;
            }

            var collapsed = Collapsed(word);
            bool shrank = collapsed.Length < word.Length;
            // «5 мм» is millimetres, not mumbling — a number on the left clears the suspicion.
            bool afterNumber = prevWord.Any(char.IsDigit);
            // The length requirement only matters for drawn-out sounds and is already implied by
            // `shrank`. The unconditional fillers («э», «хм») need no stretching: a lone «э» is
            // the same rubbish as «э-э-э».
            bool isFiller = !afterNumber && collapsed.Length <= 2
                && (FillerWords.Contains(collapsed) || (shrank && FillerSounds.Contains(collapsed)));

            if (isFiller)
            {
                // The separator BEFORE the filler is kept and handed to the next word, the one
                // after it is skipped in the loop. Eating both glues words together: "проверка
                // хмм всяких" became "проверкався ких".
                dropped = true;
                didDrop = true;
                // The filler's own case hints that it opened a sentence, but the decision is not
                // made here: the separator the next word will inherit has not been chosen yet
                // (see PickSeparator).
                droppedWasUpper = char.IsUpper(word[0]);
                word = "";
                return;
            }

            // A drawn-out real word is straightened, keeping the case of its first letter.
            bool stretched = shrank && Stretchable.Contains(collapsed);
            var emitted = stretched ? RestoreCase(collapsed, word) : word;
            if (pendingCapital)
            {
                emitted = char.ToUpperInvariant(emitted[0]) + emitted[1..];
                pendingCapital = false;
            }
            result.Append(sep).Append(emitted);
            prevWord = word;
            dropped = false;
            word = ""; sep = "";
        }

        // Removing a filler leaves TWO separators — before it and after. One has to go, and it is
        // the one carrying punctuation that stays: «Ну, эээ, дальше» → «Ну, дальше», but
        // «Проверка ии. Сто» → «Проверка. Сто», not two sentences glued into one.
        void PickSeparator()
        {
            static bool HasPunct(string t) => t.Any(char.IsPunctuation);
            // Priority goes to the separator BEFORE the filler: that is where the end of the
            // sentence sits, while after the filler there is usually just the comma that set it
            // off. Otherwise «Почему? А-а, как бы» lost its question mark.
            if (!HasPunct(sep) && HasPunct(sepAfter)) sep = sepAfter;
            // Capitalize the next word if the filler opened a sentence. Only now can that be
            // decided: there were two separators around the filler and exactly one survived, and it
            // is the one that says whether a sentence ended.
            //
            // ⚠️ The filler's OWN case counts only at the very start of the text, where there is no
            // separator to its left. In the middle it lies: the engine writes a filler capitalized
            // as a remark of its own, and "их закрыли, Хмм, потом решили" produced "закрыли, Потом"
            // — a live line from the macOS history, and the same rule written from the brief broke
            // on it there before it could break here.
            if ((result.Length == 0 && droppedWasUpper) || EndsSentence(sep)) pendingCapital = true;
            droppedWasUpper = false;
            sepAfter = "";
            dropped = false;
        }

        foreach (char ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-')
            {
                if (dropped) PickSeparator();
                word += ch;
            }
            else
            {
                Flush();
                if (dropped) sepAfter += ch; else sep += ch;
            }
        }
        if (dropped) PickSeparator();
        Flush();
        // Trailing punctuation after the last word — without this the final full stop was lost.
        result.Append(sep);

        // The tidy-up repairs traces of the removal. With nothing removed it must not run: it
        // would reformat punctuation nobody asked it to touch.
        return didDrop ? Tidy(result.ToString()) : result.ToString();
    }

    /// <summary>Whether the separator carries the end of a sentence, so the next word starts one.</summary>
    private static bool EndsSentence(string separator)
    {
        var trimmed = separator.TrimEnd();
        if (trimmed.Length == 0) return false;
        char last = trimmed[trimmed.Length - 1];
        return last is '.' or '!' or '?' or '…';
    }

    private static string RestoreCase(string s, string original) =>
        original.Length > 0 && char.IsUpper(original[0]) && s.Length > 0
            ? char.ToUpperInvariant(s[0]) + s[1..]
            : s;

    /// <summary>
    /// Cleans up after a removal, and only after one: a doubled space, and a mark left at the very
    /// start of the line. The punctuation of the surrounding text is not touched — the separator
    /// after a filler is skipped while parsing, so no dangling commas are left behind, and other
    /// people's ellipses are none of our business.
    /// </summary>
    private static string Tidy(string s)
    {
        s = Regex.Replace(s, "[ \t]{2,}", " ");
        s = Regex.Replace(s, @"^[\s,;:.!?…]+", "");
        return s.Trim();
    }
}
