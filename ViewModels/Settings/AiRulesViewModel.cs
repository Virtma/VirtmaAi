using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.AI;
using VirtmaAi.Services.FileSystem;
using VirtmaAi.Services.Notifications;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.ViewModels.Settings;

public sealed partial class AiRulesViewModel : ViewModelBase
{
    public const string ShowThinkingSettingKey = "chat.showThinking";
    public const string AllowSelfModifySettingKey = "ai.selfmodify.allowed";
    public const string AppRootSettingKey = "ai.selfmodify.appRoot";

    private readonly IAiRulesService _rules;
    private readonly ISettingsService _settings;
    private readonly IFilePickerService _filePicker;
    private readonly IToastService _toast;
    private readonly ILogger<AiRulesViewModel> _logger;

    public AiRulesViewModel(
        IAiRulesService rules,
        ISettingsService settings,
        IFilePickerService filePicker,
        IToastService toast,
        ILogger<AiRulesViewModel> logger)
    {
        _rules = rules;
        _settings = settings;
        _filePicker = filePicker;
        _toast = toast;
        _logger = logger;
        _showThinking       = _settings.Get<bool>(ShowThinkingSettingKey, defaultValue: true);
        _allowSelfModify    = _settings.Get<bool>(AllowSelfModifySettingKey, defaultValue: false);
        _selfModifyAppRoot  = _settings.Get<string>(AppRootSettingKey) ?? string.Empty;
    }

    public ObservableCollection<AiRule> Rules { get; } = new();

    [ObservableProperty] private AiRule? _selected;
    [ObservableProperty] private string _editorTitle = string.Empty;
    [ObservableProperty] private string _editorBody = string.Empty;
    [ObservableProperty] private int _editorPriority = 100;
    [ObservableProperty] private bool _editorEnabled = true;

    // ===== Global toggles surfaced on the same page =====
    [ObservableProperty] private bool _showThinking;
    [ObservableProperty] private bool _allowSelfModify;
    [ObservableProperty] private string _selfModifyAppRoot;

    partial void OnShowThinkingChanged(bool value)      => _settings.Set(ShowThinkingSettingKey, value);
    partial void OnAllowSelfModifyChanged(bool value)   => _settings.Set(AllowSelfModifySettingKey, value);
    partial void OnSelfModifyAppRootChanged(string value) => _settings.Set(AppRootSettingKey, value ?? string.Empty);

    partial void OnSelectedChanged(AiRule? value)
    {
        EditorTitle    = value?.Title ?? string.Empty;
        EditorBody     = value?.Body ?? string.Empty;
        EditorPriority = value?.Priority ?? 100;
        EditorEnabled  = value?.Enabled ?? true;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            var list = await _rules.ListAsync();
            Rules.Clear();
            foreach (var r in list) Rules.Add(r);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load rules");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Could not load rules: " + ex.Message);
        }
    }

    [RelayCommand]
    public void New()
    {
        Selected = null;
        EditorTitle = "New rule";
        EditorBody = string.Empty;
        EditorPriority = 100;
        EditorEnabled = true;
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(EditorBody))
        {
            await _toast.WarningAsync("Rule body is required.");
            return;
        }
        try
        {
            if (Selected is null)
            {
                var created = await _rules.CreateAsync(new AiRule
                {
                    Title = EditorTitle ?? string.Empty,
                    Body = EditorBody,
                    Priority = EditorPriority,
                    Enabled = EditorEnabled
                });
                await _toast.SuccessAsync($"Rule added: {created.Title}");
            }
            else
            {
                Selected.Title = EditorTitle ?? string.Empty;
                Selected.Body = EditorBody;
                Selected.Priority = EditorPriority;
                Selected.Enabled = EditorEnabled;
                await _rules.UpdateAsync(Selected);
                await _toast.SuccessAsync("Rule updated.");
            }
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save rule");
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
            await _rules.DeleteAsync(Selected.Id);
            Selected = null;
            await LoadAsync();
            await _toast.SuccessAsync("Rule deleted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete rule");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Delete failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Opens a file picker and appends the file's content to <see cref="EditorBody"/>
    /// so the user can anchor a rule to a specific document (README, spec, schema, etc.).
    /// Supports PDF (PdfPig text extraction), plain text, and code files up to 512 KB.
    /// </summary>
    [RelayCommand]
    public async Task AttachFileAsync()
    {
        try
        {
            var path = await _filePicker.PickFileAsync("Attach file to rule").ConfigureAwait(true);
            if (path is null) return;

            const long maxBytes = 512 * 1024;
            string fileText;
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (ext == "pdf")
            {
                using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
                var sb = new StringBuilder();
                foreach (var page in doc.GetPages())
                    sb.Append(page.Text).Append('\n');
                fileText = sb.ToString();
                if (fileText.Length > maxBytes) fileText = fileText[..(int)maxBytes] + "\n…[truncated]";
            }
            else
            {
                var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
                if (bytes.Length > maxBytes)
                    fileText = Encoding.UTF8.GetString(bytes[..(int)maxBytes]) + "\n…[truncated]";
                else
                    fileText = Encoding.UTF8.GetString(bytes);
            }

            var fileName = Path.GetFileName(path);
            var separator = string.IsNullOrWhiteSpace(EditorBody) ? string.Empty : "\n\n";
            EditorBody += $"{separator}--- Attached: {fileName} ---\n{fileText}";
            await _toast.ShowAsync($"Attached: {fileName}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Attach file to rule");
            await _toast.ErrorAsync("Could not attach file: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task ToggleEnabledAsync(AiRule? rule)
    {
        if (rule is null) return;
        try
        {
            await _rules.SetEnabledAsync(rule.Id, rule.Enabled);
            await _toast.ShowAsync(rule.Enabled ? $"Rule enabled: {rule.Title}" : $"Rule disabled: {rule.Title}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggle rule");
            await _toast.ErrorAsync("Toggle failed: " + ex.Message);
        }
    }
}
