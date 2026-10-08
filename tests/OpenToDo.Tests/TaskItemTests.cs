using OpenToDo.Core;

namespace OpenToDo.Tests;

public class TaskItemTests
{
    [Fact]
    public void NewTask_IsNotCompleted()
    {
        var task = new TaskItem("test", "First task");
        Assert.False(task.IsCompleted);
    }
}
