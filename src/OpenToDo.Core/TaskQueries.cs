namespace OpenToDo.Core;

public static class TaskLimits
{
    public const int MaxTitleLength = 120;
    public const int MaxDescriptionLength = 2000;
}

/// <summary>Pure, testable rules for ordering, filtering and completing tasks.</summary>
public static class TaskQueries
{
    /// <summary>
    /// Active tasks first; dated tasks (soonest first) before undated ones; then oldest-created first.
    /// </summary>
    public static IEnumerable<TaskItem> Sort(IEnumerable<TaskItem> tasks) => tasks
        .OrderBy(t => t.IsCompleted)
        .ThenBy(t => t.DueDate is null)
        .ThenBy(t => t.DueDate?.Date)
        .ThenBy(t => t.CreatedAt ?? DateTimeOffset.MinValue)
        .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
        .ThenBy(t => t.Id, StringComparer.Ordinal);

    // A due date is a calendar date: compare using the date in the offset it was stored with,
    // so travelling across time zones never shifts a task's due day.
    public static bool IsOverdue(TaskItem task, DateTime today) =>
        !task.IsCompleted && task.DueDate is { } due && due.Date < today.Date;

    public static bool IsDueToday(TaskItem task, DateTime today) =>
        !task.IsCompleted && task.DueDate is { } due && due.Date == today.Date;

    public static DateTime? LastCompletion(TaskItem task)
    {
        var history = task.CompletionHistory;
        return history is null || history.Count == 0 ? null : history.Max().LocalDateTime;
    }

    public static TaskItem Complete(TaskItem task, DateTimeOffset now)
    {
        if (task.IsCompleted)
            return task;

        var history = (task.CompletionHistory ?? Array.Empty<DateTimeOffset>()).ToList();
        history.Add(now);
        return task with { IsCompleted = true, CompletionHistory = history };
    }

    /// <summary>Undo a completion: the most recent completion is removed so restoring a task does not inflate analytics.</summary>
    public static TaskItem Restore(TaskItem task)
    {
        if (!task.IsCompleted)
            return task;

        var history = (task.CompletionHistory ?? Array.Empty<DateTimeOffset>()).ToList();
        if (history.Count > 0)
            history.Remove(history.Max());

        return task with { IsCompleted = false, CompletionHistory = history };
    }

    public static bool Matches(TaskItem task, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        var q = query.Trim();
        return task.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (task.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
