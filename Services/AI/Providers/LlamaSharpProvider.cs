// LLamaSharp is only available on Windows desktop targets.
// The #if guard matches the conditional PackageReference in VirtmaAi.csproj.
#if WINDOWS

using System.Runtime.CompilerServices;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;

// Explicit global aliases to avoid ambiguity with VirtmaAi.Services.System.*
using SysIO   = global::System.IO;
using SysText = global::System.Text;

namespace VirtmaAi.Services.AI.Providers;

/// <summary>
/// An <see cref="IAiProvider"/> that loads GGUF model files directly via LLamaSharp
/// (llama.cpp .NET binding).  No Ollama installation is required — point at any
/// <c>.gguf</c> file on disk and start chatting.
///
/// <para>
/// Key design decisions:
/// <list type="bullet">
///   <item><description><see cref="LLamaWeights"/> are cached by file-path (loading a
///     GGUF is expensive — can take tens of seconds for large models).</description></item>
///   <item><description><see cref="StatelessExecutor"/> is created fresh per
///     <see cref="StreamAsync"/> call so each request gets a clean context — no
///     conversation state leaks across calls.  Only the weights are reused.</description></item>
///   <item><description>Thinking blocks emitted as <c>&lt;think&gt;…&lt;/think&gt;</c>
///     tags (DeepSeek-R1, Qwen3 …) are split by <see cref="ThinkTagSplitter"/> into
///     <see cref="ThinkingChunk"/> events, which the UI collapses into the expandable
///     thinking panel.</description></item>
///   <item><description>Prompt format defaults to ChatML (Qwen, Phi, Mistral …).
///     Llama-3 chat format is auto-selected when the model file-name contains "llama-3"
///     or "llama3".</description></item>
/// </list>
/// </para>
///
/// <para><b>GPU offload:</b> currently forced to CPU (<c>GpuLayerCount = 0</c>).
/// GPU support is wired up in Phase 3 when <c>HardwareProbe</c> can query VRAM.
/// </para>
/// </summary>
public sealed class LlamaSharpProvider : IAiProvider, IDisposable
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public string Id          => "llamasharp";
    public string DisplayName => "Local GGUF (direct)";
    public bool SupportsThinking => true; // via ThinkTagSplitter

    // ── Fields ────────────────────────────────────────────────────────────────
    private readonly ILogger<LlamaSharpProvider> _logger;

    // Weight cache: loading weights is the expensive step (allocates native memory).
    private readonly Dictionary<string, LLamaWeights> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    // ── Constructor ───────────────────────────────────────────────────────────
    public LlamaSharpProvider(ILogger<LlamaSharpProvider> logger)
    {
        _logger = logger;
    }

    // ── IAiProvider ───────────────────────────────────────────────────────────

    public async IAsyncEnumerable<ChatEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // For LlamaSharp the ModelId is the absolute path to the .gguf file.
        var modelPath = request.ModelId;

        if (string.IsNullOrWhiteSpace(modelPath) || !SysIO.File.Exists(modelPath))
        {
            yield return new StreamError(
                $"GGUF model file not found: \"{modelPath}\". " +
                "Add a .gguf file to your models directory and refresh the model list.", null);
            yield break;
        }

        // ── Pre-stream setup: load weights and build executor ─────────────────
        // This block does NOT yield so it can use try/catch freely.
        LLamaWeights? weights = null;
        string? setupError = null;

        try
        {
            weights = await LoadWeightsAsync(modelPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load GGUF model: {Path}", modelPath);
            setupError = $"Model load failed: {ex.Message}";
        }

        if (setupError is not null || weights is null)
        {
            yield return new StreamError(setupError ?? "Unknown load error.", null);
            yield break;
        }

        var contextParams = new ModelParams(modelPath)
        {
            ContextSize   = 4096,   // ~4K token window; increase for larger conversations
            GpuLayerCount = 0,      // CPU only — Phase 3 adds hardware-probe GPU offload
            BatchSize     = 512,
        };

        var antiPrompts = BuildAntiPrompts(modelPath, request.StopSequences);
        var inferParams = new InferenceParams
        {
            MaxTokens    = request.MaxTokens ?? -1,   // -1 = stream until stop sequence
            AntiPrompts  = antiPrompts,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = request.Temperature,
            }
        };

        var prompt   = BuildPrompt(modelPath, request);
        var splitter = new ThinkTagSplitter();
        var executor = new StatelessExecutor(weights, contextParams);

        // ── Streaming loop ────────────────────────────────────────────────────
        // C# iterators cannot yield inside catch clauses, so we use try/finally
        // only for cleanup and let exceptions propagate to ChatViewModel's handler.
        await foreach (var token in executor
                           .InferAsync(prompt, inferParams, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            // Route through the think-tag splitter so models that emit
            // <think>…</think> blocks have their reasoning split into
            // ThinkingChunk events automatically.
            foreach (var evt in splitter.Push(token))
                yield return evt;
        }

        // Flush any partial tag buffered at end-of-stream.
        foreach (var evt in splitter.Flush())
            yield return evt;

        yield return new StreamCompleted(null, null, "stop");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads weights from disk on first call; returns the cached instance thereafter.
    /// Thread-safe via a <see cref="SemaphoreSlim"/> so concurrent requests don't
    /// trigger multiple loads.
    /// </summary>
    private async Task<LLamaWeights> LoadWeightsAsync(string path, CancellationToken ct)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(path, out cached)) return cached;

            _logger.LogInformation("Loading GGUF model weights: {Path}", path);
            var modelParams = new ModelParams(path) { GpuLayerCount = 0 };
            var weights = LLamaWeights.LoadFromFile(modelParams);
            _cache[path] = weights;
            _logger.LogInformation("GGUF model loaded: {Path}", path);
            return weights;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Formats the full conversation history into a single prompt string.
    /// Uses ChatML by default; switches to Llama-3 chat format for model files
    /// whose name contains "llama-3" or "llama3".
    /// </summary>
    private static string BuildPrompt(string modelPath, ChatRequest request)
    {
        var fileName = SysIO.Path.GetFileNameWithoutExtension(modelPath).ToLowerInvariant();
        return IsLlama3Model(fileName)
            ? BuildLlama3Prompt(request)
            : BuildChatMlPrompt(request);
    }

    private static bool IsLlama3Model(string fileNameLower) =>
        fileNameLower.Contains("llama-3") || fileNameLower.Contains("llama3");

    /// <summary>
    /// ChatML format — compatible with Qwen, Phi-3, Mistral, and many community GGUF files.
    /// </summary>
    private static string BuildChatMlPrompt(ChatRequest request)
    {
        var sb = new SysText.StringBuilder();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            sb.Append("<|im_start|>system\n");
            sb.Append(request.SystemPrompt!.TrimEnd());
            sb.Append("\n<|im_end|>\n");
        }

        foreach (var msg in request.Messages)
        {
            if (msg.Role == ChatRole.Thinking) continue; // do not re-inject thinking
            var role = msg.Role switch
            {
                ChatRole.User      => "user",
                ChatRole.Assistant => "assistant",
                ChatRole.System    => "system",
                _                  => "user"
            };
            sb.Append("<|im_start|>").Append(role).Append('\n');
            sb.Append(msg.Content ?? string.Empty);
            sb.Append("\n<|im_end|>\n");
        }

        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }

    /// <summary>Meta Llama-3 chat format.</summary>
    private static string BuildLlama3Prompt(ChatRequest request)
    {
        var sb = new SysText.StringBuilder();
        sb.Append("<|begin_of_text|>");

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            sb.Append("<|start_header_id|>system<|end_header_id|>\n\n");
            sb.Append(request.SystemPrompt!.TrimEnd());
            sb.Append("<|eot_id|>");
        }

        foreach (var msg in request.Messages)
        {
            if (msg.Role == ChatRole.Thinking) continue;
            var role = msg.Role switch
            {
                ChatRole.User      => "user",
                ChatRole.Assistant => "assistant",
                ChatRole.System    => "system",
                _                  => "user"
            };
            sb.Append("<|start_header_id|>").Append(role).Append("<|end_header_id|>\n\n");
            sb.Append(msg.Content ?? string.Empty);
            sb.Append("<|eot_id|>");
        }

        sb.Append("<|start_header_id|>assistant<|end_header_id|>\n\n");
        return sb.ToString();
    }

    private static List<string> BuildAntiPrompts(string modelPath, IReadOnlyList<string>? extra)
    {
        var fileName = SysIO.Path.GetFileNameWithoutExtension(modelPath).ToLowerInvariant();
        var anti     = new List<string>();

        if (IsLlama3Model(fileName))
        {
            anti.Add("<|eot_id|>");
            anti.Add("<|end_of_text|>");
        }
        else
        {
            anti.Add("<|im_end|>");
            anti.Add("<|endoftext|>");
        }

        if (extra is not null)
            anti.AddRange(extra);

        return anti;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        foreach (var w in _cache.Values)
        {
            try { w.Dispose(); } catch { /* best-effort */ }
        }
        _cache.Clear();
        _loadGate.Dispose();
    }
}

#endif
