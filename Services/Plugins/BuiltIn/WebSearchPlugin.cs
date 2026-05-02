using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.Plugins.BuiltIn;

/// <summary>
/// Web Search plugin — searches the internet and browses URLs.
///
/// <para>Input JSON shapes:</para>
/// <code>
/// // Search the web (engine optional: duckduckgo | google | bing, default duckduckgo)
/// { "action": "search", "query": "...", "engine": "duckduckgo", "max_results": 5 }
///
/// // Fetch and read a URL
/// { "action": "browse", "url": "https://..." }
/// </code>
///
/// <para>DuckDuckGo works without any API key.
/// Google requires <c>google.customsearch.apikey</c> + <c>google.customsearch.cx</c> in Settings → API Keys.
/// Bing requires <c>bing.search.apikey</c> in Settings → API Keys.</para>
///
/// <para>The per-operation timeout is controlled via Settings → Plugins → Web Search timeout.
/// Set it to 0 to disable timeouts entirely (wait indefinitely). Default: 30 s.</para>
/// </summary>
public sealed class WebSearchPlugin : IBuiltInPlugin
{
    // ── User-facing setting ───────────────────────────────────────────────────────
    /// <summary>Settings key for the per-operation timeout (seconds). 0 = no timeout.</summary>
    public const string SettingTimeoutKey = "web.search.timeout.seconds";
    public const int    DefaultTimeoutSeconds = 30;

    private const int DefaultMaxResults = 5;
    private const int MaxResultsCap     = 10;
    private const int MaxPageChars      = 16_000; // cap browsed page text returned to model

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // ── Regex helpers ─────────────────────────────────────────────────────────────
    private static readonly Regex ScriptStyleRegex = new(
        @"<(script|style)[^>]*>[\s\S]*?<\/(script|style)>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TagRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled);
    private static readonly Regex MultiSpaceRegex = new(
        @"[ \t]{2,}",
        RegexOptions.Compiled);
    private static readonly Regex MultiNewlineRegex = new(
        @"\n{3,}",
        RegexOptions.Compiled);
    // DDG Lite redirects external results through /l/?uddg=<encoded-url>
    private static readonly Regex DdgRedirectRegex = new(
        @"<a[^>]+href=""(?:/l/\?[^""]*uddg=([^""&]+)[^""]*)"">([^<]+)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DirectLinkRegex = new(
        @"<a[^>]+href=""(https?://(?!duckduckgo)[^""]+)"">([^<]+)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingsService   _settings;
    private readonly ILogger<WebSearchPlugin> _logger;

    public WebSearchPlugin(
        IHttpClientFactory httpFactory,
        ISettingsService settings,
        ILogger<WebSearchPlugin> logger)
    {
        _httpFactory = httpFactory;
        _settings    = settings;
        _logger      = logger;
    }

    public string Name        => "web-search";
    public string Description =>
        "Searches the internet (DuckDuckGo / Google / Bing) or browses a URL. " +
        "Use for current events, documentation, facts, and any information not in training data.";

    public async Task<PluginInvocationResult> InvokeAsync(string input, CancellationToken ct)
    {
        WebSearchCommand? cmd;
        try { cmd = JsonSerializer.Deserialize<WebSearchCommand>(input, JsonOpts); }
        catch (Exception ex)
        { return new PluginInvocationResult(false, string.Empty, "invalid JSON: " + ex.Message); }

        if (cmd is null)
            return new PluginInvocationResult(false, string.Empty,
                "web-search requires 'action' (search or browse)");

        // Respect the user-configured per-operation timeout.
        // 0 means "no timeout" — the CancellationToken 'ct' (user Stop) is the only limit.
        var timeoutSeconds = _settings.Get<int>(SettingTimeoutKey, DefaultTimeoutSeconds);

        try
        {
            return (cmd.Action ?? "search").ToLowerInvariant() switch
            {
                "browse" => await BrowseAsync(cmd, ct, timeoutSeconds).ConfigureAwait(false),
                _        => await SearchAsync(cmd, ct, timeoutSeconds).ConfigureAwait(false),
            };
        }
        // Only re-throw when the USER cancelled — HttpClient internal timeouts also throw
        // OperationCanceledException, and we do NOT want those to look like user stops.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "web-search failed");
            return new PluginInvocationResult(false, string.Empty, ex.Message);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Search
    // ──────────────────────────────────────────────────────────────────────────────

    private async Task<PluginInvocationResult> SearchAsync(
        WebSearchCommand cmd, CancellationToken ct, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(cmd.Query))
            return new PluginInvocationResult(false, string.Empty, "web-search requires 'query'");

        var engine     = (cmd.Engine ?? "duckduckgo").Trim().ToLowerInvariant();
        var maxResults = Math.Clamp(cmd.MaxResults ?? DefaultMaxResults, 1, MaxResultsCap);

        var result = engine switch
        {
            "google" => await SearchGoogleAsync(cmd.Query, maxResults, ct, timeoutSeconds).ConfigureAwait(false),
            "bing"   => await SearchBingAsync(cmd.Query, maxResults, ct, timeoutSeconds).ConfigureAwait(false),
            _        => await SearchDuckDuckGoAsync(cmd.Query, maxResults, ct, timeoutSeconds).ConfigureAwait(false),
        };

        return new PluginInvocationResult(true, result);
    }

    // ── DuckDuckGo (no API key required) ─────────────────────────────────────────

    private async Task<string> SearchDuckDuckGoAsync(
        string query, int maxResults, CancellationToken ct, int timeoutSeconds)
    {
        // Apply per-operation timeout budget, shared across both DDG phases.
        // Distinguishing user-cancel (re-throw) from timeout (log + continue) is done
        // via `when (ct.IsCancellationRequested)` in each catch block.
        CancellationTokenSource? timeoutCts = null;
        CancellationTokenSource? linked = null;
        CancellationToken linkedToken = ct;

        if (timeoutSeconds > 0)
        {
            timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            linkedToken = linked.Token;
        }

        try
        {
            using var http = CreateClient(timeoutSeconds);
            var sb = new StringBuilder();
            sb.AppendLine($"## Search: {query}");
            sb.AppendLine();

            // ── Phase 1: Instant Answers API ─────────────────────────────────────
            try
            {
                var iaUrl = "https://api.duckduckgo.com/?q=" + Uri.EscapeDataString(query) +
                            "&format=json&no_html=1&skip_disambig=1&t=virtmaai";
                var iaJson = await http.GetStringAsync(iaUrl, linkedToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(iaJson);
                var root = doc.RootElement;

                var answer = root.TryGetProperty("Answer", out var a) ? a.GetString() : null;
                if (!string.IsNullOrWhiteSpace(answer))
                {
                    sb.AppendLine("### Direct Answer");
                    sb.AppendLine(answer);
                    sb.AppendLine();
                }

                var abstractText = root.TryGetProperty("AbstractText", out var at) ? at.GetString() : null;
                var abstractUrl  = root.TryGetProperty("AbstractURL",  out var au) ? au.GetString() : null;
                var abstractSrc  = root.TryGetProperty("AbstractSource", out var asrc) ? asrc.GetString() : null;
                if (!string.IsNullOrWhiteSpace(abstractText))
                {
                    sb.AppendLine("### Summary");
                    sb.AppendLine(abstractText);
                    if (!string.IsNullOrWhiteSpace(abstractSrc))
                        sb.AppendLine($"Source: [{abstractSrc}]({abstractUrl})");
                    sb.AppendLine();
                }

                if (root.TryGetProperty("RelatedTopics", out var topics) && topics.GetArrayLength() > 0)
                {
                    int count = 0;
                    var topicSb = new StringBuilder();
                    foreach (var topic in topics.EnumerateArray())
                    {
                        if (count >= maxResults) break;
                        var text = topic.TryGetProperty("Text",     out var t) ? t.GetString() : null;
                        var url  = topic.TryGetProperty("FirstURL", out var u) ? u.GetString() : null;
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        topicSb.AppendLine($"- {text}");
                        if (!string.IsNullOrWhiteSpace(url)) topicSb.AppendLine($"  <{url}>");
                        count++;
                    }
                    if (topicSb.Length > 0)
                    {
                        sb.AppendLine("### Related Topics");
                        sb.Append(topicSb);
                        sb.AppendLine();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DDG Instant Answers API failed");
            }

            // ── Phase 2: DDG Lite HTML ────────────────────────────────────────────
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://lite.duckduckgo.com/lite/");
                req.Content = new FormUrlEncodedContent(
                    new[] { new KeyValuePair<string, string>("q", query) });
                var resp = await http.SendAsync(req, linkedToken).ConfigureAwait(false);
                var html = await resp.Content.ReadAsStringAsync(linkedToken).ConfigureAwait(false);
                var webResults = ParseDdgLiteHtml(html, maxResults);
                if (!string.IsNullOrWhiteSpace(webResults))
                {
                    sb.AppendLine("### Web Results");
                    sb.Append(webResults);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DDG Lite HTML fetch failed");
            }

            return sb.Length > 30 ? sb.ToString() : $"No results found for: {query}";
        }
        finally
        {
            linked?.Dispose();
            timeoutCts?.Dispose();
        }
    }

    private static string ParseDdgLiteHtml(string html, int maxResults)
    {
        html = ScriptStyleRegex.Replace(html, string.Empty);
        var sb   = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int found = 0;

        // DDG Lite redirects: /l/?uddg=<percent-encoded real URL>
        foreach (Match m in DdgRedirectRegex.Matches(html))
        {
            if (found >= maxResults) break;
            string url;
            try   { url = Uri.UnescapeDataString(m.Groups[1].Value); }
            catch { url = m.Groups[1].Value; }
            var title = WebUtility.HtmlDecode(m.Groups[2].Value.Trim());
            if (string.IsNullOrWhiteSpace(title) || title.Length < 3) continue;
            if (!seen.Add(url)) continue;
            sb.AppendLine($"{found + 1}. **{title}**");
            sb.AppendLine($"   <{url}>");
            found++;
        }

        // Fallback: direct external links
        if (found == 0)
        {
            foreach (Match m in DirectLinkRegex.Matches(html))
            {
                if (found >= maxResults) break;
                var url   = m.Groups[1].Value;
                var title = WebUtility.HtmlDecode(m.Groups[2].Value.Trim());
                if (string.IsNullOrWhiteSpace(title) || title.Length < 4) continue;
                if (!seen.Add(url)) continue;
                sb.AppendLine($"{found + 1}. **{title}**");
                sb.AppendLine($"   <{url}>");
                found++;
            }
        }

        return sb.ToString();
    }

    // ── Google Custom Search ──────────────────────────────────────────────────────

    private async Task<string> SearchGoogleAsync(
        string query, int maxResults, CancellationToken ct, int timeoutSeconds)
    {
        var apiKey = await _settings.GetSecretAsync("google.customsearch.apikey").ConfigureAwait(false);
        var cx     = _settings.Get<string>("google.customsearch.cx") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(cx))
            return "Google Custom Search requires an API key and Search Engine ID. " +
                   "Add 'google.customsearch.apikey' and 'google.customsearch.cx' in Settings → API Keys. " +
                   "Alternatively use engine=duckduckgo (no key required).";

        using var http = CreateClient(timeoutSeconds);
        var url = $"https://www.googleapis.com/customsearch/v1?key={apiKey}&cx={cx}" +
                  $"&q={Uri.EscapeDataString(query)}&num={maxResults}";
        var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine($"## Google search: {query}");
        sb.AppendLine();

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            var msg = err.TryGetProperty("message", out var m) ? m.GetString() : json;
            return $"Google API error: {msg}";
        }

        if (!doc.RootElement.TryGetProperty("items", out var items))
            return $"No results found for: {query}";

        int i = 1;
        foreach (var item in items.EnumerateArray())
        {
            var title   = item.TryGetProperty("title",   out var t) ? t.GetString() : null;
            var link    = item.TryGetProperty("link",    out var l) ? l.GetString() : null;
            var snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() : null;
            sb.AppendLine($"{i++}. **{title}**");
            sb.AppendLine($"   <{link}>");
            if (!string.IsNullOrWhiteSpace(snippet)) sb.AppendLine($"   {snippet}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ── Bing Web Search ───────────────────────────────────────────────────────────

    private async Task<string> SearchBingAsync(
        string query, int maxResults, CancellationToken ct, int timeoutSeconds)
    {
        var apiKey = await _settings.GetSecretAsync("bing.search.apikey").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
            return "Bing Web Search requires an API key. " +
                   "Add 'bing.search.apikey' in Settings → API Keys. " +
                   "Alternatively use engine=duckduckgo (no key required).";

        using var http = CreateClient(timeoutSeconds);
        http.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", apiKey);
        var url = $"https://api.bing.microsoft.com/v7.0/search" +
                  $"?q={Uri.EscapeDataString(query)}&count={maxResults}&mkt=en-US";
        var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine($"## Bing search: {query}");
        sb.AppendLine();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("webPages", out var pages) ||
            !pages.TryGetProperty("value", out var results))
            return $"No results found for: {query}";

        int i = 1;
        foreach (var r in results.EnumerateArray())
        {
            var name    = r.TryGetProperty("name",    out var n) ? n.GetString() : null;
            var link    = r.TryGetProperty("url",     out var u) ? u.GetString() : null;
            var snippet = r.TryGetProperty("snippet", out var s) ? s.GetString() : null;
            sb.AppendLine($"{i++}. **{name}**");
            sb.AppendLine($"   <{link}>");
            if (!string.IsNullOrWhiteSpace(snippet)) sb.AppendLine($"   {snippet}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Browse URL
    // ──────────────────────────────────────────────────────────────────────────────

    private async Task<PluginInvocationResult> BrowseAsync(
        WebSearchCommand cmd, CancellationToken ct, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(cmd.Url))
            return new PluginInvocationResult(false, string.Empty, "browse requires 'url'");

        if (!Uri.TryCreate(cmd.Url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new PluginInvocationResult(false, string.Empty,
                "Invalid URL — only http:// and https:// are supported");

        CancellationTokenSource? timeoutCts = null;
        CancellationTokenSource? linked = null;
        CancellationToken linkedToken = ct;

        if (timeoutSeconds > 0)
        {
            timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            linkedToken = linked.Token;
        }

        try
        {
            using var http = CreateClient(timeoutSeconds);
            var response = await http.GetAsync(cmd.Url, linkedToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new PluginInvocationResult(false, string.Empty,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {cmd.Url}");

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!contentType.StartsWith("text/",         StringComparison.OrdinalIgnoreCase) &&
                !contentType.Contains("json",            StringComparison.OrdinalIgnoreCase) &&
                !contentType.Contains("xml",             StringComparison.OrdinalIgnoreCase))
                return new PluginInvocationResult(false, string.Empty,
                    $"Cannot read content-type '{contentType}' — only HTML, JSON, and XML pages are supported. " +
                    $"To download binary files use the project-files plugin.");

            var raw   = await response.Content.ReadAsStringAsync(linkedToken).ConfigureAwait(false);
            var clean = ExtractPageText(raw, cmd.Url);
            return new PluginInvocationResult(true, clean);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            var label = timeoutSeconds > 0 ? $"{timeoutSeconds} s" : "the configured limit";
            return new PluginInvocationResult(false, string.Empty,
                $"Browse timed out after {label} — the page took too long to respond: {cmd.Url}");
        }
        // Non-cancellation exceptions (DNS, connection refused, etc.) bubble to InvokeAsync's handler.
        finally
        {
            linked?.Dispose();
            timeoutCts?.Dispose();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // HTML → plain text
    // ──────────────────────────────────────────────────────────────────────────────

    private static string ExtractPageText(string html, string url)
    {
        var text = ScriptStyleRegex.Replace(html, " ");
        text = Regex.Replace(text,
            @"<(br|p|div|h[1-6]|li|tr|blockquote|pre|hr)[^>]*>",
            "\n", RegexOptions.IgnoreCase);
        text = TagRegex.Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = MultiSpaceRegex.Replace(text, " ");
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        text = MultiNewlineRegex.Replace(text, "\n\n").Trim();

        if (text.Length > MaxPageChars)
            text = text[..MaxPageChars] +
                   $"\n\n…[page content truncated at {MaxPageChars:N0} characters. " +
                   "Use a more targeted URL or search query to retrieve specific content.]";

        return $"## Page: {url}\n\n{text}";
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Shared HTTP client
    // ──────────────────────────────────────────────────────────────────────────────

    private HttpClient CreateClient(int timeoutSeconds)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        // Mirror the user's per-operation timeout so HttpClient's internal safety net
        // is consistent with the linked CancellationToken budget.
        // 0 = no timeout → wait indefinitely (user's Stop button is the only limit).
        // TimeSpan(-1 ms) is the canonical "infinite" value for HttpClient.Timeout.
        http.Timeout = timeoutSeconds > 0
            ? TimeSpan.FromSeconds(timeoutSeconds)
            : TimeSpan.FromMilliseconds(-1);
        return http;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Command shape
    // ──────────────────────────────────────────────────────────────────────────────

    private sealed class WebSearchCommand
    {
        /// <summary>"search" (default) or "browse".</summary>
        public string? Action { get; set; }
        /// <summary>Search query (action=search).</summary>
        public string? Query { get; set; }
        /// <summary>URL to fetch (action=browse).</summary>
        public string? Url { get; set; }
        /// <summary>Search engine: duckduckgo (default, no key), google, bing.</summary>
        public string? Engine { get; set; }
        /// <summary>Number of results to return (1–10). Default: 5.</summary>
        public int? MaxResults { get; set; }
    }
}
