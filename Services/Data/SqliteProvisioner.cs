using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.Data;

public sealed class SqliteProvisioner : IDatabaseProvisioner
{
    private readonly ISettingsService _settings;
    private readonly ILogger<SqliteProvisioner> _logger;

    public SqliteProvisioner(ISettingsService settings, ILogger<SqliteProvisioner> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public DatabaseKind Kind => DatabaseKind.Sqlite;

    public Task<DatabaseConnectionInfo> EnsureProvisionedAsync(
        IProgress<ProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ProvisionProgress("Preparing SQLite data directory"));

        var dbPath = Path.Combine(_settings.DataDirectory, "virtmaai.db");
        Directory.CreateDirectory(_settings.DataDirectory);
        _logger.LogInformation("SQLite database path: {Path}", dbPath);

        var connectionString = $"Data Source={dbPath};Cache=Shared;Foreign Keys=True";
        progress?.Report(new ProvisionProgress("SQLite ready", 1.0));
        return Task.FromResult(new DatabaseConnectionInfo(DatabaseKind.Sqlite, connectionString));
    }
}
