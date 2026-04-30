using System.Data;

namespace VirtmaAi.Services.Database;

public interface IDbManager
{
    bool IsAvailable { get; }
    string Kind { get; }
    Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListTablesAsync(string? database, CancellationToken ct = default);
    Task<IReadOnlyList<DbColumnInfo>> ListColumnsAsync(string? database, string table, CancellationToken ct = default);
    Task<IReadOnlyList<DbIndexInfo>> ListIndexesAsync(string? database, string table, CancellationToken ct = default);
    Task<IReadOnlyList<DbTriggerInfo>> ListTriggersAsync(string? database, CancellationToken ct = default);
    Task<DbQueryResult> ExecuteAsync(string sql, string? database = null, int? limit = null, CancellationToken ct = default);
    Task<int> ExecuteNonQueryAsync(string sql, string? database = null, CancellationToken ct = default);
}

public sealed record DbColumnInfo(string Name, string DataType, bool Nullable, string? Default, bool IsPrimaryKey);

public sealed record DbIndexInfo(string Name, string Columns, bool Unique);

public sealed record DbTriggerInfo(string Name, string Table, string Event, string Timing, string Body);

public sealed class DbQueryResult
{
    public List<string> Columns { get; } = new();
    public List<List<object?>> Rows { get; } = new();
    public int? RowsAffected { get; set; }
    public string? ErrorMessage { get; set; }
    public TimeSpan Elapsed { get; set; }
}
