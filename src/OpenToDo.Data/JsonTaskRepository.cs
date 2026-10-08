using System.Text.Json;
using OpenToDo.Core;

namespace OpenToDo.Data;

public sealed class JsonTaskRepository : ITaskRepository
{
    private readonly string _root;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public JsonTaskRepository(string rootDirectory) => _root = rootDirectory;
    public async Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var tasks = new List<TaskItem>();
        foreach (var file in Directory.EnumerateFiles(_root, "task.json", SearchOption.AllDirectories))
        {
            await using var stream = File.OpenRead(file);
            var task = await JsonSerializer.DeserializeAsync<TaskItem>(stream, Options, cancellationToken);
            if (task is not null) tasks.Add(task);
        }
        return tasks.OrderBy(t => t.IsCompleted).ThenBy(t => t.DueDate).ThenBy(t => t.Title).ToArray();
    }
    public async Task SaveAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(_root, task.Id);
        Directory.CreateDirectory(directory);
        await using var stream = File.Create(Path.Combine(directory, "task.json"));
        await JsonSerializer.SerializeAsync(stream, task, Options, cancellationToken);
    }
}
