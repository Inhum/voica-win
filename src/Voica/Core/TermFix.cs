using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Voica;

/// <summary>
/// Deterministic term fixing (spec §6.2): rules, not a model — zero download, zero RAM, works
/// offline and identically on both engines. It runs in the shared tail of recognition, BEFORE the
/// AI correction of §6.1, so the model only gets what actually needs understanding.
///
/// It turns itself on whenever the vocabulary is non-empty; there is no separate setting (the user
/// filled the vocabulary precisely so those words come out right — asking twice is pointless).
///
/// The core idea is the <b>consonant skeleton</b>: vowels are what the engine loses and confuses,
/// consonants hold. Every live mangling of <c>DeepSeek</c> — <c>Dпсик</c>, <c>Dпсиcк</c>,
/// <c>Deepsc</c> — collapses to the same skeleton <c>dpsk</c> as the term itself.
///
/// A skeleton match alone is not proof, so the requirements differ by what the candidate word looks
/// like, and nothing is touched when no rule fires: a false positive costs more than a miss (the
/// miss is picked up by the LLM right after, while a corrupted word goes unnoticed).
/// </summary>
public static class TermFix
{
    /// <summary>
    /// Letter-level closeness a pure-Latin candidate must reach on top of the skeleton match
    /// (Levenshtein, as a ratio). Without it "Greek" would become "Groq" — they share the skeleton
    /// <c>grk</c>. Measured: <c>Deepsc</c>/<c>DeepSeek</c> = 0.63 → fix, <c>Greek</c>/<c>Groq</c> =
    /// 0.40 → leave alone.
    /// </summary>
    public const double MinLatinSimilarity = 0.6;

    /// <summary>
    /// How far a mixed-alphabet word's skeleton may drift from the term's and still count (one
    /// consonant, in practice). Such a word is proof of mangling by itself, so the skeleton does not
    /// have to line up exactly — but only for a single word: inside a glued window this leniency is
    /// off, because gluing is already an assumption of its own.
    /// </summary>
    public const double MinMixedSkeletonSimilarity = 0.75;

    // Minimum skeleton length, stepped by how strong the evidence of mangling is. Not tuning to a
    // case but a consequence of a short skeleton meaning nothing: `vice` yields `vk` exactly like
    // `Voica`, with a letter closeness of 0.60 — right on the threshold, which turned "vice versa"
    // into "Voica versa". Nudging the threshold to 0.62 would have been fitting to that one case;
    // demanding evidence is not.
    public const int MinSkeletonMixed = 2;      // a mixed alphabet is itself proof of mangling
    public const int MinSkeletonLatin = 3;      // plain Latin proves nothing on its own
    public const int MinSkeletonCyrillic = 4;   // plain Cyrillic proves nothing at all

    /// <summary>
    /// Compound terms are searched with a window of up to two consecutive words. The width does NOT
    /// follow from the term's word count: the engine skews both ways — "Claude Code" arrives as one
    /// word (<c>клодкод</c>) and "Tailscale" as two (<c>Tail scale</c>).
    /// </summary>
    public const int MaxWindowWords = 2;

    private enum Alphabet { None, Latin, Cyrillic, Mixed }

    private sealed record Term(string Canonical, string Skeleton, string Letters);

    // Building the term table transliterates the whole vocabulary; the vocabulary changes rarely and
    // dictations arrive one at a time, so keep the last one.
    private static readonly object Gate = new();
    private static string? _cachedVocabulary;
    private static Term[] _cachedTerms = Array.Empty<Term>();

    /// <summary>
    /// Applies the rules to <paramref name="text"/>. Returns it unchanged when the vocabulary is
    /// empty or nothing matched. The replacement is the canonical spelling from the vocabulary (its
    /// case included, §6); punctuation and spacing around the word are preserved.
    /// </summary>
    public static string Apply(string text, string? vocabulary)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var terms = TermsFor(vocabulary);
        if (terms.Length == 0) return text;

        var words = Tokenize(text);
        if (words.Count == 0) return text;

        StringBuilder? sb = null;
        int copied = 0;   // how much of `text` is already in `sb`

        for (int i = 0; i < words.Count; i++)
        {
            var (start, end) = words[i];
            string? replacement = null;
            int consumed = 1;

            // The two-word window is tried first so the longer reading wins ("Tail scale" must not
            // be considered as "Tail" alone).
            if (i + 1 < words.Count && Gluable(text, end, words[i + 1].Start))
            {
                var first = text.Substring(start, end - start);
                var second = text.Substring(words[i + 1].Start, words[i + 1].End - words[i + 1].Start);
                // Every word in the window has to contribute at least one consonant, otherwise the
                // window swallows a bystander: "дипсик и" yields the same skeleton `dpsk`, and the
                // conjunction disappeared from the text.
                //
                // The glued skeleton is the two word skeletons joined, NOT the skeleton of the
                // glued letters: repeats collapse inside a word but never across the seam. Found on
                // the corpus — "в Voica" glues into `вVoica`, whose `vv` collapses to `v`, matching
                // `Voica` exactly and eating the preposition. Same disease as the "в Cowork" one
                // the spec records; letting the seam erase a letter is one assumption too many.
                if (Skeleton(first).Length > 0 && Skeleton(second).Length > 0)
                {
                    replacement = Match(first + second, terms, Skeleton(first) + Skeleton(second));
                    if (replacement is not null) { consumed = 2; end = words[i + 1].End; }
                }
            }

            replacement ??= Match(text.Substring(start, end - start), terms);
            if (replacement is null) continue;

            if (!string.Equals(replacement, text.Substring(start, end - start), StringComparison.Ordinal))
            {
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, start - copied).Append(replacement);
                copied = end;
            }
            i += consumed - 1;
        }

        if (sb is null) return text;
        sb.Append(text, copied, text.Length - copied);
        return sb.ToString();
    }

    /// <summary>
    /// Splits the vocabulary the way §6 stores it (free-form, comma or newline separated).
    /// </summary>
    public static string[] ParseTerms(string? vocabulary) =>
        (vocabulary ?? string.Empty)
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The terms the rules are allowed to use. Russian terms are left out entirely: the skeleton of
    /// <c>аферту</c> matches <c>оферта</c> exactly, but substituting would produce "Отправь оферта"
    /// — what was said is inflected while the vocabulary holds the nominative. Declension needs
    /// understanding, which is §6.1's job; Latin terms do not inflect inside Russian text, so they
    /// are safe.
    /// </summary>
    private static Term[] TermsFor(string? vocabulary)
    {
        var vocab = vocabulary ?? string.Empty;
        lock (Gate)
        {
            if (_cachedVocabulary is not null && string.Equals(_cachedVocabulary, vocab, StringComparison.Ordinal))
                return _cachedTerms;

            _cachedTerms = ParseTerms(vocab)
                .Where(t => Classify(t) == Alphabet.Latin)
                .Select(t => new Term(t, Skeleton(t), Letters(t)))
                .Where(t => t.Skeleton.Length > 0)
                .ToArray();
            _cachedVocabulary = vocab;
            return _cachedTerms;
        }
    }

    /// <summary>
    /// Picks the term a candidate (one word, or two glued) stands for — or null when nothing
    /// qualifies. The skeleton must always match exactly; what is required on top of it depends on
    /// the candidate's alphabet.
    /// </summary>
    private static string? Match(string candidate, Term[] terms, string? glueSkeleton = null)
    {
        bool glued = glueSkeleton is not null;
        var alphabet = Classify(candidate);
        int min = alphabet switch
        {
            Alphabet.Mixed => MinSkeletonMixed,
            Alphabet.Latin => MinSkeletonLatin,
            Alphabet.Cyrillic => MinSkeletonCyrillic,
            _ => int.MaxValue,
        };
        var skeleton = glueSkeleton ?? Skeleton(candidate);
        if (skeleton.Length < min) return null;

        var letters = Letters(candidate);
        string? best = null;
        int bestDistance = int.MaxValue;

        foreach (var term in terms)
        {
            // A mixed alphabet in one word is proof of mangling by itself — normal Russian text has
            // none of it (§2.5) — so the skeleton is enough, and one consonant is even allowed to
            // differ; letter by letter such a word is far from the term anyway. Inside a glued
            // window that leniency is off: gluing is already an assumption. Plain Cyrillic proves
            // nothing, but the term is Latin and Latin terms do not inflect, so an exact skeleton is
            // the whole bar. Plain Latin is ordinary in Russian dictation and needs closeness too.
            bool hit = string.Equals(term.Skeleton, skeleton, StringComparison.Ordinal);
            if (!hit && alphabet == Alphabet.Mixed && !glued
                && skeleton.Length >= 3 && term.Skeleton.Length >= 3)
                hit = Similarity(skeleton, term.Skeleton) >= MinMixedSkeletonSimilarity;
            if (!hit) continue;

            if (alphabet == Alphabet.Latin && Similarity(letters, term.Letters) < MinLatinSimilarity)
                continue;

            // Several terms on one skeleton — take the one closest in length to what was said.
            int distance = Math.Abs(term.Canonical.Length - candidate.Length);
            if (distance < bestDistance) { best = term.Canonical; bestDistance = distance; }
        }
        return best;
    }

    /// <summary>
    /// Consonant skeleton (spec §6.2): the word is transliterated into Latin, vowels are dropped,
    /// <c>c</c> is folded to <c>k</c>, repeats are collapsed. Digits and punctuation take no part.
    /// Only <c>c</c> is folded, not <c>q</c>: the spec's aside that <c>Groq</c> and <c>Greek</c>
    /// share the skeleton <c>grk</c> is loose prose (the reference folds <c>c</c> alone, leaving
    /// <c>grq</c>), and folding <c>q</c> costs a real trap — <c>Grok</c>, a product name of its own,
    /// would then be rewritten into <c>Groq</c> at a letter closeness of 0.75.
    /// </summary>
    public static string Skeleton(string word)
    {
        var latin = new StringBuilder(word.Length);
        foreach (char raw in word)
        {
            char c = char.ToLowerInvariant(raw);
            if (IsCyrillic(c)) latin.Append(Translit(c));
            else if (c >= 'a' && c <= 'z') latin.Append(c);
        }

        var skeleton = new StringBuilder(latin.Length);
        for (int i = 0; i < latin.Length; i++)
        {
            char c = latin[i] == 'c' ? 'k' : latin[i];
            if (IsVowel(c)) continue;
            if (skeleton.Length > 0 && skeleton[skeleton.Length - 1] == c) continue;   // collapse repeats
            skeleton.Append(c);
        }
        return skeleton.ToString();
    }

    /// <summary>
    /// Letter-level closeness as a ratio: <c>1 - distance / max(length)</c> (Levenshtein). Two empty
    /// strings count as identical.
    /// </summary>
    public static double Similarity(string a, string b)
    {
        int max = Math.Max(a.Length, b.Length);
        if (max == 0) return 1.0;
        return 1.0 - (double)Levenshtein(a, b) / max;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// The word in one alphabet, lowercased — what <see cref="Similarity"/> compares letter by
    /// letter. Digits and punctuation drop out, so <c>focus-radio</c> and <c>focusradio</c> compare
    /// as the same spelling.
    /// </summary>
    private static string Letters(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char raw in s)
        {
            char c = char.ToLowerInvariant(raw);
            if (IsCyrillic(c)) sb.Append(Translit(c));
            else if (c >= 'a' && c <= 'z') sb.Append(c);
        }
        return sb.ToString();
    }

    private static Alphabet Classify(string s)
    {
        bool latin = false, cyrillic = false, other = false;
        foreach (char raw in s)
        {
            char c = char.ToLowerInvariant(raw);
            if (c >= 'a' && c <= 'z') latin = true;
            else if (IsCyrillic(c)) cyrillic = true;
            else if (char.IsLetter(c)) other = true;
        }
        if (other) return Alphabet.None;                       // an alphabet we cannot judge
        if (latin && cyrillic) return Alphabet.Mixed;
        if (latin) return Alphabet.Latin;
        if (cyrillic) return Alphabet.Cyrillic;
        return Alphabet.None;
    }

    /// <summary>Words are runs of letters and digits; everything else separates them.</summary>
    private static List<(int Start, int End)> Tokenize(string text)
    {
        var words = new List<(int, int)>();
        int i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i])) { i++; continue; }
            int start = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i])) i++;
            words.Add((start, i));
        }
        return words;
    }

    /// <summary>
    /// Whether two neighbouring words may be glued into one candidate: only whitespace or a hyphen
    /// may stand between them. Gluing is already an assumption; a second assumption on top of it
    /// eats the neighbours — "в Cowork" glued into <c>вCowork</c>, differed from <c>Cowork</c> by
    /// one letter and swallowed the preposition. That is why the glued form is only ever matched
    /// exactly (an exact skeleton, never a lenient one).
    /// </summary>
    private static bool Gluable(string text, int end, int nextStart)
    {
        for (int i = end; i < nextStart; i++)
        {
            char c = text[i];
            if (!char.IsWhiteSpace(c) && !IsHyphen(c)) return false;
        }
        return true;
    }

    private static bool IsHyphen(char c) => c == '-' || (c >= '‐' && c <= '―');

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';

    /// <summary>Cyrillic letter — also what §6.4 uses to decide that guillemets belong.</summary>
    internal static bool IsCyrillicLetter(char c) => IsCyrillic(char.ToLowerInvariant(c));

    private static bool IsCyrillic(char c) => c >= 'Ѐ' && c <= 'ӿ';

    /// <summary>Cyrillic → Latin, for skeleton purposes only: it has to be stable, not pretty.</summary>
    private static string Translit(char c) => c switch
    {
        'а' => "a", 'б' => "b", 'в' => "v", 'г' => "g", 'д' => "d", 'е' => "e", 'ё' => "e",
        'ж' => "zh", 'з' => "z", 'и' => "i", 'й' => "i", 'к' => "k", 'л' => "l", 'м' => "m",
        'н' => "n", 'о' => "o", 'п' => "p", 'р' => "r", 'с' => "s", 'т' => "t", 'у' => "u",
        'ф' => "f", 'х' => "h", 'ц' => "ts", 'ч' => "ch", 'ш' => "sh", 'щ' => "sch",
        'ъ' => "", 'ы' => "y", 'ь' => "", 'э' => "e", 'ю' => "yu", 'я' => "ya",
        _ => "",
    };
}
