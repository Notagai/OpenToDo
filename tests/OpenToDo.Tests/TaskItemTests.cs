using OpenToDo.Core;
using Xunit;

namespace OpenToDo.Tests;

public class TaskItemTests
{
    private static readonly DateTime Today = new(2026, 10, 14); // a Wednesday

    [Fact]
    public void NewTask_IsNotCompleted() => Assert.False(new TaskItem("test", "First task").IsCompleted);

    [Fact]
    public void Sort_PutsDatedTasksBeforeUndated_AndActiveBeforeCompleted()
    {
        var undated = new TaskItem("a", "Undated");
        var late = new TaskItem("b", "Late", DueDate: new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        var soon = new TaskItem("c", "Soon", DueDate: new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));
        var done = new TaskItem("d", "Done", IsCompleted: true, DueDate: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var order = TaskQueries.Sort(new[] { undated, done, late, soon }).Select(t => t.Id).ToArray();

        Assert.Equal(new[] { "c", "b", "a", "d" }, order);
    }

    [Fact]
    public void Overdue_IgnoresCompleted_AndComparesCalendarDates()
    {
        var due = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.FromHours(-7));
        Assert.True(TaskQueries.IsOverdue(new TaskItem("a", "x", DueDate: due), Today));
        Assert.False(TaskQueries.IsOverdue(new TaskItem("a", "x", IsCompleted: true, DueDate: due), Today));
        Assert.True(TaskQueries.IsDueToday(new TaskItem("a", "x", DueDate: new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.FromHours(9))), Today));
    }

    [Fact]
    public void Restore_RemovesLatestCompletion_SoStatsAreNotInflated()
    {
        var task = TaskQueries.Complete(new TaskItem("a", "x"), DateTimeOffset.Now);
        Assert.True(task.IsCompleted);
        Assert.Single(task.CompletionHistory!);

        var restored = TaskQueries.Restore(task);
        Assert.False(restored.IsCompleted);
        Assert.Empty(restored.CompletionHistory!);
    }

    [Fact]
    public void Matches_SearchesTitleAndDescription_CaseInsensitively()
    {
        var task = new TaskItem("a", "Buy Milk", "from the corner shop");
        Assert.True(TaskQueries.Matches(task, "milk"));
        Assert.True(TaskQueries.Matches(task, "CORNER"));
        Assert.True(TaskQueries.Matches(task, "  "));
        Assert.False(TaskQueries.Matches(task, "bread"));
    }
}
