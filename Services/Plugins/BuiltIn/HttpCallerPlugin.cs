using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Integrations;

namespace VirtmaAi.Services.Plugins.BuiltIn;

/// <summary>
/// Generic HTTP request plugin — input JSON describes method, url, headers, body.
/// If "integration" is supplied, stored credentials are resolved and applied
/// (apiKey → Authorization: Bearer, accessToken → Authorization: Bearer).
/// </summary>
public sealed class HttpCallerPlugin : IBuiltInPlugin
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly IHttpClientFactory _httpFactory;
    private readonly IIntegrationService _integrations;
    private readonly ILogger<HttpCallerPlugin> _logger;

    public HttpCallerPlugin(IHttpClientFactory httpFactory, IIntegrationService integrations, ILogger<HttpCallerPlugin> logger)
    {
        _httpFactory = httpFactory;
        _integrations = integrations;
        _logger = logger;
    }

    public string Name => "http-caller";
    public string Description => "Arbitrary HTTP requests (method/url/headers/body); supports stored integrations";

    public async Task<PluginInvocationResult> InvokeAsync(string input, CancellationToken ct)
    {
        HttpRequestDescriptor? req;
        try { req = JsonSerializer.Deserialize<HttpRequestDescriptor>(input, JsonOpts); }
        catch (Exception ex) { return new PluginInvocationResult(false, string.Empty, "invalid request json: " + ex.Message); }
        if (req is null || string.IsNullOrWhiteSpace(req.Url))
            return new PluginInvocationResult(false, string.Empty, "missing url");

        using var http = _httpFactory.CreateClient();
        using var message = new HttpRequestMessage(new HttpMethod(req.Method ?? "GET"), req.Url);
        if (req.Headers is not null)
        {
            foreach (var kvp in req.Headers)
            {
                if (!message.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value))
                    message.Content?.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
            }
        }
        if (!string.IsNullOrEmpty(req.Body))
        {
            message.Content = new StringContent(req.Body, Encoding.UTF8);
            if (!string.IsNullOrEmpty(req.ContentType))
                message.Content.Headers.ContentType = new MediaTypeHeaderValue(req.ContentType);
        }

        if (!string.IsNullOrWhiteSpace(req.Integration))
        {
            var creds = await _integrations.GetCredentialsByServiceAsync(req.Integration);
            if (creds is not null)
            {
                if (creds.TryGetValue("accessToken", out var at) && !string.IsNullOrEmpty(at))
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", at);
                else if (creds.TryGetValue("apiKey", out var ak) && !string.IsNullOrEmpty(ak))
                {
                    var scheme = req.AuthScheme ?? "Bearer";
                    message.Headers.Authorization = new AuthenticationHeaderValue(scheme, ak);
                }
            }
        }

        try
        {
            using var response = await http.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return new PluginInvocationResult(
                Success: response.IsSuccessStatusCode,
                Output: text,
                Error: response.IsSuccessStatusCode ? null : response.StatusCode.ToString(),
                ExitCode: (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP call failed");
            return new PluginInvocationResult(false, string.Empty, ex.Message);
        }
    }

    private sealed class HttpRequestDescriptor
    {
        public string? Method { get; set; }
        public string? Url { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
        public string? Body { get; set; }
        public string? ContentType { get; set; }
        public string? Integration { get; set; }
        public string? AuthScheme { get; set; }
    }
}
