namespace VirtmaAi.Services.Integrations;

/// <summary>
/// Built-in OAuth 2.0 client identifiers shipped with VirtmaAi. Each provider's `client_id` is a
/// public identifier for an app registration — it's safe to embed in a desktop binary (the secret,
/// where applicable, is exchanged via PKCE rather than a client_secret).
///
/// **TODO (Daniel):** before public release, replace each `null` with a real client ID registered
/// against the provider's developer console. The lookup falls back to whatever the user enters in
/// the Integrations → Advanced pane if the bundled value is null.
///
/// Registration links:
///   - Google:    https://console.cloud.google.com/apis/credentials  (OAuth 2.0 Client ID, type "Desktop app")
///   - GitHub:    https://github.com/settings/developers              (OAuth Apps → New OAuth App)
///   - Microsoft: https://portal.azure.com/ → App registrations
///   - Twitch:    https://dev.twitch.tv/console/apps
///   - LinkedIn:  https://www.linkedin.com/developers/apps
/// </summary>
public static class OAuthClientRegistry
{
    private static readonly Dictionary<string, string?> _defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        // Fill these in once registered. Until then, the user can still bring their own via the
        // Advanced toggle.
        ["Google"]    = null,
        ["YouTube"]   = null, // shares Google's OAuth — usually the same client id as Google
        ["Microsoft"] = null,
        ["GitHub"]    = null,
        ["Twitch"]    = null,
        ["LinkedIn"]  = null,
    };

    /// <summary>
    /// Returns the bundled client ID for the given service name, or <c>null</c> if none has been
    /// shipped yet (the UI should fall back to a user-supplied id).
    /// </summary>
    public static string? GetClientId(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return null;
        return _defaults.TryGetValue(serviceName, out var id) ? id : null;
    }

    public static bool HasBundledClient(string serviceName) => !string.IsNullOrWhiteSpace(GetClientId(serviceName));
}
