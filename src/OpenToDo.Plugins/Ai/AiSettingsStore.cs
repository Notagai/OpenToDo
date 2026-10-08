using System.Text.Json;

namespace OpenToDo.Plugins.Ai;

public sealed class AiSettingsStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AiSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenToDo",
            "settings.json");
    }

    public async Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            return new AiSettings();

        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<AiSettings>(stream, Options, cancellationToken)
            ?? new AiSettings();
    }

    public async Task SaveAsync(AiSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
    }
}
