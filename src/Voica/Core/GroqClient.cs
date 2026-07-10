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
    public GroqException(string message) : base(message) { }
}

/// <summary>
/// Groq Speech-to-Text client (spec §2) and vocabulary prompt preparation (spec §6).
/// </summary>
public static class GroqClient
{
    public const string Model = "whisper-large-v3-turbo";
    public const int PromptCharBudget = 800;

    public static readonly Uri Endpoint = new("https://api.groq.com/openai/v1/audio/transcriptions");
    public static readonly Uri ModelsEndpoint = new("https://api.groq.com/openai/v1/models");

    private static readonly TimeSpan TranscribeTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ValidateTimeout = TimeSpan.FromSeconds(20);

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
            throw new GroqException(S.GroqTimeout);
        }
        catch (HttpRequestException ex)
        {
            throw new GroqException(string.Format(S.GroqNetworkFmt, ex.Message));
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
