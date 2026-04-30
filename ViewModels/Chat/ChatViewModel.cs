using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.AI;
using VirtmaAi.Services.Data;
using VirtmaAi.Services.FileSystem;
using VirtmaAi.Services.Notifications;
using VirtmaAi.Services.Plugins;
using VirtmaAi.Services.References;
using VirtmaAi.Services.Routines;
using VirtmaAi.Services.Settings;
using VirtmaAi.Services.Skills;
using VirtmaAi.Services.Themes;
using VirtmaAi.ViewModels.Preview;

namespace VirtmaAi.ViewModels.Chat;

public sealed partial class ChatViewModel : ViewModelBase
{
    private const string SettingLastMode = "chat.lastMode";
    private const string SettingLastModel = "chat.lastModel";

    private static readonly Uri DefaultOllamaUri = new("http://localhost:11434/");

    private static readonly IReadOnlyDictionary<string, string[]> FallbackProviderModels =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ollama"] = new[] { "llama3.2", "llama3.1", "mistral", "phi4" },
            ["anthropic"] = new[] { "claude-sonnet-4-6", "claude-opus-4-7", "claude-haiku-4-5-20251001" },
            ["openai"] = new[] { "gpt-4o", "gpt-4o-mini", "gpt-4-turbo" }
        };

    private const string SettingShowThinking = "chat.showThinking";

    private readonly IDatabaseService _db;
    private readonly IProviderRouter _router;
    private readonly ISandboxedFileSystem _sandbox;
    private readonly ISkillMatcher _skillMatcher;
    private readonly ISkillRegistry _skillRegistry;
    private readonly IThemeService _themes;
    private readonly IRoutineScheduler _routines;
    private readonly IReferenceService _references;
    private readonly IAiRulesService _aiRules;
    private readonly IOllamaRegistryClient _ollama;
    private readonly ISettingsService _settings;
    private readonly IToastService _toast;
    private readonly IFolderPickerService _folderPicker;
    private readonly IFilePickerService  _filePicker;
    private readonly IPluginHost _plugins;
    private readonly ILogger<ChatViewModel> _logger;
    private CancellationTokenSource? _streamCts;
    private bool _suppressModeSave;
    private bool _loadingConversation;

    public ChatViewModel(
        IDatabaseService db,
        IProviderRouter router,
        ISandboxedFileSystem sandbox,
        ISkillMatcher skillMatcher,
        ISkillRegistry skillRegistry,
        IThemeService themes,
        IRoutineScheduler routines,
        IReferenceService references,
        IAiRulesService aiRules,
        IOllamaRegistryClient ollama,
        ISettingsService settings,
        IToastService toast,
        IFolderPickerService folderPicker,
        IFilePickerService filePicker,
        IPluginHost plugins,
        PreviewViewModel preview,
        ILogger<ChatViewModel> logger)
    {
        _db = db;
        _router = router;
        _sandbox = sandbox;
        _skillMatcher = skillMatcher;
        _skillRegistry = skillRegistry;
        _themes = themes;
        _routines = routines;
        _references = references;
        _aiRules = aiRules;
        _ollama = ollama;
        _settings = settings;
        _toast = toast;
        _folderPicker = folderPicker;
        _filePicker = filePicker;
        _plugins = plugins;
        Preview = preview;
        _logger = logger;

        AvailableModes = new[] { ConversationMode.Chat, ConversationMode.Code, ConversationMode.CoWork };

        _suppressModeSave = true;
        var savedMode = _settings.Get<int>(SettingLastMode, (int)ConversationMode.Chat);
        if (Enum.IsDefined(typeof(ConversationMode), savedMode))
            NewConversationMode = (ConversationMode)savedMode;
        _suppressModeSave = false;
    }

    public PreviewViewModel Preview { get; }

    public ObservableCollection<ConversationListItem> Conversations { get; } = new();
    public ObservableCollection<ChatMessageItem> Messages { get; } = new();
    public ObservableCollection<ModelOption> AvailableModels { get; } = new();
    public ObservableCollection<object> SelectedConversations { get; } = new();

    public IReadOnlyList<ConversationMode> AvailableModes { get; }

    [ObservableProperty]
    private ConversationListItem? _activeConversation;

    [ObservableProperty]
    private string _draft = string.Empty;

    [ObservableProperty]
    private string _selectedProviderId = "ollama";

    [ObservableProperty]
    private string _selectedModelId = "llama3.2";

    [ObservableProperty]
    private ModelOption? _selectedModel;

    [ObservableProperty]
    private bool _isLoadingModels;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private ConversationMode _newConversationMode = ConversationMode.Chat;

    [ObservableProperty]
    private string? _projectDir;

    [ObservableProperty]
    private bool _isMultiSelectMode;

    [ObservableProperty]
    private bool _isEditingTitle;

    [ObservableProperty]
    private string _titleDraft = string.Empty;

    [ObservableProperty]
    private string? _detectedThemeJson;

    [ObservableProperty]
    private string? _detectedSkillJson;

    [ObservableProperty]
    private string? _detectedRoutineJson;

    /// <summary>
    /// Last URL or local file path detected in an assistant message that hasn't been
    /// previewed yet. Bound to the notice banner's "Open in viewer" button.
    /// </summary>
    [ObservableProperty]
    private string? _detectedMediaUrl;

    /// <summary>
    /// Files the user has attached to the current draft message. They are included
    /// as inline text blocks when the message is sent, then cleared.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<string> AttachedFiles { get; } = new();

    [ObservableProperty]
    private string? _notice;

    public IReadOnlyCollection<IAiProvider> AvailableProviders => _router.All;

    partial void OnSelectedModelChanged(ModelOption? value)
    {
        if (value is null) return;
        SelectedProviderId = value.ProviderId;
        SelectedModelId = value.ModelId;
        try { _settings.Set(SettingLastModel, value.Key); }
        catch (Exception ex) { _logger.LogWarning(ex, "Persist last model failed"); }
    }

    partial void OnNewConversationModeChanged(ConversationMode value)
    {
        if (_suppressModeSave) return;
        // Persist as the default for the next new conversation.
        try { _settings.Set(SettingLastMode, (int)value); }
        catch (Exception ex) { _logger.LogWarning(ex, "Persist last mode failed"); }
        // ALSO update the currently-open conversation's stored mode so the picker is per-conversation.
        if (ActiveConversation is { } active)
        {
            active.Mode = value;
            _ = SaveModeForConversationAsync(active.Id, value);
        }
    }

    private async Task SaveModeForConversationAsync(Guid conversationId, ConversationMode mode)
    {
        try
        {
            await using var ctx = _db.CreateContext();
            var conv = await ctx.Conversations.FindAsync(conversationId);
            if (conv is null || conv.Mode == mode) return;
            conv.Mode = mode;
            conv.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Save conversation mode failed");
        }
    }

    partial void OnActiveConversationChanged(ConversationListItem? value)
    {
        IsEditingTitle = false;
        TitleDraft = value?.Title ?? string.Empty;
        if (value is null)
        {
            Messages.Clear();
            return;
        }
        // Fire-and-forget the load; OpenAsync drives this same path explicitly too,
        // and a re-entry guard prevents double-loading when OpenAsync sets ActiveConversation.
        _ = LoadConversationMessagesAsync(value);
    }

    private async Task LoadConversationMessagesAsync(ConversationListItem item)
    {
        if (_loadingConversation) return;
        _loadingConversation = true;
        try
        {
            Messages.Clear();
            await using var ctx = _db.CreateContext();
            var conv = await ctx.Conversations.FindAsync(item.Id);
            var msgs = await ctx.Messages
                .Where(m => m.ConversationId == item.Id)
                .OrderBy(m => m.CreatedAt)
                .ToListAsync();
            foreach (var m in msgs) Messages.Add(new ChatMessageItem(m));

            ProjectDir = conv?.ProjectDir;
            TrySetSandboxRoot(ProjectDir);

            if (conv is not null)
            {
                _suppressModeSave = true;
                NewConversationMode = conv.Mode;
                _suppressModeSave = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open conversation failed");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Open failed: " + ex.Message);
        }
        finally
        {
            _loadingConversation = false;
        }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        await RefreshModelsAsync();

        if (_db.Current is null) return;
        try
        {
            await using var ctx = _db.CreateContext();
            var list = await ctx.Conversations
                .OrderByDescending(c => c.UpdatedAt)
                .Take(100)
                .ToListAsync();

            // Preserve the user's current selection across reloads. Conversations.Clear() can
            // null out ActiveConversation via the SelectedItem TwoWay binding, so capture the
            // intended id BEFORE clearing the list.
            var preserveId = ActiveConversation?.Id;
            ActiveConversation = null;
            Conversations.Clear();
            foreach (var c in list) Conversations.Add(new ConversationListItem(c));

            ConversationListItem? toOpen = null;
            if (preserveId is Guid id)
                toOpen = Conversations.FirstOrDefault(c => c.Id == id);
            toOpen ??= Conversations.FirstOrDefault();

            if (toOpen is not null)
                ActiveConversation = toOpen; // change handler loads messages
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load conversations failed");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Could not load conversations: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task RefreshModelsAsync()
    {
        if (IsLoadingModels) return;
        IsLoadingModels = true;
        try
        {
            var previouslySelected = SelectedModel?.Key
                ?? _settings.Get<string>(SettingLastModel);

            var options = new List<ModelOption>();

            foreach (var provider in _router.All)
            {
                if (string.Equals(provider.Id, "ollama", StringComparison.OrdinalIgnoreCase))
                {
                    var detected = await _ollama.ListInstalledAsync(DefaultOllamaUri).ConfigureAwait(true);
                    if (detected.Count > 0)
                    {
                        foreach (var m in detected)
                            options.Add(new ModelOption(provider.Id, provider.DisplayName, m.Name));
                    }
                    else if (FallbackProviderModels.TryGetValue(provider.Id, out var fallback))
                    {
                        foreach (var name in fallback)
                            options.Add(new ModelOption(provider.Id, provider.DisplayName, name));
                    }
                }
                else if (string.Equals(provider.Id, "llamasharp", StringComparison.OrdinalIgnoreCase))
                {
                    // Scan the user's models directory for .gguf files.
                    // Only compiled on Windows — the LlamaSharpProvider is only registered there.
#if WINDOWS
                    ScanGgufModels(options, provider);
#endif
                }
                else if (FallbackProviderModels.TryGetValue(provider.Id, out var fallback))
                {
                    foreach (var name in fallback)
                        options.Add(new ModelOption(provider.Id, provider.DisplayName, name));
                }
            }

            AvailableModels.Clear();
            foreach (var opt in options) AvailableModels.Add(opt);

            if (!string.IsNullOrWhiteSpace(previouslySelected))
            {
                SelectedModel = AvailableModels.FirstOrDefault(o =>
                    string.Equals(o.Key, previouslySelected, StringComparison.OrdinalIgnoreCase));
            }
            SelectedModel ??= AvailableModels.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refresh models failed");
            await _toast.WarningAsync("Could not list models: " + ex.Message);
        }
        finally
        {
            IsLoadingModels = false;
        }
    }

    /// <summary>
    /// Searches the VirtmaAi models directory for .gguf files and adds each as a
    /// <see cref="ModelOption"/> backed by the LlamaSharp provider.  The ModelId is the
    /// full absolute path so <see cref="Services.AI.Providers.LlamaSharpProvider"/> can
    /// locate and load the file directly.
    /// </summary>
    private static void ScanGgufModels(List<ModelOption> options, IAiProvider provider)
    {
        // Canonical models directory: ~/.virtmaai/models/ (same as the plan's Phase 1 layout).
        // Also check FileSystem.AppDataDirectory/models as a fallback for portable installs.
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".virtmaai", "models"),
            Path.Combine(FileSystem.AppDataDirectory, "models"),
        };

        foreach (var dir in roots)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.gguf",
                    SearchOption.AllDirectories))
                {
                    // Use the file name (without extension) as the display name.
                    var displayName = Path.GetFileNameWithoutExtension(file);
                    options.Add(new ModelOption(provider.Id, provider.DisplayName, file)
                    {
                        // ModelOption.DisplayLabel already formats "provider • modelId";
                        // since modelId is a long path, we override ToString so the picker
                        // shows just the file name.
                    });
                }
            }
            catch (Exception)
            {
                // Permission issues etc. — skip silently.
            }
        }
    }

    [RelayCommand]
    public async Task NewAsync()
    {
        try
        {
            await using var ctx = _db.CreateContext();
            var conv = new Conversation
            {
                Title = "New conversation",
                Mode = NewConversationMode
            };
            ctx.Conversations.Add(conv);
            await ctx.SaveChangesAsync();
            var item = new ConversationListItem(conv);
            Conversations.Insert(0, item);
            await OpenAsync(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Create conversation failed");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Could not create conversation: " + ex.Message);
        }
    }

    [RelayCommand]
    public Task OpenAsync(ConversationListItem? item)
    {
        if (item is null) return Task.CompletedTask;
        // Setting ActiveConversation triggers OnActiveConversationChanged → LoadConversationMessagesAsync.
        // If it's already the active one, force a reload by routing through the helper.
        if (ReferenceEquals(item, ActiveConversation))
            return LoadConversationMessagesAsync(item);
        ActiveConversation = item;
        return Task.CompletedTask;
    }

    [RelayCommand]
    public async Task BrowseProjectDirAsync()
    {
        if (ActiveConversation is null)
        {
            await _toast.WarningAsync("Open a conversation first.");
            return;
        }
        try
        {
            var picked = await _folderPicker.PickFolderAsync(ProjectDir);
            if (string.IsNullOrWhiteSpace(picked)) return;
            await SetProjectDirAsync(picked);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browse project dir failed");
            await _toast.ErrorAsync("Could not open folder picker: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task SetProjectDirAsync(string? path)
    {
        if (ActiveConversation is null) return;
        try
        {
            await using var ctx = _db.CreateContext();
            var conv = await ctx.Conversations.FindAsync(ActiveConversation.Id);
            if (conv is null) return;
            conv.ProjectDir = string.IsNullOrWhiteSpace(path) ? null : path;
            conv.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
            ProjectDir = conv.ProjectDir;
            TrySetSandboxRoot(ProjectDir);
            await _toast.SuccessAsync(string.IsNullOrWhiteSpace(path)
                ? "Project directory cleared."
                : "Project directory set.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Set project dir failed");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Could not set project directory: " + ex.Message);
        }
    }

    private void TrySetSandboxRoot(string? path)
    {
        try { _sandbox.SetProjectRoot(path); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sandbox root rejected");
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    public async Task DeleteAsync(ConversationListItem? item)
    {
        if (item is null) return;
        try
        {
            await using var ctx = _db.CreateContext();
            var conv = await ctx.Conversations.FindAsync(item.Id);
            if (conv is null) return;
            var msgIds = await ctx.Messages.Where(m => m.ConversationId == item.Id).Select(m => m.Id).ToListAsync();
            if (msgIds.Count > 0)
            {
                var attachments = ctx.MessageAttachments.Where(a => msgIds.Contains(a.MessageId));
                ctx.MessageAttachments.RemoveRange(attachments);
            }
            var graphs = ctx.GraphifyGraphs.Where(g => g.ConversationId == item.Id);
            ctx.GraphifyGraphs.RemoveRange(graphs);
            var msgs = ctx.Messages.Where(m => m.ConversationId == item.Id);
            ctx.Messages.RemoveRange(msgs);
            ctx.Conversations.Remove(conv);
            await ctx.SaveChangesAsync();
            Conversations.Remove(item);
            if (ActiveConversation == item)
            {
                ActiveConversation = null;
                Messages.Clear();
            }
            await _toast.SuccessAsync("Conversation deleted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete conversation failed");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Delete failed: " + ex.Message);
        }
    }

    [RelayCommand]
    public void ToggleMultiSelect()
    {
        IsMultiSelectMode = !IsMultiSelectMode;
        if (!IsMultiSelectMode) SelectedConversations.Clear();
    }

    [RelayCommand]
    public void SelectAllConversations()
    {
        SelectedConversations.Clear();
        foreach (var c in Conversations) SelectedConversations.Add(c);
    }

    [RelayCommand]
    public void DeselectAllConversations() => SelectedConversations.Clear();

    [RelayCommand]
    public async Task DeleteSelectedAsync()
    {
        if (SelectedConversations.Count == 0)
        {
            await _toast.WarningAsync("Nothing selected.");
            return;
        }
        var items = SelectedConversations.OfType<ConversationListItem>().ToList();
        try
        {
            await using var ctx = _db.CreateContext();
            foreach (var item in items)
            {
                var conv = await ctx.Conversations.FindAsync(item.Id);
                if (conv is null) continue;
                var msgIds = await ctx.Messages.Where(m => m.ConversationId == item.Id).Select(m => m.Id).ToListAsync();
                if (msgIds.Count > 0)
                {
                    var attachments = ctx.MessageAttachments.Where(a => msgIds.Contains(a.MessageId));
                    ctx.MessageAttachments.RemoveRange(attachments);
                }
                var graphs = ctx.GraphifyGraphs.Where(g => g.ConversationId == item.Id);
                ctx.GraphifyGraphs.RemoveRange(graphs);
                var msgs = ctx.Messages.Where(m => m.ConversationId == item.Id);
                ctx.Messages.RemoveRange(msgs);
                ctx.Conversations.Remove(conv);
            }
            await ctx.SaveChangesAsync();
            foreach (var item in items)
            {
                Conversations.Remove(item);
                if (ActiveConversation == item)
                {
                    ActiveConversation = null;
                    Messages.Clear();
                }
            }
            SelectedConversations.Clear();
            IsMultiSelectMode = false;
            await _toast.SuccessAsync($"Deleted {items.Count} conversation(s).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete selected failed");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Delete failed: " + ex.Message);
        }
    }

    [RelayCommand]
    public void BeginRenameTitle()
    {
        if (ActiveConversation is null) return;
        TitleDraft = ActiveConversation.Title;
        IsEditingTitle = true;
    }

    [RelayCommand]
    public async Task CommitRenameTitleAsync()
    {
        if (ActiveConversation is null)
        {
            IsEditingTitle = false;
            return;
        }
        var newTitle = (TitleDraft ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(newTitle))
        {
            await _toast.WarningAsync("Title cannot be empty.");
            return;
        }
        try
        {
            await using var ctx = _db.CreateContext();
            var conv = await ctx.Conversations.FindAsync(ActiveConversation.Id);
            if (conv is null) return;
            conv.Title = newTitle;
            conv.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
            ActiveConversation.Title = newTitle;
            ActiveConversation.UpdatedAt = conv.UpdatedAt;
            IsEditingTitle = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rename failed");
            await _toast.ErrorAsync("Rename failed: " + ex.Message);
        }
    }

    [RelayCommand]
    public void CancelRenameTitle()
    {
        IsEditingTitle = false;
        TitleDraft = ActiveConversation?.Title ?? string.Empty;
    }

    [RelayCommand]
    public void Stop()
    {
        _streamCts?.Cancel();
    }

    /// <summary>Opens the system file picker and adds the chosen file to <see cref="AttachedFiles"/>.</summary>
    [RelayCommand]
    public async Task AttachFileAsync()
    {
        var path = await _filePicker.PickFileAsync("Attach a file to your message");
        if (!string.IsNullOrWhiteSpace(path) && !AttachedFiles.Contains(path))
            AttachedFiles.Add(path);
    }

    /// <summary>Removes a file from <see cref="AttachedFiles"/>.</summary>
    [RelayCommand]
    public void RemoveAttachment(string? path)
    {
        if (path is not null) AttachedFiles.Remove(path);
    }

    [RelayCommand]
    public async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(Draft)) return;
        if (ActiveConversation is null) await NewAsync();
        if (ActiveConversation is null) return;

        // Capture the conversation that owns this stream. If the user switches conversations
        // mid-stream, all DB writes and history reads stay bound to THIS conversation —
        // the response cannot leak into a different conversation.
        var streamConvId = ActiveConversation.Id;
        var streamConvItem = ActiveConversation;

        var userText = Draft;
        Draft = string.Empty;

        // Inline any attached files as clearly labelled blocks appended to the message.
        //
        // Processing by file type:
        //   PDF          → text extraction via UglyToad.PdfPig (≤ 100 000 chars then truncated)
        //   Image        → base64-encoded for vision APIs (jpg/jpeg/png/gif/webp, ≤ 20 MB)
        //   Audio/video  → filename + metadata description (transcription not supported)
        //   Text/code    → verbatim inline (≤ 512 KB)
        //   Binary       → size-only note
        //
        // Label comes FIRST so the model recognises the content is already provided and
        // does NOT invoke any plugin to re-read it.
        var attachedImages = new List<VirtmaAi.Services.AI.MessageImage>();

        if (AttachedFiles.Count > 0)
        {
            var sb = new System.Text.StringBuilder(userText);
            foreach (var fp in AttachedFiles)
            {
                var name = System.IO.Path.GetFileName(fp);
                var ext  = System.IO.Path.GetExtension(fp).TrimStart('.').ToLowerInvariant();
                sb.AppendLine().AppendLine();
                sb.AppendLine($"[Attached file: {name}]");
                try
                {
                    var info = new System.IO.FileInfo(fp);
                    if (!info.Exists) { sb.AppendLine("*(File not found)*"); continue; }

                    // ── PDF → text extraction ─────────────────────────────────────────
                    if (ext == "pdf")
                    {
                        try
                        {
                            using var pdf = UglyToad.PdfPig.PdfDocument.Open(fp);
                            var pdfText = string.Join("\n\n",
                                pdf.GetPages().Select(p => string.Join(" ",
                                    p.GetWords().Select(w => w.Text))));
                            const int pdfMaxChars = 100_000;
                            sb.AppendLine("```text");
                            sb.AppendLine(pdfText.Length > pdfMaxChars
                                ? pdfText[..pdfMaxChars] + $"\n…[truncated — {pdfText.Length:N0} chars total]"
                                : pdfText);
                            sb.AppendLine("```");
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLine($"*(PDF text extraction failed: {ex.Message})*");
                        }
                        continue;
                    }

                    // ── Images → vision ───────────────────────────────────────────────
                    if (ext is "jpg" or "jpeg" or "png" or "gif" or "webp")
                    {
                        const long imageMaxBytes = 20L * 1024 * 1024; // 20 MB
                        if (info.Length <= imageMaxBytes)
                        {
                            var bytes = await System.IO.File.ReadAllBytesAsync(fp);
                            var mime = ext switch
                            {
                                "jpg" or "jpeg" => "image/jpeg",
                                "png"           => "image/png",
                                "gif"           => "image/gif",
                                "webp"          => "image/webp",
                                _               => "image/jpeg"
                            };
                            attachedImages.Add(new VirtmaAi.Services.AI.MessageImage(
                                Convert.ToBase64String(bytes), mime));
                            sb.AppendLine($"*(Image sent via vision — {info.Length / 1024:N0} KB, {ext.ToUpperInvariant()})*");
                        }
                        else
                        {
                            sb.AppendLine($"*(Image too large for vision — {info.Length / 1024 / 1024.0:F1} MB; max 20 MB)*");
                        }
                        continue;
                    }

                    // ── Audio / video → metadata only ─────────────────────────────────
                    if (ext is "mp4" or "mp3" or "mov" or "mkv" or "webm" or "wav"
                            or "ogg" or "aac" or "m4a" or "flac" or "opus")
                    {
                        sb.AppendLine(
                            $"*(Media file — {ext.ToUpperInvariant()}, " +
                            $"{info.Length / 1024.0 / 1024.0:F1} MB. " +
                            "Audio/video content cannot be transcribed automatically. " +
                            "Please describe what you need analysed based on the filename and any context.)*");
                        continue;
                    }

                    // ── Text / code → inline verbatim ─────────────────────────────────
                    if (info.Length <= 512 * 1024)
                    {
                        var bytes = await System.IO.File.ReadAllBytesAsync(fp);
                        var sample = bytes[..Math.Min(512, bytes.Length)];
                        var nonPrint = sample.Count(b => b < 9 || (b > 13 && b < 32) || b == 127);
                        if (nonPrint < sample.Length / 20)
                        {
                            sb.Append("```").AppendLine(ext.Length > 0 ? ext : "text");
                            sb.AppendLine(System.Text.Encoding.UTF8.GetString(bytes));
                            sb.AppendLine("```");
                        }
                        else
                        {
                            sb.AppendLine($"*(Binary file — {info.Length:N0} bytes, cannot display inline)*");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"*(File too large to embed inline — {info.Length:N0} bytes)*");
                    }
                }
                catch
                {
                    sb.AppendLine("*(Could not read file)*");
                }
            }
            userText = sb.ToString();
            AttachedFiles.Clear();
        }

        var userMsg = new Message
        {
            ConversationId = streamConvId,
            Role = MessageRole.User,
            Content = userText
        };
        Messages.Add(new ChatMessageItem(userMsg));

        var assistantItem = new ChatMessageItem(Guid.NewGuid(), MessageRole.Assistant) { IsStreaming = true };
        Messages.Add(assistantItem);
        var currentItem = assistantItem;

        // Private history list bound to streamConvId. Used for provider requests so the user
        // switching conversations mid-stream doesn't poison the next iteration's context.
        var privateHistory = new List<ChatMessage>();
        foreach (var m in Messages)
        {
            if (m == assistantItem) continue;
            if (m.Role == MessageRole.User)
                privateHistory.Add(new ChatMessage(ChatRole.User, m.Content));
            else if (m.Role == MessageRole.Assistant && !m.IsStreaming && !string.IsNullOrEmpty(m.Content))
                privateHistory.Add(new ChatMessage(ChatRole.Assistant, m.Content));
        }

        // Attach any vision images to the most recent user message (current turn only —
        // they are never re-sent in follow-up tool-call iterations).
        if (attachedImages.Count > 0)
        {
            for (int i = privateHistory.Count - 1; i >= 0; i--)
            {
                if (privateHistory[i].Role != ChatRole.User) continue;
                privateHistory[i] = privateHistory[i] with { Images = attachedImages };
                break;
            }
        }

        try
        {
            await using (var ctx = _db.CreateContext())
            {
                ctx.Messages.Add(userMsg);
                var conv = await ctx.Conversations.FindAsync(streamConvId);
                if (conv is not null)
                {
                    conv.UpdatedAt = DateTime.UtcNow;
                    if (conv.Title == "New conversation")
                        conv.Title = Truncate(userText, 48);
                    streamConvItem.Title = conv.Title;
                    streamConvItem.UpdatedAt = conv.UpdatedAt;
                }
                await ctx.SaveChangesAsync();
            }

            _streamCts = new CancellationTokenSource();
            IsStreaming = true;

            var provider = _router.Get(SelectedProviderId);
            var baseSystem = BuildSystemPrompt();
            var toolsAug = BuildToolsAugmentation();
            var rulesAug = await _aiRules.BuildSystemPromptBlockAsync().ConfigureAwait(true);
            var skillAug = await _skillMatcher.BuildAugmentationAsync(userText, _streamCts.Token).ConfigureAwait(true);
            var refAug = await _references.BuildAugmentationAsync(userText, _streamCts.Token).ConfigureAwait(true);
            // Rules go FIRST so they can't be overridden by later prompt sections.
            var systemPrompt = string.Join("\n\n",
                new[] { rulesAug, baseSystem, toolsAug, skillAug, refAug }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(systemPrompt)) systemPrompt = null;

            // Honor the "show thinking" preference. When disabled, drop thinking chunks instead of
            // surfacing them — the model still reasons internally, the user just doesn't see it.
            var showThinking = _settings.Get<bool>(SettingShowThinking, defaultValue: true);

            const int maxToolIterations = 4;

            for (int iter = 0; iter < maxToolIterations; iter++)
            {
                // Use the private snapshot (locked to streamConvId), NOT BuildHistory() —
                // BuildHistory reads the current Messages collection which may have been
                // replaced if the user switched conversations.
                var request = new ChatRequest(SelectedModelId, privateHistory, SystemPrompt: systemPrompt);

                var thinkingBuf = new System.Text.StringBuilder();
                var contentBuf = new System.Text.StringBuilder();
                StreamCompleted? completed = null;

                // Throttle UI property writes — every chunk re-parses the entire message in MarkdownView,
                // which is expensive and is a major source of memory pressure on long streams.
                // We coalesce content/thinking flushes to ~10fps and always flush on completion.
                const long flushIntervalTicks = TimeSpan.TicksPerMillisecond * 100;
                long lastContentFlushTicks = 0;
                long lastThinkingFlushTicks = 0;
                bool contentDirty = false;
                bool thinkingDirty = false;

                await foreach (var evt in provider.StreamAsync(request, _streamCts.Token).ConfigureAwait(true))
                {
                    switch (evt)
                    {
                        case ContentChunk c:
                            // First content chunk implies thinking has settled — collapse it so
                            // the body of the response is the focus (user can re-expand to review).
                            if (currentItem.IsThinkingActive)
                            {
                                if (thinkingDirty) currentItem.Thinking = thinkingBuf.ToString();
                                currentItem.IsThinkingActive = false;
                                thinkingDirty = false;
                            }
                            contentBuf.Append(c.Text);
                            contentDirty = true;
                            var nowC = DateTime.UtcNow.Ticks;
                            if (nowC - lastContentFlushTicks >= flushIntervalTicks)
                            {
                                currentItem.Content = contentBuf.ToString();
                                lastContentFlushTicks = nowC;
                                contentDirty = false;
                            }
                            break;
                        case ThinkingChunk t:
                            // Drop thinking entirely when the user has disabled the surface — no
                            // buffer growth, no UI churn, nothing surfaced post-stream.
                            if (!showThinking) break;
                            thinkingBuf.Append(t.Text);
                            thinkingDirty = true;
                            // First thinking chunk lights the live indicator and ensures the
                            // panel is expanded so the user sees the live stream.
                            if (!currentItem.IsThinkingActive)
                            {
                                currentItem.IsThinkingActive = true;
                                currentItem.IsThinkingExpanded = true;
                            }
                            var nowT = DateTime.UtcNow.Ticks;
                            if (nowT - lastThinkingFlushTicks >= flushIntervalTicks)
                            {
                                currentItem.Thinking = thinkingBuf.ToString();
                                lastThinkingFlushTicks = nowT;
                                thinkingDirty = false;
                            }
                            break;
                        case StreamError e:
                            ErrorMessage = e.Message;
                            await _toast.ErrorAsync(e.Message);
                            break;
                        case StreamCompleted done:
                            // Final flush of any unflushed buffer text.
                            if (contentDirty) currentItem.Content = contentBuf.ToString();
                            if (thinkingDirty) currentItem.Thinking = thinkingBuf.ToString();
                            currentItem.IsThinkingActive = false; // also collapses thinking block
                            currentItem.IsStreaming = false;
                            completed = done;
                            break;
                    }
                }

                // Belt-and-suspenders: if the stream ended without an explicit StreamCompleted event
                // (provider quirk), make sure the buffer reaches the UI.
                if (contentDirty) currentItem.Content = contentBuf.ToString();
                if (thinkingDirty) currentItem.Thinking = thinkingBuf.ToString();

                if (completed is not null)
                    await PersistAssistantAsync(streamConvId, currentItem, completed);
                if (!string.IsNullOrEmpty(currentItem.Content))
                    privateHistory.Add(new ChatMessage(ChatRole.Assistant, currentItem.Content));
                DetectArtifacts(currentItem.Content);

                var toolResults = await ExecuteToolCallsAsync(currentItem.Content, _streamCts.Token).ConfigureAwait(true);
                if (string.IsNullOrEmpty(toolResults)) break;

                var toolMsg = new Message
                {
                    ConversationId = streamConvId,
                    Role = MessageRole.User,
                    Content = "[tool execution results — continue your response using these]\n\n" + toolResults
                };
                if (ActiveConversation?.Id == streamConvId)
                    Messages.Add(new ChatMessageItem(toolMsg));
                privateHistory.Add(new ChatMessage(ChatRole.User, toolMsg.Content));
                await using (var ctx = _db.CreateContext())
                {
                    ctx.Messages.Add(toolMsg);
                    await ctx.SaveChangesAsync();
                }

                currentItem = new ChatMessageItem(Guid.NewGuid(), MessageRole.Assistant) { IsStreaming = true };
                if (ActiveConversation?.Id == streamConvId)
                    Messages.Add(currentItem);
            }
        }
        catch (OperationCanceledException)
        {
            currentItem.IsStreaming = false;
            currentItem.Content += "\n\n_(stopped)_";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stream failed");
            ErrorMessage = ex.Message;
            currentItem.IsStreaming = false;
            await _toast.ErrorAsync("Chat failed: " + ex.Message);
        }
        finally
        {
            IsStreaming = false;
            _streamCts?.Dispose();
            _streamCts = null;
        }
    }

    private string? BuildSystemPrompt()
    {
        if (ActiveConversation is null) return null;
        if (ActiveConversation.Mode != ConversationMode.Code) return null;
        if (_sandbox.ProjectRoot is null) return null;
        return
            "You are operating in Code mode with a sandboxed project directory at: " + _sandbox.ProjectRoot + ".\n" +
            "Produce code changes and diffs directly — do not include verbose markdown prose, summaries, or tutorial explanations unless explicitly asked.\n" +
            "All file operations are restricted to this directory and paths that escape it will be blocked.\n" +
            "**When the user asks you to create, modify, or delete files, you MUST invoke the `project-files` plugin via a `vplugin` block** (see Tools section). Do NOT just print file contents in markdown — printing alone does not write the file. Always use the plugin to actually write to disk.";
    }

    private string? BuildToolsAugmentation()
    {
        if (_plugins.BuiltIn.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Tools / Plugins Available");
        sb.AppendLine();
        sb.AppendLine("**ATTACHED FILES:** When the user's message contains a block labelled `[Attached file: <name>]` followed by a code fence, the file content is ALREADY embedded in the message — you can read it directly. Do NOT invoke any plugin (desktop-commander, project-files, etc.) to re-read an attached file. Just analyse the content that is right there in the message.");
        sb.AppendLine();
        sb.AppendLine("You CAN execute local actions on this machine through the following built-in plugins. When the user asks you to perform an operation that one of these plugins handles, **invoke it** — do not refuse or claim you cannot. To invoke a plugin, emit a fenced code block with the language tag `vplugin` containing JSON in this exact shape:");
        sb.AppendLine();
        sb.AppendLine("```vplugin");
        sb.AppendLine("{");
        sb.AppendLine("  \"schema\": \"vplugin/v1\",");
        sb.AppendLine("  \"plugin\": \"<plugin-name>\",");
        sb.AppendLine("  \"input\": { ... plugin-specific JSON ... }");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("After you emit a `vplugin` block, the system will execute the plugin and feed the output back to you in the next turn so you can summarize or act on it. You can chain calls across turns.");
        sb.AppendLine();
        sb.AppendLine("## Available plugins");
        sb.AppendLine();
        foreach (var p in _plugins.BuiltIn)
        {
            sb.Append("- **").Append(p.Name).Append("** — ").AppendLine(p.Description);
        }
        sb.AppendLine();
        sb.AppendLine("### media-player input shapes (in-app playback — use this instead of opening a browser)");
        sb.AppendLine("- `{ \"action\": \"open\", \"target\": \"https://.../song.mp3\" }` — plays an audio URL in the in-app preview panel.");
        sb.AppendLine("- `{ \"action\": \"open\", \"target\": \"https://.../video.mp4\" }` — plays video inline.");
        sb.AppendLine("- `{ \"action\": \"open\", \"target\": \"C:/path/file.pdf\" }` — renders PDFs, images, web pages, and text inline.");
        sb.AppendLine("- `{ \"action\": \"close\" }` — hides the preview panel.");
        sb.AppendLine();
        sb.AppendLine("### desktop-control input shapes (synthetic input + screen capture, Windows)");
        sb.AppendLine("- `{ \"action\": \"type\", \"text\": \"hello\", \"delay\": 0 }` — types text into the focused window. `delay` is per-char ms.");
        sb.AppendLine("- `{ \"action\": \"key-press\", \"key\": \"Enter\" }` — presses a single key. Supported names: Enter/Return, Tab, Escape, Space, Backspace, Delete, Up/Down/Left/Right, Home, End, or any single character.");
        sb.AppendLine("- `{ \"action\": \"mouse-move\", \"x\": 100, \"y\": 200 }`");
        sb.AppendLine("- `{ \"action\": \"mouse-click\", \"button\": \"left|right|middle\", \"x\": 100, \"y\": 200 }`");
        sb.AppendLine("- `{ \"action\": \"cursor-pos\" }` — returns the current cursor position as JSON.");
        sb.AppendLine("- `{ \"action\": \"screenshot\", \"region\": \"primary|all\" }` — captures the screen and returns a markdown image (rendered inline).");
        sb.AppendLine();
        sb.AppendLine("### desktop-commander input shapes");
        sb.AppendLine("- `{ \"action\": \"shell\", \"command\": \"<shell command>\", \"timeoutSeconds\": 30 }` — runs a shell command (cmd on Windows, /bin/sh on Unix). Use for ping, curl, dir/ls, etc. Bounded by `timeoutSeconds` (default 30, max 300). **Do not use shell to play media or open web URLs** — use `open-url` instead.");
        sb.AppendLine("- `{ \"action\": \"open-url\", \"target\": \"<url or file path>\" }` — last-resort: opens the target with the OS default handler (external browser/app). **Prefer the `media-player` plugin for media URLs (mp3/mp4/m3u/…), web pages, PDFs, and images** — it plays them in-app instead of launching an external program.");
        sb.AppendLine("- `{ \"action\": \"list-processes\" }`");
        sb.AppendLine("- `{ \"action\": \"kill-process\", \"pid\": <int> }`");
        sb.AppendLine("- `{ \"action\": \"list-network\" }`");
        sb.AppendLine("- `{ \"action\": \"system-info\" }`");
        sb.AppendLine();
        sb.AppendLine("### project-files input shapes");
        if (_sandbox.ProjectRoot is not null)
        {
            sb.Append("Project root for this conversation: `").Append(_sandbox.ProjectRoot).AppendLine("`. All paths are resolved relative to this root unless absolute (and absolute paths must still be inside the root).");
        }
        else
        {
            sb.AppendLine("**No project directory is currently set on this conversation.** Tell the user to click \"Browse…\" in the project bar to pick one before asking you to read or write files.");
        }
        sb.AppendLine("- `{ \"action\": \"read-file\", \"path\": \"relative/or/absolute/path.ext\" }`");
        sb.AppendLine("- `{ \"action\": \"write-file\", \"path\": \"...\", \"content\": \"<full file contents>\" }` — last resort: overwrites the entire file. **Prefer the surgical edits below for code files.**");
        sb.AppendLine("- `{ \"action\": \"insert-at-line\", \"path\": \"...\", \"line\": 42, \"content\": \"new code\\n\" }` — inserts content BEFORE line 42 (1-based). Use line=N+1 to append.");
        sb.AppendLine("- `{ \"action\": \"insert-at-offset\", \"path\": \"...\", \"offset\": 1024, \"content\": \"…\" }` — inserts at a 0-based character offset.");
        sb.AppendLine("- `{ \"action\": \"replace-lines\", \"path\": \"...\", \"startLine\": 10, \"endLine\": 12, \"content\": \"replacement block\\n\" }` — replaces lines 10..12 inclusive.");
        sb.AppendLine("- `{ \"action\": \"replace-text\", \"path\": \"...\", \"find\": \"<exact substring>\", \"replace\": \"<new>\" }` — surgical search-and-replace. Errors if `find` isn't unique unless you pass `allowMultiple: true`. Always include enough surrounding context to make `find` unique on the first try.");
        sb.AppendLine("- `{ \"action\": \"list-files\", \"path\": \".\", \"pattern\": \"*\", \"recursive\": false }`");
        sb.AppendLine("- `{ \"action\": \"delete-file\", \"path\": \"relative/path.ext\" }`");
        sb.AppendLine("- `{ \"action\": \"project-root\" }` — returns the absolute project root path.");
        sb.AppendLine();
        sb.AppendLine("### app-selfmodify input shapes (modify VirtmaAi itself — gated)");
        sb.AppendLine("Same actions as `project-files`, but operates on VirtmaAi's own source tree. **Disabled until the user enables it in Settings → AI Permissions.** If you get a `disabled` error, tell the user how to enable it; do NOT try to bypass it.");
        sb.AppendLine();
        sb.AppendLine("### script-runner input shape (generate and execute a custom script)");
        sb.AppendLine("Use this plugin **whenever no other plugin can accomplish the task**. Generate the script yourself, submit it here, and act on the output. This is your escape hatch for any local operation.");
        sb.AppendLine("- `{ \"language\": \"python\",     \"code\": \"import sys; print(sys.version)\", \"timeoutSeconds\": 30 }`");
        sb.AppendLine("- `{ \"language\": \"powershell\", \"code\": \"Get-Process | Select-Object -First 5\", \"timeoutSeconds\": 30 }`");
        sb.AppendLine("- `{ \"language\": \"node\",       \"code\": \"console.log(JSON.stringify(process.versions))\", \"timeoutSeconds\": 30 }`");
        sb.AppendLine("- `{ \"language\": \"bash\",       \"code\": \"ls -la /tmp\", \"timeoutSeconds\": 30 }`");
        sb.AppendLine("- `{ \"language\": \"batch\",      \"code\": \"dir /b\", \"timeoutSeconds\": 30 }` (Windows only)");
        sb.AppendLine("- `{ \"language\": \"ruby\",       \"code\": \"puts RUBY_VERSION\", \"timeoutSeconds\": 30 }`");
        sb.AppendLine("`timeoutSeconds` is optional (1–300, default 30). The interpreter must be on PATH. Stdout + stderr are returned. **Never refuse a task just because no existing plugin covers it — write a script.**");
        sb.AppendLine();
        sb.AppendLine("### audio-transcribe input shape");
        sb.AppendLine("Transcribes speech from an audio or video file using OpenAI Whisper. Requires OpenAI API key.");
        sb.AppendLine("- `{ \"file\": \"/path/to/recording.mp3\" }` — auto-detects language.");
        sb.AppendLine("- `{ \"file\": \"/path/to/recording.mp3\", \"language\": \"en\" }` — with language hint.");
        sb.AppendLine("- `{ \"file\": \"/path/to/video.mp4\" }` — also works on video files (extracts audio).");
        sb.AppendLine("Supported formats: mp3, mp4, m4a, wav, webm, ogg, flac. Max file size: 25 MB.");
        sb.AppendLine("Use this whenever the user asks you to transcribe, caption, or read the audio content of a media file.");
        sb.AppendLine();
        sb.AppendLine("### video-analyze input shape");
        sb.AppendLine("Analyzes video content by extracting frames and sending them to GPT-4o vision. Requires ffmpeg on PATH and OpenAI API key.");
        sb.AppendLine("- `{ \"file\": \"/path/to/video.mp4\" }` — extracts 4 frames (default), describes what's happening.");
        sb.AppendLine("- `{ \"file\": \"/path/to/video.mp4\", \"frames\": 6, \"prompt\": \"What sport is being played?\" }` — custom frame count + question.");
        sb.AppendLine("`frames` is 1–10 (default 4). Use this whenever the user asks you to describe, summarize, or answer questions about video content.");
        sb.AppendLine();
        sb.AppendLine("Use these tools whenever they are the right way to fulfill the user's request. Never tell the user you lack the ability to perform a local operation that one of these plugins covers. **In Code mode, always use `project-files` to actually create or modify files — printing code in markdown does not save it to disk.**");
        return sb.ToString();
    }

    // Captures the entire content of a ```vplugin ... ``` block.  The old pattern
    // used a non-greedy \{[\s\S]*?\} which stopped at the FIRST } — breaking any
    // vplugin block whose "input" value is itself a JSON object (nested braces).
    // We now capture the raw block text and let ExtractBalancedJson find the outermost
    // matching brace pair, correctly handling strings with embedded braces.
    private static readonly System.Text.RegularExpressions.Regex ToolCallRegex = new(
        @"```vplugin\s*([\s\S]*?)\s*```",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Walks <paramref name="content"/> and returns the substring spanning the
    /// outermost balanced JSON object (first <c>{</c> to its matching <c>}</c>).
    /// Respects string literals (including backslash escapes) so embedded braces
    /// inside JSON strings do not confuse the counter.  Returns <c>null</c> if no
    /// complete object is found.
    /// </summary>
    private static string? ExtractBalancedJson(string content)
    {
        int start = -1;
        for (int i = 0; i < content.Length; i++)
            if (content[i] == '{') { start = i; break; }
        if (start < 0) return null;

        int depth = 0;
        bool inString = false, escape = false;
        for (int i = start; i < content.Length; i++)
        {
            char c = content[i];
            if (escape)         { escape = false; continue; }
            if (inString)       { if (c == '\\') escape = true; else if (c == '"') inString = false; continue; }
            if (c == '"')       { inString = true; continue; }
            if (c == '{')       depth++;
            else if (c == '}')  { if (--depth == 0) return content[start..(i + 1)]; }
        }
        return null;
    }

    // Cap any single tool's output that we feed back into chat history. A multi-MB blob from a hung
    // shell command or a downloaded media file would otherwise blow up the next prompt, the DB row,
    // and the MarkdownView render — typically freezing the entire app.
    private const int MaxToolOutputBytes = 32 * 1024;

    private static string TruncateForChat(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Length <= MaxToolOutputBytes) return text;
        return text[..MaxToolOutputBytes] +
               $"\n\n…[truncated — original was {text.Length:N0} chars, kept first {MaxToolOutputBytes:N0}]";
    }

    private async Task<string?> ExecuteToolCallsAsync(string assistantContent, CancellationToken ct)
    {
        var matches = ToolCallRegex.Matches(assistantContent);
        if (matches.Count == 0) return null;

        var results = new System.Text.StringBuilder();
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            // Extract the outermost balanced JSON object from the raw block content.
            // Groups[1] is the full text between the ``` fences; ExtractBalancedJson
            // handles nested objects that would have tripped the old non-greedy regex.
            var blockContent = m.Groups[1].Value.Trim();
            var json = ExtractBalancedJson(blockContent);
            if (string.IsNullOrWhiteSpace(json)) continue;
            string pluginName = "?";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json!);
                var root = doc.RootElement;
                if (!root.TryGetProperty("plugin", out var pluginEl)) continue;
                pluginName = pluginEl.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(pluginName)) continue;
                var inputJson = root.TryGetProperty("input", out var inEl)
                    ? inEl.GetRawText()
                    : "{}";

                _logger.LogInformation("Invoking built-in plugin {Plugin}", pluginName);
                var result = await _plugins.InvokeBuiltInAsync(pluginName, inputJson, ct).ConfigureAwait(false);
                results.Append("### Tool result: ").AppendLine(pluginName);
                results.AppendLine("```");
                if (!string.IsNullOrEmpty(result.Output)) results.AppendLine(TruncateForChat(result.Output).TrimEnd());
                if (!string.IsNullOrEmpty(result.Error)) results.Append("[stderr] ").AppendLine(TruncateForChat(result.Error).TrimEnd());
                results.Append("[exit ").Append(result.ExitCode ?? (result.Success ? 0 : -1)).AppendLine("]");
                results.AppendLine("```");
                results.AppendLine();

                // ── Post-invocation side effects ──────────────────────────────────
                // When the model writes a file via project-files, auto-open it in the
                // Preview panel so the user can immediately view/download it.
                if (result.Success &&
                    string.Equals(pluginName, "project-files", StringComparison.OrdinalIgnoreCase))
                {
                    TryOpenCreatedFileInPreview(inEl);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tool call execution failed for {Plugin}", pluginName);
                results.Append("### Tool error: ").AppendLine(pluginName);
                results.Append("```\n").Append(ex.Message).AppendLine("\n```");
            }
        }
        return results.Length == 0 ? null : results.ToString();
    }

    /// <summary>
    /// If the <c>project-files</c> plugin just wrote a file, resolve its absolute path and
    /// open it in the Preview panel. Handles both write-file and the insert-/replace-* edits
    /// so the user always sees the modified file after any write operation.
    /// </summary>
    private void TryOpenCreatedFileInPreview(System.Text.Json.JsonElement inputElement)
    {
        try
        {
            if (!inputElement.TryGetProperty("action", out var actionEl)) return;
            var action = actionEl.GetString() ?? string.Empty;
            // Only open after write or editing actions, not reads/lists.
            if (!action.StartsWith("write", StringComparison.OrdinalIgnoreCase) &&
                !action.StartsWith("insert", StringComparison.OrdinalIgnoreCase) &&
                !action.StartsWith("replace", StringComparison.OrdinalIgnoreCase)) return;

            if (!inputElement.TryGetProperty("path", out var pathEl)) return;
            var relPath = pathEl.GetString();
            if (string.IsNullOrWhiteSpace(relPath)) return;

            string absPath;
            if (System.IO.Path.IsPathRooted(relPath))
                absPath = relPath;
            else if (_sandbox.ProjectRoot is not null)
                absPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(_sandbox.ProjectRoot, relPath));
            else
                return; // Can't resolve without project root.

            if (!System.IO.File.Exists(absPath)) return;

            // Fire-and-forget — open the file in the preview panel.
            _ = Preview.OpenAsync(absPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TryOpenCreatedFileInPreview failed");
        }
    }

    private IReadOnlyList<ChatMessage> BuildHistory()
    {
        var list = new List<ChatMessage>();
        foreach (var m in Messages)
        {
            if (m.Role == MessageRole.User)
                list.Add(new ChatMessage(ChatRole.User, m.Content));
            else if (m.Role == MessageRole.Assistant && !m.IsStreaming && !string.IsNullOrEmpty(m.Content))
                list.Add(new ChatMessage(ChatRole.Assistant, m.Content));
        }
        return list;
    }

    private async Task PersistAssistantAsync(Guid conversationId, ChatMessageItem item, StreamCompleted done)
    {
        try
        {
            await using var ctx = _db.CreateContext();
            ctx.Messages.Add(new Message
            {
                Id = item.Id,
                ConversationId = conversationId,
                Role = MessageRole.Assistant,
                Content = item.Content,
                TokenCount = done.CompletionTokens ?? 0
            });
            if (!string.IsNullOrEmpty(item.Thinking))
            {
                ctx.Messages.Add(new Message
                {
                    ConversationId = conversationId,
                    Role = MessageRole.Thinking,
                    Content = item.Thinking,
                    ParentMessageId = item.Id
                });
            }
            var conv = await ctx.Conversations.FindAsync(conversationId);
            if (conv is not null) conv.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Persist assistant message failed");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\u2026";

    // Matches a URL that appears on its own line (possibly with surrounding whitespace/punctuation)
    // so we don't pick up every hyperlink embedded in prose.
    private static readonly System.Text.RegularExpressions.Regex _standaloneUrlRegex = new(
        @"(?:^|\s)(https?://\S{8,})",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);

    private void DetectArtifacts(string content)
    {
        if (_themes.TryDetectThemeBlock(content, out var themeJson) && themeJson is not null)
        {
            DetectedThemeJson = themeJson;
            Notice = "Theme detected in assistant output \u2014 accept to apply.";
        }
        if (_skillRegistry.TryDetectSkillBlock(content, out var skillJson) && skillJson is not null)
        {
            DetectedSkillJson = skillJson;
            Notice = "Skill detected in assistant output \u2014 accept to import.";
        }
        if (_routines.TryDetectRoutineBlock(content, out var routineJson) && routineJson is not null)
        {
            DetectedRoutineJson = routineJson;
            Notice = "Routine detected in assistant output \u2014 accept to import.";
        }

        // Detect standalone URLs and offer to open them in the viewer panel.
        // We only pick up the FIRST new URL per assistant turn to avoid banner spam.
        var urlMatch = _standaloneUrlRegex.Match(content);
        if (urlMatch.Success)
        {
            var url = urlMatch.Groups[1].Value.TrimEnd('.', ',', ')', ']', '\'', '"');
            if (url != DetectedMediaUrl)
            {
                DetectedMediaUrl = url;
                if (Notice is null)
                    Notice = $"URL detected \u2014 open in viewer?";
            }
        }
    }

    [RelayCommand]
    public async Task AcceptDetectedThemeAsync()
    {
        if (string.IsNullOrWhiteSpace(DetectedThemeJson)) return;
        try
        {
            var def = await _themes.ImportFromJsonAsync(DetectedThemeJson);
            await _themes.ApplyAsync(def);
            await _themes.SaveAsync(def);
            Notice = "Theme applied.";
            DetectedThemeJson = null;
            await _toast.SuccessAsync("Theme applied: " + def.Name);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Theme apply failed: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task AcceptDetectedSkillAsync()
    {
        if (string.IsNullOrWhiteSpace(DetectedSkillJson)) return;
        try
        {
            var def = await _skillRegistry.ImportJsonAsync(DetectedSkillJson);
            await _skillRegistry.CreateAsync(def);
            Notice = "Skill imported.";
            DetectedSkillJson = null;
            await _toast.SuccessAsync("Skill imported: " + def.Name);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Skill import failed: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task AcceptDetectedRoutineAsync()
    {
        if (string.IsNullOrWhiteSpace(DetectedRoutineJson)) return;
        try
        {
            var draft = await _routines.ImportJsonAsync(DetectedRoutineJson);
            await _routines.CreateAsync(draft);
            Notice = "Routine imported.";
            DetectedRoutineJson = null;
            await _toast.SuccessAsync("Routine imported.");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Routine import failed: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task OpenDetectedUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(DetectedMediaUrl)) return;
        try { await Preview.OpenAsync(DetectedMediaUrl); }
        catch (Exception ex) { await _toast.ErrorAsync("Could not open in viewer: " + ex.Message); }
        DetectedMediaUrl = null;
        if (DetectedThemeJson is null && DetectedSkillJson is null && DetectedRoutineJson is null)
            Notice = null;
    }

    [RelayCommand]
    public void DismissDetection()
    {
        DetectedThemeJson = null;
        DetectedSkillJson = null;
        DetectedRoutineJson = null;
        DetectedMediaUrl = null;
        Notice = null;
    }

    [RelayCommand]
    public async Task OpenLinkAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            // Normalize bare hostnames so things like "example.com" still resolve.
            if (!url.Contains("://", StringComparison.Ordinal) &&
                !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url.TrimStart('/');
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                await _toast.WarningAsync("Invalid URL: " + url);
                return;
            }
            await Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open link failed: {Url}", url);
            await _toast.ErrorAsync("Could not open link: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task OpenEmailAsync(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        try
        {
            var target = address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                ? address
                : "mailto:" + address;
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
            {
                await _toast.WarningAsync("Invalid email: " + address);
                return;
            }
            await Launcher.Default.OpenAsync(uri);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open email failed: {Address}", address);
            await _toast.ErrorAsync("Could not open mail client: " + ex.Message);
        }
    }
}
