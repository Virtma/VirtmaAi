using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OllamaMessage = OllamaSharp.Models.Chat.Message;
using OllamaChatRequest = OllamaSharp.Models.Chat.ChatRequest;
using OllamaChatRole = OllamaSharp.Models.Chat.ChatRole;

namespace VirtmaAi.Services.AI.Providers;

public sealed class OllamaProvider : IAiProvider
{
    private readonly ILogger<OllamaProvider> _logger;
    private readonly Uri _baseUri;

    public OllamaProvider(ILogger<OllamaProvider> logger, Uri? baseUri = null)
    {
        _logger = logger;
        _baseUri = baseUri ?? new Uri("http://127.0.0.1:11434");
    }

    public string Id => "ollama";
    public string DisplayName => "Ollama (local)";

    // OllamaSharp 5.4+ exposes native thinking via ChatRequest.Think and
    // ChatResponseStream.Message.Thinking — so we get real thinking tokens, not raw tags.
    public bool SupportsThinking => true;

    public async IAsyncEnumerable<ChatEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = new OllamaApiClient(_baseUri, request.ModelId);

        // Build the message list. Thinking-role messages are stored for display but are NOT
        // re-fed into the model — Ollama handles the thinking context internally.
        var messages = new List<OllamaMessage>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new OllamaMessage { Role = OllamaChatRole.System, Content = request.SystemPrompt });
        foreach (var m in request.Messages)
        {
            if (m.Role == ChatRole.Thinking) continue;
            var msg = new OllamaMessage
            {
                Role    = MapRole(m.Role),
                Content = m.Content ?? string.Empty,
            };
            // OllamaSharp accepts images as an array of raw base64 strings (no data-URI prefix).
            if (m.Images is { Count: > 0 })
                msg.Images = m.Images.Select(i => i.Base64Data).ToArray();
            messages.Add(msg);
        }

        var ollamaRequest = new OllamaChatRequest
        {
            Model = request.ModelId,
            Messages = messages,
            Stream = true,
            Think = true,   // Enable native thinking for qwen3, deepseek-r1, phi4-reasoning, etc.
                            // When Think=true, thinking tokens arrive in Message.Thinking (separate
                            // from Message.Content) so they never pollute the response body.
            Options = new OllamaSharp.Models.RequestOptions
            {
                Temperature = (float?)request.Temperature,
                NumPredict = request.MaxTokens,
                Stop = request.StopSequences?.ToArray()
            }
        };

        // ThinkTagSplitter is kept as a belt-and-suspenders fallback: if an older Ollama
        // version or a non-reasoning model still emits raw <think>…</think> tags in the
        // content stream, the splitter will catch them and route them as ThinkingChunks.
        var splitter = new ThinkTagSplitter();

        await foreach (var update in client.ChatAsync(ollamaRequest, cancellationToken)
            .ConfigureAwait(false))
        {
            if (update is null) continue;
            if (cancellationToken.IsCancellationRequested) yield break;

            // ── Native thinking tokens ────────────────────────────────────────────────
            // When Think=true and the model supports it, Ollama routes thinking tokens
            // here instead of into the content — no <think> tags, clean separation.
            var thinkText = update.Message?.Thinking;
            if (!string.IsNullOrEmpty(thinkText))
                yield return new ThinkingChunk(thinkText!);

            // ── Response content ──────────────────────────────────────────────────────
            // Pipe through ThinkTagSplitter as a fallback for models that do NOT honour
            // the native thinking field and still emit raw tags in the content stream.
            var contentText = update.Message?.Content;
            if (!string.IsNullOrEmpty(contentText))
            {
                foreach (var evt in splitter.Push(contentText!))
                    yield return evt;
            }
        }

        // Drain any partial-tag remainder held by the splitter at end of stream.
        foreach (var evt in splitter.Flush())
            yield return evt;

        yield return new StreamCompleted(null, null, "stop");
    }

    private static OllamaChatRole MapRole(ChatRole role) => role switch
    {
        ChatRole.System    => OllamaChatRole.System,
        ChatRole.User      => OllamaChatRole.User,
        ChatRole.Assistant => OllamaChatRole.Assistant,
        _                  => OllamaChatRole.User
    };
}
