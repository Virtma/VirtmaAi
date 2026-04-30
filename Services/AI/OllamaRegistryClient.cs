using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VirtmaAi.Services.AI;

public interface IOllamaRegistryClient
{
    Task<IReadOnlyList<OllamaModelInfo>> ListInstalledAsync(Uri baseUri, CancellationToken cancellationToken = default);
}

public sealed record OllamaModelInfo(string Name, long SizeBytes, DateTime ModifiedAt);

public sealed class OllamaRegistryClient : IOllamaRegistryClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OllamaRegistryClient> _logger;

    public OllamaRegistryClient(IHttpClientFactory httpFactory, ILogger<OllamaRegistryClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OllamaModelInfo>> ListInstalledAsync(
        Uri baseUri, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            var json = await http.GetStringAsync(new Uri(baseUri, "/api/tags"), cancellationToken)
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var list = new List<OllamaModelInfo>();
            if (doc.RootElement.TryGetProperty("models", out var arr))
            {
                foreach (var m in arr.EnumerateArray())
                {
                    var name = m.GetProperty("name").GetString() ?? "";
                    var size = m.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                    var modified = m.TryGetProperty("modified_at", out var mod) && mod.ValueKind == JsonValueKind.String
                        ? DateTime.Parse(mod.GetString()!)
                        : DateTime.UtcNow;
                    list.Add(new OllamaModelInfo(name, size, modified));
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Ollama registry query failed (server not running?)");
            return Array.Empty<OllamaModelInfo>();
        }
    }
}
