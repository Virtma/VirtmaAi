using System.Text.Json;
using Microsoft.Extensions.Logging;
using VirtmaAi.ViewModels.Preview;

namespace VirtmaAi.Services.Plugins.BuiltIn;

/// <summary>
/// Plays media (audio/video/PDF/web/image/text) inside the app's Preview panel rather than
/// handing it off to the OS default browser/player. The AI invokes this plugin instead of
/// desktop-commander.open-url for media, so songs play in the in-app MediaElement, PDFs render
/// in the panel, and the user never leaves the chat window.
/// </summary>
public sealed class MediaPlayerPlugin : IBuiltInPlugin
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly PreviewViewModel _preview;
    private readonly ILogger<MediaPlayerPlugin> _logger;

    public MediaPlayerPlugin(PreviewViewModel preview, ILogger<MediaPlayerPlugin> logger)
    {
        _preview = preview;
        _logger = logger;
    }

    public string Name => "media-player";
    public string Description => "Play audio, video, PDFs, web pages, images, or text inline in the preview panel — never opens an external browser.";

    public async Task<PluginInvocationResult> InvokeAsync(string input, CancellationToken ct)
    {
        MediaCommand? cmd;
        try { cmd = JsonSerializer.Deserialize<MediaCommand>(input, JsonOpts); }
        catch (Exception ex) { return new PluginInvocationResult(false, string.Empty, "invalid command json: " + ex.Message); }
        if (cmd is null) return new PluginInvocationResult(false, string.Empty, "empty command");

        var action = (cmd.Action ?? "open").ToLowerInvariant();
        var target = cmd.Target ?? cmd.Url ?? cmd.Path ?? cmd.Source;

        if (action == "close")
        {
            await Application.Current!.Dispatcher.DispatchAsync(() => _preview.Close());
            return new PluginInvocationResult(true, "preview closed");
        }

        if (string.IsNullOrWhiteSpace(target))
            return new PluginInvocationResult(false, string.Empty, "media-player requires 'target' (URL or local file path)");

        try
        {
            await Application.Current!.Dispatcher.DispatchAsync(async () =>
            {
                await _preview.OpenAsync(target);
            });
            return new PluginInvocationResult(true, $"opened {target} in the preview panel");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "media-player open failed for {Target}", target);
            return new PluginInvocationResult(false, string.Empty, "open failed: " + ex.Message);
        }
    }

    private sealed class MediaCommand
    {
        public string? Action { get; set; }
        public string? Target { get; set; }
        public string? Url { get; set; }
        public string? Path { get; set; }
        public string? Source { get; set; }
    }
}
