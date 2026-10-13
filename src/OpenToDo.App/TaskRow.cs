using OpenToDo.Core;

namespace OpenToDo.App;

/// <summary>Display model for a task: all formatting happens once here, not in XAML.</summary>
public sealed record TaskRow(
    TaskItem Task,
    string Title,
    string Description,
    bool HasDescription,
    string DueText,
    bool HasDue,
    bool IsOverdue,
    bool IsDueToday,
    string CompletedText)
{
    public static TaskRow From(TaskItem task, DateTime today)
    {
        var description = string.Join(' ', (task.Description ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var hasDue = !task.IsCompleted && task.DueDate is not null;
        var overdue = TaskQueries.IsOverdue(task, today);
        var dueToday = TaskQueries.IsDueToday(task, today);
        var dueText = string.Empty;

        if (hasDue)
        {
            var date = task.DueDate!.Value.Date;
            var dayText = date.ToString(date.Year == today.Year ? "MMM d" : "MMM d, yyyy");
            dueText =
                date == today ? "Today" :
                date == today.AddDays(1) ? "Tomorrow" :
                date == today.AddDays(-1) ? "Yesterday" :
                overdue ? $"Overdue · {dayText}" : dayText;
        }

        var last = TaskQueries.LastCompletion(task);
        var completed = last is { } l ? $"Done {l:MMM d}, {l:t}" : "Done";

        return new TaskRow(task, task.Title, description, description.Length > 0, dueText, hasDue, overdue, dueToday, completed);
    }
}
