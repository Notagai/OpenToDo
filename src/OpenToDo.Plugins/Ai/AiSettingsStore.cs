using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeySharp;

namespace OpenToDo.Plugins.Ai;

public sealed class AiSettingsStore
{
    private const string Service = "OpenToDo";
    private const string AccountPrefix = "ai-api-key:";
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
        AiSettings settings = new();

        if (File.Exists(_path))
        {
            await using var stream = File.OpenRead(_path);
            settings = await JsonSerializer.DeserializeAsync<AiSettings>(stream, Options, cancellationToken) ?? new();
        }

        // Migrate API keys from the old plaintext settings format once.
        if (File.Exists(_path))
        {
            var json = await File.ReadAllTextAsync(_path, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            settings.OpenAiApiKey = await LoadOrMigrateSecretAsync(
                AiProvider.OpenAI, root, "openAiApiKey", cancellationToken);
            settings.GroqApiKey = await LoadOrMigrateSecretAsync(
                AiProvider.Groq, root, "groqApiKey", cancellationToken);
            settings.OpenRouterApiKey = await LoadOrMigrateSecretAsync(
                AiProvider.OpenRouter, root, "openRouterApiKey", cancellationToken);
        }
        else
        {
            settings.OpenAiApiKey = await GetSecretAsync(AiProvider.OpenAI) ?? string.Empty;
            settings.GroqApiKey = await GetSecretAsync(AiProvider.Groq) ?? string.Empty;
            settings.OpenRouterApiKey = await GetSecretAsync(AiProvider.OpenRouter) ?? string.Empty;
        }

        return settings;
    }

    public async Task SaveAsync(AiSettings settings, CancellationToken cancellationToken = default)
    {
        await SaveSecretAsync(AiProvider.OpenAI, settings.OpenAiApiKey);
        await SaveSecretAsync(AiProvider.Groq, settings.GroqApiKey);
        await SaveSecretAsync(AiProvider.OpenRouter, settings.OpenRouterApiKey);

        // Only non-secret configuration is persisted here.
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
    }

    private async Task<string> LoadOrMigrateSecretAsync(
        AiProvider provider,
        JsonElement root,
        string legacyProperty,
        CancellationToken cancellationToken)
    {
        var existing = await GetSecretAsync(provider);
        if (!string.IsNullOrEmpty(existing))
            return existing;

        if (root.TryGetProperty(legacyProperty, out var legacy) &&
            legacy.ValueKind == JsonValueKind.String)
        {
            var value = legacy.GetString() ?? string.Empty;
            if (!string.IsNullOrEmpty(value))
            {
                await SaveSecretAsync(provider, value);
                return value;
            }
        }

        return string.Empty;
    }

    private static string Account(AiProvider provider) => AccountPrefix + provider.ToString().ToLowerInvariant();

    private static Task<string?> GetSecretAsync(AiProvider provider)
    {
        return Task.Run(() =>
        {
            try
            {
                return Keyring.GetPassword(Service, Account(provider), "api-key");
            }
            catch (PlatformNotSupportedException)
            {
                return null;
            }
        });
    }

    private static Task SaveSecretAsync(AiProvider provider, string secret)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrEmpty(secret))
                return;

            if (OperatingSystem.IsWindows())
            {
                // Windows DPAPI, current-user scope. The encrypted value never
                // goes into settings.json.
                var protectedBytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(secret),
                    Encoding.UTF8.GetBytes(Service),
                    DataProtectionScope.CurrentUser);

                Keyring.SetPassword(
                    Service,
                    Account(provider),
                    "dpapi:" + Convert.ToBase64String(protectedBytes));
                return;
            }

            // Linux/macOS: KeySharp uses the OS keyring / Secret Service.
            Keyring.SetPassword(Service, Account(provider), "api-key", secret);
        });
    }
}
