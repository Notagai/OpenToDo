using System.Text.Json;
using OpenToDo.Core;

namespace OpenToDo.Plugins.Ai;

/// <summary>What the assistant said, plus any task changes it asked for.</summary>
public sealed record AiAssistantResponse(string Message, IReadOnlyList<TaskCommand> Commands);

/// <summary>
/// Provider-agnostic assistant. It uses a small, explicit JSON protocol so it works uniformly across
/// OpenAI, Groq and OpenRouter without provider-specific tool-calling APIs. The model is told to
/// answer with <c>{"message": "...", "actions": [...]}</c>; <see cref="Parse"/> turns that into
/// display text plus <see cref="TaskCommand"/>s the UI can apply and persist.
/// </summary>
public sealed class AiAssistant
{
    private const int MaxHistoryTurns = 12;
    private readonly AiClient _client;

    public AiAssistant(AiClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

    public const string Instructions =
        "You are the built-in assistant for OpenToDo, a local-first to-do app. " +
        "You help the user understand their progress and manage their tasks.\n\n" +
        "Reply with a single JSON object and nothing else, in exactly this shape:\n" +
        "{\"message\": \"<short, friendly reply to show the user>\", \"actions\": [ ... ]}\n\n" +
        "Each entry in \"actions\" is one of:\n" +
        "{\"op\":\"add\",\"title\":\"...\",\"notes\":\"...\",\"due\":\"YYYY-MM-DD\"}\n" +
        "{\"op\":\"complete\",\"match\":\"distinctive words from the task title\"}\n" +
        "{\"op\":\"reopen\",\"match\":\"...\"}\n" +
        "{\"op\":\"update\",\"match\":\"...\",\"title\":\"...\",\"notes\":\"...\",\"due\":\"YYYY-MM-DD\"}\n" +
        "{\"op\":\"delete\",\"match\":\"...\"}\n\n" +
        "Rules:\n" +
        "- Use \"match\" with a distinctive part of an existing task title; prefer \"id\" when the state block provides one.\n" +
        "- Only change what the user actually asked for. Never invent tasks or make things up.\n" +
        "- Use \"actions\": [] when the user just wants an answer.\n" +
        "- Due dates accept YYYY-MM-DD or the words \"today\", \"tomorrow\" or \"yesterday\"; use \"none\" to clear one.\n" +
        "- Keep \"message\" to one or two sentences and never mention the JSON itself.";

    public async Task<AiAssistantResponse> SendAsync(
        AiProvider provider,
        string apiKey,
        string model,
        double temperature,
        int maxOutputTokens,
        string taskContext,
        IReadOnlyList<AiPromptMessage> history,
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<AiPromptMessage>
        {
            new("system", Instructions + "\n\n" + taskContext)
        };

        if (history is { Count: > 0 })
        {
            var recent = history.Count > MaxHistoryTurns ? history.Skip(history.Count - MaxHistoryTurns) : history;
            messages.AddRange(recent.Where(h => !string.IsNullOrWhiteSpace(h.Content)));
        }

        messages.Add(new AiPromptMessage("user", userMessage));

        var raw = await _client.GenerateChatAsync(
            provider, apiKey, model, messages, temperature, maxOutputTokens, cancellationToken);

        return Parse(raw);
    }

    /// <summary>Tolerant parser: falls back to showing the raw text when the model didn't return JSON.</summary>
    public static AiAssistantResponse Parse(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
            return new AiAssistantResponse("I didn't get a response. Please try again.", Array.Empty<TaskCommand>());

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return new AiAssistantResponse(text, Array.Empty<TaskCommand>());

        try
        {
            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new AiAssistantResponse(text, Array.Empty<TaskCommand>());

            var message = ReadString(root, "message");
            var commands = new List<TaskCommand>();

            if (root.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
            {
                foreach (var action in actions.EnumerateArray())
                {
                    if (action.ValueKind != JsonValueKind.Object)
                        continue;

                    var kind = MapKind(ReadString(action, "op") ?? ReadString(action, "type"));
                    if (kind is null)
                        continue;

                    commands.Add(new TaskCommand(
                        kind.Value,
                        ReadString(action, "id"),
                        ReadString(action, "title"),
                        ReadString(action, "notes") ?? ReadString(action, "description"),
                        ReadString(action, "due") ?? ReadString(action, "dueDate"),
                        ReadString(action, "match") ?? ReadString(action, "task")));
                }
            }

            return new AiAssistantResponse(string.IsNullOrWhiteSpace(message) ? text : message!, commands);
        }
        catch (JsonException)
        {
            return new AiAssistantResponse(text, Array.Empty<TaskCommand>());
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static TaskCommandKind? MapKind(string? op) => op?.Trim().ToLowerInvariant() switch
    {
        "add" or "create" or "new" => TaskCommandKind.Add,
        "complete" or "done" or "finish" or "mark_done" or "markdown" => TaskCommandKind.Complete,
        "reopen" or "restore" or "undo" or "uncomplete" => TaskCommandKind.Reopen,
        "update" or "edit" or "modify" or "rename" => TaskCommandKind.Update,
        "delete" or "remove" => TaskCommandKind.Delete,
        _ => (TaskCommandKind?)null
    };
}
