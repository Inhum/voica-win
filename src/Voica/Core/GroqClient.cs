using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
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
    public const string Model = "whisper-large-v3-turbo";
    // Spec §6.1. Groq periodically removes/renames models (qwen/qwen3-32b vanished → 404), so the
    // availability probe must distinguish 404 (update the app) from 403 (blocked in the Groq org).
    public const string PostProcessModel = "llama-3.3-70b-versatile";
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

    /// <summary>Transcribes an audio file. Throws <see cref="GroqException"/> with a user message on failure.</summary>
    public static async Task<TranscriptionResult> TranscribeAsync(
        string audioFilePath, string apiKey, string? vocabulary, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TranscribeTimeout);

        using var form = new MultipartFormDataContent();

        await using var fileStream = File.OpenRead(audioFilePath);
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileContent, "file", Path.GetFileName(audioFilePath));

        form.Add(new StringContent(Model), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("0"), "temperature");

        var prompt = PromptField(vocabulary);
        if (prompt is not null)
            form.Add(new StringContent(prompt), "prompt");

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
            throw new GroqException(MapError(response.StatusCode, body));

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("text", out var textEl))
                throw new GroqException(S.GroqNoText);

            var text = (textEl.GetString() ?? string.Empty).Trim();
            string? language = root.TryGetProperty("language", out var langEl) ? langEl.GetString() : null;
            double? duration = root.TryGetProperty("duration", out var durEl) && durEl.TryGetDouble(out var d) ? d : null;

            return new TranscriptionResult(text, language, duration);
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

    /// <summary>
    /// Corrects mangled vocabulary terms via the Groq chat model (spec §6.1). Fail-open: on any
    /// error/timeout/non-2xx/empty answer the ORIGINAL text is returned — post-processing never
    /// blocks dictation.
    /// </summary>
    public static async Task<string> PostProcessAsync(string text, string apiKey, string? vocabulary,
        CancellationToken cancellationToken = default)
    {
        var prompt = PostProcessPromptText(text, vocabulary);
        if (prompt is null) return text;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(PostProcessTimeout);

        try
        {
            using var request = BuildChatRequest(apiKey, prompt, maxCompletionTokens: 4096);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!response.IsSuccessStatusCode) return text;

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return text;
            var content = choices[0].GetProperty("message").GetProperty("content").GetString();
            var cleaned = (content ?? string.Empty).Trim();
            return cleaned.Length == 0 ? text : cleaned;
        }
        catch
        {
            return text;   // fail-open (spec §6.1)
        }
    }

    /// <summary>
    /// Probes the chat model's availability for AI correction (spec §6.1 UX). Null = available;
    /// otherwise a user-facing description (403 → "allow the model in the Groq console" hint).
    /// </summary>
    public static async Task<string?> ValidateChatModelAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ChatProbeTimeout);

        try
        {
            using var request = BuildChatRequest(apiKey, "ok", maxCompletionTokens: 8);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return (int)response.StatusCode switch
            {
                >= 200 and < 300 => null,
                403 => string.Format(S.LlmBlockedFmt, PostProcessModel),
                404 => string.Format(S.LlmNotFoundFmt, PostProcessModel),
                401 => S.KeyValidRejected,
                var code => $"HTTP {code}",
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return S.KeyValidTimeout;
        }
        catch (HttpRequestException ex)
        {
            return ex.Message;
        }
    }

    private static HttpRequestMessage BuildChatRequest(string apiKey, string userContent, int maxCompletionTokens)
    {
        var payload = new
        {
            model = PostProcessModel,
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

    private static string MapError(HttpStatusCode status, string body) => (int)status switch
    {
        401 => S.GroqRejected,
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
