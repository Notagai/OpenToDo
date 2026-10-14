using System.Globalization;

namespace OpenToDo.Core;

/// <summary>The kinds of change an assistant (or any automation) can ask for.</summary>
public enum TaskCommandKind
{
    Add,
    Complete,
    Reopen,
    Update,
    Delete
}

/// <summary>
/// A single requested change. Fields are optional so one shape covers every operation:
/// <see cref="Id"/> or <see cref="Match"/> locate an existing task, the rest carry new values.
/// </summary>
public sealed record TaskCommand(
    TaskCommandKind Kind,
    string? Id = null,
    string? Title = null,
    string? Notes = null,
    string? Due = null,
    string? Match = null);

/// <summary>
/// The outcome of planning a <see cref="TaskCommand"/>. When <see cref="Item"/> is set it should be
/// persisted; when <see cref="DeleteId"/> is set that task should be removed. The planner never
/// touches disk itself, which keeps it pure and easy to test.
/// </summary>
public sealed record TaskCommandResult(bool Success, string Message, TaskItem? Item = null, string? DeleteId = null)
{
    public static TaskCommandResult Failed(string message) => new(false, message);
}

/// <summary>Turns a <see cref="TaskCommand"/> into the concrete task to save or delete.</summary>
public static class TaskCommandPlanner
{
    public static TaskCommandResult Apply(
        TaskCommand command,
        IReadOnlyList<TaskItem> tasks,
        DateTime today,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        tasks ??= Array.Empty<TaskItem>();

        switch (command.Kind)
        {
            case TaskCommandKind.Add:
            {
                var title = Clamp(command.Title, TaskLimits.MaxTitleLength);
                if (string.IsNullOrWhiteSpace(title))
                    return TaskCommandResult.Failed("I need a title before I can add that task.");

                var task = new TaskItem(
                    Guid.NewGuid().ToString("N"),
                    title,
                    Clamp(command.Notes, TaskLimits.MaxDescriptionLength),
                    IsCompleted: false,
                    DueDate: ParseDue(command.Due, today),
                    CompletionHistory: Array.Empty<DateTimeOffset>(),
                    CreatedAt: now);
                return new TaskCommandResult(true, $"Added “{title}”.", task);
            }

            case TaskCommandKind.Complete:
            {
                var match = Find(command, tasks);
                if (match is null)
                    return TaskCommandResult.Failed(MissingMessage(command));

                if (match.IsCompleted)
                    return new TaskCommandResult(true, $"“{match.Title}” is already done.");

                var done = TaskQueries.Complete(match, now);
                return new TaskCommandResult(true, $"Marked “{match.Title}” as done.", done);
            }

            case TaskCommandKind.Reopen:
            {
                var match = Find(command, tasks);
                if (match is null)
                    return TaskCommandResult.Failed(MissingMessage(command));

                if (!match.IsCompleted)
                    return new TaskCommandResult(true, $"“{match.Title}” is already open.");

                var reopened = TaskQueries.Restore(match);
                return new TaskCommandResult(true, $"Reopened “{match.Title}”.", reopened);
            }

            case TaskCommandKind.Update:
            {
                var match = Find(command, tasks);
                if (match is null)
                    return TaskCommandResult.Failed(MissingMessage(command));

                string title;
                if (command.Title is null)
                {
                    title = match.Title;
                }
                else
                {
                    var candidate = Clamp(command.Title, TaskLimits.MaxTitleLength);
                    if (string.IsNullOrWhiteSpace(candidate))
                        return TaskCommandResult.Failed("A task can't have an empty title.");
                    title = candidate;
                }

                var notes = command.Notes is null ? match.Description : Clamp(command.Notes, TaskLimits.MaxDescriptionLength);
                var due = command.Due is null ? match.DueDate : ParseDue(command.Due, today);

                var updated = match with { Title = title, Description = notes, DueDate = due };
                return new TaskCommandResult(true, $"Updated “{updated.Title}”.", updated);
            }

            case TaskCommandKind.Delete:
            {
                var match = Find(command, tasks);
                if (match is null)
                    return TaskCommandResult.Failed(MissingMessage(command));

                return new TaskCommandResult(true, $"Deleted “{match.Title}”.", DeleteId: match.Id);
            }

            default:
                return TaskCommandResult.Failed("I didn't understand that request.");
        }
    }

    private static TaskItem? Find(TaskCommand command, IReadOnlyList<TaskItem> tasks)
    {
        if (!string.IsNullOrWhiteSpace(command.Id))
        {
            var id = command.Id.Trim();
            var byId = tasks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
                return byId;
        }

        var needle = (command.Match ?? command.Title)?.Trim();
        if (string.IsNullOrWhiteSpace(needle))
            return null;

        var matches = tasks
            .Where(t => t.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Prefer something that still needs doing, so "finish the report" hits the open task.
        return matches.FirstOrDefault(t => !t.IsCompleted) ?? matches.FirstOrDefault();
    }

    private static string MissingMessage(TaskCommand command)
    {
        var what = (command.Match ?? command.Title ?? command.Id ?? "").Trim();
        return what.Length == 0
            ? "Which task did you mean?"
            : $"I couldn't find a task matching “{what}”.";
    }

    private static string? Clamp(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    /// <summary>Parses flexible due text ("today", "2026-10-20", "none") into a local calendar date.</summary>
    internal static DateTimeOffset? ParseDue(string? raw, DateTime today)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
            value == "-")
            return null;

        if (value.Equals("today", StringComparison.OrdinalIgnoreCase))
            value = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        else if (value.Equals("tomorrow", StringComparison.OrdinalIgnoreCase))
            value = today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        else if (value.Equals("yesterday", StringComparison.OrdinalIgnoreCase))
            value = today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return null;

        var local = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
