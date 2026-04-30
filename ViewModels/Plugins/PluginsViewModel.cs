using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Plugins;

namespace VirtmaAi.ViewModels.Plugins;

public sealed partial class PluginsViewModel : ViewModelBase
{
    private readonly IPluginHost _host;
    private readonly ILogger<PluginsViewModel> _logger;

    public PluginsViewModel(IPluginHost host, ILogger<PluginsViewModel> logger)
    {
        _host = host;
        _logger = logger;
        foreach (var b in _host.BuiltIn) BuiltIn.Add(new BuiltInPluginRow(b.Name, b.Description));
    }

    public ObservableCollection<Plugin> All { get; } = new();
    public ObservableCollection<BuiltInPluginRow> BuiltIn { get; } = new();

    [ObservableProperty] private Plugin? _selected;
    [ObservableProperty] private string _editorName = string.Empty;
    [ObservableProperty] private string _editorTriggers = string.Empty;
    [ObservableProperty] private string _editorInstructions = string.Empty;
    [ObservableProperty] private string _editorExePath = string.Empty;
    [ObservableProperty] private string _editorArgsTemplate = string.Empty;
    [ObservableProperty] private string _testInput = string.Empty;
    [ObservableProperty] private string _testOutput = string.Empty;

    partial void OnSelectedChanged(Plugin? value)
    {
        EditorName = value?.Name ?? string.Empty;
        EditorTriggers = value?.Triggers ?? "[]";
        EditorInstructions = value?.Instructions ?? string.Empty;
        EditorExePath = value?.ExecutablePath ?? string.Empty;
        EditorArgsTemplate = value?.ArgumentsTemplate ?? string.Empty;
        TestOutput = string.Empty;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            var list = await _host.ListAsync();
            All.Clear();
            foreach (var p in list) All.Add(p);
        }
        catch (Exception ex) { _logger.LogError(ex, "Load plugins"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public void New()
    {
        Selected = null;
        EditorName = "New plugin";
        EditorTriggers = "[]";
        EditorInstructions = string.Empty;
        EditorExePath = string.Empty;
        EditorArgsTemplate = string.Empty;
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(EditorName)) return;
        try
        {
            if (Selected is null)
            {
                await _host.CreateAsync(new Plugin
                {
                    Name = EditorName,
                    Triggers = EditorTriggers,
                    Instructions = EditorInstructions,
                    ExecutablePath = string.IsNullOrWhiteSpace(EditorExePath) ? null : EditorExePath,
                    ArgumentsTemplate = EditorArgsTemplate
                });
            }
            else
            {
                Selected.Name = EditorName;
                Selected.Triggers = EditorTriggers;
                Selected.Instructions = EditorInstructions;
                Selected.ExecutablePath = string.IsNullOrWhiteSpace(EditorExePath) ? null : EditorExePath;
                Selected.ArgumentsTemplate = EditorArgsTemplate;
                await _host.UpdateAsync(Selected);
            }
            await LoadAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "Save plugin"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (Selected is null) return;
        try { await _host.DeleteAsync(Selected.Id); Selected = null; await LoadAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Delete plugin"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task TestAsync()
    {
        if (Selected is null) { TestOutput = "(no plugin selected)"; return; }
        var result = await _host.InvokeAsync(Selected.Id, TestInput);
        TestOutput = result.Success
            ? result.Output
            : "ERROR: " + (result.Error ?? "unknown") + "\n\n" + result.Output;
    }
}

public sealed record BuiltInPluginRow(string Name, string Description);
