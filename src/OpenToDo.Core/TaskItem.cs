namespace OpenToDo.Core;

public sealed record TaskItem(
    string Id,
    string Title,
    string? Description = null,
    bool IsCompleted = false,
    DateTimeOffset? DueDate = null,
    IReadOnlyList<DateTimeOffset>? CompletionHistory = null);
