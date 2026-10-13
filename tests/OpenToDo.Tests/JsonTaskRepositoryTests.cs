using OpenToDo.Core;
using OpenToDo.Data;
using Xunit;

namespace OpenToDo.Tests;

public sealed class JsonTaskRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "opentodo-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        var repo = new JsonTaskRepository(_root);
        await repo.SaveAsync(new TaskItem("one", "  Write tests  ", "desc", CreatedAt: DateTimeOffset.Now));

        var all = await repo.GetAllAsync();

        var task = Assert.Single(all);
        Assert.Equal("Write tests", task.Title);
        Assert.Equal("desc", task.Description);
        Assert.NotNull(task.CompletionHistory);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task Save_RejectsUnsafeIds(string id)
    {
        var repo = new JsonTaskRepository(_root);
        await Assert.ThrowsAsync<ArgumentException>(() => repo.SaveAsync(new TaskItem(id, "x")));
    }

    [Fact]
    public async Task Save_RejectsBlankTitle()
    {
        var repo = new JsonTaskRepository(_root);
        await Assert.ThrowsAsync<ArgumentException>(() => repo.SaveAsync(new TaskItem("one", "   ")));
    }

    [Fact]
    public async Task CorruptFile_IsSkipped_AndCounted()
    {
        var repo = new JsonTaskRepository(_root);
        await repo.SaveAsync(new TaskItem("good", "Good"));
        Directory.CreateDirectory(Path.Combine(_root, "bad"));
        await File.WriteAllTextAsync(Path.Combine(_root, "bad", "task.json"), "{ not json");

        var all = await repo.GetAllAsync();

        Assert.Single(all);
        Assert.Equal(1, repo.LastSkippedCount);
    }

    [Fact]
    public async Task Delete_RemovesTask_AndIsIdempotent()
    {
        var repo = new JsonTaskRepository(_root);
        await repo.SaveAsync(new TaskItem("one", "x"));
        await repo.DeleteAsync("one");
        await repo.DeleteAsync("one");
        Assert.Empty(await repo.GetAllAsync());
    }

    [Fact]
    public async Task ConcurrentSaves_DoNotCorruptTheFile()
    {
        var repo = new JsonTaskRepository(_root);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => repo.SaveAsync(new TaskItem("same", "Title " + i))));
        var task = Assert.Single(await repo.GetAllAsync());
        Assert.StartsWith("Title ", task.Title);
    }
}
