using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Notifications;
using VirtmaAi.Services.Themes;

namespace VirtmaAi.ViewModels.Settings;

public sealed partial class ThemesViewModel : ViewModelBase
{
    private readonly IThemeService _themes;
    private readonly IToastService _toast;
    private readonly ILogger<ThemesViewModel> _logger;

    public ThemesViewModel(IThemeService themes, IToastService toast, ILogger<ThemesViewModel> logger)
    {
        _themes = themes;
        _toast = toast;
        _logger = logger;
        _themes.ThemeChanged += (_, def) => ActiveName = def.Name;
        ActiveName = _themes.Active.Name;
    }

    public ObservableCollection<ThemeDefinition> All { get; } = new();

    [ObservableProperty]
    private ThemeDefinition? _selected;

    [ObservableProperty]
    private string? _exportJson;

    [ObservableProperty]
    private string? _importJson;

    [ObservableProperty]
    private string _activeName = string.Empty;

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            All.Clear();
            foreach (var t in _themes.GetBuiltIn()) All.Add(t);
            var user = await _themes.GetUserAsync();
            foreach (var t in user) All.Add(t);
            Selected ??= All.FirstOrDefault(t =>
                string.Equals(t.Name, _themes.Active.Name, StringComparison.OrdinalIgnoreCase))
                ?? All.FirstOrDefault();
            ActiveName = _themes.Active.Name;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load themes failed");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Could not load themes: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task ApplyAsync()
    {
        if (Selected is null)
        {
            await _toast.WarningAsync("Pick a theme first.");
            return;
        }
        try
        {
            await _themes.ApplyAsync(Selected);
            ActiveName = Selected.Name;
            await _toast.SuccessAsync($"Applied theme: {Selected.Name}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Apply theme failed");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Apply theme failed: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (Selected is null) return;
        try
        {
            await _themes.SaveAsync(Selected);
            await LoadAsync();
            await _toast.SuccessAsync($"Saved theme: {Selected.Name}");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Save theme failed: " + ex.Message);
        }
    }

    [RelayCommand]
    public void ExportSelected()
    {
        if (Selected is null) return;
        ExportJson = _themes.ExportToJson(Selected);
    }

    [RelayCommand]
    public async Task ImportAsync()
    {
        if (string.IsNullOrWhiteSpace(ImportJson))
        {
            await _toast.WarningAsync("Paste theme JSON first.");
            return;
        }
        try
        {
            var def = await _themes.ImportFromJsonAsync(ImportJson);
            await _themes.SaveAsync(def);
            await LoadAsync();
            Selected = All.FirstOrDefault(t => t.Name == def.Name) ?? Selected;
            await _toast.SuccessAsync($"Imported theme: {def.Name}");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Import failed: " + ex.Message);
        }
    }
}
