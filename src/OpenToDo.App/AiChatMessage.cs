using Avalonia.Layout;

namespace OpenToDo.App;

/// <summary>One bubble in the AI sidebar. User messages align right, assistant messages align left.</summary>
public sealed record AiChatMessage(string Text, bool IsUser)
{
    public HorizontalAlignment Alignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    /// <summary>Action results from the assistant are shown as compact system notes.</summary>
    public bool IsNote { get; init; }

    public static AiChatMessage User(string text) => new(text, true);
    public static AiChatMessage Assistant(string text) => new(text, false);
    public static AiChatMessage Note(string text) => new(text, false) { IsNote = true };
}
