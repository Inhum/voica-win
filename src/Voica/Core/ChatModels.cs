using System;
using System.Collections.Generic;
using System.Linq;

namespace Voica;

/// <summary>
/// Chat-model selection for AI term correction (spec §6.1). Groq removes and renames models over
/// time, so the model is not hardcoded: the app reads the live model list, picks the best available
/// one by a priority chain, and heals itself when the choice disappears — no release required.
/// This class holds the pure logic (fully covered by the self-test); network calls live in
/// <see cref="GroqClient"/>.
/// </summary>
public static class ChatModels
{
    /// <summary>Setting value meaning "let the app pick the best available model".</summary>
    public const string Auto = "auto";

    /// <summary>Seed used before the first successful resolve (first run / offline).</summary>
    public const string Seed = "openai/gpt-oss-120b";

    /// <summary>
    /// Preference order when resolving automatically (spec §6.1), largest first among what Groq
    /// actually serves. The chain is a consumable, not a constant: it gets revised whenever the
    /// provider retires a model (`gemma2-9b-it` vanished; `llama-3.3-70b-versatile` was withdrawn
    /// on 2026-08-16, and Groq itself named gpt-oss-120b and qwen3.6-27b as the replacements).
    /// Free-tier rate limits deliberately do NOT influence the order — one correction request is
    /// a vocabulary plus a single dictation, so even the lowest tier covers hundreds a day, and
    /// correction quality matters more than headroom.
    /// </summary>
    public static readonly string[] PriorityChain =
    {
        "openai/gpt-oss-120b",
        "qwen/qwen3.6-27b",
        "openai/gpt-oss-20b",
        "llama-3.1-8b-instant",
    };

    /// <summary>
    /// Substrings marking non-chat models. Groq exposes no "is chat" flag, so we use a denylist:
    /// anything not obviously speech/embedding/safety related counts as usable, which lets new
    /// chat families work without an app update. <c>compound</c> is excluded for a different
    /// reason (spec §6.1): those are Groq's agentic systems with their own routing and tools —
    /// an extra layer for one short term fix, and they route to models the org may not have.
    /// <c>allam</c> is excluded for a third reason: it is an Arabic model, and since the live list
    /// comes back alphabetically while the last-resort pick is simply the first entry,
    /// <c>allam-2-7b</c> would quietly become the fallback for correcting RUSSIAN terms. If Arabic
    /// is ever asked for, bring it back and pick the fallback by language rather than by alphabet.
    /// The match is a substring, so the self-test guards that it does not catch <c>meta-llama</c>
    /// (which reads "a-llama", not "allam").
    /// </summary>
    private static readonly string[] Denylist =
    {
        "whisper", "tts", "orpheus", "guard", "embed", "moderation", "distil", "compound", "allam",
    };

    /// <summary>True if a model id looks like a usable chat model.</summary>
    public static bool IsChatModel(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var lower = id.ToLowerInvariant();
        return !Denylist.Any(bad => lower.Contains(bad));
    }

    /// <summary>Filters a raw model-id list down to usable chat models, preserving order.</summary>
    public static IReadOnlyList<string> FilterChatModels(IEnumerable<string> ids) =>
        ids.Where(IsChatModel).ToList();

    /// <summary>
    /// Resolves which model to send (spec §6.1): an explicit choice wins while it is still
    /// available; otherwise the first entry of the priority chain that exists; otherwise the first
    /// available model. Null when nothing usable is available.
    /// </summary>
    public static string? Resolve(IReadOnlyCollection<string> available, string? preferred)
    {
        var chat = available.Where(IsChatModel).ToList();
        if (chat.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferred) && preferred != Auto && chat.Contains(preferred))
            return preferred;

        foreach (var candidate in PriorityChain)
            if (chat.Contains(candidate))
                return candidate;

        return chat[0];
    }

    /// <summary>
    /// True if an explicitly chosen model is gone from the live list — the caller then falls back
    /// to <see cref="Auto"/> silently (spec §6.1).
    /// </summary>
    public static bool ChoiceRetired(IReadOnlyCollection<string> available, string? preferred) =>
        !string.IsNullOrWhiteSpace(preferred) && preferred != Auto
        && available.Count > 0 && !available.Contains(preferred);
}
