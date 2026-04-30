using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VirtmaAi.Services.Notifications;

namespace VirtmaAi.ViewModels.Settings;

public sealed partial class LogsViewModel : ViewModelBase
{
    private readonly IErrorLogService _errorLog;

    public LogsViewModel(IErrorLogService errorLog)
    {
        _errorLog = errorLog;
        Entries = errorLog.Entries;
    }

    public ReadOnlyObservableCollection<LogEntry> Entries { get; }

    [ObservableProperty]
    private bool _showInfo = true;

    [ObservableProperty]
    private bool _showWarning = true;

    [ObservableProperty]
    private bool _showError = true;

    [RelayCommand]
    public void Clear() => _errorLog.Clear();

    [RelayCommand]
    public void ToggleInfo() => ShowInfo = !ShowInfo;

    [RelayCommand]
    public void ToggleWarning() => ShowWarning = !ShowWarning;

    [RelayCommand]
    public void ToggleError() => ShowError = !ShowError;
}
