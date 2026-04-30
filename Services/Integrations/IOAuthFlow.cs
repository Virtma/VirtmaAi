namespace VirtmaAi.Services.Integrations;

public interface IOAuthFlow
{
    Task<OAuthTokens> RunAuthorizationCodeFlowAsync(
        OAuthFlowOptions options,
        CancellationToken ct = default);
}

public sealed record OAuthFlowOptions(
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string ClientId,
    string? ClientSecret,
    string Scope,
    int LoopbackPort = 0,
    string? AudienceOrResource = null);
