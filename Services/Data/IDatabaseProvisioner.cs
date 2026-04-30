namespace VirtmaAi.Services.Data;

public interface IDatabaseProvisioner
{
    DatabaseKind Kind { get; }

    Task<DatabaseConnectionInfo> EnsureProvisionedAsync(
        IProgress<ProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public enum DatabaseKind
{
    Sqlite,
    MySql
}

public sealed record DatabaseConnectionInfo(DatabaseKind Kind, string ConnectionString);

public sealed record ProvisionProgress(string Stage, double? Percent = null, string? Detail = null);
