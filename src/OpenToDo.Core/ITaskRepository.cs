namespace OpenToDo.Core;

public interface ITaskRepository
{
    Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(TaskItem task, CancellationToken cancellationToken = default);
}
