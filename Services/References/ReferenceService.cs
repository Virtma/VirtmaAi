using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Data;

namespace VirtmaAi.Services.References;

public sealed class ReferenceService : IReferenceService
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    private readonly IDatabaseService _db;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ReferenceService> _logger;

    public ReferenceService(IDatabaseService db, IHttpClientFactory http, ILogger<ReferenceService> logger)
    {
        _db = db;
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Reference>> ListAsync()
    {
        if (_db.Current is null) return Array.Empty<Reference>();
        await using var ctx = _db.CreateContext();
        return await ctx.References.OrderByDescending(r => r.CreatedAt).ToListAsync();
    }

    public async Task<Reference> CreateAsync(Reference reference)
    {
        await using var ctx = _db.CreateContext();
        ctx.References.Add(reference);
        await ctx.SaveChangesAsync();
        return reference;
    }

    public async Task<Reference> UpdateAsync(Reference reference)
    {
        await using var ctx = _db.CreateContext();
        var existing = await ctx.References.FindAsync(reference.Id)
            ?? throw new KeyNotFoundException("reference not found");
        existing.Title = reference.Title;
        existing.Triggers = reference.Triggers;
        existing.SourceType = reference.SourceType;
        existing.SourceValue = reference.SourceValue;
        existing.AppliesTo = reference.AppliesTo;
        await ctx.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var ctx = _db.CreateContext();
        var row = await ctx.References.FindAsync(id);
        if (row is null) return;
        ctx.References.Remove(row);
        await ctx.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<Reference>> MatchAsync(string userMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return Array.Empty<Reference>();
        var all = await ListAsync();
        if (all.Count == 0) return Array.Empty<Reference>();

        var tokens = Tokenize(userMessage);
        var matches = new List<(Reference R, int Score)>();
        foreach (var r in all)
        {
            var triggerTokens = Tokenize(string.Join(" ", ParseTriggers(r.Triggers)) + " " + r.Title);
            var score = ScoreOverlap(tokens, triggerTokens);
            if (score > 0) matches.Add((r, score));
        }
        return matches.OrderByDescending(m => m.Score).Take(5).Select(m => m.R).ToList();
    }

    public async Task<string?> BuildAugmentationAsync(string userMessage, CancellationToken ct = default)
    {
        var matched = await MatchAsync(userMessage, ct);
        if (matched.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("Relevant references the user has stored:");
        foreach (var r in matched)
        {
            sb.AppendLine();
            sb.Append("### ").AppendLine(r.Title);
            var body = await LoadSourceAsync(r, ct);
            if (!string.IsNullOrWhiteSpace(body)) sb.AppendLine(body);
        }
        return sb.ToString();
    }

    public async Task<Reference> RememberAsync(string title, string text, IEnumerable<string>? triggers = null)
    {
        var triggerList = triggers?.ToList() ?? new List<string>();
        var reference = new Reference
        {
            Title = title,
            Triggers = JsonSerializer.Serialize(triggerList, JsonOpts),
            SourceType = ReferenceSourceType.Text,
            SourceValue = text,
            CreatedBy = ReferenceCreator.Ai
        };
        return await CreateAsync(reference);
    }

    private async Task<string?> LoadSourceAsync(Reference r, CancellationToken ct)
    {
        try
        {
            return r.SourceType switch
            {
                ReferenceSourceType.Text => r.SourceValue,
                ReferenceSourceType.File => File.Exists(r.SourceValue) ? await File.ReadAllTextAsync(r.SourceValue, ct) : null,
                ReferenceSourceType.Url => await FetchUrlAsync(r.SourceValue, ct),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Load reference source {Title}", r.Title);
            return null;
        }
    }

    private async Task<string?> FetchUrlAsync(string url, CancellationToken ct)
    {
        using var http = _http.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var text = await resp.Content.ReadAsStringAsync(ct);
        return text.Length > 8000 ? text[..8000] + "\n…(truncated)" : text;
    }

    private static IEnumerable<string> ParseTriggers(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        List<string>? parsed;
        try { parsed = JsonSerializer.Deserialize<List<string>>(json, JsonOpts); }
        catch { parsed = null; }
        if (parsed is null) yield break;
        foreach (var t in parsed) yield return t;
    }

    private static HashSet<string> Tokenize(string s)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in s.Split(new[] { ' ', ',', '.', ';', ':', '\t', '\n', '\r', '/', '\\', '(', ')', '[', ']', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var w = raw.Trim().ToLowerInvariant();
            if (w.Length >= 3) set.Add(w);
        }
        return set;
    }

    private static int ScoreOverlap(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var small = a.Count <= b.Count ? a : b;
        var large = a.Count <= b.Count ? b : a;
        var score = 0;
        foreach (var w in small) if (large.Contains(w)) score++;
        return score;
    }
}
