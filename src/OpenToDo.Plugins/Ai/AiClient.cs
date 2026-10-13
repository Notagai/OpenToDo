using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenToDo.Plugins.Ai;

public sealed class AiClient
{
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AiClient(HttpClient? httpClient = null) =>
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<IReadOnlyList<AiModel>> GetModelsAsync(
        AiProvider provider,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        apiKey = ValidateKey(apiKey);

        using var request = new HttpRequestMessage(HttpMethod.Get, GetModelsUri(provider));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var body = await ExecuteAsync(request, cancellationToken);
        using var document = ParseBody(body);
        var models = new List<AiModel>();

        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String)
                    continue;

                var modelId = id.GetString();
                if (string.IsNullOrWhiteSpace(modelId))
                    continue;

                var owner = item.TryGetProperty("owned_by", out var ownerElement) &&
                            ownerElement.ValueKind == JsonValueKind.String
                    ? ownerElement.GetString()
                    : null;

                models.Add(new AiModel(modelId, owner));
            }
        }

        var distinct = models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        // Hide embedding / speech / image models that can't answer a text prompt (fall back to everything if that empties the list).
        var chatCapable = distinct.Where(m => IsLikelyTextModel(provider, m.Id)).ToArray();

        return (chatCapable.Length > 0 ? chatCapable : distinct)
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string> GenerateAsync(
        AiProvider provider,
        string apiKey,
        string model,
        string prompt,
        double temperature,
        int maxOutputTokens,
        CancellationToken cancellationToken = default)
    {
        apiKey = ValidateKey(apiKey);
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Select a model first.");
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Enter a prompt.");

        temperature = double.IsNaN(temperature) ? 0.7 : Math.Clamp(temperature, 0, 2);
        maxOutputTokens = Math.Clamp(maxOutputTokens, 1, AiSettings.MaxTokenLimit);

        return provider == AiProvider.OpenAI
            ? await GenerateOpenAiAsync(apiKey, model.Trim(), prompt, maxOutputTokens, cancellationToken)
            : await GenerateChatCompletionAsync(provider, apiKey, model.Trim(), prompt, temperature, maxOutputTokens, cancellationToken);
    }

    private async Task<string> GenerateOpenAiAsync(string apiKey, string model, string prompt, int maxOutputTokens, CancellationToken cancellationToken)
    {
        var payload = new
        {
            model,
            input = prompt,
            // The Responses API rejects values below 16.
            max_output_tokens = Math.Max(16, maxOutputTokens),
            store = false
        };

        using var request = BuildJsonRequest("https://api.openai.com/v1/responses", apiKey, payload);
        var body = await ExecuteAsync(request, cancellationToken);
        using var document = ParseBody(body);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("output_text", out var outputText) &&
            outputText.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(outputText.GetString()))
            return outputText.GetString()!;

        var parts = new List<string>();

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("output", out var output) &&
            output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var contentItem in content.EnumerateArray())
                {
                    if (contentItem.ValueKind == JsonValueKind.Object &&
                        contentItem.TryGetProperty("text", out var text) &&
                        text.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(text.GetString()))
                        parts.Add(text.GetString()!);
                }
            }
        }

        if (parts.Count > 0)
            return string.Join(Environment.NewLine, parts);

        var reason = root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("incomplete_details", out var details) &&
                     details.ValueKind == JsonValueKind.Object &&
                     details.TryGetProperty("reason", out var r) &&
                     r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : null;

        throw new InvalidOperationException(reason == "max_output_tokens"
            ? "The model ran out of output tokens before writing any text. Raise \"Max output tokens\" and try again."
            : "The model returned no text.");
    }

    private async Task<string> GenerateChatCompletionAsync(
        AiProvider provider, string apiKey, string model, string prompt, double temperature, int maxOutputTokens, CancellationToken cancellationToken)
    {
        object payload = provider == AiProvider.Groq
            ? new
            {
                model,
                messages = new[] { new { role = "user", content = prompt } },
                temperature,
                max_completion_tokens = maxOutputTokens
            }
            : new
            {
                model,
                messages = new[] { new { role = "user", content = prompt } },
                temperature,
                max_tokens = maxOutputTokens
            };

        using var request = BuildJsonRequest(GetChatUri(provider), apiKey, payload);
        var body = await ExecuteAsync(request, cancellationToken);
        using var document = ParseBody(body);
        var root = document.RootElement;

        // Some gateways (OpenRouter) answer HTTP 200 with an error object in the body.
        if (root.ValueKind == JsonValueKind.Object &&
            !root.TryGetProperty("choices", out _) &&
            root.TryGetProperty("error", out var error))
            throw new InvalidOperationException(ExtractErrorMessage(error) ?? "The provider reported an error.");

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
            throw new InvalidOperationException("The provider returned no choices.");

        var choice = choices[0];
        string? finish = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() : null;

        if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The provider returned an unexpected response.");

        var text = ReadMessageContent(message);
        if (!string.IsNullOrWhiteSpace(text))
            return text;

        throw new InvalidOperationException(finish == "length"
            ? "The model ran out of output tokens before writing any text. Raise \"Max output tokens\" and try again."
            : "The model returned no text.");
    }

    private static string ReadMessageContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
            return string.Empty;

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        // Content may be an array of parts: [{ "type": "text", "text": "..." }]
        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
                if (part.ValueKind == JsonValueKind.Object &&
                    part.TryGetProperty("text", out var t) &&
                    t.ValueKind == JsonValueKind.String)
                    sb.Append(t.GetString());
            return sb.ToString();
        }

        return string.Empty;
    }

    private static HttpRequestMessage BuildJsonRequest(string uri, string apiKey, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>Sends the request, returns the body, and turns every failure into a readable message.</summary>
    private async Task<string> ExecuteAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(DescribeError(response, body));
            return body;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The request timed out. Check your connection and try again.");
        }
    }

    private static JsonDocument ParseBody(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The provider returned a response that wasn't valid JSON (a proxy or captive portal may be interfering).");
        }
    }

    private static string ValidateKey(string? apiKey)
    {
        var key = apiKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
            throw new InvalidOperationException("Enter an API key first.");
        if (key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new InvalidOperationException("The API key contains spaces or line breaks. Re-paste it.");
        return key;
    }

    private static bool IsLikelyTextModel(AiProvider provider, string id)
    {
        var s = id.ToLowerInvariant();
        var blocked = provider switch
        {
            AiProvider.OpenAI => new[] { "embedding", "whisper", "tts", "dall-e", "moderation", "davinci", "babbage", "transcribe", "realtime", "audio", "image", "sora" },
            AiProvider.Groq => new[] { "whisper", "tts", "guard", "playai", "embed" },
            _ => Array.Empty<string>()
        };
        return !blocked.Any(b => s.Contains(b));
    }

    private static string GetModelsUri(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => "https://api.openai.com/v1/models",
        AiProvider.Groq => "https://api.groq.com/openai/v1/models",
        AiProvider.OpenRouter => "https://openrouter.ai/api/v1/models",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static string GetChatUri(AiProvider provider) => provider switch
    {
        AiProvider.Groq => "https://api.groq.com/openai/v1/chat/completions",
        AiProvider.OpenRouter => "https://openrouter.ai/api/v1/chat/completions",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static string? ExtractErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
            return error.GetString();
        if (error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("message", out var m) &&
            m.ValueKind == JsonValueKind.String)
            return m.GetString();
        return null;
    }

    private static string DescribeError(HttpResponseMessage response, string body)
    {
        string? detail = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var error))
                detail = ExtractErrorMessage(error);
        }
        catch (JsonException) { }

        detail = string.IsNullOrWhiteSpace(detail) ? body.Trim() : detail.Trim();
        if (detail.Length > 300)
            detail = detail[..300] + "…";

        var hint = (int)response.StatusCode switch
        {
            401 or 403 => " Check that the API key is correct and has access.",
            429 => " You're being rate limited or are out of quota.",
            _ => string.Empty
        };

        return $"{(int)response.StatusCode} {response.ReasonPhrase}: {detail}{hint}".Replace(": :", ":").TrimEnd();
    }
}
