using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.AI.Providers;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.Plugins.BuiltIn;

/// <summary>
/// Audio Transcription plugin — sends an audio or video file to OpenAI Whisper
/// (<c>POST /v1/audio/transcriptions</c>) and returns the full transcribed text.
///
/// <para>Input JSON shape:</para>
/// <code>
/// {
///   "file":     "/absolute/or/relative/path/to/audio.mp3",
///   "language": "en"   // optional ISO-639-1 hint; omit for auto-detect
/// }
/// </code>
///
/// <para>Supported formats: mp3, mp4, mpeg, mpga, m4a, wav, webm, ogg, flac.
/// OpenAI Whisper enforces a 25 MB file-size limit.</para>
///
/// <para>Requires the OpenAI API key to be configured in Settings → API Keys.</para>
/// </summary>
public sealed class AudioTranscribePlugin : IBuiltInPlugin
{
    private const string WhisperEndpoint = "https://api.openai.com/v1/audio/transcriptions";
    private const long MaxFileSizeBytes = 25 * 1024 * 1024; // 25 MB — Whisper hard limit

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingsService _settings;
    private readonly ILogger<AudioTranscribePlugin> _logger;

    public AudioTranscribePlugin(
        IHttpClientFactory httpFactory,
        ISettingsService settings,
        ILogger<AudioTranscribePlugin> logger)
    {
        _httpFactory = httpFactory;
        _settings = settings;
        _logger = logger;
    }

    public string Name => "audio-transcribe";
    public string Description => "Transcribes speech from an audio or video file (OpenAI Whisper). Supports mp3, mp4, m4a, wav, webm, ogg, flac.";

    public async Task<PluginInvocationResult> InvokeAsync(string input, CancellationToken ct)
    {
        AudioTranscribeCommand? cmd;
        try { cmd = JsonSerializer.Deserialize<AudioTranscribeCommand>(input, JsonOpts); }
        catch (Exception ex) { return new PluginInvocationResult(false, string.Empty, "invalid JSON: " + ex.Message); }
        if (cmd is null || string.IsNullOrWhiteSpace(cmd.File))
            return new PluginInvocationResult(false, string.Empty, "audio-transcribe requires 'file' path");

        if (!File.Exists(cmd.File))
            return new PluginInvocationResult(false, string.Empty, "file not found: " + cmd.File);

        var fileInfo = new FileInfo(cmd.File);
        if (fileInfo.Length > MaxFileSizeBytes)
            return new PluginInvocationResult(false, string.Empty,
                $"file is {fileInfo.Length / (1024 * 1024.0):F1} MB — OpenAI Whisper enforces a 25 MB limit");

        var apiKey = await _settings.GetSecretAsync(OpenAiProvider.ApiKeySecretName).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
            return new PluginInvocationResult(false, string.Empty,
                "OpenAI API key not configured — add it in Settings → API Keys");

        try
        {
            using var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            http.Timeout = TimeSpan.FromMinutes(5); // large files can take a while to upload + process

            using var form = new MultipartFormDataContent();

            var fileBytes = await File.ReadAllBytesAsync(cmd.File, ct).ConfigureAwait(false);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(cmd.File));
            form.Add(fileContent, "file", Path.GetFileName(cmd.File));
            form.Add(new StringContent("whisper-1"), "model");
            form.Add(new StringContent("text"), "response_format");
            if (!string.IsNullOrWhiteSpace(cmd.Language))
                form.Add(new StringContent(cmd.Language.Trim()), "language");

            _logger.LogInformation("Sending {File} ({Size:N0} bytes) to Whisper", cmd.File, fileInfo.Length);
            var response = await http.PostAsync(WhisperEndpoint, form, ct).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Whisper API returned {Status}: {Body}", (int)response.StatusCode, responseText);
                return new PluginInvocationResult(false, string.Empty,
                    $"Whisper API error {(int)response.StatusCode}: {responseText}");
            }

            // response_format=text → plain text body (no JSON wrapper).
            return new PluginInvocationResult(true, responseText.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "audio-transcribe failed");
            return new PluginInvocationResult(false, string.Empty, ex.Message);
        }
    }

    private static string GetMimeType(string filePath) =>
        Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant() switch
        {
            "mp3"  => "audio/mpeg",
            "mp4"  => "video/mp4",
            "m4a"  => "audio/mp4",
            "mpeg" or "mpga" => "audio/mpeg",
            "wav"  => "audio/wav",
            "webm" => "audio/webm",
            "ogg"  => "audio/ogg",
            "flac" => "audio/flac",
            "mov"  => "video/quicktime",
            "mkv"  => "video/x-matroska",
            _      => "application/octet-stream"
        };

    private sealed class AudioTranscribeCommand
    {
        public string? File { get; set; }
        /// <summary>ISO-639-1 language code (e.g. "en", "fr"). Omit for automatic detection.</summary>
        public string? Language { get; set; }
    }
}
