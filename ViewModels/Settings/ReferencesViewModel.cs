using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.FileSystem;
using VirtmaAi.Services.Notifications;
using VirtmaAi.Services.References;

namespace VirtmaAi.ViewModels.Settings;

public sealed partial class ReferencesViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IReferenceService _service;
    private readonly IFilePickerService _filePicker;
    private readonly IToastService _toast;
    private readonly ILogger<ReferencesViewModel> _logger;

    public ReferencesViewModel(
        IReferenceService service,
        IFilePickerService filePicker,
        IToastService toast,
        ILogger<ReferencesViewModel> logger)
    {
        _service = service;
        _filePicker = filePicker;
        _toast = toast;
        _logger = logger;
        foreach (var t in Enum.GetValues<ReferenceSourceType>()) SourceTypes.Add(t);
    }

    public ObservableCollection<Reference> All { get; } = new();
    public ObservableCollection<ReferenceSourceType> SourceTypes { get; } = new();

    [ObservableProperty] private Reference? _selected;
    [ObservableProperty] private string _editorTitle = string.Empty;
    [ObservableProperty] private string _editorTriggers = string.Empty;
    [ObservableProperty] private ReferenceSourceType _editorSourceType = ReferenceSourceType.Text;
    [ObservableProperty] private string _editorSourceValue = string.Empty;
    [ObservableProperty] private string _editorAppliesTo = string.Empty;
    [ObservableProperty] private string _matchProbe = string.Empty;
    [ObservableProperty] private string _matchResults = string.Empty;

    public bool IsFileSource => EditorSourceType == ReferenceSourceType.File;
    public bool IsUrlSource => EditorSourceType == ReferenceSourceType.Url;
    public bool IsTextSource => EditorSourceType == ReferenceSourceType.Text;

    public string SourceValueLabel => EditorSourceType switch
    {
        ReferenceSourceType.File => "File path",
        ReferenceSourceType.Url => "URL",
        _ => "Text body"
    };

    public string SourceValuePlaceholder => EditorSourceType switch
    {
        ReferenceSourceType.File => "Click Browse to choose a file…",
        ReferenceSourceType.Url => "https://example.com/docs",
        _ => "Free-form text to remember"
    };

    partial void OnEditorSourceTypeChanged(ReferenceSourceType value)
    {
        OnPropertyChanged(nameof(IsFileSource));
        OnPropertyChanged(nameof(IsUrlSource));
        OnPropertyChanged(nameof(IsTextSource));
        OnPropertyChanged(nameof(SourceValueLabel));
        OnPropertyChanged(nameof(SourceValuePlaceholder));
    }

    partial void OnSelectedChanged(Reference? value)
    {
        EditorTitle = value?.Title ?? string.Empty;
        EditorTriggers = DisplayTriggers(value?.Triggers);
        EditorSourceType = value?.SourceType ?? ReferenceSourceType.Text;
        EditorSourceValue = value?.SourceValue ?? string.Empty;
        EditorAppliesTo = value?.AppliesTo ?? string.Empty;
    }

    private static string DisplayTriggers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "[]") return string.Empty;
        return raw;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            var list = await _service.ListAsync();
            All.Clear();
            foreach (var r in list) All.Add(r);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load references");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Could not load references: " + ex.Message);
        }
    }

    [RelayCommand]
    public void New()
    {
        Selected = null;
        EditorTitle = "New reference";
        EditorTriggers = string.Empty;
        EditorSourceType = ReferenceSourceType.Text;
        EditorSourceValue = string.Empty;
        EditorAppliesTo = string.Empty;
    }

    [RelayCommand]
    public async Task BrowseFileAsync()
    {
        var path = await _filePicker.PickFileAsync("Pick a reference file");
        if (!string.IsNullOrWhiteSpace(path))
        {
            EditorSourceValue = path;
        }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(EditorTitle))
        {
            await _toast.WarningAsync("Title is required.");
            return;
        }
        try
        {
            NormalizeTriggers();
            if (Selected is null)
            {
                await _service.CreateAsync(new Reference
                {
                    Title = EditorTitle,
                    Triggers = EditorTriggers,
                    SourceType = EditorSourceType,
                    SourceValue = EditorSourceValue,
                    AppliesTo = string.IsNullOrWhiteSpace(EditorAppliesTo) ? null : EditorAppliesTo,
                    CreatedBy = ReferenceCreator.User
                });
            }
            else
            {
                Selected.Title = EditorTitle;
                Selected.Triggers = EditorTriggers;
                Selected.SourceType = EditorSourceType;
                Selected.SourceValue = EditorSourceValue;
                Selected.AppliesTo = string.IsNullOrWhiteSpace(EditorAppliesTo) ? null : EditorAppliesTo;
                await _service.UpdateAsync(Selected);
            }
            await LoadAsync();
            await _toast.SuccessAsync("Reference saved: " + EditorTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save reference");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Save failed: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (Selected is null) return;
        try
        {
            await _service.DeleteAsync(Selected.Id);
            Selected = null;
            await LoadAsync();
            await _toast.SuccessAsync("Reference deleted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete reference");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Delete failed: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task ProbeAsync()
    {
        if (string.IsNullOrWhiteSpace(MatchProbe)) { MatchResults = "(enter a user message to match)"; return; }
        var matches = await _service.MatchAsync(MatchProbe);
        if (matches.Count == 0) { MatchResults = "(no matching references)"; return; }
        MatchResults = string.Join("\n", matches.Select(r => "\u2022 " + r.Title));
    }

    private void NormalizeTriggers()
    {
        if (string.IsNullOrWhiteSpace(EditorTriggers)) { EditorTriggers = "[]"; return; }
        try { JsonSerializer.Deserialize<List<string>>(EditorTriggers, JsonOpts); return; }
        catch { }
        var parts = EditorTriggers
            .Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('"'))
            .Where(p => p.Length > 0)
            .ToList();
        EditorTriggers = JsonSerializer.Serialize(parts, JsonOpts);
    }
}
