using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.AI.Providers;

public sealed class AnthropicProvider : IAiProvider
{
    public const string ApiKeySecretName = "virtmaai.anthropic.api_key";
    private const string ApiBase = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingsService _settings;
    private readonly ILogger<AnthropicProvider> _logger;

    public AnthropicProvider(
        IHttpClientFactory httpFactory,
        ISettingsService settings,
        ILogger<AnthropicProvider> logger)
    {
        _httpFactory = httpFactory;
        _settings = settings;
        _logger = logger;
    }

    public string Id => "anthropic";
    public string DisplayName => "Claude (Anthropic)";
    public bool SupportsThinking => true;

    public async IAsyncEnumerable<ChatEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var apiKey = await _settings.GetSecretAsync(ApiKeySecretName).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            yield return new StreamError("Anthropic API key not configured", null);
            yield break;
        }

        var payload = BuildRequestJson(request);
        using var http = _httpFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", ApiVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage? response = null;
        Exception? sendError = null;
        try
        {
            response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sendError = ex;
        }
        if (sendError is not null || response is null)
        {
            yield return new StreamError("Anthropic request failed: " + (sendError?.Message ?? "no response"), sendError);
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                yield return new StreamError($"Anthropic HTTP {(int)response.StatusCode}: {err}", null);
                yield break;
            }

            await foreach (var evt in ParseSseAsync(response, cancellationToken).ConfigureAwait(false))
                yield return evt;
        }
    }

    private static string BuildRequestJson(ChatRequest request)
    {
        var messages = request.Messages
            .Where(m => m.Role is ChatRole.User or ChatRole.Assistant)
            .Select(m => (object)new
            {
                role = m.Role == ChatRole.User ? "user" : "assistant",
                content = BuildMessageContent(m)
            })
            .ToArray();

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxTokens ?? 4096,
            ["temperature"] = request.Temperature,
            ["stream"] = true
        };
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            body["system"] = request.SystemPrompt;
        if (request.StopSequences is { Count: > 0 })
            body["stop_sequences"] = request.StopSequences;

        return JsonSerializer.Serialize(body);
    }

    /// <summary>
    /// Returns either a plain string or a content-block array for the Anthropic API.
    /// Vision requests use the array form: image source blocks followed by the text block.
    /// </summary>
    private static object BuildMessageContent(ChatMessage m)
    {
        if (m.Images is not { Count: > 0 })
            return m.Content;

        // Vision request: one image block per attached image, then the text.
        var parts = new List<object>(m.Images.Count + 1);
        foreach (var img in m.Images)
        {
            parts.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = img.MimeType,
                    data = img.Base64Data
                }
            });
        }
        if (!string.IsNullOrEmpty(m.Content))
            parts.Add(new { type = "text", text = m.Content });
        return parts;
    }

    private static async IAsyncEnumerable<ChatEvent> ParseSseAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? currentEvent = null;
        int? promptTokens = null;
        int? completionTokens = null;
        string? stopReason = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0) { currentEvent = null; continue; }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line[6..].Trim();
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].Trim();
            if (string.IsNullOrEmpty(data) || data == "[DONE]") continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : currentEvent;

                switch (type)
                {
                    case "content_block_delta":
                        if (root.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("type", out var deltaType))
                        {
                            var dt = deltaType.GetString();
                            var text = delta.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                            var thinking = delta.TryGetProperty("thinking", out var th) ? th.GetString() : null;
                            if (dt == "text_delta" && !string.IsNullOrEmpty(text))
                                yield return new ContentChunk(text!);
                            else if (dt == "thinking_delta" && !string.IsNullOrEmpty(thinking))
                                yield return new ThinkingChunk(thinking!);
                        }
                        break;

                    case "message_delta":
                        if (root.TryGetProperty("delta", out var mdelta) &&
                            mdelta.TryGetProperty("stop_reason", out var sr))
                            stopReason = sr.GetString();
                        if (root.TryGetProperty("usage", out var usage) &&
                            usage.TryGetProperty("output_tokens", out var ot))
                            completionTokens = ot.GetInt32();
                        break;

                    case "message_start":
                        if (root.TryGetProperty("message", out var msg) &&
                            msg.TryGetProperty("usage", out var u0) &&
                            u0.TryGetProperty("input_tokens", out var it))
                            promptTokens = it.GetInt32();
                        break;

                    case "message_stop":
                        yield return new StreamCompleted(promptTokens, completionTokens, stopReason);
                        yield break;
                }
            }
        }

        yield return new StreamCompleted(promptTokens, completionTokens, stopReason);
    }
}
