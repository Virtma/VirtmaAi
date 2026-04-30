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
using VirtmaAi.Services.System;

namespace VirtmaAi.ViewModels.Models;

public sealed partial class ModelLibraryViewModel : ViewModelBase
{
    private static readonly Uri DefaultOllamaUri = new("http://127.0.0.1:11434/");

    private readonly IDatabaseService _db;
    private readonly IOllamaRegistryClient _ollama;
    private readonly ILocalServiceProber _localProber;
    private readonly IHardwareProbe _hardware;
    private readonly IModelDownloadService _downloader;
    private readonly IFolderPickerService _folderPicker;
    private readonly IToastService _toast;
    private readonly ILogger<ModelLibraryViewModel> _logger;
    private CancellationTokenSource? _downloadCts;

    public ModelLibraryViewModel(
        IDatabaseService db,
        IOllamaRegistryClient ollama,
        ILocalServiceProber localProber,
        IHardwareProbe hardware,
        IModelDownloadService downloader,
        IFolderPickerService folderPicker,
        IToastService toast,
        ILogger<ModelLibraryViewModel> logger)
    {
        _db = db;
        _ollama = ollama;
        _localProber = localProber;
        _hardware = hardware;
        _downloader = downloader;
        _folderPicker = folderPicker;
        _toast = toast;
        _logger = logger;
        DownloadFolder = _downloader.DefaultModelsDirectory;
    }

    public ObservableCollection<AiModel> Models { get; } = new();

    [ObservableProperty]
    private string _hardwareSummary = string.Empty;

    [ObservableProperty]
    private string _ollamaStatus = "Checking…";

    [ObservableProperty]
    private bool _isOllamaReachable;

    [ObservableProperty]
    private bool _showAdvanced;

    [ObservableProperty]
    private string _newModelName = string.Empty;

    [ObservableProperty]
    private string _newModelProvider = "ollama";

    [ObservableProperty]
    private string _newModelEndpoint = string.Empty;

    /// <summary>Optional public API key sent as <c>X-API-Key</c> header when calling this model.</summary>
    [ObservableProperty]
    private string _newModelPublicKey = string.Empty;

    /// <summary>Optional private API key sent as Bearer token when calling this model.</summary>
    [ObservableProperty]
    private string _newModelPrivateKey = string.Empty;

    [ObservableProperty]
    private string _detectedServicesSummary = string.Empty;

    // ===== Download UI state =====

    [ObservableProperty]
    private bool _showDownload;

    [ObservableProperty]
    private string _downloadUrl = string.Empty;

    [ObservableProperty]
    private string _downloadFolder = string.Empty;

    [ObservableProperty]
    private string _downloadFileName = string.Empty;

    [ObservableProperty]
    private string _downloadStage = string.Empty;

    [ObservableProperty]
    private double _downloadPercent;

    [ObservableProperty]
    private string _downloadStatus = string.Empty;

    [ObservableProperty]
    private bool _isDownloading;

    public string DownloadToggleLabel => ShowDownload ? "Hide downloader" : "Download model from URL";

    partial void OnShowDownloadChanged(bool value) => OnPropertyChanged(nameof(DownloadToggleLabel));

    public string OllamaStatusColorKey => IsOllamaReachable ? "Accent" : "Gray500";
    public string AdvancedToggleLabel => ShowAdvanced ? "Hide advanced" : "Show advanced";

    partial void OnIsOllamaReachableChanged(bool value) => OnPropertyChanged(nameof(OllamaStatusColorKey));
    partial void OnShowAdvancedChanged(bool value) => OnPropertyChanged(nameof(AdvancedToggleLabel));

    [RelayCommand]
    public void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    [RelayCommand]
    public void ToggleDownload() => ShowDownload = !ShowDownload;

    [RelayCommand]
    public async Task PickDownloadFolderAsync()
    {
        try
        {
            var picked = await _folderPicker.PickFolderAsync(DownloadFolder);
            if (!string.IsNullOrWhiteSpace(picked)) DownloadFolder = picked;
        }
        catch (Exception ex)
        {
            await _toast.ErrorAsync("Folder picker failed: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task StartDownloadAsync()
    {
        if (IsDownloading) return;
        if (string.IsNullOrWhiteSpace(DownloadUrl))
        {
            await _toast.WarningAsync("Enter a model URL.");
            return;
        }
        if (!Uri.TryCreate(DownloadUrl.Trim(), UriKind.Absolute, out var uri))
        {
            await _toast.WarningAsync("URL is not valid.");
            return;
        }
        try
        {
            Directory.CreateDirectory(DownloadFolder);
            var fileName = string.IsNullOrWhiteSpace(DownloadFileName)
                ? GuessFileName(uri)
                : DownloadFileName.Trim();
            var dest = Path.Combine(DownloadFolder, fileName);
            DownloadFileName = fileName;

            IsDownloading = true;
            DownloadPercent = 0;
            DownloadStage = "Starting…";
            DownloadStatus = string.Empty;

            _downloadCts?.Cancel();
            _downloadCts = new CancellationTokenSource();
            var ct = _downloadCts.Token;

            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                DownloadStage = p.Stage;
                DownloadPercent = p.PercentComplete ?? 0;
                DownloadStatus = FormatProgress(p);
            });

            var path = await _downloader.DownloadAsync(uri, dest, progress, ct);

            // Register the downloaded file as a local AiModel so it shows up in the picker.
            await using (var ctx = _db.CreateContext())
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var existing = await ctx.AiModels.FirstOrDefaultAsync(m =>
                    m.DownloadedPath == path || (m.Provider == "local" && m.Name == name));
                if (existing is null)
                {
                    var m = new AiModel
                    {
                        Name = name,
                        Provider = "local",
                        Endpoint = path,
                        IsLocal = true,
                        DownloadedPath = path,
                        SizeBytes = new FileInfo(path).Length
                    };
                    ctx.AiModels.Add(m);
                    await ctx.SaveChangesAsync();
                    Models.Add(m);
                }
                else
                {
                    existing.DownloadedPath = path;
                    existing.SizeBytes = new FileInfo(path).Length;
                    await ctx.SaveChangesAsync();
                }
            }

            await _toast.SuccessAsync($"Downloaded {fileName}.");
        }
        catch (OperationCanceledException)
        {
            DownloadStage = "Cancelled";
            await _toast.WarningAsync("Download cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model download failed");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Download failed: " + ex.Message);
        }
        finally
        {
            IsDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    public void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    private static string GuessFileName(Uri uri)
    {
        var name = Path.GetFileName(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(name) || !name.Contains('.'))
            name = "model-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".gguf";
        return name;
    }

    private static string FormatProgress(ModelDownloadProgress p)
    {
        var done = Bytes(p.BytesDownloaded);
        var total = p.TotalBytes.HasValue ? Bytes(p.TotalBytes.Value) : "?";
        var rate = p.BytesPerSecond > 0 ? Bytes((long)p.BytesPerSecond) + "/s" : "—";
        var eta = p.Eta is { } e && e > TimeSpan.Zero ? FormatEta(e) : "—";
        return $"{done} of {total}  ·  {rate}  ·  ETA {eta}  ·  {p.DestinationPath}";
    }

    private static string FormatEta(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_db.Current is null) return;
        try
        {
            IsBusy = true;
            var hw = _hardware.Probe();
            HardwareSummary = $"{hw.OsDescription} \u00B7 {hw.Architecture} \u00B7 {hw.LogicalProcessors} threads \u00B7 {Bytes(hw.TotalMemoryBytes)} RAM \u00B7 {Bytes(hw.AvailableDiskBytes)} free";

            await using var ctx = _db.CreateContext();
            var existing = await ctx.AiModels.OrderBy(m => m.Name).ToListAsync();

            var discovered = await _ollama.ListInstalledAsync(DefaultOllamaUri);
            IsOllamaReachable = discovered.Count > 0;
            OllamaStatus = IsOllamaReachable
                ? $"Ollama running \u2014 {discovered.Count} model(s) detected at {DefaultOllamaUri}"
                : "Ollama not detected at localhost:11434 (start the Ollama desktop app to auto-discover local models)";

            if (IsOllamaReachable)
            {
                foreach (var info in discovered)
                {
                    var match = existing.FirstOrDefault(e => e.Provider == "ollama" && e.Name == info.Name);
                    if (match is null)
                    {
                        var m = new AiModel
                        {
                            Name = info.Name,
                            Provider = "ollama",
                            Endpoint = DefaultOllamaUri.ToString(),
                            IsLocal = true,
                            SizeBytes = info.SizeBytes
                        };
                        ctx.AiModels.Add(m);
                        existing.Add(m);
                    }
                    else
                    {
                        match.SizeBytes = info.SizeBytes;
                        match.Endpoint = DefaultOllamaUri.ToString();
                    }
                }
                await ctx.SaveChangesAsync();
            }

            var detected = await _localProber.ProbeAllAsync();
            var detectedNonOllama = detected.Where(d => !string.Equals(d.Provider, "ollama", StringComparison.OrdinalIgnoreCase)).ToList();
            if (detectedNonOllama.Count > 0)
            {
                foreach (var d in detectedNonOllama)
                {
                    var match = existing.FirstOrDefault(e => string.Equals(e.Provider, d.Provider, StringComparison.OrdinalIgnoreCase) && e.Name == d.Name);
                    if (match is null)
                    {
                        var m = new AiModel
                        {
                            Name = d.Name,
                            Provider = d.Provider,
                            Endpoint = d.Endpoint,
                            IsLocal = true
                        };
                        ctx.AiModels.Add(m);
                        existing.Add(m);
                    }
                    else
                    {
                        match.Endpoint = d.Endpoint;
                    }
                }
                await ctx.SaveChangesAsync();
            }

            var serviceGroups = detected.GroupBy(d => d.Provider)
                .Select(g => $"{g.Key} ({g.Count()})")
                .ToList();
            DetectedServicesSummary = serviceGroups.Count == 0
                ? "No local model services detected on common ports."
                : "Local services found: " + string.Join(", ", serviceGroups);

            Models.Clear();
            foreach (var m in existing.OrderBy(m => m.Provider).ThenBy(m => m.Name))
                Models.Add(m);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load models failed");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Could not load models: " + ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task AddManualAsync()
    {
        if (string.IsNullOrWhiteSpace(NewModelName))
        {
            await _toast.WarningAsync("Enter a model name.");
            return;
        }
        try
        {
            await using var ctx = _db.CreateContext();
            var endpoint = string.IsNullOrWhiteSpace(NewModelEndpoint) ? null : NewModelEndpoint.Trim();
            var providerNorm = string.IsNullOrWhiteSpace(NewModelProvider) ? "custom" : NewModelProvider.Trim();
            var m = new AiModel
            {
                Name = NewModelName.Trim(),
                Provider = providerNorm,
                Endpoint = endpoint,
                IsLocal = endpoint is not null && (
                    endpoint.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                    || endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase)),
                PublicApiKey = string.IsNullOrWhiteSpace(NewModelPublicKey) ? null : NewModelPublicKey.Trim(),
                PrivateApiKey = string.IsNullOrWhiteSpace(NewModelPrivateKey) ? null : NewModelPrivateKey.Trim()
            };
            ctx.AiModels.Add(m);
            await ctx.SaveChangesAsync();
            Models.Add(m);
            NewModelName = string.Empty;
            NewModelEndpoint = string.Empty;
            NewModelPublicKey = string.Empty;
            NewModelPrivateKey = string.Empty;
            await _toast.SuccessAsync($"Added: {m.Name}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Add model failed");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Add failed: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task RemoveAsync(AiModel? model)
    {
        if (model is null) return;
        try
        {
            await using var ctx = _db.CreateContext();
            var entity = await ctx.AiModels.FindAsync(model.Id);
            if (entity is null) return;
            ctx.AiModels.Remove(entity);
            await ctx.SaveChangesAsync();
            Models.Remove(model);
            await _toast.SuccessAsync($"Removed: {model.Name}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remove model failed");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Remove failed: " + ex.Message);
        }
    }

    private static string Bytes(long bytes)
    {
        if (bytes < 0) return "?";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double b = bytes;
        int u = 0;
        while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
        return $"{b:0.##} {units[u]}";
    }
}
