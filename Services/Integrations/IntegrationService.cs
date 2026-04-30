using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Data;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.Integrations;

public sealed class IntegrationService : IIntegrationService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private static readonly IntegrationCatalogEntry[] DefaultCatalog =
    {
        new("Google",         "Calendar, Drive, Gmail",        IntegrationAuthType.OAuth2, "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token", "https://www.googleapis.com/auth/calendar https://www.googleapis.com/auth/drive.readonly https://www.googleapis.com/auth/gmail.readonly", "https://developers.google.com/identity/protocols/oauth2"),
        new("Microsoft",      "Outlook, OneDrive, Teams",      IntegrationAuthType.OAuth2, "https://login.microsoftonline.com/common/oauth2/v2.0/authorize", "https://login.microsoftonline.com/common/oauth2/v2.0/token", "offline_access User.Read Files.Read Mail.Read", "https://learn.microsoft.com/azure/active-directory/develop/v2-oauth2-auth-code-flow"),
        new("GitHub",         "Code, issues, PRs",             IntegrationAuthType.OAuth2, "https://github.com/login/oauth/authorize", "https://github.com/login/oauth/access_token", "repo read:user", "https://docs.github.com/developers/apps/building-oauth-apps"),
        new("YouTube",        "Channel, analytics",            IntegrationAuthType.OAuth2, "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token", "https://www.googleapis.com/auth/youtube.readonly", null),
        new("Twitch",         "Stream / chat",                 IntegrationAuthType.OAuth2, "https://id.twitch.tv/oauth2/authorize", "https://id.twitch.tv/oauth2/token", "user:read:email chat:read chat:edit", null),
        new("LinkedIn",       "Profile, jobs",                 IntegrationAuthType.OAuth2, "https://www.linkedin.com/oauth/v2/authorization", "https://www.linkedin.com/oauth/v2/accessToken", "r_liteprofile r_emailaddress", null),
        new("Steam",          "Game library, friends",         IntegrationAuthType.ApiKey, null, null, null, "https://steamcommunity.com/dev/apikey"),
        new("Indeed",         "Job search",                    IntegrationAuthType.ApiKey, null, null, null, "https://developer.indeed.com/docs"),
        new("Amazon",         "PA-API, orders",                IntegrationAuthType.ApiKey, null, null, null, "https://webservices.amazon.com/paapi5/documentation/"),
        new("Anthropic",      "Claude API",                    IntegrationAuthType.ApiKey, null, null, null, "https://docs.anthropic.com"),
        new("OpenAI",         "OpenAI API",                    IntegrationAuthType.ApiKey, null, null, null, "https://platform.openai.com/docs"),
        new("Postman",        "Collections, monitors",         IntegrationAuthType.ApiKey, null, null, null, "https://learning.postman.com/docs/developer/intro-api/"),
        new("Apple",          "iCloud, calendar",              IntegrationAuthType.Unknown, null, null, null, null)
    };

    private readonly IDatabaseService _db;
    private readonly ISettingsService _settings;
    private readonly ILogger<IntegrationService> _logger;

    public IntegrationService(IDatabaseService db, ISettingsService settings, ILogger<IntegrationService> logger)
    {
        _db = db;
        _settings = settings;
        _logger = logger;
    }

    public IReadOnlyList<IntegrationCatalogEntry> Catalog => DefaultCatalog;

    public async Task<IReadOnlyList<Integration>> ListAsync()
    {
        if (_db.Current is null) return Array.Empty<Integration>();
        await using var ctx = _db.CreateContext();
        return await ctx.Integrations.OrderBy(i => i.ServiceName).ToListAsync();
    }

    public async Task<Integration> ConnectApiKeyAsync(string serviceName, string? accountIdentifier, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) throw new ArgumentException("service required");
        var bag = new Dictionary<string, string> { ["apiKey"] = apiKey };
        return await UpsertAsync(serviceName, accountIdentifier, bag);
    }

    public async Task<Integration> ConnectOAuthAsync(string serviceName, string? accountIdentifier, OAuthTokens tokens)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) throw new ArgumentException("service required");
        var bag = new Dictionary<string, string>
        {
            ["accessToken"] = tokens.AccessToken
        };
        if (!string.IsNullOrWhiteSpace(tokens.RefreshToken)) bag["refreshToken"] = tokens.RefreshToken;
        if (tokens.ExpiresAt is { } exp) bag["expiresAt"] = exp.ToString("O");
        if (!string.IsNullOrWhiteSpace(tokens.Scope)) bag["scope"] = tokens.Scope;
        return await UpsertAsync(serviceName, accountIdentifier, bag);
    }

    public async Task<Integration?> FindByServiceAsync(string serviceName)
    {
        if (_db.Current is null) return null;
        await using var ctx = _db.CreateContext();
        return await ctx.Integrations.FirstOrDefaultAsync(i => i.ServiceName == serviceName);
    }

    public async Task<Dictionary<string, string>?> GetCredentialsAsync(Guid integrationId)
    {
        if (_db.Current is null) return null;
        await using var ctx = _db.CreateContext();
        var entity = await ctx.Integrations.FindAsync(integrationId);
        if (entity is null) return null;
        var secret = await _settings.GetSecretAsync(entity.SecureStorageKey);
        if (string.IsNullOrWhiteSpace(secret)) return null;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(secret, JsonOpts);
    }

    public async Task<Dictionary<string, string>?> GetCredentialsByServiceAsync(string serviceName)
    {
        var entity = await FindByServiceAsync(serviceName);
        if (entity is null) return null;
        return await GetCredentialsAsync(entity.Id);
    }

    public async Task UpdateCredentialsAsync(Guid integrationId, Dictionary<string, string> credentials)
    {
        if (_db.Current is null) throw new InvalidOperationException("database not initialized");
        await using var ctx = _db.CreateContext();
        var entity = await ctx.Integrations.FindAsync(integrationId)
            ?? throw new KeyNotFoundException("integration not found");
        await _settings.SetSecretAsync(entity.SecureStorageKey, JsonSerializer.Serialize(credentials, JsonOpts));
    }

    public async Task DisconnectAsync(Guid id)
    {
        if (_db.Current is null) return;
        await using var ctx = _db.CreateContext();
        var entity = await ctx.Integrations.FindAsync(id);
        if (entity is null) return;
        try { await _settings.RemoveSecretAsync(entity.SecureStorageKey); } catch { }
        ctx.Integrations.Remove(entity);
        await ctx.SaveChangesAsync();
    }

    private async Task<Integration> UpsertAsync(string serviceName, string? accountIdentifier, Dictionary<string, string> credentials)
    {
        if (_db.Current is null) throw new InvalidOperationException("database not initialized");
        await using var ctx = _db.CreateContext();
        var existing = await ctx.Integrations.FirstOrDefaultAsync(i => i.ServiceName == serviceName);
        Integration entity;
        if (existing is null)
        {
            entity = new Integration
            {
                ServiceName = serviceName,
                AccountIdentifier = accountIdentifier,
                SecureStorageKey = "integration:" + Guid.NewGuid().ToString("N"),
                ConnectedAt = DateTime.UtcNow
            };
            ctx.Integrations.Add(entity);
        }
        else
        {
            existing.AccountIdentifier = accountIdentifier;
            existing.ConnectedAt = DateTime.UtcNow;
            entity = existing;
        }
        await _settings.SetSecretAsync(entity.SecureStorageKey, JsonSerializer.Serialize(credentials, JsonOpts));
        await ctx.SaveChangesAsync();
        return entity;
    }
}
