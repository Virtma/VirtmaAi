namespace VirtmaAi.Services.Data;

public interface IDatabaseService
{
    DatabaseConnectionInfo? Current { get; }
    Task InitializeAsync(DatabaseConnectionInfo info, CancellationToken cancellationToken = default);
    /// <summary>
    /// Apply idempotent schema patches (ALTER TABLE … ADD COLUMN) for any columns that have been
    /// added to entities since the database was first created. Call this on every startup.
    /// </summary>
    Task EnsureSchemaUpToDateAsync(CancellationToken cancellationToken = default);
    AppDbContext CreateContext();
}
