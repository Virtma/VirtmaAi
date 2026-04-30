using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Data;
using VirtmaAi.Services.Data.MySqlBootstrap;
using VirtmaAi.Services.FirstRun;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.ViewModels.FirstRun;

public enum FirstRunStep
{
    Welcome = 0,
    DataDirectory = 1,
    Database = 2,
    Provisioning = 3,
    Complete = 4
}

public sealed partial class FirstRunViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IFirstRunService _firstRun;
    private readonly IDatabaseService _database;
    private readonly IMySqlLifecycleService _mysqlLifecycle;
    private readonly SqliteProvisioner _sqlite;
    private readonly MySqlProvisioner _mysql;
    private readonly ILogger<FirstRunViewModel> _logger;

    public FirstRunViewModel(
        ISettingsService settings,
        IFirstRunService firstRun,
        IDatabaseService database,
        IMySqlLifecycleService mysqlLifecycle,
        SqliteProvisioner sqlite,
        MySqlProvisioner mysql,
        ILogger<FirstRunViewModel> logger)
    {
        _settings = settings;
        _firstRun = firstRun;
        _database = database;
        _mysqlLifecycle = mysqlLifecycle;
        _sqlite = sqlite;
        _mysql = mysql;
        _logger = logger;

        DataDirectory = _settings.DataDirectory;
        PreferMySql = _mysqlLifecycle.IsSupported;
        Step = FirstRunStep.Welcome;
    }

    [ObservableProperty]
    private FirstRunStep _step;

    [ObservableProperty]
    private string _dataDirectory = string.Empty;

    [ObservableProperty]
    private bool _preferMySql;

    [ObservableProperty]
    private string _progressStage = string.Empty;

    [ObservableProperty]
    private double _progressPercent;

    public bool IsMySqlSupported => _mysqlLifecycle.IsSupported;

    public string DefaultDataDirectory => Path.Combine(FileSystem.AppDataDirectory, "data");

    [RelayCommand]
    private void Next()
    {
        if (Step < FirstRunStep.Complete)
            Step = (FirstRunStep)((int)Step + 1);
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > FirstRunStep.Welcome)
            Step = (FirstRunStep)((int)Step - 1);
    }

    [RelayCommand]
    private void UseDefaultPath() => DataDirectory = DefaultDataDirectory;

    [RelayCommand]
    private void Launch() => _firstRun.NotifyCompleted();

    [RelayCommand]
    private async Task ProvisionAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(DataDirectory))
            {
                Directory.CreateDirectory(DataDirectory);
                _settings.SetDataDirectory(DataDirectory);
            }

            Step = FirstRunStep.Provisioning;
            var progress = new Progress<ProvisionProgress>(OnProgress);

            DatabaseConnectionInfo info;
            if (PreferMySql && IsMySqlSupported)
            {
                try
                {
                    info = await _mysql.EnsureProvisionedAsync(progress);
                }
                catch (MySqlNotInstalledException ex)
                {
                    _logger.LogWarning(ex, "MySQL unavailable — falling back to SQLite");
                    ErrorMessage = ex.Message + " Falling back to SQLite.";
                    info = await _sqlite.EnsureProvisionedAsync(progress);
                }
            }
            else
            {
                info = await _sqlite.EnsureProvisionedAsync(progress);
            }

            await _database.InitializeAsync(info);
            await _firstRun.MarkCompleteAsync();
            Step = FirstRunStep.Complete;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "First-run provisioning failed");
            ErrorMessage = ex.Message;
            Step = FirstRunStep.Database;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnProgress(ProvisionProgress p)
    {
        ProgressStage = p.Stage;
        if (p.Percent.HasValue) ProgressPercent = p.Percent.Value;
    }
}
