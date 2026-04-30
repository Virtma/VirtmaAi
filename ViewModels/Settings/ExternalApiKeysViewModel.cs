using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.ExternalApi;

namespace VirtmaAi.ViewModels.Settings;

public sealed partial class ExternalApiKeysViewModel : ViewModelBase
{
    private readonly IExternalApiKeyService _keys;
    private readonly IExternalApiHost _host;
    private readonly ILogger<ExternalApiKeysViewModel> _logger;

    public ExternalApiKeysViewModel(IExternalApiKeyService keys, IExternalApiHost host, ILogger<ExternalApiKeysViewModel> logger)
    {
        _keys = keys;
        _host = host;
        _logger = logger;
    }

    public ObservableCollection<ExternalApiKey> Keys { get; } = new();

    [ObservableProperty] private string _newProgramName = string.Empty;
    [ObservableProperty] private string _newScopes = "chat,plugins,skills,routines";
    [ObservableProperty] private string? _lastIssuedKey;
    [ObservableProperty] private int _hostPort = 33070;
    [ObservableProperty] private bool _hostRunning;
    [ObservableProperty] private string _hostStatus = "Stopped";

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            var list = await _keys.ListAsync();
            Keys.Clear();
            foreach (var k in list) Keys.Add(k);
            HostRunning = _host.IsRunning;
            HostStatus = _host.IsRunning ? $"Listening on http://127.0.0.1:{_host.Port}/" : "Stopped";
        }
        catch (Exception ex) { _logger.LogError(ex, "Load keys"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task IssueAsync()
    {
        if (string.IsNullOrWhiteSpace(NewProgramName)) return;
        try
        {
            var scopes = NewScopes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0);
            var (_, plain) = await _keys.IssueAsync(NewProgramName, scopes);
            LastIssuedKey = plain;
            NewProgramName = string.Empty;
            await LoadAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "Issue key"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task RevokeAsync(ExternalApiKey? key)
    {
        if (key is null) return;
        try { await _keys.RevokeAsync(key.Id); await LoadAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Revoke key"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task StartHostAsync()
    {
        try
        {
            await _host.StartAsync(HostPort);
            HostRunning = true;
            HostStatus = $"Listening on http://127.0.0.1:{HostPort}/";
        }
        catch (Exception ex) { _logger.LogError(ex, "Start API host"); ErrorMessage = ex.Message; HostStatus = "Error: " + ex.Message; }
    }

    [RelayCommand]
    public async Task StopHostAsync()
    {
        try { await _host.StopAsync(); HostRunning = false; HostStatus = "Stopped"; }
        catch (Exception ex) { _logger.LogError(ex, "Stop API host"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public void DismissLastKey() => LastIssuedKey = null;
}
