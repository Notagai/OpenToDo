using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenToDo.Plugins.Ai;

public sealed class AiClient
{
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public AiClient(HttpClient? httpClient = null) => _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<IReadOnlyList<AiModel>> GetModelsAsync(AiProvider provider, string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Enter an API key first.");
        using var request = new HttpRequestMessage(HttpMethod.Get, GetModelsUri(provider)); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken); var body = await response.Content.ReadAsStringAsync(cancellationToken); EnsureSuccess(response, body);
        using var document = JsonDocument.Parse(body); var models = new List<AiModel>();
        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var item in data.EnumerateArray()) if (item.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString())) models.Add(new AiModel(id.GetString()!, item.TryGetProperty("owned_by", out var owner) ? owner.GetString() : null));
        return models.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<string> GenerateAsync(AiProvider provider, string apiKey, string model, string prompt, double temperature, int maxOutputTokens, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Enter an API key first."); if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("Select a model first."); if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("Enter a prompt.");
        return provider == AiProvider.OpenAI ? await GenerateOpenAiAsync(apiKey, model, prompt, maxOutputTokens, cancellationToken) : await GenerateChatCompletionAsync(provider, apiKey, model, prompt, temperature, maxOutputTokens, cancellationToken);
    }

    private async Task<string> GenerateOpenAiAsync(string apiKey, string model, string prompt, int maxOutputTokens, CancellationToken cancellationToken)
    {
        var payload = new { model, input = prompt, max_output_tokens = maxOutputTokens, store = false };
        using var response = await SendJsonAsync(HttpMethod.Post, "https://api.openai.com/v1/responses", apiKey, payload, cancellationToken); var body = await response.Content.ReadAsStringAsync(cancellationToken); EnsureSuccess(response, body);
        using var document = JsonDocument.Parse(body); if (document.RootElement.TryGetProperty("output_text", out var outputText)) return outputText.GetString() ?? string.Empty;
        var parts = new List<string>(); if (document.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array) foreach (var item in output.EnumerateArray()) if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array) foreach (var contentItem in content.EnumerateArray()) if (contentItem.TryGetProperty("text", out var text)) parts.Add(text.GetString() ?? string.Empty);
        return string.Join(Environment.NewLine, parts);
    }

    private async Task<string> GenerateChatCompletionAsync(AiProvider provider, string apiKey, string model, string prompt, double temperature, int maxOutputTokens, CancellationToken cancellationToken)
    {
        object payload = provider == AiProvider.Groq ? new { model, messages = new[] { new { role = "user", content = prompt } }, temperature, max_completion_tokens = maxOutputTokens } : new { model, messages = new[] { new { role = "user", content = prompt } }, temperature, max_tokens = maxOutputTokens };
        using var response = await SendJsonAsync(HttpMethod.Post, GetChatUri(provider), apiKey, payload, cancellationToken); var body = await response.Content.ReadAsStringAsync(cancellationToken); EnsureSuccess(response, body);
        using var document = JsonDocument.Parse(body); return document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    private async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string uri, string apiKey, object payload, CancellationToken cancellationToken) { var request = new HttpRequestMessage(method, uri); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey); request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"); return await _httpClient.SendAsync(request, cancellationToken); }
    private static string GetModelsUri(AiProvider provider) => provider switch { AiProvider.OpenAI => "https://api.openai.com/v1/models", AiProvider.Groq => "https://api.groq.com/openai/v1/models", AiProvider.OpenRouter => "https://openrouter.ai/api/v1/models", _ => throw new ArgumentOutOfRangeException(nameof(provider)) };
    private static string GetChatUri(AiProvider provider) => provider switch { AiProvider.Groq => "https://api.groq.com/openai/v1/chat/completions", AiProvider.OpenRouter => "https://openrouter.ai/api/v1/chat/completions", _ => throw new ArgumentOutOfRangeException(nameof(provider)) };
    private static void EnsureSuccess(HttpResponseMessage response, string body) { if (response.IsSuccessStatusCode) return; var detail = body.Length > 500 ? body[..500] : body; throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {detail}"); }
}