namespace OpenToDo.Core;

public sealed record CompletionSummary(int Total, int Today, int Week, int Month, int Year, int Streak);

public static class TaskAnalytics
{
    /// <summary>Local calendar dates of every recorded completion, excluding any in the future (clock changes).</summary>
    public static IReadOnlyList<DateTime> CompletionDates(IEnumerable<TaskItem> tasks, DateTime today) =>
        tasks.SelectMany(t => t.CompletionHistory ?? Array.Empty<DateTimeOffset>())
             .Select(d => d.LocalDateTime.Date)
             .Where(d => d <= today.Date)
             .ToArray();

    public static DateTime StartOfWeek(DateTime date) =>
        date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7)); // Monday

    public static CompletionSummary Summarize(IEnumerable<TaskItem> tasks, DateTime today)
    {
        today = today.Date;
        var dates = CompletionDates(tasks, today);
        var weekStart = StartOfWeek(today);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var yearStart = new DateTime(today.Year, 1, 1);

        var days = new HashSet<DateTime>(dates);
        var cursor = days.Contains(today) ? today : today.AddDays(-1);
        var streak = 0;
        while (days.Contains(cursor))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }

        return new CompletionSummary(
            dates.Count,
            dates.Count(d => d == today),
            dates.Count(d => d >= weekStart),
            dates.Count(d => d >= monthStart),
            dates.Count(d => d >= yearStart),
            streak);
    }

    /// <summary>Completions per day for the <paramref name="days"/> days ending today (oldest first).</summary>
    public static int[] DailyCounts(IEnumerable<TaskItem> tasks, DateTime today, int days)
    {
        if (days <= 0)
            return Array.Empty<int>();

        today = today.Date;
        var perDay = CompletionDates(tasks, today)
            .GroupBy(d => d)
            .ToDictionary(g => g.Key, g => g.Count());

        return Enumerable.Range(0, days)
            .Select(i => today.AddDays(i - (days - 1)))
            .Select(d => perDay.TryGetValue(d, out var c) ? c : 0)
            .ToArray();
    }
}
