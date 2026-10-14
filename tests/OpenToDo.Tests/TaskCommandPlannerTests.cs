using OpenToDo.Core;
using Xunit;

namespace OpenToDo.Tests;

public sealed class TaskCommandPlannerTests
{
    private static readonly DateTime Today = new(2026, 10, 14);
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

    private static TaskItem MakeTask(string id, string title, bool done = false) =>
        new(id, title, IsCompleted: done, CompletionHistory: Array.Empty<DateTimeOffset>(), CreatedAt: Now);

    [Fact]
    public void Add_CreatesTaskWithParsedDueDate()
    {
        var result = TaskCommandPlanner.Apply(
            new TaskCommand(TaskCommandKind.Add, Title: "Pay rent", Due: "tomorrow"),
            Array.Empty<TaskItem>(), Today, Now);

        Assert.True(result.Success);
        var item = Assert.IsType<TaskItem>(result.Item);
        Assert.Equal("Pay rent", item.Title);
        Assert.NotNull(item.DueDate);
        Assert.Equal(Today.AddDays(1), item.DueDate!.Value.Date);
    }

    [Fact]
    public void Add_WithoutTitle_Fails()
    {
        var result = TaskCommandPlanner.Apply(
            new TaskCommand(TaskCommandKind.Add, Title: "   "),
            Array.Empty<TaskItem>(), Today, Now);

        Assert.False(result.Success);
        Assert.Null(result.Item);
    }

    [Fact]
    public void Complete_MatchesByTitleFragment_AndRecordsCompletion()
    {
        var tasks = new[] { MakeTask("a", "Write the quarterly report"), MakeTask("b", "Water plants") };

        var result = TaskCommandPlanner.Apply(
            new TaskCommand(TaskCommandKind.Complete, Match: "quarterly"),
            tasks, Today, Now);

        Assert.True(result.Success);
        Assert.NotNull(result.Item);
        Assert.True(result.Item!.IsCompleted);
        Assert.Single(result.Item.CompletionHistory!);
    }

    [Fact]
    public void Complete_PrefersOpenTaskOverAnAlreadyDoneOne()
    {
        var tasks = new[]
        {
            MakeTask("done", "Renew passport", done: true),
            MakeTask("open", "Renew passport (urgent)")
        };

        var result = TaskCommandPlanner.Apply(
            new TaskCommand(TaskCommandKind.Complete, Match: "passport"),
            tasks, Today, Now);

        Assert.True(result.Success);
        Assert.Equal("open", result.Item!.Id);
    }

    [Fact]
    public void Complete_UnknownTask_Fails()
    {
        var result = TaskCommandPlanner.Apply(
            new TaskCommand(TaskCommandKind.Complete, Match: "does not exist"),
            new[] { MakeTask("a", "Something else") }, Today, Now);

        Assert.False(result.Success);
    }

    [Fact]
    public void Complete_ById_WorksEvenWhenTitleChanged()
    {
        var tasks = new[] { MakeTask("abc123", "Old title") };

        var result = TaskCommandPlanner.Apply(
            new TaskCommand(TaskCommandKind.Complete, Id: "abc123"),
            tasks, Today, Now);

        Assert.True(result.Success);
        Assert.True(result.Item!.IsCompleted);
    }

    [Fact]
    public void Update_ChangesOnlySuppliedFields()
    {
        var tasks = new[] { new TaskItem("a", "Draft email", "to Sam", false, Today.AddDays(2), Array.Empty<DateTimeOffset>(), Now) };

        var result = TaskCommandPlanner.Apply(
            new TaskCommand(TaskCommandKind.Update, Match: "Draft email", Title: "Send email", Due: "today"),
            tasks, Today, Now);

        Assert.True(result.Success);
        var item = result.Item!;
        Assert.Equal("Send email", item.Title);
        Assert.Equal("to Sam", item.Description); // untouched
        Assert.Equal(Today, item.DueDate!.Value.Date);
    }

    [Fact]
    public void Update_WithDueNone_ClearsTheDueDate()
    {
        var tasks = new[] { new TaskItem("a", "Draft email", null, false, Today.AddDays(2), Array.Empty<DateTimeOffset>(), Now) };

        var result = TaskCommandPlanner.Apply(
            new TaskCommand(TaskCommandKind.Update, Id: "a", Due: "none"),
            tasks, Today, Now);

        Assert.True(result.Success);
        Assert.Null(result.Item!.DueDate);
    }

    [Fact]
    public void Delete_ReturnsDeleteId()
    {
        var tasks = new[] { MakeTask("a", "Old note"), MakeTask("b", "Keep me") };

        var result = TaskCommandPlanner.Apply(
            new TaskCommand(TaskCommandKind.Delete, Match: "Old note"),
            tasks, Today, Now);

        Assert.True(result.Success);
        Assert.Equal("a", result.DeleteId);
        Assert.Null(result.Item);
    }

    [Fact]
    public void Reopen_ClearsCompletionAndHistoryHead()
    {
        var completed = TaskQueries.Complete(MakeTask("a", "Water plants"), Now);

        var result = TaskCommandPlanner.Apply(
            new TaskCommand(TaskCommandKind.Reopen, Id: "a"),
            new[] { completed }, Today, Now);

        Assert.True(result.Success);
        Assert.False(result.Item!.IsCompleted);
        Assert.Empty(result.Item.CompletionHistory!);
    }
}
