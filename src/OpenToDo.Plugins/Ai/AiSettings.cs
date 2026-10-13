using System.Text.Json.Serialization;

namespace OpenToDo.Plugins.Ai;

public sealed class AiSettings
{
    public const int MaxTokenLimit = 131072;

    [JsonIgnore]
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string OpenAiModel { get; set; } = string.Empty;

    [JsonIgnore]
    public string GroqApiKey { get; set; } = string.Empty;
    public string GroqModel { get; set; } = string.Empty;

    [JsonIgnore]
    public string OpenRouterApiKey { get; set; } = string.Empty;
    public string OpenRouterModel { get; set; } = string.Empty;

    public double Temperature { get; set; } = 0.7;
    public int MaxOutputTokens { get; set; } = 1024;

    /// <summary>Repairs hand-edited or corrupt numeric values and null strings.</summary>
    public void Normalize()
    {
        Temperature = double.IsNaN(Temperature) || double.IsInfinity(Temperature) ? 0.7 : Math.Clamp(Temperature, 0, 2);
        MaxOutputTokens = Math.Clamp(MaxOutputTokens, 1, MaxTokenLimit);
        OpenAiModel ??= string.Empty;
        GroqModel ??= string.Empty;
        OpenRouterModel ??= string.Empty;
    }
}
