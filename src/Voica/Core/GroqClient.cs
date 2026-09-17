using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Voica;

/// <summary>Successful transcription result (spec §2).</summary>
public sealed record TranscriptionResult(string Text, string? Language, double? Duration);

/// <summary>Outcome of an API-key validation check (spec §2, "Валидация ключа").</summary>
public enum KeyStatus { Valid, Rejected, Error }

public sealed record KeyValidation(KeyStatus Status, string Message);

/// <summary>A Groq error already mapped to a user-facing message (spec §2).</summary>
public sealed class GroqException : Exception
{
    public GroqException(string message, bool isNetworkError = false) : base(message)
        => IsNetworkError = isNetworkError;

    /// <summary>True for connectivity failures/timeouts — the offline-fallback trigger (spec §2.5).</summary>
    public bool IsNetworkError { get; }
}

/// <summary>
/// Groq Speech-to-Text client (spec §2) and vocabulary prompt preparation (spec §6).
/// </summary>
public static class GroqClient
{
    /// <summary>Default speech-to-text model (spec §2): the faster of the two.</summary>
    public const string DefaultSttModel = "whisper-large-v3-turbo";

    /// <summary>Selectable STT models (spec §2): turbo is faster, large-v3 is more accurate.</summary>
    public static readonly string[] SttModels = { "whisper-large-v3-turbo", "whisper-large-v3" };

    /// <summary>Selectable recognition languages (spec §2): "auto" omits the field entirely.</summary>
    public static readonly string[] Languages = { "auto", "ru", "en" };

    /// <summary>Falls back to the default when a stored model is unknown (spec §2).</summary>
    public static string NormalizeSttModel(string? model) =>
        Array.Exists(SttModels, m => m == model) ? model! : DefaultSttModel;

    /// <summary>Falls back to "auto" when a stored language is unknown (spec §2).</summary>
    public static string NormalizeLanguage(string? language) =>
        Array.Exists(Languages, l => l == language) ? language! : "auto";
    // Spec §6.1: the chat model is resolved dynamically from the live model list — see ChatModels.
    public const int PromptCharBudget = 800;

    public static readonly Uri Endpoint = new("https://api.groq.com/openai/v1/audio/transcriptions");
    public static readonly Uri ModelsEndpoint = new("https://api.groq.com/openai/v1/models");
    public static readonly Uri ChatEndpoint = new("https://api.groq.com/openai/v1/chat/completions");

    private static readonly TimeSpan TranscribeTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ValidateTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PostProcessTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ChatProbeTimeout = TimeSpan.FromSeconds(15);

    // Shared client with no built-in timeout; each call applies its own via a CancellationToken.
    // One client for every network call in the app (spec §9.5): a proxy setting that reaches
    // recognition but not the model download would be worse than none.
    private static HttpClient Http => Net.Shared;

    /// <summary>
    /// Prepares the Whisper <c>prompt</c> field from the vocabulary string (spec §6):
    /// trims; empty → null; longer than the budget → keep the tail.
    /// </summary>
    public static string? PromptField(string? vocabulary)
    {
        var trimmed = (vocabulary ?? string.Empty).Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > PromptCharBudget)
            return trimmed[^PromptCharBudget..];
        return trimmed;
    }

    /// <summary>
    /// Transcribes an audio file (spec §2). <paramref name="sttModel"/> and
    /// <paramref name="language"/> come from settings; "auto" language omits the field so Whisper
    /// detects it. Throws <see cref="GroqException"/> with a user message on failure.
    /// </summary>
    public static async Task<TranscriptionResult> TranscribeAsync(
        string audioFilePath, string apiKey, string? vocabulary,
        string? sttModel = null, string? language = null, CancellationToken cancellationToken = default)
    {
        var model = NormalizeSttModel(sttModel);
        var lang = NormalizeLanguage(language);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TranscribeTimeout);

        using var form = new MultipartFormDataContent();

        await using var fileStream = File.OpenRead(audioFilePath);
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileContent, "file", Path.GetFileName(audioFilePath));

        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("0"), "temperature");

        var prompt = PromptField(vocabulary);
        if (prompt is not null)
            form.Add(new StringContent(prompt), "prompt");

        // "auto" → don't send the field at all, so Whisper detects the language itself (spec §2).
        if (lang != "auto")
            form.Add(new StringContent(lang), "language");

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GroqException(S.GroqTimeout, isNetworkError: true);
        }
        catch (HttpRequestException ex)
        {
            // One translation point (spec §9.5): a proxy failure must read the same here as it does
            // in the model download and the update check.
            throw new GroqException(Net.IsProxyAuthFailure(ex)
                ? Net.Describe(ex, Endpoint)
                : string.Format(S.GroqNetworkFmt, ex.Message), isNetworkError: true);
        }

        var body = await response.Content.ReadAsStringAsync(cts.Token);

        if (!response.IsSuccessStatusCode)
            throw new GroqException(MapError(response.StatusCode, body, model));

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("text", out var textEl))
                throw new GroqException(S.GroqNoText);

            var text = (textEl.GetString() ?? string.Empty).Trim();
            string? detectedLanguage = root.TryGetProperty("language", out var langEl) ? langEl.GetString() : null;
            double? duration = root.TryGetProperty("duration", out var durEl) && durEl.TryGetDouble(out var d) ? d : null;

            return new TranscriptionResult(text, detectedLanguage, duration);
        }
        catch (JsonException)
        {
            throw new GroqException(S.GroqParse);
        }
    }

    // --- LLM post-processing: fix mangled vocabulary terms (spec §6.1) ---

    /// <summary>
    /// Builds the correction prompt (spec §6.1). Null when the vocabulary is empty — post-processing
    /// is skipped entirely. The wording mirrors the reference `GroqClient.postProcessPrompt` verbatim
    /// (it is the semantic contract and is intentionally Russian on all locales, as in macOS).
    /// </summary>
    public static string? PostProcessPromptText(string text, string? vocabulary)
    {
        var vocab = (vocabulary ?? string.Empty).Trim();
        if (vocab.Length == 0) return null;
        return
            "Ты — корректор диктовки. Ниже словарь терминов пользователя и распознанный текст. " +
            "В тексте могут встречаться искажённые варианты этих терминов (речь распознавалась на слух). " +
            "Верни ТОЛЬКО исправленный текст: замени искажённые варианты на правильные написания из словаря, " +
            "согласуя с падежом и контекстом. Если под искажение подходят несколько терминов словаря — " +
            "выбирай наиболее близкий по ЗВУЧАНИЮ к тому, что записано (например, «кубер стил» звучит как " +
            "kubectl, а не Kubernetes). Если слово в тексте уже совпадает со словарным термином " +
            "(пусть и в другом регистре, например с заглавной буквы) — оно правильное: не трогай его " +
            "и не меняй его регистр. Больше ничего не меняй — ни слова, ни пунктуацию. " +
            "Если исправлять нечего — верни текст как есть.\n\n" +
            $"СЛОВАРЬ: {vocab}\n\n" +
            $"ТЕКСТ: {text}";
    }

    /// <summary>Lists model ids the key can see (spec §6.1). Empty on any failure.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(string apiKey, CancellationToken cancellationToken = default)
        => (await TryListModelsAsync(apiKey, cancellationToken)).Ids;

    /// <summary>
    /// The same listing, plus the reason it came back empty. Without it a proxy that answers 407
    /// reads as "your Groq org has no chat models" (spec §9.5): the request never left the network,
    /// and the one thing the user needs — the proxy's address — is the thing we threw away.
    /// </summary>
    private static async Task<(IReadOnlyList<string> Ids, string? Error)> TryListModelsAsync(
        string apiKey, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ValidateTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!response.IsSuccessStatusCode)
                return (Array.Empty<string>(), response.StatusCode == HttpStatusCode.Unauthorized
                    ? S.KeyValidRejected
                    : $"HTTP {(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return (Array.Empty<string>(), S.GroqParse);

            var ids = new List<string>();
            foreach (var item in data.EnumerateArray())
                if (item.TryGetProperty("id", out var idEl) && idEl.GetString() is { } id)
                    ids.Add(id);
            return (ids, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (Array.Empty<string>(), S.KeyValidTimeout);
        }
        catch (Exception ex)
        {
            return (Array.Empty<string>(), Net.Describe(ex, ModelsEndpoint));
        }
    }

    /// <summary>
    /// Corrects mangled vocabulary terms via the Groq chat model (spec §6.1). Fail-open: on any
    /// error/timeout/non-2xx/empty answer the ORIGINAL text is returned — post-processing never
    /// blocks dictation. A refusal heals without a release, and this dictation keeps its
    /// correction: on a 404 (model retired) or a 403 (model not enabled for the org, "auto" only)
    /// the resolution is recomputed and the request is retried ONCE on the new model.
    /// </summary>
    /// <param name="notify">
    /// Receives a user-facing message when a model is refused for the org (403), once per model per
    /// session. The step-down is never silent: a 403 is one checkbox in the Groq console away from
    /// fixed, and without a word the person would stay on a worse model with no hint the better one
    /// can come back (spec §6.1).
    /// </param>
    public static async Task<string> PostProcessAsync(string text, string apiKey, string? vocabulary,
        Action<string>? notify = null, CancellationToken cancellationToken = default)
    {
        var prompt = PostProcessPromptText(text, vocabulary);
        if (prompt is null) return text;

        // ONE fail-open budget for the whole correction — both requests and the re-resolve between
        // them. Separate timeouts would let a refusal double what the person waits (spec §6.1).
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(PostProcessTimeout);
        var fingerprint = ChatModels.KeyFingerprint(apiKey);

        var model = Prefs.ActiveChatModel;
        var (result, status) = await TryPostProcessAsync(text, apiKey, prompt, model, budget.Token);
        if (status == 403)
        {
            // /v1/models lists what the platform serves, not what this key may use — a new model
            // arrives in the org switched OFF. So a 403 is a reason to step down the chain,
            // exactly like a model disappearing (spec §6.1).
            Prefs.MarkChatModelBlocked(model, fingerprint);
            if (Prefs.ChatModel != ChatModels.Auto)
            {
                // Retrying on another model would be a substitution, and a manual choice is the
                // person's own: mark it, say so, leave the text uncorrected (spec §6.1).
                ReportBlocked(model, null, notify);
                return text;
            }
        }
        else if (status == RetiredStatus)
        {
            Log.Info($"chat model {model} is retired — re-resolving");   // a stale manual pick goes back to auto
        }
        else
        {
            return result;
        }

        var next = await ResolveAndCacheChatModelAsync(apiKey, budget.Token);
        if (next is null || next == model)
        {
            if (status == 403) ReportBlocked(model, null, notify);
            return text;
        }

        // Exactly one retry, never a walk down the whole chain inside one dictation: with several
        // refusals in a row the person would wait for each. If the retry fails too, the text goes as
        // it is, and the resolution recomputed from the marks fixes the next dictation.
        Log.Info($"chat model {model} {(status == 403 ? "refused (403)" : "retired")} — retrying once on {next}");
        var (retry, retryStatus) = await TryPostProcessAsync(text, apiKey, prompt, next, budget.Token);
        if (retryStatus == 403) Prefs.MarkChatModelBlocked(next, fingerprint);
        if (status == 403) ReportBlocked(model, retryStatus == 403 ? null : next, notify);
        return retry;
    }

    /// <summary>Models already reported as refused — at most one notice per model per session.</summary>
    private static readonly HashSet<string> BlockedReported = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="next">
    /// The model correction carries on with, or null when there is nowhere to step (manual choice,
    /// or every candidate refused). The difference matters: in the first case correction still
    /// works and the person is told how to get the better model back, in the second it does not.
    /// </param>
    private static void ReportBlocked(string model, string? next, Action<string>? notify)
    {
        lock (BlockedReported)
            if (!BlockedReported.Add(model)) return;

        Log.Error(next is null
            ? $"chat model {model} is blocked for this Groq org (403)"
            : $"chat model {model} is blocked for this Groq org (403) — stepped down to {next}");
        notify?.Invoke(next is null
            ? string.Format(S.NoticeChatBlockedFmt, model)
            : string.Format(S.NoticeChatSteppedFmt, model, next));
    }

    /// <summary>
    /// The status a retired model is reported as. Groq has two answers for one fact — 404
    /// <c>model_not_found</c> and 400 <c>model_decommissioned</c> (spec §6.1) — so the second is folded
    /// into the first and the healing code has one case to handle.
    /// </summary>
    private const int RetiredStatus = 404;

    /// <summary>
    /// True when a response says the model has been decommissioned (spec §6.1): 400 with
    /// <c>error.code == "model_decommissioned"</c>. By the CODE, never the message — the wording is
    /// the provider's to change any day, the code is the contract. A bare 400 is not a retired
    /// model: it is just as likely our own malformed request, and re-resolving on it would be wrong.
    /// </summary>
    public static bool IsDecommissioned(int status, string? body)
    {
        if (status != 400 || string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() == "model_decommissioned";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The HTTP code of a failed chat response, with a decommissioned model reported as retired.</summary>
    private static async Task<int> RefusalStatusAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (status != 400) return status;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return IsDecommissioned(status, body) ? RetiredStatus : status;
    }

    /// <summary>Runs one correction request; Status is the HTTP code, or 0 when we failed open.</summary>
    private static async Task<(string Text, int Status)> TryPostProcessAsync(
        string text, string apiKey, string prompt, string model, CancellationToken budget)
    {
        // No timeout of its own: the caller's budget covers the retry as well (spec §6.1).
        try
        {
            using var request = BuildChatRequest(apiKey, prompt, maxCompletionTokens: 4096, model);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, budget);
            if (!response.IsSuccessStatusCode)
                return (text, await RefusalStatusAsync(response, budget));

            var body = await response.Content.ReadAsStringAsync(budget);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return (text, 0);
            var raw = (choices[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty).Trim();
            var cleaned = StripReasoning(raw);
            if (cleaned.Length != raw.Length)
                Log.Info($"chat answer carried reasoning — stripped {raw.Length - cleaned.Length} chars ({model})");
            if (!IsPlausibleCorrection(text, cleaned))
            {
                Log.Info($"chat answer rejected as implausible ({cleaned.Length} chars for {text.Length}) — keeping the original");
                return (text, 0);
            }
            return (cleaned, 0);
        }
        catch
        {
            return (text, 0);   // fail-open (spec §6.1)
        }
    }

    // Reasoning models put their train of thought into `content` itself, wrapped in <think>...</think>,
    // with the actual answer after it. Provider-side switches (reasoning_format and friends) are not
    // an option: they are specific to one API and a model without reasoning can answer 400, which by
    // fail-open would silently drop the correction for EVERY model. So we clean it up ourselves.
    private static readonly Regex ThinkBlock =
        new(@"<think[^>]*>.*?</think>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // An unclosed tag means the answer was cut off by max_completion_tokens mid-thought — nothing
    // useful follows, so everything from the tag on goes.
    private static readonly Regex ThinkOpen =
        new(@"<think[^>]*>.*", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Removes reasoning blocks from a chat answer (spec §6.1): every <c>&lt;think&gt;...&lt;/think&gt;</c>
    /// regardless of case or attributes, then any unclosed opener through the end. Mirrors
    /// <c>GroqClient.stripReasoning</c> in the macOS app.
    /// </summary>
    public static string StripReasoning(string content) =>
        ThinkOpen.Replace(ThinkBlock.Replace(content, string.Empty), string.Empty).Trim();

    /// <summary>
    /// Sanity check on a correction (spec §6.1): term fixing swaps individual words, so the answer
    /// cannot be wildly longer than the original. Empty, or longer than <c>original * 2 + 50</c>,
    /// means the model rambled — the caller then delivers the original text. This is the second line
    /// behind fail-open: it catches any chatty model, not just the &lt;think&gt; format.
    /// </summary>
    public static bool IsPlausibleCorrection(string original, string cleaned) =>
        cleaned.Length > 0 && cleaned.Length <= original.Length * 2 + 50;

    /// <summary>
    /// Re-resolves the chat model from the live list and caches it (spec §6.1 self-healing).
    /// Silently drops an explicit choice that no longer exists, and skips models this key was
    /// refused (403). Null when nothing usable is available.
    /// </summary>
    public static async Task<string?> ResolveAndCacheChatModelAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var available = await ListModelsAsync(apiKey, cancellationToken);
        return available.Count == 0 ? null : ResolveAndCache(available, apiKey);
    }

    private static string? ResolveAndCache(IReadOnlyList<string> available, string apiKey)
    {
        if (ChatModels.ChoiceRetired(available, Prefs.ChatModel))
        {
            Log.Info($"chosen chat model '{Prefs.ChatModel}' is gone — falling back to auto");
            Prefs.ChatModel = ChatModels.Auto;
        }

        var blocked = Prefs.BlockedChatModels(ChatModels.KeyFingerprint(apiKey));
        var resolved = ChatModels.Resolve(available, Prefs.ChatModel, blocked);
        if (resolved is not null) Prefs.ResolvedChatModel = resolved;
        return resolved;
    }

    /// <summary>
    /// Outcome of the chat-model check shown in Settings (spec §6.1 UX). <c>SteppedFrom</c> is the
    /// model that answered 403 when the check had to step down to <c>Model</c>.
    /// </summary>
    public sealed record ChatModelCheck(bool Available, string? Model, string? Problem, bool Switched,
        string? SteppedFrom = null);

    /// <summary>
    /// Refreshes the model list, re-resolves (self-healing), and probes the resolved model
    /// (spec §6.1): distinguishes 403 (blocked for the org) from other failures. In "auto" a 403
    /// steps down the chain and probes again, so the status names both the refused model and the
    /// one in use.
    /// </summary>
    public static async Task<ChatModelCheck> CheckChatModelAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        // The check IS the re-check: a mark is not forever, or a model enabled in the console after
        // the refusal would never be noticed (spec §6.1).
        Prefs.ClearBlockedChatModels();

        var before = Prefs.ActiveChatModel;
        var (available, listError) = await TryListModelsAsync(apiKey, cancellationToken);
        if (available.Count == 0)
            return new ChatModelCheck(false, null, listError ?? S.LlmNoModels, false);

        var resolved = ResolveAndCache(available, apiKey);
        if (resolved is null)
            return new ChatModelCheck(false, null, S.LlmNoModels, false);

        var fingerprint = ChatModels.KeyFingerprint(apiKey);
        string? refused = null;
        for (int left = ChatModels.PriorityChain.Length; ; left--)
        {
            var (status, problem) = await ProbeChatModelAsync(apiKey, resolved, cancellationToken);
            if (status == 403 && Prefs.ChatModel == ChatModels.Auto && left > 0)
            {
                Prefs.MarkChatModelBlocked(resolved, fingerprint);
                refused ??= resolved;
                var next = ChatModels.Resolve(available, ChatModels.Auto, Prefs.BlockedChatModels(fingerprint));
                if (next is not null && next != resolved)
                {
                    Log.Info($"chat check: {resolved} refused (403) — trying {next}");
                    Prefs.ResolvedChatModel = next;
                    resolved = next;
                    continue;
                }
            }

            if (status == 403 && refused is not null)
                problem = string.Format(S.LlmBlockedFmt, refused);   // nowhere left: name the head
            return new ChatModelCheck(problem is null, resolved, problem, resolved != before,
                problem is null ? refused : null);
        }
    }

    /// <summary>One light request to a chat model; Status is the HTTP code, 0 when none came back.</summary>
    private static async Task<(int Status, string? Problem)> ProbeChatModelAsync(
        string apiKey, string model, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ChatProbeTimeout);
        try
        {
            using var request = BuildChatRequest(apiKey, "ok", maxCompletionTokens: 8, model);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var code = response.IsSuccessStatusCode ? (int)response.StatusCode : await RefusalStatusAsync(response, cts.Token);
            return (code, code switch
            {
                >= 200 and < 300 => null,
                403 => string.Format(S.LlmBlockedFmt, model),
                RetiredStatus => string.Format(S.LlmNotFoundFmt, model),
                401 => S.KeyValidRejected,
                _ => $"HTTP {code}",
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (0, S.KeyValidTimeout);
        }
        catch (HttpRequestException ex)
        {
            return (0, Net.Describe(ex, ChatEndpoint));
        }
    }

    private static HttpRequestMessage BuildChatRequest(string apiKey, string userContent, int maxCompletionTokens, string model)
    {
        var payload = new
        {
            model,
            temperature = 0,
            max_completion_tokens = maxCompletionTokens,
            messages = new[] { new { role = "user", content = userContent } },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, ChatEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    /// <summary>Validates a key against the models endpoint (spec §2): 200 → valid, 401 → rejected, else HTTP N.</summary>
    public static async Task<KeyValidation> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ValidateTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new KeyValidation(KeyStatus.Valid, S.KeyValidValid),
                HttpStatusCode.Unauthorized => new KeyValidation(KeyStatus.Rejected, S.KeyValidRejected),
                var code => new KeyValidation(KeyStatus.Error, $"HTTP {(int)code}"),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new KeyValidation(KeyStatus.Error, S.KeyValidTimeout);
        }
        catch (HttpRequestException ex)
        {
            // Through the shared translation, not ex.Message: on macOS this exact line was the one
            // showing a raw error code while dictation named the proxy properly (spec §9.5).
            return new KeyValidation(KeyStatus.Error, Net.Describe(ex, ModelsEndpoint));
        }
    }

    private static string MapError(HttpStatusCode status, string body, string model) => (int)status switch
    {
        401 => S.GroqRejected,
        // A model can be disabled for the user's Groq org — same fix as for the chat model (§6.1).
        403 => string.Format(S.SttBlockedFmt, model),
        413 => S.GroqTooLong,
        429 => S.GroqRateLimit,
        var code => string.Format(S.GroqReturnedFmt, code, Trim(body)),
    };

    private static string Trim(string body)
    {
        body = (body ?? string.Empty).Trim();
        return body.Length <= 200 ? body : body[..200];
    }
}
