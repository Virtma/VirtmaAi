using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.Data;

/// <summary>
/// Lightweight schema patches applied on every startup. We don't run full EF migrations
/// (the project uses <c>EnsureCreated</c>) so when we add a new column to an entity, existing
/// databases need an ALTER TABLE. Each entry is idempotent — guarded by a column-existence check.
/// Add new entries here whenever an entity gains a column.
/// </summary>
internal static class SchemaPatches
{
    public sealed record Patch(string Table, string Column, string SqliteType, string MySqlType);

    /// <summary>Whole-table create. Used for entities added after the user's first install.</summary>
    public sealed record TablePatch(string Table, string SqliteCreate, string MySqlCreate);

    public static readonly Patch[] All =
    {
        new("AiModels", "PublicApiKey",  "TEXT",                "varchar(512) NULL"),
        new("AiModels", "PrivateApiKey", "TEXT",                "varchar(512) NULL"),
    };

    public static readonly TablePatch[] Tables =
    {
        new("AiRules",
            // SQLite — guid as TEXT, datetimes as TEXT (ISO-8601), int columns as INTEGER.
            "CREATE TABLE IF NOT EXISTS \"AiRules\" (" +
              "\"Id\" TEXT NOT NULL PRIMARY KEY, " +
              "\"Title\" TEXT NOT NULL, " +
              "\"Body\" TEXT NOT NULL, " +
              "\"Priority\" INTEGER NOT NULL, " +
              "\"Enabled\" INTEGER NOT NULL, " +
              "\"CreatedAt\" TEXT NOT NULL, " +
              "\"UpdatedAt\" TEXT NOT NULL)",
            // MySQL
            "CREATE TABLE IF NOT EXISTS `AiRules` (" +
              "`Id` char(36) NOT NULL PRIMARY KEY, " +
              "`Title` varchar(160) NOT NULL, " +
              "`Body` longtext NOT NULL, " +
              "`Priority` int NOT NULL, " +
              "`Enabled` tinyint(1) NOT NULL, " +
              "`CreatedAt` datetime(6) NOT NULL, " +
              "`UpdatedAt` datetime(6) NOT NULL) ENGINE=InnoDB"),
    };
}

public sealed class DatabaseService : IDatabaseService
{
    public const string ConnectionKey = "virtmaai.db.connection";
    public const string KindKey = "virtmaai.db.kind";

    private readonly ISettingsService _settings;
    private readonly ILogger<DatabaseService> _logger;
    private DatabaseConnectionInfo? _current;

    public DatabaseService(ISettingsService settings, ILogger<DatabaseService> logger)
    {
        _settings = settings;
        _logger = logger;

        var kindStr = _settings.Get<string>(KindKey);
        var conn = _settings.Get<string>(ConnectionKey);
        if (!string.IsNullOrWhiteSpace(kindStr) && !string.IsNullOrWhiteSpace(conn)
            && Enum.TryParse<DatabaseKind>(kindStr, out var kind))
        {
            _current = new DatabaseConnectionInfo(kind, conn);
        }
    }

    public DatabaseConnectionInfo? Current => _current;

    public async Task InitializeAsync(DatabaseConnectionInfo info, CancellationToken cancellationToken = default)
    {
        _current = info;
        _settings.Set(KindKey, info.Kind.ToString());
        _settings.Set(ConnectionKey, info.ConnectionString);

        await using var ctx = CreateContext();
        _logger.LogInformation("Ensuring database schema ({Kind})", info.Kind);
        await ctx.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await ApplySchemaPatchesAsync(ctx, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Idempotent — adds columns that have been added to entities since the database was created.
    /// Call this on every startup, not just first-run. EF's <c>EnsureCreated</c> is a one-shot, so
    /// new columns require an out-of-band ALTER.
    /// </summary>
    public async Task EnsureSchemaUpToDateAsync(CancellationToken cancellationToken = default)
    {
        if (_current is null) return;
        try
        {
            await using var ctx = CreateContext();
            await ApplySchemaPatchesAsync(ctx, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schema patch failed");
        }
    }

    private async Task ApplySchemaPatchesAsync(AppDbContext ctx, CancellationToken ct)
    {
        if (_current is null) return;

        // Whole-new-table patches first — column patches below depend on tables existing.
        foreach (var t in SchemaPatches.Tables)
        {
            try
            {
                var sql = _current.Kind == DatabaseKind.Sqlite ? t.SqliteCreate : t.MySqlCreate;
                _logger.LogInformation("Schema patch (table): {Sql}", sql);
                await ctx.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Schema patch for table {Table} failed", t.Table);
            }
        }

        foreach (var p in SchemaPatches.All)
        {
            try
            {
                bool exists = _current.Kind switch
                {
                    DatabaseKind.Sqlite => await SqliteColumnExistsAsync(ctx, p.Table, p.Column, ct).ConfigureAwait(false),
                    DatabaseKind.MySql  => await MySqlColumnExistsAsync(ctx, p.Table, p.Column, ct).ConfigureAwait(false),
                    _ => true
                };
                if (exists) continue;

                var typeSql = _current.Kind == DatabaseKind.Sqlite ? p.SqliteType : p.MySqlType;
                var alter = $"ALTER TABLE \"{p.Table}\" ADD COLUMN \"{p.Column}\" {typeSql}";
                _logger.LogInformation("Schema patch: {Sql}", alter);
                await ctx.Database.ExecuteSqlRawAsync(alter, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Schema patch for {Table}.{Column} failed", p.Table, p.Column);
            }
        }
    }

    private static async Task<bool> SqliteColumnExistsAsync(AppDbContext ctx, string table, string column, CancellationToken ct)
    {
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != global::System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // Schema: cid, name, type, notnull, dflt_value, pk
            var name = reader.GetString(1);
            if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static async Task<bool> MySqlColumnExistsAsync(AppDbContext ctx, string table, string column, CancellationToken ct)
    {
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != global::System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND COLUMN_NAME = @c LIMIT 1";
        AddParam(cmd, "@t", table);
        AddParam(cmd, "@c", column);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null && result is not DBNull;

        static void AddParam(global::System.Data.Common.DbCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name; p.Value = value; cmd.Parameters.Add(p);
        }
    }

    public AppDbContext CreateContext()
    {
        if (_current is null)
            throw new InvalidOperationException("Database has not been initialized");

        var options = new DbContextOptionsBuilder<AppDbContext>();
        switch (_current.Kind)
        {
            case DatabaseKind.Sqlite:
                options.UseSqlite(_current.ConnectionString);
                break;
            case DatabaseKind.MySql:
                options.UseMySql(
                    _current.ConnectionString,
                    ServerVersion.AutoDetect(_current.ConnectionString));
                break;
            default:
                throw new InvalidOperationException("Unknown database kind " + _current.Kind);
        }
        return new AppDbContext(options.Options);
    }
}
