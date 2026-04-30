using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.AI.Providers;

public sealed class OpenAiProvider : IAiProvider
{
    public const string ApiKeySecretName = "virtmaai.openai.api_key";
    private const string ApiBase = "https://api.openai.com/v1/chat/completions";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingsService _settings;
    private readonly ILogger<OpenAiProvider> _logger;

    public OpenAiProvider(
        IHttpClientFactory httpFactory,
        ISettingsService settings,
        ILogger<OpenAiProvider> logger)
    {
        _httpFactory = httpFactory;
        _settings = settings;
        _logger = logger;
    }

    public string Id => "openai";
    public string DisplayName => "OpenAI (ChatGPT)";
    public bool SupportsThinking => false;

    public async IAsyncEnumerable<ChatEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var apiKey = await _settings.GetSecretAsync(ApiKeySecretName).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            yield return new StreamError("OpenAI API key not configured", null);
            yield break;
        }

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new { role = "system", content = request.SystemPrompt });
        foreach (var m in request.Messages)
            messages.Add(new { role = MapRole(m.Role), content = BuildMessageContent(m) });

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["messages"] = messages,
            ["temperature"] = request.Temperature,
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true }
        };
        if (request.MaxTokens is int mt) body["max_tokens"] = mt;
        if (request.StopSequences is { Count: > 0 }) body["stop"] = request.StopSequences;

        var payload = JsonSerializer.Serialize(body);
        using var http = _httpFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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
            yield return new StreamError("OpenAI request failed: " + (sendError?.Message ?? "no response"), sendError);
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                yield return new StreamError($"OpenAI HTTP {(int)response.StatusCode}: {err}", null);
                yield break;
            }

            await foreach (var evt in ParseSseAsync(response, cancellationToken).ConfigureAwait(false))
                yield return evt;
        }
    }

    private static string MapRole(ChatRole role) => role switch
    {
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.System => "system",
        ChatRole.Thinking => "assistant",
        _ => "user"
    };

    /// <summary>
    /// Returns a plain string for text-only messages or an array of content parts
    /// (image_url + text) for vision requests.
    /// </summary>
    private static object BuildMessageContent(ChatMessage m)
    {
        if (m.Images is not { Count: > 0 })
            return m.Content;

        // Vision request: one image_url block per image, followed by the text block.
        var parts = new List<object>(m.Images.Count + 1);
        foreach (var img in m.Images)
        {
            parts.Add(new
            {
                type = "image_url",
                image_url = new { url = $"data:{img.MimeType};base64,{img.Base64Data}" }
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

        int? promptTokens = null;
        int? completionTokens = null;
        string? stopReason = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (string.IsNullOrEmpty(data)) continue;
            if (data == "[DONE]")
            {
                yield return new StreamCompleted(promptTokens, completionTokens, stopReason);
                yield break;
            }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); } catch { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.String)
                    {
                        var text = content.GetString();
                        if (!string.IsNullOrEmpty(text))
                            yield return new ContentChunk(text!);
                    }
                    if (first.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                        stopReason = fr.GetString();
                }
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var ct)) completionTokens = ct.GetInt32();
                }
            }
        }

        yield return new StreamCompleted(promptTokens, completionTokens, stopReason);
    }
}
