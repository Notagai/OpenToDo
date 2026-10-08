namespace OpenToDo.Plugins.Ai;

public sealed class AiSettings
{
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string OpenAiModel { get; set; } = string.Empty;

    public string GroqApiKey { get; set; } = string.Empty;
    public string GroqModel { get; set; } = string.Empty;

    public string OpenRouterApiKey { get; set; } = string.Empty;
    public string OpenRouterModel { get; set; } = string.Empty;

    public double Temperature { get; set; } = 0.7;
    public int MaxOutputTokens { get; set; } = 1024;
}
