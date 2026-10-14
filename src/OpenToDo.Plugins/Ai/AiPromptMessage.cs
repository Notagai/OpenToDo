namespace OpenToDo.Plugins.Ai;

/// <summary>One message in a chat conversation. Role is "system", "user" or "assistant".</summary>
public sealed record AiPromptMessage(string Role, string Content);
