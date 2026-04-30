using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VirtmaAi.Services.Graphify;

/// <summary>
/// Minimal JSON-RPC 2.0 over stdio client for MCP servers. Graphify's MCP server is launched as
/// a subprocess; requests are written as line-delimited JSON to stdin, responses read from stdout.
/// </summary>
public sealed class McpStdioClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly ILogger<McpStdioClient> _logger;
    private Process? _process;
    private int _nextId = 1;
    private readonly object _lock = new();

    public McpStdioClient(ILogger<McpStdioClient> logger)
    {
        _logger = logger;
    }

    public bool IsRunning => _process is { HasExited: false };

    public void Start(string exe, string args)
    {
        Stop();
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        _process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start MCP server");
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken ct)
    {
        if (_process is null || _process.HasExited)
            throw new InvalidOperationException("MCP server not running");

        int id;
        lock (_lock) { id = _nextId++; }

        var envelope = new { jsonrpc = "2.0", id, method, @params = parameters };
        var line = JsonSerializer.Serialize(envelope);
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
        await _process.StandardInput.FlushAsync(ct);

        string? responseLine;
        while ((responseLine = await _process.StandardOutput.ReadLineAsync(ct)) is not null)
        {
            using var doc = JsonDocument.Parse(responseLine);
            if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.GetInt32() == id)
            {
                if (doc.RootElement.TryGetProperty("error", out var err))
                    throw new InvalidOperationException("MCP error: " + err.ToString());
                if (doc.RootElement.TryGetProperty("result", out var result))
                    return result.Clone();
            }
        }
        throw new InvalidOperationException("MCP server closed unexpectedly");
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false }) { _process.Kill(entireProcessTree: true); _process.WaitForExit(2000); }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "stop MCP"); }
        _process?.Dispose();
        _process = null;
    }

    public void Dispose() => Stop();
}
