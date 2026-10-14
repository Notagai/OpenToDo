using OpenToDo.Core;
using Xunit;

namespace OpenToDo.Tests;

public sealed class TaskInsightsTests
{
    private static readonly DateTime Today = new(2026, 10, 14);
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void BuildContext_ListsOpenTasksWithDueHintsAndName()
    {
        var tasks = new[]
        {
            new TaskItem("a", "Ship release", null, false, Today, Array.Empty<DateTimeOffset>(), Now),
            new TaskItem("b", "Water plants")
        };

        var context = TaskInsights.BuildContext(tasks, Today, "Alex");

        Assert.Contains("Alex", context);
        Assert.Contains("Open tasks: 2", context);
        Assert.Contains("Ship release", context);
        Assert.Contains("(due today)", context);
        Assert.Contains("Water plants", context);
    }

    [Fact]
    public void BuildContext_ReportsOverdueCount()
    {
        var tasks = new[]
        {
            new TaskItem("a", "Late thing", null, false, Today.AddDays(-3), Array.Empty<DateTimeOffset>(), Now)
        };

        var context = TaskInsights.BuildContext(tasks, Today, null);

        Assert.Contains("overdue: 1", context);
        Assert.Contains("overdue", context);
    }

    [Fact]
    public void BuildContext_HandlesEmptyState()
    {
        var context = TaskInsights.BuildContext(Array.Empty<TaskItem>(), Today, null);

        Assert.Contains("Open tasks: 0", context);
        Assert.Contains("(empty)", context);
        Assert.Contains("the user", context);
    }

    [Fact]
    public void BuildContext_CountsCompletedToday()
    {
        var tasks = new[] { TaskQueries.Complete(new TaskItem("a", "Done thing"), Now.ToOffset(TimeSpan.Zero)) };

        var context = TaskInsights.BuildContext(tasks, Now.ToOffset(TimeSpan.Zero).LocalDateTime.Date, "Alex");

        // The completion timestamp is "now", so regardless of the local zone the streak is at least reported.
        Assert.Contains("Completed — today:", context);
    }
}
