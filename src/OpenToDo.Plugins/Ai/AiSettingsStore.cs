using System.Text.Json;
using KeySharp;

namespace OpenToDo.Plugins.Ai;

public sealed record AiSaveResult(bool SecretsStored, string? Warning);

public sealed class AiSettingsStore
{
    private const string Application = "com.notagai.opentodo";
    private const string Service = "OpenToDo";
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AiSettingsStore(string? path = null) =>
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenToDo", "settings.json");

    /// <summary>Full path of the settings file this store reads and writes.</summary>
    public string FilePath => _path;

    /// <summary>Whether settings.json exists on disk yet (false on a brand-new install).</summary>
    public bool Exists => File.Exists(_path);

    /// <summary>Set when the last load had to recover from a problem (corrupt file, keyring unavailable...).</summary>
    public string? LoadWarning { get; private set; }

    public async Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        LoadWarning = null;
        var settings = new AiSettings();
        JsonDocument? legacy = null;

        try
        {
            if (File.Exists(_path))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_path, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        settings = JsonSerializer.Deserialize<AiSettings>(json, Options) ?? new AiSettings();
                        legacy = JsonDocument.Parse(json);
                    }
                }
                catch (JsonException)
                {
                    settings = new AiSettings();
                    legacy?.Dispose();
                    legacy = null;
                    LoadWarning = BackupCorruptFile()
                        ? "settings.json was damaged, so defaults were loaded. The old file was kept as settings.json.corrupt."
                        : "settings.json was damaged, so defaults were loaded.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LoadWarning = $"Couldn't read settings.json ({ex.Message}). Defaults were loaded.";
                }
            }

            settings.Normalize();

            var root = legacy?.RootElement;
            var migrated = false;
            (settings.OpenAiApiKey, var m1) = await LoadOrMigrateSecretAsync(AiProvider.OpenAI, root, "openAiApiKey");
            (settings.GroqApiKey, var m2) = await LoadOrMigrateSecretAsync(AiProvider.Groq, root, "groqApiKey");
            (settings.OpenRouterApiKey, var m3) = await LoadOrMigrateSecretAsync(AiProvider.OpenRouter, root, "openRouterApiKey");
            migrated = m1 || m2 || m3;

            // Once a plaintext key has safely moved into the keyring, scrub it from settings.json right away.
            if (migrated)
            {
                try { await WriteFileAsync(settings, cancellationToken); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        finally
        {
            legacy?.Dispose();
        }

        return settings;
    }

    public async Task<AiSaveResult> SaveAsync(AiSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();

        var failed = new List<string>();
        if (!await SaveSecretAsync(AiProvider.OpenAI, settings.OpenAiApiKey)) failed.Add("OpenAI");
        if (!await SaveSecretAsync(AiProvider.Groq, settings.GroqApiKey)) failed.Add("Groq");
        if (!await SaveSecretAsync(AiProvider.OpenRouter, settings.OpenRouterApiKey)) failed.Add("OpenRouter");

        // Always persist the non-secret settings, even if the credential store is unavailable.
        await WriteFileAsync(settings, cancellationToken);

        return failed.Count == 0
            ? new AiSaveResult(true, null)
            : new AiSaveResult(false,
                $"Settings saved, but the {string.Join(", ", failed)} key couldn't be written to the OS credential store " +
                "(on Linux this needs a running Secret Service / libsecret). The key works for this session only.");
    }

    private async Task WriteFileAsync(AiSettings settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var persisted = new AiSettings
        {
            Username = settings.Username,
            OnboardingCompleted = settings.OnboardingCompleted,
            OpenAiModel = settings.OpenAiModel,
            GroqModel = settings.GroqModel,
            OpenRouterModel = settings.OpenRouterModel,
            Temperature = settings.Temperature,
            MaxOutputTokens = settings.MaxOutputTokens
        };

        // Write to a temp file and swap, so a crash mid-write can never leave a truncated settings.json.
        var temp = _path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, persisted, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    private bool BackupCorruptFile()
    {
        try
        {
            File.Move(_path, _path + ".corrupt", overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<(string Secret, bool Migrated)> LoadOrMigrateSecretAsync(AiProvider provider, JsonElement? root, string legacyProperty)
    {
        var existing = await GetSecretAsync(provider);
        if (!string.IsNullOrEmpty(existing))
            return (existing, false);

        if (root is { ValueKind: JsonValueKind.Object } r &&
            r.TryGetProperty(legacyProperty, out var legacy) &&
            legacy.ValueKind == JsonValueKind.String)
        {
            var value = legacy.GetString() ?? string.Empty;
            if (!string.IsNullOrEmpty(value))
            {
                var stored = await SaveSecretAsync(provider, value);
                if (!stored)
                    LoadWarning ??= "An API key from an older settings.json couldn't be moved into the OS credential store.";
                return (value, stored);
            }
        }

        return (string.Empty, false);
    }

    private static string Account(AiProvider provider) => provider.ToString().ToLowerInvariant();

    private static Task<string?> GetSecretAsync(AiProvider provider) => Task.Run(() =>
    {
        try { return Keyring.GetPassword(Application, Service, Account(provider)); }
        catch (Exception) { return null; } // missing entry, no keyring service, unsupported platform...
    });

    private static Task<bool> SaveSecretAsync(AiProvider provider, string secret) => Task.Run(() =>
    {
        try
        {
            if (string.IsNullOrEmpty(secret))
            {
                try { Keyring.DeletePassword(Application, Service, Account(provider)); }
                catch (Exception) { /* nothing stored: nothing to delete */ }
                return true;
            }

            Keyring.SetPassword(Application, Service, Account(provider), secret);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });
}
