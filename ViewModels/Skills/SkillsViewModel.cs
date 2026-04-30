using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.FileSystem;
using VirtmaAi.Services.Notifications;
using VirtmaAi.Services.Skills;

namespace VirtmaAi.ViewModels.Skills;

public enum SkillContextKind { File, Url, Text }

public sealed partial class SkillContextEntry : ObservableObject
{
    public Guid UiId { get; } = Guid.NewGuid();

    [ObservableProperty]
    private SkillContextKind _kind;

    [ObservableProperty]
    private string _value = string.Empty;

    public string KindLabel => Kind switch
    {
        SkillContextKind.File => "File",
        SkillContextKind.Url => "URL",
        _ => "Text"
    };

    public string DisplayValue => Kind == SkillContextKind.Text && Value.Length > 120
        ? Value[..120] + "\u2026"
        : Value;

    partial void OnKindChanged(SkillContextKind value) => OnPropertyChanged(nameof(KindLabel));

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(DisplayValue));
}

public sealed partial class SkillsViewModel : ViewModelBase
{
    private readonly ISkillRegistry _skills;
    private readonly IFilePickerService _filePicker;
    private readonly IToastService _toast;
    private readonly ILogger<SkillsViewModel> _logger;

    public SkillsViewModel(
        ISkillRegistry skills,
        IFilePickerService filePicker,
        IToastService toast,
        ILogger<SkillsViewModel> logger)
    {
        _skills = skills;
        _filePicker = filePicker;
        _toast = toast;
        _logger = logger;
    }

    public ObservableCollection<Skill> All { get; } = new();
    public ObservableCollection<SkillContextEntry> ContextEntries { get; } = new();

    public IReadOnlyList<SkillContextKind> ContextKinds { get; } = new[]
    {
        SkillContextKind.File,
        SkillContextKind.Url,
        SkillContextKind.Text
    };

    [ObservableProperty]
    private Skill? _selected;

    [ObservableProperty]
    private string _editorName = string.Empty;

    [ObservableProperty]
    private string _editorTrigger = string.Empty;

    [ObservableProperty]
    private string _editorInstructions = string.Empty;

    [ObservableProperty]
    private bool _isContextModalOpen;

    [ObservableProperty]
    private SkillContextKind _draftKind = SkillContextKind.Text;

    [ObservableProperty]
    private string _draftValue = string.Empty;

    [ObservableProperty]
    private SkillContextEntry? _editingEntry;

    [ObservableProperty]
    private string? _importJson;

    [ObservableProperty]
    private string? _exportJson;

    partial void OnSelectedChanged(Skill? value)
    {
        EditorName = value?.Name ?? string.Empty;
        EditorTrigger = value?.TriggerDescription ?? string.Empty;
        EditorInstructions = value?.InstructionsMd ?? string.Empty;
        RebuildContextEntriesFrom(value);
    }

    private void RebuildContextEntriesFrom(Skill? skill)
    {
        ContextEntries.Clear();
        if (skill is null) return;
        foreach (var cf in skill.ContextFiles)
        {
            if (!string.IsNullOrEmpty(cf.FilePath))
            {
                ContextEntries.Add(new SkillContextEntry
                {
                    Kind = SkillContextKind.File,
                    Value = cf.FilePath
                });
            }
            else if (!string.IsNullOrEmpty(cf.Text))
            {
                var kind = IsUrl(cf.Text) ? SkillContextKind.Url : SkillContextKind.Text;
                ContextEntries.Add(new SkillContextEntry
                {
                    Kind = kind,
                    Value = cf.Text
                });
            }
        }
    }

    private static bool IsUrl(string s)
        => Uri.TryCreate(s.Trim(), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            var list = await _skills.ListAsync();
            All.Clear();
            foreach (var s in list) All.Add(s);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load skills");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Could not load skills: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task ToggleEnabledAsync(Skill? skill)
    {
        if (skill is null) return;
        try
        {
            await _skills.SetEnabledAsync(skill.Id, skill.Enabled);
            // No toast — the Switch itself provides immediate visual feedback.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggle skill enabled");
            await _toast.ErrorAsync("Could not change skill: " + ex.Message);
        }
    }

    [RelayCommand]
    public void New()
    {
        Selected = null;
        EditorName = "New skill";
        EditorTrigger = string.Empty;
        EditorInstructions = string.Empty;
        ContextEntries.Clear();
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(EditorName))
        {
            await _toast.WarningAsync("Name is required.");
            return;
        }
        try
        {
            var fileOrUrlEntries = ContextEntries
                .Where(e => e.Kind == SkillContextKind.File || e.Kind == SkillContextKind.Url)
                .Select(e => e.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();
            // Send each text entry separately so we don't lose distinct context references —
            // the previous "join with ---" approach merged them into a single blob and the
            // user saw fewer entries than they added when reopening the skill.
            var textEntries = ContextEntries
                .Where(e => e.Kind == SkillContextKind.Text && !string.IsNullOrWhiteSpace(e.Value))
                .Select(e => e.Value)
                .ToList();

            var def = new SkillDefinition
            {
                Name = EditorName,
                TriggerDescription = EditorTrigger,
                Instructions = EditorInstructions,
                ContextFiles = fileOrUrlEntries,
                ContextTexts = textEntries
            };
            if (Selected is null) await _skills.CreateAsync(def);
            else await _skills.UpdateAsync(Selected.Id, def);
            await LoadAsync();
            await _toast.SuccessAsync("Skill saved: " + EditorName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save skill");
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
            await _skills.DeleteAsync(Selected.Id);
            Selected = null;
            await LoadAsync();
            await _toast.SuccessAsync("Skill deleted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete skill");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Delete failed: " + ex.Message);
        }
    }

    [RelayCommand]
    public void OpenAddContext()
    {
        EditingEntry = null;
        DraftKind = SkillContextKind.Text;
        DraftValue = string.Empty;
        IsContextModalOpen = true;
    }

    [RelayCommand]
    public void EditContext(SkillContextEntry? entry)
    {
        if (entry is null) return;
        EditingEntry = entry;
        DraftKind = entry.Kind;
        DraftValue = entry.Value;
        IsContextModalOpen = true;
    }

    [RelayCommand]
    public void DeleteContext(SkillContextEntry? entry)
    {
        if (entry is null) return;
        ContextEntries.Remove(entry);
    }

    /// <summary>Opens a file picker and populates DraftValue with the chosen path.</summary>
    [RelayCommand]
    public async Task BrowseContextFileAsync()
    {
        var path = await _filePicker.PickFileAsync("Select context file").ConfigureAwait(true);
        if (path is null) return;
        DraftKind  = SkillContextKind.File;
        DraftValue = path;
    }

    [RelayCommand]
    public async Task CommitContextAsync()
    {
        if (string.IsNullOrWhiteSpace(DraftValue))
        {
            await _toast.WarningAsync("Enter a value.");
            return;
        }
        if (EditingEntry is null)
        {
            ContextEntries.Add(new SkillContextEntry
            {
                Kind = DraftKind,
                Value = DraftValue.Trim()
            });
        }
        else
        {
            EditingEntry.Kind = DraftKind;
            EditingEntry.Value = DraftValue.Trim();
        }
        IsContextModalOpen = false;
        EditingEntry = null;
        DraftValue = string.Empty;
    }

    [RelayCommand]
    public void CancelContext()
    {
        IsContextModalOpen = false;
        EditingEntry = null;
        DraftValue = string.Empty;
    }

    [RelayCommand]
    public void ExportSelected()
    {
        if (Selected is null) return;
        ExportJson = _skills.ExportJson(Selected);
    }

    [RelayCommand]
    public async Task ImportAsync()
    {
        if (string.IsNullOrWhiteSpace(ImportJson)) return;
        try
        {
            var def = await _skills.ImportJsonAsync(ImportJson);
            await _skills.CreateAsync(def);
            await LoadAsync();
            await _toast.SuccessAsync("Skill imported: " + def.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import skill");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Import failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Opens a file picker and imports the selected skill file.  Supports VirtmaAi
    /// native .vskill.json, generic .json (Claude / other apps), and .md / .txt files.
    /// </summary>
    [RelayCommand]
    public async Task ImportFromFileAsync()
    {
        try
        {
            var path = await _filePicker.PickFileAsync(
                "Import skill file",
                new[] { ".vskill.json", ".json", ".md", ".txt" }).ConfigureAwait(true);
            if (path is null) return;

            var def = await _skills.ImportFromFileAsync(path);
            await _skills.CreateAsync(def);
            await LoadAsync();
            await _toast.SuccessAsync("Skill imported: " + def.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import skill from file");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Import failed: " + ex.Message);
        }
    }
}
