using System.Text.Json;

namespace VirtmaAi.Services.Settings;

public sealed class SettingsService : ISettingsService
{
    private const string DataDirKey = "virtmaai.data_directory";

    public string DataDirectory
    {
        get
        {
            var stored = Preferences.Default.Get<string>(DataDirKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(stored))
                return stored;

            var fallback = Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "data");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public void SetDataDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path required", nameof(path));

        Directory.CreateDirectory(path);
        Preferences.Default.Set(DataDirKey, path);
    }

    public T? Get<T>(string key, T? defaultValue = default)
    {
        if (!Preferences.Default.ContainsKey(key))
            return defaultValue;

        var raw = Preferences.Default.Get<string>(key, string.Empty);
        if (string.IsNullOrEmpty(raw))
            return defaultValue;

        if (typeof(T) == typeof(string))
            return (T)(object)raw;

        try
        {
            return JsonSerializer.Deserialize<T>(raw);
        }
        catch
        {
            return defaultValue;
        }
    }

    public void Set<T>(string key, T value)
    {
        var serialized = typeof(T) == typeof(string)
            ? value?.ToString() ?? string.Empty
            : JsonSerializer.Serialize(value);
        Preferences.Default.Set(key, serialized);
    }

    public bool Contains(string key) => Preferences.Default.ContainsKey(key);

    public void Remove(string key) => Preferences.Default.Remove(key);

    public async Task<string?> GetSecretAsync(string key)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(key);
        }
        catch
        {
            return null;
        }
    }

    public Task SetSecretAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);

    public Task RemoveSecretAsync(string key)
    {
        SecureStorage.Default.Remove(key);
        return Task.CompletedTask;
    }
}
