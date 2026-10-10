using System.Text.Json;
using OpenToDo.Core;

namespace OpenToDo.Data;

public sealed class JsonTaskRepository : ITaskRepository
{
    private readonly string _root;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonTaskRepository(string rootDirectory) => _root = Path.GetFullPath(rootDirectory);

    public async Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var tasks = new List<TaskItem>();

        foreach (var file in Directory.EnumerateFiles(_root, "task.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var stream = File.OpenRead(file);
                var task = await JsonSerializer.DeserializeAsync<TaskItem>(stream, Options, cancellationToken);
                if (task is not null && IsSafeId(task.Id) && !string.IsNullOrWhiteSpace(task.Title))
                    tasks.Add(task);
            }
            catch (JsonException)
            {
                // Ignore a single malformed task so one damaged file cannot prevent the app from starting.
            }
            catch (IOException)
            {
                // Ignore files that disappear or become temporarily unreadable while enumerating.
            }
        }

        return tasks
            .OrderBy(t => t.IsCompleted)
            .ThenBy(t => t.SortOrder)
            .ThenBy(t => t.DueDate)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task SaveAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ValidateId(task.Id);

        var directory = Path.Combine(_root, task.Id);
        Directory.CreateDirectory(directory);

        var target = Path.Combine(directory, "task.json");
        var temp = Path.Combine(directory, $".task-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, task, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public Task DeleteAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ValidateId(taskId);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.Combine(_root, taskId);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        return Task.CompletedTask;
    }

    private static void ValidateId(string id)
    {
        if (!IsSafeId(id))
            throw new ArgumentException("Task id must be a single safe path segment.", nameof(id));
    }

    private static bool IsSafeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.Length > 128)
            return false;

        return id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !id.Contains(Path.DirectorySeparatorChar)
            && !id.Contains(Path.AltDirectorySeparatorChar);
    }
}
