using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Database;

namespace VirtmaAi.ViewModels.Database;

public sealed partial class DbManagerViewModel : ViewModelBase
{
    private readonly IDbManager _dbm;
    private readonly ILogger<DbManagerViewModel> _logger;

    public DbManagerViewModel(IDbManager dbm, ILogger<DbManagerViewModel> logger)
    {
        _dbm = dbm;
        _logger = logger;
    }

    public ObservableCollection<string> Databases { get; } = new();
    public ObservableCollection<string> Tables { get; } = new();
    public ObservableCollection<DbColumnInfo> Columns { get; } = new();
    public ObservableCollection<DbIndexInfo> Indexes { get; } = new();
    public ObservableCollection<DbTriggerInfo> Triggers { get; } = new();
    public ObservableCollection<ResultRow> QueryRows { get; } = new();
    public ObservableCollection<string> QueryColumns { get; } = new();
    public ObservableCollection<string> QueryHistory { get; } = new();

    [ObservableProperty] private string? _selectedDatabase;
    [ObservableProperty] private string? _selectedTable;
    [ObservableProperty] private string _sqlText = "SELECT * FROM Conversations LIMIT 100;";
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private int? _rowsAffected;
    [ObservableProperty] private double _elapsedMs;
    [ObservableProperty] private int _rowLimit = 500;
    [ObservableProperty] private string _kind = "None";

    [ObservableProperty] private string _triggerName = string.Empty;
    [ObservableProperty] private string _triggerTimingEvent = "AFTER INSERT";
    [ObservableProperty] private string _triggerBody = string.Empty;

    partial void OnSelectedDatabaseChanged(string? value)
    {
        _ = RefreshTablesAsync();
    }

    partial void OnSelectedTableChanged(string? value)
    {
        _ = RefreshColumnsAsync();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        Kind = _dbm.Kind;
        if (!_dbm.IsAvailable) { Status = "Database not initialized."; return; }
        try
        {
            Databases.Clear();
            foreach (var d in await _dbm.ListDatabasesAsync()) Databases.Add(d);
            SelectedDatabase = Databases.FirstOrDefault();
            await RefreshTriggersAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "Load databases"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task RefreshTablesAsync()
    {
        if (!_dbm.IsAvailable) return;
        try
        {
            Tables.Clear();
            foreach (var t in await _dbm.ListTablesAsync(SelectedDatabase)) Tables.Add(t);
            SelectedTable = Tables.FirstOrDefault();
            await RefreshTriggersAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "Refresh tables"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task RefreshColumnsAsync()
    {
        Columns.Clear();
        Indexes.Clear();
        if (!_dbm.IsAvailable || string.IsNullOrWhiteSpace(SelectedTable)) return;
        try
        {
            foreach (var c in await _dbm.ListColumnsAsync(SelectedDatabase, SelectedTable)) Columns.Add(c);
            foreach (var i in await _dbm.ListIndexesAsync(SelectedDatabase, SelectedTable)) Indexes.Add(i);
        }
        catch (Exception ex) { _logger.LogError(ex, "Refresh columns"); ErrorMessage = ex.Message; }
    }

    private async Task RefreshTriggersAsync()
    {
        Triggers.Clear();
        if (!_dbm.IsAvailable) return;
        try
        {
            foreach (var t in await _dbm.ListTriggersAsync(SelectedDatabase)) Triggers.Add(t);
        }
        catch (Exception ex) { _logger.LogError(ex, "Refresh triggers"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task RunQueryAsync()
    {
        if (!_dbm.IsAvailable || string.IsNullOrWhiteSpace(SqlText)) return;
        QueryColumns.Clear();
        QueryRows.Clear();
        ErrorMessage = null;
        try
        {
            var result = await _dbm.ExecuteAsync(SqlText, SelectedDatabase, RowLimit);
            if (result.ErrorMessage is not null) { ErrorMessage = result.ErrorMessage; return; }
            foreach (var c in result.Columns) QueryColumns.Add(c);
            foreach (var r in result.Rows)
            {
                var row = new ResultRow();
                for (int i = 0; i < r.Count; i++) row.Cells.Add(r[i]?.ToString() ?? "NULL");
                QueryRows.Add(row);
            }
            RowsAffected = result.RowsAffected;
            ElapsedMs = result.Elapsed.TotalMilliseconds;
            Status = result.RowsAffected is int affected
                ? $"{affected} row(s) affected in {ElapsedMs:F1}ms"
                : $"{QueryRows.Count} row(s) in {ElapsedMs:F1}ms";

            if (QueryHistory.Count >= 20) QueryHistory.RemoveAt(QueryHistory.Count - 1);
            QueryHistory.Insert(0, SqlText.Length > 80 ? SqlText[..80] + "…" : SqlText);
        }
        catch (Exception ex) { _logger.LogError(ex, "Run query"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task DropSelectedTableAsync()
    {
        if (!_dbm.IsAvailable || string.IsNullOrWhiteSpace(SelectedTable)) return;
        var quote = Kind == "MySql" ? "`" : "\"";
        SqlText = $"DROP TABLE {quote}{SelectedTable}{quote};";
        await RunQueryAsync();
        await RefreshTablesAsync();
    }

    [RelayCommand]
    public async Task TruncateSelectedTableAsync()
    {
        if (!_dbm.IsAvailable || string.IsNullOrWhiteSpace(SelectedTable)) return;
        var quote = Kind == "MySql" ? "`" : "\"";
        SqlText = Kind == "MySql"
            ? $"TRUNCATE TABLE {quote}{SelectedTable}{quote};"
            : $"DELETE FROM {quote}{SelectedTable}{quote};";
        await RunQueryAsync();
    }

    [RelayCommand]
    public async Task CreateTriggerAsync()
    {
        if (!_dbm.IsAvailable) return;
        if (string.IsNullOrWhiteSpace(TriggerName) || string.IsNullOrWhiteSpace(SelectedTable) || string.IsNullOrWhiteSpace(TriggerBody)) return;
        var quote = Kind == "MySql" ? "`" : "\"";
        var sql = $"CREATE TRIGGER {quote}{TriggerName}{quote} {TriggerTimingEvent} ON {quote}{SelectedTable}{quote} FOR EACH ROW BEGIN {TriggerBody} END";
        SqlText = sql;
        await RunQueryAsync();
        await RefreshTriggersAsync();
    }

    [RelayCommand]
    public void LoadFromHistory(string? sql)
    {
        if (!string.IsNullOrWhiteSpace(sql)) SqlText = sql.EndsWith("…") ? sql : sql;
    }

    public sealed class ResultRow
    {
        public List<string> Cells { get; } = new();
    }
}
