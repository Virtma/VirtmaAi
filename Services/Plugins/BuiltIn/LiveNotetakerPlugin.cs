using System.Text.Json;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Capture;

namespace VirtmaAi.Services.Plugins.BuiltIn;

/// <summary>
/// Live Notetaker plugin — thin wrapper around <see cref="ILiveNotetaker"/> so agents can start/stop
/// recording, drop timestamped notes, and list prior sessions.
/// </summary>
public sealed class LiveNotetakerPlugin : IBuiltInPlugin
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly ILiveNotetaker _notetaker;
    private readonly ILogger<LiveNotetakerPlugin> _logger;

    public LiveNotetakerPlugin(ILiveNotetaker notetaker, ILogger<LiveNotetakerPlugin> logger)
    {
        _notetaker = notetaker;
        _logger = logger;
    }

    public string Name => "live-notetaker";
    public string Description => "Periodic screen captures + timeline notes";

    public async Task<PluginInvocationResult> InvokeAsync(string input, CancellationToken ct)
    {
        NotetakerCommand? cmd;
        try { cmd = JsonSerializer.Deserialize<NotetakerCommand>(input, JsonOpts); }
        catch (Exception ex) { return new PluginInvocationResult(false, string.Empty, "invalid command json: " + ex.Message); }
        if (cmd is null) return new PluginInvocationResult(false, string.Empty, "empty command");

        try
        {
            return cmd.Action?.ToLowerInvariant() switch
            {
                "start" => new PluginInvocationResult(true, await _notetaker.StartAsync(cmd.Label, TimeSpan.FromSeconds(cmd.IntervalSeconds <= 0 ? 30 : cmd.IntervalSeconds), ct)),
                "stop" => await StopAsync(),
                "capture" => new PluginInvocationResult(true, await _notetaker.CaptureOnceAsync(cmd.Note, ct)),
                "list" => new PluginInvocationResult(true, JsonSerializer.Serialize(_notetaker.ListSessions(), new JsonSerializerOptions { WriteIndented = true })),
                "status" => new PluginInvocationResult(true, JsonSerializer.Serialize(new { recording = _notetaker.IsRecording, session = _notetaker.CurrentSessionDirectory })),
                _ => new PluginInvocationResult(false, string.Empty, "unknown action: " + cmd.Action)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "live-notetaker action failed");
            return new PluginInvocationResult(false, string.Empty, ex.Message);
        }
    }

    private async Task<PluginInvocationResult> StopAsync()
    {
        await _notetaker.StopAsync();
        return new PluginInvocationResult(true, "stopped");
    }

    private sealed class NotetakerCommand
    {
        public string? Action { get; set; }
        public string? Label { get; set; }
        public string? Note { get; set; }
        public int IntervalSeconds { get; set; }
    }
}
