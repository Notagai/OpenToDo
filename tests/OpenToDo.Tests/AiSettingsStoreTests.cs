using OpenToDo.Plugins.Ai;
using Xunit;

namespace OpenToDo.Tests;

public sealed class AiSettingsStoreTests
{
    [Fact]
    public async Task SaveThenLoad_PersistsUsernameAndOnboarding()
    {
        var path = Path.Combine(Path.GetTempPath(), $"opentodo-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new AiSettingsStore(path);
            Assert.False(store.Exists);

            var settings = await store.LoadAsync();
            settings.Username = "Alex";
            settings.OnboardingCompleted = true;
            settings.OpenAiModel = "gpt-4o-mini";

            await store.SaveAsync(settings);

            Assert.True(store.Exists);

            var reloaded = await store.LoadAsync();
            Assert.Equal("Alex", reloaded.Username);
            Assert.True(reloaded.OnboardingCompleted);
            Assert.Equal("gpt-4o-mini", reloaded.OpenAiModel);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Username_IsTrimmedAndLengthLimited()
    {
        var path = Path.Combine(Path.GetTempPath(), $"opentodo-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new AiSettingsStore(path);
            var settings = new AiSettings
            {
                Username = "  " + new string('x', 80) + "  "
            };

            await store.SaveAsync(settings);

            var reloaded = await store.LoadAsync();
            Assert.Equal(AiSettings.MaxUsernameLength, reloaded.Username.Length);
            Assert.DoesNotContain(" ", reloaded.Username);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
