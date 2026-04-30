using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.AI.Providers;
using VirtmaAi.Services.Settings;

#pragma warning disable CA1416 // Process.Start is desktop-only; this plugin is a no-op on mobile (ffmpeg/ffprobe unavailable)

namespace VirtmaAi.Services.Plugins.BuiltIn;

/// <summary>
/// Video Analysis plugin — extracts evenly-spaced frames from a video file using
/// <c>ffmpeg</c> / <c>ffprobe</c> (must be on PATH), then sends them to GPT-4o vision
/// for analysis.
///
/// <para>Input JSON shape:</para>
/// <code>
/// {
///   "file":   "/path/to/video.mp4",
///   "frames": 4,                              // optional 1–10, default 4
///   "prompt": "Describe what is happening"    // optional analysis question
/// }
/// </code>
///
/// <para>Requirements:
/// <list type="bullet">
///   <item>ffmpeg and ffprobe on PATH (any modern version).</item>
///   <item>OpenAI API key configured in Settings → API Keys (uses GPT-4o vision).</item>
/// </list>
/// </para>
/// </summary>
public sealed class VideoAnalyzePlugin : IBuiltInPlugin
{
    private const string VisionEndpoint = "https://api.openai.com/v1/chat/completions";
    private const string VisionModel = "gpt-4o";
    private const int DefaultFrames = 4;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingsService _settings;
    private readonly ILogger<VideoAnalyzePlugin> _logger;

    public VideoAnalyzePlugin(
        IHttpClientFactory httpFactory,
        ISettingsService settings,
        ILogger<VideoAnalyzePlugin> logger)
    {
        _httpFactory = httpFactory;
        _settings = settings;
        _logger = logger;
    }

    public string Name => "video-analyze";
    public string Description => "Analyzes video content by extracting frames (requires ffmpeg on PATH) and sending to GPT-4o vision.";

    public async Task<PluginInvocationResult> InvokeAsync(string input, CancellationToken ct)
    {
        VideoAnalyzeCommand? cmd;
        try { cmd = JsonSerializer.Deserialize<VideoAnalyzeCommand>(input, JsonOpts); }
        catch (Exception ex) { return new PluginInvocationResult(false, string.Empty, "invalid JSON: " + ex.Message); }
        if (cmd is null || string.IsNullOrWhiteSpace(cmd.File))
            return new PluginInvocationResult(false, string.Empty, "video-analyze requires 'file' path");
        if (!File.Exists(cmd.File))
            return new PluginInvocationResult(false, string.Empty, "file not found: " + cmd.File);

        var apiKey = await _settings.GetSecretAsync(OpenAiProvider.ApiKeySecretName).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
            return new PluginInvocationResult(false, string.Empty,
                "OpenAI API key not configured — add it in Settings → API Keys");

        var frameCount = Math.Clamp(cmd.Frames ?? DefaultFrames, 1, 10);
        var prompt = string.IsNullOrWhiteSpace(cmd.Prompt)
            ? "Describe what is happening in this video, frame by frame."
            : cmd.Prompt;

        var tempDir = Path.Combine(Path.GetTempPath(), $"virtmaai_vid_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // ── 1. Get video duration via ffprobe ─────────────────────────────────
            var duration = await GetDurationAsync(cmd.File, ct).ConfigureAwait(false);
            if (duration <= 0)
                return new PluginInvocationResult(false, string.Empty,
                    "could not determine video duration — ensure ffprobe is installed and on PATH");

            // ── 2. Extract evenly-spaced frames ───────────────────────────────────
            var framePaths = new List<string>();
            for (int i = 0; i < frameCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                // Sample at the midpoint of each equal-width segment for better coverage.
                var ts = duration * (i + 0.5) / frameCount;
                var outFile = Path.Combine(tempDir, $"frame_{i:D2}.png");
                await ExtractFrameAsync(cmd.File, ts, outFile, ct).ConfigureAwait(false);
                if (File.Exists(outFile)) framePaths.Add(outFile);
            }

            if (framePaths.Count == 0)
                return new PluginInvocationResult(false, string.Empty,
                    "no frames extracted — ensure ffmpeg is installed and on PATH");

            _logger.LogInformation("Extracted {Count}/{Requested} frames from {File}",
                framePaths.Count, frameCount, cmd.File);

            // ── 3. Send frames to GPT-4o vision ───────────────────────────────────
            var analysis = await AnalyzeFramesAsync(framePaths, prompt, apiKey, ct).ConfigureAwait(false);
            return new PluginInvocationResult(true, analysis);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "video-analyze failed");
            return new PluginInvocationResult(false, string.Empty, ex.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // FFmpeg helpers
    // ──────────────────────────────────────────────────────────────────────────────

    private static async Task<double> GetDurationAsync(string filePath, CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v error -show_entries format=duration -of csv=p=0 \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute  = false,
                CreateNoWindow   = true
            }
        };
        proc.Start();
        var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return double.TryParse(output.Trim(),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var d) ? d : 0;
    }

    private static async Task ExtractFrameAsync(string videoPath, double timestamp, string outPath, CancellationToken ct)
    {
        // -ss before -i = fast seek; -frames:v 1 = single frame; -q:v 2 = high quality JPEG-equivalent
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -ss {timestamp.ToString("F3", CultureInfo.InvariantCulture)} " +
                            $"-i \"{videoPath}\" -frames:v 1 -q:v 2 \"{outPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute  = false,
                CreateNoWindow   = true
            }
        };
        proc.Start();
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Vision API
    // ──────────────────────────────────────────────────────────────────────────────

    private async Task<string> AnalyzeFramesAsync(
        IReadOnlyList<string> framePaths,
        string prompt,
        string apiKey,
        CancellationToken ct)
    {
        // Build the content array: one image_url block per frame, followed by the text prompt.
        var contentParts = new List<object>(framePaths.Count + 1);
        for (int i = 0; i < framePaths.Count; i++)
        {
            var bytes = await File.ReadAllBytesAsync(framePaths[i], ct).ConfigureAwait(false);
            var b64 = Convert.ToBase64String(bytes);
            contentParts.Add(new
            {
                type = "image_url",
                image_url = new { url = $"data:image/png;base64,{b64}", detail = "low" }
            });
        }
        contentParts.Add(new { type = "text", text = $"[{FrameCountLabel(framePaths.Count)} extracted at even intervals]\n\n{prompt}" });

        var requestBody = JsonSerializer.Serialize(new
        {
            model = VisionModel,
            messages = new[]
            {
                new { role = "user", content = contentParts }
            },
            max_tokens = 1024
        });

        using var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        http.Timeout = TimeSpan.FromMinutes(2);

        using var req = new HttpRequestMessage(HttpMethod.Post, VisionEndpoint)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };

        var response = await http.SendAsync(req, ct).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Vision API returned {Status}: {Body}", (int)response.StatusCode, responseText);
            return $"Vision API error {(int)response.StatusCode}: {responseText}";
        }

        using var doc = JsonDocument.Parse(responseText);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? responseText;
    }

    private static string FrameCountLabel(int count) =>
        count == 1 ? "1 frame" : $"{count} frames";

    // ──────────────────────────────────────────────────────────────────────────────
    // Command shape
    // ──────────────────────────────────────────────────────────────────────────────

    private sealed class VideoAnalyzeCommand
    {
        public string? File { get; set; }
        /// <summary>Number of frames to extract (1–10). Default: 4.</summary>
        public int? Frames { get; set; }
        /// <summary>Analysis prompt sent to the vision model alongside the frames.</summary>
        public string? Prompt { get; set; }
    }
}
