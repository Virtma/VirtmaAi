using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using VirtmaAi.Services.Data;

namespace VirtmaAi.Services.Database;

public sealed class DbManager : IDbManager
{
    private readonly IDatabaseService _db;
    private readonly ILogger<DbManager> _logger;

    public DbManager(IDatabaseService db, ILogger<DbManager> logger)
    {
        _db = db;
        _logger = logger;
    }

    public bool IsAvailable => _db.Current is not null;

    public string Kind => _db.Current?.Kind.ToString() ?? "None";

    private DbConnection Open(string? database = null)
    {
        var info = _db.Current ?? throw new InvalidOperationException("database not initialized");
        DbConnection conn = info.Kind switch
        {
            DatabaseKind.MySql => new MySqlConnection(info.ConnectionString),
            DatabaseKind.Sqlite => new SqliteConnection(info.ConnectionString),
            _ => throw new InvalidOperationException("unknown db kind")
        };
        conn.Open();
        if (info.Kind == DatabaseKind.MySql && !string.IsNullOrWhiteSpace(database))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "USE `" + database.Replace("`", "``") + "`";
            cmd.ExecuteNonQuery();
        }
        return conn;
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken ct = default)
    {
        var info = _db.Current ?? throw new InvalidOperationException("database not initialized");
        if (info.Kind == DatabaseKind.Sqlite) return new[] { "main" };
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SHOW DATABASES";
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(reader.GetString(0));
        return list;
    }

    public async Task<IReadOnlyList<string>> ListTablesAsync(string? database, CancellationToken ct = default)
    {
        var info = _db.Current ?? throw new InvalidOperationException("database not initialized");
        await using var conn = Open(database);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = info.Kind == DatabaseKind.MySql
            ? "SHOW TABLES"
            : "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(reader.GetString(0));
        return list;
    }

    public async Task<IReadOnlyList<DbColumnInfo>> ListColumnsAsync(string? database, string table, CancellationToken ct = default)
    {
        var info = _db.Current ?? throw new InvalidOperationException("database not initialized");
        await using var conn = Open(database);
        var list = new List<DbColumnInfo>();
        if (info.Kind == DatabaseKind.MySql)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SHOW FULL COLUMNS FROM `" + table.Replace("`", "``") + "`";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                var type = reader.GetString(1);
                var nullableStr = reader.GetString(3);
                var key = reader.GetString(4);
                var defaultVal = reader.IsDBNull(5) ? null : reader.GetValue(5)?.ToString();
                list.Add(new DbColumnInfo(name, type, nullableStr == "YES", defaultVal, key == "PRI"));
            }
        }
        else
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(\"" + table.Replace("\"", "\"\"") + "\")";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(1);
                var type = reader.GetString(2);
                var notnull = reader.GetInt32(3) == 1;
                var defaultVal = reader.IsDBNull(4) ? null : reader.GetValue(4)?.ToString();
                var pk = reader.GetInt32(5) > 0;
                list.Add(new DbColumnInfo(name, type, !notnull, defaultVal, pk));
            }
        }
        return list;
    }

    public async Task<IReadOnlyList<DbIndexInfo>> ListIndexesAsync(string? database, string table, CancellationToken ct = default)
    {
        var info = _db.Current ?? throw new InvalidOperationException("database not initialized");
        await using var conn = Open(database);
        var list = new List<DbIndexInfo>();
        if (info.Kind == DatabaseKind.MySql)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SHOW INDEX FROM `" + table.Replace("`", "``") + "`";
            var byName = new Dictionary<string, (List<string> cols, bool unique)>(StringComparer.Ordinal);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var nonUnique = reader.GetInt32(1);
                var keyName = reader.GetString(2);
                var col = reader.GetString(4);
                if (!byName.TryGetValue(keyName, out var tup))
                    tup = (new List<string>(), nonUnique == 0);
                tup.cols.Add(col);
                byName[keyName] = tup;
            }
            foreach (var kv in byName) list.Add(new DbIndexInfo(kv.Key, string.Join(", ", kv.Value.cols), kv.Value.unique));
        }
        else
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA index_list(\"" + table.Replace("\"", "\"\"") + "\")";
            var indexes = new List<(string name, bool unique)>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    indexes.Add((reader.GetString(1), reader.GetInt32(2) == 1));
                }
            }
            foreach (var (name, unique) in indexes)
            {
                await using var cmd2 = conn.CreateCommand();
                cmd2.CommandText = "PRAGMA index_info(\"" + name.Replace("\"", "\"\"") + "\")";
                var cols = new List<string>();
                await using var reader2 = await cmd2.ExecuteReaderAsync(ct);
                while (await reader2.ReadAsync(ct)) cols.Add(reader2.GetString(2));
                list.Add(new DbIndexInfo(name, string.Join(", ", cols), unique));
            }
        }
        return list;
    }

    public async Task<IReadOnlyList<DbTriggerInfo>> ListTriggersAsync(string? database, CancellationToken ct = default)
    {
        var info = _db.Current ?? throw new InvalidOperationException("database not initialized");
        await using var conn = Open(database);
        var list = new List<DbTriggerInfo>();
        if (info.Kind == DatabaseKind.MySql)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SHOW TRIGGERS";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                var evt = reader.GetString(1);
                var table = reader.GetString(2);
                var body = reader.GetString(3);
                var timing = reader.GetString(4);
                list.Add(new DbTriggerInfo(name, table, evt, timing, body));
            }
        }
        else
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name, tbl_name, sql FROM sqlite_master WHERE type='trigger' ORDER BY name";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                var table = reader.GetString(1);
                var sql = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                list.Add(new DbTriggerInfo(name, table, "", "", sql));
            }
        }
        return list;
    }

    public async Task<DbQueryResult> ExecuteAsync(string sql, string? database = null, int? limit = null, CancellationToken ct = default)
    {
        var result = new DbQueryResult();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var conn = Open(database);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 60;
            if (IsQueryLike(sql))
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                for (int i = 0; i < reader.FieldCount; i++) result.Columns.Add(reader.GetName(i));
                var rowCount = 0;
                while (await reader.ReadAsync(ct))
                {
                    if (limit is int cap && rowCount >= cap) break;
                    var row = new List<object?>(reader.FieldCount);
                    for (int i = 0; i < reader.FieldCount; i++)
                        row.Add(reader.IsDBNull(i) ? null : reader.GetValue(i));
                    result.Rows.Add(row);
                    rowCount++;
                }
                result.RowsAffected = null;
            }
            else
            {
                result.RowsAffected = await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQL failed");
            result.ErrorMessage = ex.Message;
        }
        stopwatch.Stop();
        result.Elapsed = stopwatch.Elapsed;
        return result;
    }

    public async Task<int> ExecuteNonQueryAsync(string sql, string? database = null, CancellationToken ct = default)
    {
        await using var conn = Open(database);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static bool IsQueryLike(string sql)
    {
        var trimmed = sql.TrimStart().TrimStart('(');
        if (trimmed.Length == 0) return false;
        return trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("SHOW", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("DESCRIBE", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("DESC ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
    }
}
