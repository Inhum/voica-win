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
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

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
            throw new GroqException(string.Format(S.GroqNetworkFmt, ex.Message), isNetworkError: true);
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
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ValidateTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!response.IsSuccessStatusCode) return Array.Empty<string>();

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return Array.Empty<string>();

            var ids = new List<string>();
            foreach (var item in data.EnumerateArray())
                if (item.TryGetProperty("id", out var idEl) && idEl.GetString() is { } id)
                    ids.Add(id);
            return ids;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Corrects mangled vocabulary terms via the Groq chat model (spec §6.1). Fail-open: on any
    /// error/timeout/non-2xx/empty answer the ORIGINAL text is returned — post-processing never
    /// blocks dictation. A 404 (model retired by Groq) re-resolves the model and retries once, so
    /// the app heals itself without a release.
    /// </summary>
    /// <param name="notify">
    /// Receives a user-facing message when the model is blocked for the org (403). Unlike a 404,
    /// this cannot heal itself — the model is alive, the access is missing — so staying silent
    /// would make correction fail on every dictation with no explanation (spec §6.1).
    /// </param>
    public static async Task<string> PostProcessAsync(string text, string apiKey, string? vocabulary,
        Action<string>? notify = null, CancellationToken cancellationToken = default)
    {
        var prompt = PostProcessPromptText(text, vocabulary);
        if (prompt is null) return text;

        var active = Prefs.ActiveChatModel;
        var (result, status) = await TryPostProcessAsync(text, apiKey, prompt, active, cancellationToken);
        if (status == 403) { ReportBlocked(active, notify); return result; }
        if (status != 404) return result;

        // Model gone → refresh the resolution and retry once with whatever is available now.
        Log.Info("chat model returned 404 — re-resolving");
        var resolved = await ResolveAndCacheChatModelAsync(apiKey, cancellationToken);
        if (resolved is null || resolved == active) return text;

        var (retry, retryStatus) = await TryPostProcessAsync(text, apiKey, prompt, resolved, cancellationToken);
        if (retryStatus == 403) ReportBlocked(resolved, notify);
        return retry;
    }

    /// <summary>Models already reported as blocked — at most one notice per model per session.</summary>
    private static readonly HashSet<string> BlockedReported = new(StringComparer.OrdinalIgnoreCase);

    private static void ReportBlocked(string model, Action<string>? notify)
    {
        lock (BlockedReported)
            if (!BlockedReported.Add(model)) return;

        Log.Error($"chat model {model} is blocked for this Groq org (403)");
        notify?.Invoke(string.Format(S.LlmBlockedFmt, model));
    }

    /// <summary>Runs one correction request; Status is the HTTP code, or 0 when we failed open.</summary>
    private static async Task<(string Text, int Status)> TryPostProcessAsync(
        string text, string apiKey, string prompt, string model, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(PostProcessTimeout);

        try
        {
            using var request = BuildChatRequest(apiKey, prompt, maxCompletionTokens: 4096, model);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!response.IsSuccessStatusCode)
                return (text, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(cts.Token);
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
    /// Silently drops an explicit choice that no longer exists. Null when nothing usable is available.
    /// </summary>
    public static async Task<string?> ResolveAndCacheChatModelAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var available = await ListModelsAsync(apiKey, cancellationToken);
        if (available.Count == 0) return null;

        if (ChatModels.ChoiceRetired(available, Prefs.ChatModel))
        {
            Log.Info($"chosen chat model '{Prefs.ChatModel}' is gone — falling back to auto");
            Prefs.ChatModel = ChatModels.Auto;
        }

        var resolved = ChatModels.Resolve(available, Prefs.ChatModel);
        if (resolved is not null) Prefs.ResolvedChatModel = resolved;
        return resolved;
    }

    /// <summary>Outcome of the chat-model check shown in Settings (spec §6.1 UX).</summary>
    public sealed record ChatModelCheck(bool Available, string? Model, string? Problem, bool Switched);

    /// <summary>
    /// Refreshes the model list, re-resolves (self-healing), and probes the resolved model
    /// (spec §6.1): distinguishes 403 (blocked for the org) from other failures.
    /// </summary>
    public static async Task<ChatModelCheck> CheckChatModelAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var before = Prefs.ActiveChatModel;
        var available = await ListModelsAsync(apiKey, cancellationToken);
        if (available.Count == 0)
            return new ChatModelCheck(false, null, S.LlmNoModels, false);

        var resolved = await ResolveAndCacheChatModelAsync(apiKey, cancellationToken);
        if (resolved is null)
            return new ChatModelCheck(false, null, S.LlmNoModels, false);

        bool switched = resolved != before;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ChatProbeTimeout);
        try
        {
            using var request = BuildChatRequest(apiKey, "ok", maxCompletionTokens: 8, resolved);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var problem = (int)response.StatusCode switch
            {
                >= 200 and < 300 => null,
                403 => string.Format(S.LlmBlockedFmt, resolved),
                404 => string.Format(S.LlmNotFoundFmt, resolved),
                401 => S.KeyValidRejected,
                var code => $"HTTP {code}",
            };
            return new ChatModelCheck(problem is null, resolved, problem, switched);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ChatModelCheck(false, resolved, S.KeyValidTimeout, switched);
        }
        catch (HttpRequestException ex)
        {
            return new ChatModelCheck(false, resolved, ex.Message, switched);
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
            return new KeyValidation(KeyStatus.Error, ex.Message);
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
