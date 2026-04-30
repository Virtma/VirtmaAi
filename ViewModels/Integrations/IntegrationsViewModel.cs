using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Integrations;
using VirtmaAi.Services.Notifications;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.ViewModels.Integrations;

public sealed partial class IntegrationsViewModel : ViewModelBase
{
    private const string OauthClientIdSettingPrefix = "integrations.oauth.clientId.";

    private readonly IIntegrationService _svc;
    private readonly IOAuthFlow _oauth;
    private readonly ISettingsService _settings;
    private readonly IToastService _toast;
    private readonly ILogger<IntegrationsViewModel> _logger;

    public IntegrationsViewModel(
        IIntegrationService svc,
        IOAuthFlow oauth,
        ISettingsService settings,
        IToastService toast,
        ILogger<IntegrationsViewModel> logger)
    {
        _svc = svc;
        _oauth = oauth;
        _settings = settings;
        _toast = toast;
        _logger = logger;
        foreach (var entry in _svc.Catalog) Catalog.Add(entry);
    }

    public ObservableCollection<IntegrationCatalogEntry> Catalog { get; } = new();
    public ObservableCollection<Integration> Connected { get; } = new();

    [ObservableProperty] private IntegrationCatalogEntry? _selectedCatalog;
    [ObservableProperty] private Integration? _selectedConnected;
    [ObservableProperty] private string _accountIdentifier = string.Empty;
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string _oauthClientId = string.Empty;
    [ObservableProperty] private string _oauthClientSecret = string.Empty;
    [ObservableProperty] private string _oauthScopeOverride = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private string _unknownServiceName = string.Empty;
    [ObservableProperty] private string _unknownBaseUrl = string.Empty;
    [ObservableProperty] private string _unknownCredentials = string.Empty;

    public bool IsApiKey => SelectedCatalog?.AuthType == IntegrationAuthType.ApiKey;
    public bool IsOAuth => SelectedCatalog?.AuthType == IntegrationAuthType.OAuth2;
    public bool IsUnknown => SelectedCatalog?.AuthType == IntegrationAuthType.Unknown;

    partial void OnSelectedCatalogChanged(IntegrationCatalogEntry? value)
    {
        OnPropertyChanged(nameof(IsApiKey));
        OnPropertyChanged(nameof(IsOAuth));
        OnPropertyChanged(nameof(IsUnknown));
        ApiKey = string.Empty;
        OauthClientSecret = string.Empty;
        OauthScopeOverride = value?.Scopes ?? string.Empty;
        StatusMessage = string.Empty;
        // Resolution priority for client id: user override → bundled default → empty.
        OauthClientId = value is null
            ? string.Empty
            : _settings.Get<string>(OauthClientIdSettingPrefix + value.Name)
              ?? OAuthClientRegistry.GetClientId(value.Name)
              ?? string.Empty;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            var list = await _svc.ListAsync();
            Connected.Clear();
            foreach (var i in list) Connected.Add(i);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load integrations");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Could not load integrations: " + ex.Message);
        }
    }

    public bool IsServiceConnected(string serviceName)
        => Connected.Any(c => string.Equals(c.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    public async Task QuickConnectAsync(IntegrationCatalogEntry? entry)
    {
        if (entry is null) return;
        SelectedCatalog = entry;

        if (IsServiceConnected(entry.Name))
        {
            await _toast.WarningAsync($"{entry.Name} is already connected. Use the right pane to disconnect.");
            return;
        }

        switch (entry.AuthType)
        {
            case IntegrationAuthType.OAuth2:
                // Try bundled client id first, then any user override. The user is no longer asked
                // for one — if neither is present we surface a precise actionable error rather
                // than a generic "OAuth client ID is required".
                if (string.IsNullOrWhiteSpace(OauthClientId))
                    OauthClientId = OAuthClientRegistry.GetClientId(entry.Name) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(OauthClientId))
                {
                    await _toast.ErrorAsync(
                        $"{entry.Name} OAuth isn't shipped with a default client ID yet. " +
                        $"Open Advanced below to provide your own, or wait for an app update.");
                    return;
                }
                await ConnectOAuthAsync();
                break;
            case IntegrationAuthType.ApiKey:
                if (string.IsNullOrWhiteSpace(ApiKey))
                {
                    await _toast.WarningAsync("Paste the API key, then click Connect again.");
                    return;
                }
                await ConnectApiKeyAsync();
                break;
            case IntegrationAuthType.Unknown:
                await _toast.WarningAsync("Fill out the custom service form and click Save.");
                break;
        }
    }

    [RelayCommand]
    public async Task ConnectApiKeyAsync()
    {
        if (SelectedCatalog is null) { await _toast.WarningAsync("Pick a service first."); return; }
        if (string.IsNullOrWhiteSpace(ApiKey)) { await _toast.WarningAsync("API key is required."); return; }
        IsBusy = true;
        try
        {
            await _svc.ConnectApiKeyAsync(SelectedCatalog.Name, AccountIdentifier, ApiKey);
            StatusMessage = $"Connected {SelectedCatalog.Name}.";
            ApiKey = string.Empty;
            await LoadAsync();
            await _toast.SuccessAsync($"Connected: {SelectedCatalog.Name}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connect API key");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Connect failed: " + ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task ConnectOAuthAsync()
    {
        if (SelectedCatalog is null) { await _toast.WarningAsync("Pick a service first."); return; }
        if (string.IsNullOrWhiteSpace(OauthClientId)) { await _toast.WarningAsync("OAuth client ID is required."); return; }
        if (SelectedCatalog.AuthorizationEndpoint is null || SelectedCatalog.TokenEndpoint is null)
        {
            await _toast.ErrorAsync("This service has no OAuth endpoints configured.");
            return;
        }
        IsBusy = true;
        try
        {
            StatusMessage = "Opening browser…";
            _settings.Set(OauthClientIdSettingPrefix + SelectedCatalog.Name, OauthClientId);
            var scope = string.IsNullOrWhiteSpace(OauthScopeOverride) ? (SelectedCatalog.Scopes ?? string.Empty) : OauthScopeOverride;
            var tokens = await _oauth.RunAuthorizationCodeFlowAsync(new OAuthFlowOptions(
                SelectedCatalog.AuthorizationEndpoint,
                SelectedCatalog.TokenEndpoint,
                OauthClientId,
                string.IsNullOrWhiteSpace(OauthClientSecret) ? null : OauthClientSecret,
                scope));
            await _svc.ConnectOAuthAsync(SelectedCatalog.Name, AccountIdentifier, tokens);
            StatusMessage = $"Connected {SelectedCatalog.Name}.";
            await LoadAsync();
            await _toast.SuccessAsync($"Connected: {SelectedCatalog.Name}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connect OAuth");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("OAuth failed: " + ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task SaveUnknownAsync()
    {
        if (string.IsNullOrWhiteSpace(UnknownServiceName)) { await _toast.WarningAsync("Service name required."); return; }
        IsBusy = true;
        try
        {
            var bag = new Dictionary<string, string>
            {
                ["baseUrl"] = UnknownBaseUrl,
                ["raw"] = UnknownCredentials
            };
            var entity = await _svc.ConnectApiKeyAsync(UnknownServiceName, AccountIdentifier, UnknownCredentials);
            await _svc.UpdateCredentialsAsync(entity.Id, bag);
            StatusMessage = $"Saved {UnknownServiceName}.";
            UnknownServiceName = string.Empty;
            UnknownBaseUrl = string.Empty;
            UnknownCredentials = string.Empty;
            await LoadAsync();
            await _toast.SuccessAsync("Custom service saved.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save unknown service");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Save failed: " + ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task DisconnectAsync()
    {
        if (SelectedConnected is null) { await _toast.WarningAsync("Pick a connected service first."); return; }
        try
        {
            var name = SelectedConnected.ServiceName;
            await _svc.DisconnectAsync(SelectedConnected.Id);
            StatusMessage = "Disconnected.";
            SelectedConnected = null;
            await LoadAsync();
            await _toast.SuccessAsync($"Disconnected: {name}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Disconnect");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Disconnect failed: " + ex.Message);
        }
    }
}
