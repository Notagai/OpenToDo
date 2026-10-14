using System.Text;

namespace OpenToDo.Core;

/// <summary>
/// Builds the plain-text "state of the world" block handed to the AI so its replies and
/// actions are grounded in the user's real tasks. Pure and allocation-light so it is easy to test.
/// </summary>
public static class TaskInsights
{
    public const int MaxListedTasks = 40;

    public static string BuildContext(IEnumerable<TaskItem> tasks, DateTime today, string? username)
    {
        var all = tasks?.ToArray() ?? Array.Empty<TaskItem>();
        var summary = TaskAnalytics.Summarize(all, today);
        var ordered = TaskQueries.Sort(all).ToArray();
        var active = ordered.Where(t => !t.IsCompleted).ToArray();
        var overdue = active.Where(t => TaskQueries.IsOverdue(t, today)).ToArray();
        var dueToday = active.Where(t => TaskQueries.IsDueToday(t, today)).ToArray();

        var name = string.IsNullOrWhiteSpace(username) ? "the user" : username.Trim();
        var sb = new StringBuilder();
        sb.AppendLine("--- Current app state ---");
        sb.AppendLine($"User name: {name}");
        sb.AppendLine($"Today: {today:yyyy-MM-dd} ({today:dddd})");
        sb.AppendLine($"Open tasks: {active.Length} (overdue: {overdue.Length}, due today: {dueToday.Length})");
        sb.AppendLine($"Completed — today: {summary.Today}, this week: {summary.Week}, this month: {summary.Month}, this year: {summary.Year}, all-time: {summary.Total}");
        sb.AppendLine($"Current completion streak: {summary.Streak} day(s)");

        if (active.Length == 0)
        {
            sb.AppendLine("Open task list: (empty)");
        }
        else
        {
            sb.AppendLine($"Open task list (soonest due first, up to {MaxListedTasks}):");
            foreach (var task in active.Take(MaxListedTasks))
                sb.AppendLine(Describe(task, today));

            if (active.Length > MaxListedTasks)
                sb.AppendLine($"…and {active.Length - MaxListedTasks} more.");
        }

        var recent = ordered
            .Where(t => t.IsCompleted)
            .OrderByDescending(t => TaskQueries.LastCompletion(t) ?? DateTime.MinValue)
            .Take(8)
            .ToArray();
        if (recent.Length > 0)
        {
            sb.AppendLine("Recently completed:");
            foreach (var task in recent)
                sb.AppendLine($"- {task.Title}");
        }

        sb.AppendLine("--- End state ---");
        return sb.ToString().TrimEnd();
    }

    private static string Describe(TaskItem task, DateTime today)
    {
        var sb = new StringBuilder($"- [id: {task.Id}] {task.Title}");
        if (task.DueDate is { } due)
        {
            var day = due.Date;
            var label = day == today ? "today"
                : day == today.AddDays(1) ? "tomorrow"
                : day == today.AddDays(-1) ? "yesterday"
                : day.ToString("yyyy-MM-dd");
            sb.Append($" (due {label}{(day < today ? ", overdue" : "")})");
        }

        if (!string.IsNullOrWhiteSpace(task.Description))
            sb.Append($" — {Truncate(task.Description, 80)}");

        return sb.ToString();
    }

    private static string Truncate(string value, int max)
    {
        var flat = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > max ? flat[..max] + "…" : flat;
    }
}
