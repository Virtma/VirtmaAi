using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VirtmaAi.Services.AI;

public interface ILocalServiceProber
{
    Task<IReadOnlyList<DetectedLocalModel>> ProbeAllAsync(CancellationToken ct = default);
}

public sealed record DetectedLocalModel(string Provider, string Endpoint, string Name);

public sealed record LocalServiceCandidate(string Provider, string BaseUrl, string ListPath, string PayloadShape);

public sealed class LocalServiceProber : ILocalServiceProber
{
    private static readonly LocalServiceCandidate[] DefaultCandidates =
    {
        new("ollama",       "http://127.0.0.1:11434", "/api/tags",   "ollama"),
        new("lmstudio",     "http://127.0.0.1:1234",  "/v1/models",  "openai"),
        new("llamacpp",     "http://127.0.0.1:8080",  "/v1/models",  "openai"),
        new("oobabooga",    "http://127.0.0.1:5000",  "/v1/models",  "openai"),
        new("vllm",         "http://127.0.0.1:8000",  "/v1/models",  "openai"),
        new("jan",          "http://127.0.0.1:1337",  "/v1/models",  "openai"),
        new("openai-compat","http://127.0.0.1:4000",  "/v1/models",  "openai")
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LocalServiceProber> _logger;

    public LocalServiceProber(IHttpClientFactory httpFactory, ILogger<LocalServiceProber> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DetectedLocalModel>> ProbeAllAsync(CancellationToken ct = default)
    {
        var tasks = DefaultCandidates.Select(c => ProbeOneAsync(c, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(x => x).ToList();
    }

    private async Task<IReadOnlyList<DetectedLocalModel>> ProbeOneAsync(LocalServiceCandidate cand, CancellationToken ct)
    {
        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(2);
            var url = cand.BaseUrl + cand.ListPath;
            var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var list = new List<DetectedLocalModel>();

            if (string.Equals(cand.PayloadShape, "ollama", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in arr.EnumerateArray())
                    {
                        var name = m.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(name))
                            list.Add(new DetectedLocalModel(cand.Provider, cand.BaseUrl + "/", name!));
                    }
                }
            }
            else
            {
                if (root.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in arr.EnumerateArray())
                    {
                        var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(id))
                            list.Add(new DetectedLocalModel(cand.Provider, cand.BaseUrl + "/", id!));
                    }
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Probe {Provider} at {Url} failed", cand.Provider, cand.BaseUrl);
            return Array.Empty<DetectedLocalModel>();
        }
    }
}
