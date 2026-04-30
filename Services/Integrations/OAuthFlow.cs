using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VirtmaAi.Services.Integrations;

public sealed class OAuthFlow : IOAuthFlow
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OAuthFlow> _logger;

    public OAuthFlow(IHttpClientFactory httpFactory, ILogger<OAuthFlow> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<OAuthTokens> RunAuthorizationCodeFlowAsync(OAuthFlowOptions options, CancellationToken ct = default)
    {
        var port = options.LoopbackPort > 0 ? options.LoopbackPort : PickFreePort();
        var redirectUri = $"http://127.0.0.1:{port}/callback";

        var state = RandomUrlSafe(24);
        var verifier = RandomUrlSafe(64);
        var challenge = Sha256Base64Url(verifier);

        var authUrl = BuildAuthUrl(options, redirectUri, state, challenge);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        OpenBrowser(authUrl);

        string code;
        try
        {
            var contextTask = listener.GetContextAsync();
            using (ct.Register(() => { try { listener.Stop(); } catch { } }))
            {
                var ctx = await contextTask.ConfigureAwait(false);
                var qs = ctx.Request.Url?.Query ?? string.Empty;
                var query = ParseQuery(qs);
                if (!query.TryGetValue("state", out var gotState) || gotState != state) throw new InvalidOperationException("oauth state mismatch");
                if (query.TryGetValue("error", out var err) && !string.IsNullOrEmpty(err)) throw new InvalidOperationException($"oauth error: {err}");
                if (!query.TryGetValue("code", out var gotCode) || string.IsNullOrEmpty(gotCode)) throw new InvalidOperationException("no code received");
                code = gotCode;
                await WriteHtml(ctx, "<html><body style='font-family:system-ui;padding:32px'><h1>Connected</h1><p>You can close this window and return to VirtmaAi.</p></body></html>").ConfigureAwait(false);
            }
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }

        return await ExchangeCodeAsync(options, code, redirectUri, verifier, ct).ConfigureAwait(false);
    }

    private async Task<OAuthTokens> ExchangeCodeAsync(OAuthFlowOptions options, string code, string redirectUri, string verifier, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("oauth");
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = options.ClientId,
            ["code_verifier"] = verifier
        };
        if (!string.IsNullOrWhiteSpace(options.ClientSecret)) form["client_secret"] = options.ClientSecret;

        using var req = new HttpRequestMessage(HttpMethod.Post, options.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("OAuth token exchange failed: {Status} {Body}", (int)resp.StatusCode, body);
            throw new InvalidOperationException($"token exchange failed ({(int)resp.StatusCode}): {body}");
        }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("no access_token");
        string? refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        string? scope = root.TryGetProperty("scope", out var s) ? s.GetString() : options.Scope;
        DateTime? expires = null;
        if (root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds))
            expires = DateTime.UtcNow.AddSeconds(seconds);
        return new OAuthTokens(access, refresh, expires, scope);
    }

    private static string BuildAuthUrl(OAuthFlowOptions options, string redirectUri, string state, string challenge)
    {
        var sb = new StringBuilder(options.AuthorizationEndpoint);
        sb.Append(options.AuthorizationEndpoint.Contains('?') ? '&' : '?');
        sb.Append("response_type=code");
        sb.Append("&client_id=").Append(Uri.EscapeDataString(options.ClientId));
        sb.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        if (!string.IsNullOrWhiteSpace(options.Scope))
            sb.Append("&scope=").Append(Uri.EscapeDataString(options.Scope));
        sb.Append("&state=").Append(Uri.EscapeDataString(state));
        sb.Append("&code_challenge=").Append(challenge);
        sb.Append("&code_challenge_method=S256");
        sb.Append("&access_type=offline&prompt=consent");
        return sb.ToString();
    }

    private static int PickFreePort()
    {
        var listener = new global::System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string RandomUrlSafe(int bytes)
    {
        var buf = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToBase64String(buf).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Sha256Base64Url(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            if (OperatingSystem.IsWindows()) Process.Start("cmd", $"/c start \"\" \"{url}\"");
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
    }

    private static async Task WriteHtml(HttpListenerContext ctx, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.OutputStream.Close();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query)) return result;
        if (query.StartsWith('?')) query = query[1..];
        foreach (var piece in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = piece.IndexOf('=');
            if (eq < 0) result[Uri.UnescapeDataString(piece)] = string.Empty;
            else result[Uri.UnescapeDataString(piece[..eq])] = Uri.UnescapeDataString(piece[(eq + 1)..]);
        }
        return result;
    }
}
