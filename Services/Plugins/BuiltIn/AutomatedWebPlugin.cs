using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VirtmaAi.Services.Plugins.BuiltIn;

/// <summary>
/// Automated Web — cross-platform HTTP automation. Ships a minimal, commercial-friendly scripting
/// surface (fetch, submit, extract) that works on mobile and desktop without bundling a browser
/// runtime. Heavy-duty Playwright scripting is a post-Phase-18 optimization if/when required.
/// </summary>
public sealed partial class AutomatedWebPlugin : IBuiltInPlugin
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<meta\s+name=[""']description[""']\s+content=[""']([^""']*)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaDescriptionRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AutomatedWebPlugin> _logger;

    public AutomatedWebPlugin(IHttpClientFactory httpFactory, ILogger<AutomatedWebPlugin> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public string Name => "automated-web";
    public string Description => "Scripted HTTP requests, form submission, and HTML text extraction";

    public async Task<PluginInvocationResult> InvokeAsync(string input, CancellationToken ct)
    {
        AutomatedWebCommand? cmd;
        try { cmd = JsonSerializer.Deserialize<AutomatedWebCommand>(input, JsonOpts); }
        catch (Exception ex) { return new PluginInvocationResult(false, string.Empty, "invalid command json: " + ex.Message); }
        if (cmd is null) return new PluginInvocationResult(false, string.Empty, "empty command");

        try
        {
            return cmd.Action?.ToLowerInvariant() switch
            {
                "fetch" => await FetchAsync(cmd, ct),
                "extract-text" => await ExtractTextAsync(cmd, ct),
                "submit-form" => await SubmitFormAsync(cmd, ct),
                _ => new PluginInvocationResult(false, string.Empty, "unknown action: " + cmd.Action)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "automated-web action failed");
            return new PluginInvocationResult(false, string.Empty, ex.Message);
        }
    }

    private async Task<PluginInvocationResult> FetchAsync(AutomatedWebCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Url))
            return new PluginInvocationResult(false, string.Empty, "fetch requires 'url'");
        using var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(cmd.TimeoutSeconds <= 0 ? 20 : cmd.TimeoutSeconds);
        ApplyHeaders(client, cmd.Headers);
        using var response = await client.GetAsync(cmd.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var trimmed = body.Length > 16_000 ? body[..16_000] + "\n... [truncated]" : body;
        var payload = new
        {
            status = (int)response.StatusCode,
            url = response.RequestMessage?.RequestUri?.ToString() ?? cmd.Url,
            contentType = response.Content.Headers.ContentType?.ToString(),
            body = trimmed
        };
        return new PluginInvocationResult(response.IsSuccessStatusCode, JsonSerializer.Serialize(payload, JsonOpts));
    }

    private async Task<PluginInvocationResult> ExtractTextAsync(AutomatedWebCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Url))
            return new PluginInvocationResult(false, string.Empty, "extract-text requires 'url'");
        using var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(cmd.TimeoutSeconds <= 0 ? 20 : cmd.TimeoutSeconds);
        ApplyHeaders(client, cmd.Headers);
        var html = await client.GetStringAsync(cmd.Url, ct).ConfigureAwait(false);

        var title = TitleRegex().Match(html).Groups[1].Value.Trim();
        var desc = MetaDescriptionRegex().Match(html).Groups[1].Value.Trim();
        var stripped = HtmlTagRegex().Replace(html, " ");
        stripped = WebUtility.HtmlDecode(stripped);
        stripped = Regex.Replace(stripped, @"\s+", " ").Trim();
        if (stripped.Length > 8_000) stripped = stripped[..8_000] + "...";

        var payload = new { title, description = desc, text = stripped };
        return new PluginInvocationResult(true, JsonSerializer.Serialize(payload, JsonOpts));
    }

    private async Task<PluginInvocationResult> SubmitFormAsync(AutomatedWebCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Url))
            return new PluginInvocationResult(false, string.Empty, "submit-form requires 'url'");
        if (cmd.Fields is null || cmd.Fields.Count == 0)
            return new PluginInvocationResult(false, string.Empty, "submit-form requires 'fields'");
        using var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(cmd.TimeoutSeconds <= 0 ? 20 : cmd.TimeoutSeconds);
        ApplyHeaders(client, cmd.Headers);

        HttpContent content;
        if (string.Equals(cmd.Encoding, "json", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(cmd.Fields);
            content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        else
        {
            content = new FormUrlEncodedContent(cmd.Fields);
        }

        using var response = await client.PostAsync(cmd.Url, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var trimmed = body.Length > 16_000 ? body[..16_000] + "\n... [truncated]" : body;
        var payload = new
        {
            status = (int)response.StatusCode,
            url = response.RequestMessage?.RequestUri?.ToString() ?? cmd.Url,
            contentType = response.Content.Headers.ContentType?.ToString(),
            body = trimmed
        };
        return new PluginInvocationResult(response.IsSuccessStatusCode, JsonSerializer.Serialize(payload, JsonOpts));
    }

    private static void ApplyHeaders(HttpClient client, Dictionary<string, string>? headers)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VirtmaAi/0.1 (+automated-web)");
        if (headers is null) return;
        foreach (var kv in headers)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(kv.Key, kv.Value);
        }
    }

    private sealed class AutomatedWebCommand
    {
        public string? Action { get; set; }
        public string? Url { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
        public Dictionary<string, string>? Fields { get; set; }
        public string? Encoding { get; set; }
        public int TimeoutSeconds { get; set; }
    }
}
