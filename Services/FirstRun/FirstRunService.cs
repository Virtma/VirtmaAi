using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.FirstRun;

public sealed class FirstRunService : IFirstRunService
{
    private readonly ISettingsService _settings;

    public FirstRunService(ISettingsService settings)
    {
        _settings = settings;
    }

    public string MarkerPath => Path.Combine(_settings.DataDirectory, ".initialized");

    public bool IsFirstRun => !File.Exists(MarkerPath);

    public event EventHandler? Completed;

    public async Task MarkCompleteAsync()
    {
        Directory.CreateDirectory(_settings.DataDirectory);
        await File.WriteAllTextAsync(MarkerPath, DateTime.UtcNow.ToString("O"));
    }

    public void NotifyCompleted() => Completed?.Invoke(this, EventArgs.Empty);
}
