namespace VirtmaAi.Services.Settings;

public interface ISettingsService
{
    string DataDirectory { get; }
    void SetDataDirectory(string path);

    T? Get<T>(string key, T? defaultValue = default);
    void Set<T>(string key, T value);
    bool Contains(string key);
    void Remove(string key);

    Task<string?> GetSecretAsync(string key);
    Task SetSecretAsync(string key, string value);
    Task RemoveSecretAsync(string key);
}
