using System.Text.Json;
using OpenToDo.Core;

namespace OpenToDo.Data;

public sealed class JsonTaskRepository : ITaskRepository
{
    private const string FileName = "task.json";
    private readonly string _root;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonTaskRepository(string rootDirectory) => _root = Path.GetFullPath(rootDirectory);

    /// <summary>Number of task files that could not be read during the last <see cref="GetAllAsync"/>.</summary>
    public int LastSkippedCount { get; private set; }

    public async Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var byId = new Dictionary<string, (TaskItem Task, DateTime Written)>(StringComparer.Ordinal);
        var skipped = 0;

        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CleanStaleTempFiles(directory);

            var file = Path.Combine(directory, FileName);
            if (!File.Exists(file))
                continue;

            try
            {
                TaskItem? task;
                await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true))
                    task = await JsonSerializer.DeserializeAsync<TaskItem>(stream, Options, cancellationToken);

                if (task is null || !IsSafeId(task.Id) || string.IsNullOrWhiteSpace(task.Title))
                {
                    skipped++;
                    continue;
                }

                task = task with
                {
                    Title = task.Title.Trim(),
                    CompletionHistory = task.CompletionHistory ?? Array.Empty<DateTimeOffset>()
                };

                // If two folders hold the same id (manual copy), keep the most recently written one.
                var written = File.GetLastWriteTimeUtc(file);
                if (!byId.TryGetValue(task.Id, out var existing) || written > existing.Written)
                    byId[task.Id] = (task, written);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // One damaged or locked file must never stop the app from starting.
                skipped++;
            }
        }

        LastSkippedCount = skipped;
        return TaskQueries.Sort(byId.Values.Select(v => v.Task)).ToArray();
    }

    public async Task SaveAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ValidateId(task.Id);

        var title = task.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("A task needs a title.", nameof(task));

        task = task with { Title = title, CompletionHistory = task.CompletionHistory ?? Array.Empty<DateTimeOffset>() };

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.Combine(_root, task.Id);
            Directory.CreateDirectory(directory);

            var target = Path.Combine(directory, FileName);
            var temp = Path.Combine(directory, $".task-{Guid.NewGuid():N}.tmp");

            try
            {
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, task, Options, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temp, target, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DeleteAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ValidateId(taskId);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.Combine(_root, taskId);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static void CleanStaleTempFiles(string directory)
    {
        try
        {
            foreach (var temp in Directory.EnumerateFiles(directory, ".task-*.tmp"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(temp) > TimeSpan.FromMinutes(10))
                    File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort housekeeping only.
        }
    }

    private static void ValidateId(string id)
    {
        if (!IsSafeId(id))
            throw new ArgumentException("Task id must be a single safe path segment.", nameof(id));
    }

    private static bool IsSafeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.Length > 128 || id != id.Trim())
            return false;

        return id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !id.Contains(Path.DirectorySeparatorChar)
            && !id.Contains(Path.AltDirectorySeparatorChar);
    }
}
