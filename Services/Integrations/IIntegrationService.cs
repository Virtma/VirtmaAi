using VirtmaAi.Models.Entities;

namespace VirtmaAi.Services.Integrations;

public interface IIntegrationService
{
    IReadOnlyList<IntegrationCatalogEntry> Catalog { get; }
    Task<IReadOnlyList<Integration>> ListAsync();
    Task<Integration> ConnectApiKeyAsync(string serviceName, string? accountIdentifier, string apiKey);
    Task<Integration> ConnectOAuthAsync(string serviceName, string? accountIdentifier, OAuthTokens tokens);
    Task<Integration?> FindByServiceAsync(string serviceName);
    Task<Dictionary<string, string>?> GetCredentialsAsync(Guid integrationId);
    Task<Dictionary<string, string>?> GetCredentialsByServiceAsync(string serviceName);
    Task UpdateCredentialsAsync(Guid integrationId, Dictionary<string, string> credentials);
    Task DisconnectAsync(Guid id);
}

public sealed record IntegrationCatalogEntry(
    string Name,
    string Description,
    IntegrationAuthType AuthType,
    string? AuthorizationEndpoint = null,
    string? TokenEndpoint = null,
    string? Scopes = null,
    string? DocsUrl = null);

public enum IntegrationAuthType
{
    ApiKey,
    OAuth2,
    Unknown
}

public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTime? ExpiresAt, string? Scope);
