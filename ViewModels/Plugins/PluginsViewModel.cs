using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Notifications;
using VirtmaAi.Services.Plugins;
using VirtmaAi.Services.Plugins.BuiltIn;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.ViewModels.Plugins;

public sealed partial class PluginsViewModel : ViewModelBase
{
    private readonly IPluginHost _host;
    private readonly ISettingsService _settings;
    private readonly IToastService _toast;
    private readonly ILogger<PluginsViewModel> _logger;

    public PluginsViewModel(
        IPluginHost host,
        ISettingsService settings,
        IToastService toast,
        ILogger<PluginsViewModel> logger)
    {
        _host = host;
        _settings = settings;
        _toast = toast;
        _logger = logger;
        foreach (var b in _host.BuiltIn) BuiltIn.Add(new BuiltInPluginRow(b.Name, b.Description));

        // Load persisted web-search timeout setting.
        var saved = _settings.Get<int>(WebSearchPlugin.SettingTimeoutKey, WebSearchPlugin.DefaultTimeoutSeconds);
        _webSearchTimeoutText = saved.ToString();
    }

    public ObservableCollection<Plugin> All { get; } = new();
    public ObservableCollection<BuiltInPluginRow> BuiltIn { get; } = new();

    // ── User plugin editor ────────────────────────────────────────────────────────

    [ObservableProperty] private Plugin? _selected;
    [ObservableProperty] private string _editorName = string.Empty;
    [ObservableProperty] private string _editorTriggers = string.Empty;
    [ObservableProperty] private string _editorInstructions = string.Empty;
    [ObservableProperty] private string _editorExePath = string.Empty;
    [ObservableProperty] private string _editorArgsTemplate = string.Empty;
    [ObservableProperty] private string _testInput = string.Empty;
    [ObservableProperty] private string _testOutput = string.Empty;

    // ── Built-in plugin settings ─────────────────────────────────────────────────

    /// <summary>
    /// Text value of the web-search timeout entry (seconds).
    /// "0" means no timeout — wait indefinitely (user Stop is the only limit).
    /// </summary>
    [ObservableProperty]
    private string _webSearchTimeoutText = WebSearchPlugin.DefaultTimeoutSeconds.ToString();

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

    /// <summary>
    /// Persists the web-search timeout entered by the user.
    /// Validates that the input is a non-negative integer.
    /// </summary>
    [RelayCommand]
    public async Task SaveWebSearchSettingsAsync()
    {
        var trimmed = (WebSearchTimeoutText ?? string.Empty).Trim();
        if (!int.TryParse(trimmed, out var seconds) || seconds < 0)
        {
            ErrorMessage = "Timeout must be a non-negative whole number (0 = no timeout).";
            await _toast.WarningAsync("Enter a number ≥ 0 (0 = no timeout).");
            return;
        }

        _settings.Set(WebSearchPlugin.SettingTimeoutKey, seconds);
        ErrorMessage = null;
        var label = seconds == 0 ? "no timeout" : $"{seconds} s";
        await _toast.SuccessAsync($"Web search timeout saved: {label}.");
    }
}

public sealed record BuiltInPluginRow(string Name, string Description);
