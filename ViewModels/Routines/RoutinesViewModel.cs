using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Routines;

namespace VirtmaAi.ViewModels.Routines;

public sealed partial class RoutinesViewModel : ViewModelBase
{
    private readonly IRoutineScheduler _scheduler;
    private readonly ILogger<RoutinesViewModel> _logger;

    public RoutinesViewModel(IRoutineScheduler scheduler, ILogger<RoutinesViewModel> logger)
    {
        _scheduler = scheduler;
        _logger = logger;
        ResponseHandlings = new ObservableCollection<RoutineResponseHandling>(Enum.GetValues<RoutineResponseHandling>());
    }

    public ObservableCollection<Routine> All { get; } = new();
    public ObservableCollection<RoutineResponseHandling> ResponseHandlings { get; }

    [ObservableProperty] private Routine? _selected;
    [ObservableProperty] private string _editorName = string.Empty;
    [ObservableProperty] private string _editorCron = "*/5 * * * *";
    [ObservableProperty] private string _editorInstructions = string.Empty;
    [ObservableProperty] private RoutineResponseHandling _editorResponseHandling = RoutineResponseHandling.Log;
    [ObservableProperty] private string _editorResponseTarget = string.Empty;
    [ObservableProperty] private bool _editorEnabled = true;
    [ObservableProperty] private string _importJson = string.Empty;
    [ObservableProperty] private string _exportedJson = string.Empty;

    partial void OnSelectedChanged(Routine? value)
    {
        EditorName = value?.Name ?? string.Empty;
        EditorCron = value?.CronExpression ?? "*/5 * * * *";
        EditorInstructions = value?.Instructions ?? string.Empty;
        EditorResponseHandling = value?.ResponseHandling ?? RoutineResponseHandling.Log;
        EditorResponseTarget = value?.ResponseTarget ?? string.Empty;
        EditorEnabled = value?.Enabled ?? true;
        ExportedJson = value is null ? string.Empty : _scheduler.ExportJson(value);
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            var list = await _scheduler.ListAsync();
            All.Clear();
            foreach (var r in list) All.Add(r);
        }
        catch (Exception ex) { _logger.LogError(ex, "Load routines"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public void New()
    {
        Selected = null;
        EditorName = "New routine";
        EditorCron = "*/5 * * * *";
        EditorInstructions = string.Empty;
        EditorResponseHandling = RoutineResponseHandling.Log;
        EditorResponseTarget = string.Empty;
        EditorEnabled = true;
        ExportedJson = string.Empty;
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(EditorName) || string.IsNullOrWhiteSpace(EditorCron)) return;
        try
        {
            if (Selected is null)
            {
                await _scheduler.CreateAsync(new Routine
                {
                    Name = EditorName,
                    CronExpression = EditorCron,
                    Instructions = EditorInstructions,
                    ResponseHandling = EditorResponseHandling,
                    ResponseTarget = string.IsNullOrWhiteSpace(EditorResponseTarget) ? null : EditorResponseTarget,
                    Enabled = EditorEnabled
                });
            }
            else
            {
                Selected.Name = EditorName;
                Selected.CronExpression = EditorCron;
                Selected.Instructions = EditorInstructions;
                Selected.ResponseHandling = EditorResponseHandling;
                Selected.ResponseTarget = string.IsNullOrWhiteSpace(EditorResponseTarget) ? null : EditorResponseTarget;
                Selected.Enabled = EditorEnabled;
                await _scheduler.UpdateAsync(Selected);
            }
            await LoadAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "Save routine"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (Selected is null) return;
        try { await _scheduler.DeleteAsync(Selected.Id); Selected = null; await LoadAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Delete routine"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task RunNowAsync()
    {
        if (Selected is null) return;
        try { await _scheduler.RunNowAsync(Selected.Id, CancellationToken.None); await LoadAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Run now"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task ImportAsync()
    {
        if (string.IsNullOrWhiteSpace(ImportJson)) return;
        try
        {
            var draft = await _scheduler.ImportJsonAsync(ImportJson);
            var created = await _scheduler.CreateAsync(draft);
            ImportJson = string.Empty;
            await LoadAsync();
            Selected = All.FirstOrDefault(r => r.Id == created.Id);
        }
        catch (Exception ex) { _logger.LogError(ex, "Import routine"); ErrorMessage = ex.Message; }
    }
}
