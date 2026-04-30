using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Data;

namespace VirtmaAi.Services.ExternalApi;

public sealed class ExternalApiKeyService : IExternalApiKeyService
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    private readonly IDatabaseService _db;

    public ExternalApiKeyService(IDatabaseService db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ExternalApiKey>> ListAsync()
    {
        if (_db.Current is null) return Array.Empty<ExternalApiKey>();
        await using var ctx = _db.CreateContext();
        return await ctx.ExternalApiKeys.OrderByDescending(k => k.CreatedAt).ToListAsync();
    }

    public async Task<(ExternalApiKey Record, string PlainKey)> IssueAsync(string programName, IEnumerable<string>? scopes = null)
    {
        var plain = GeneratePlainKey();
        var hashed = HashKey(plain);
        var scopesJson = JsonSerializer.Serialize(scopes?.ToArray() ?? Array.Empty<string>(), JsonOpts);

        var record = new ExternalApiKey
        {
            ProgramName = programName,
            HashedKey = hashed,
            Scopes = scopesJson
        };

        await using var ctx = _db.CreateContext();
        ctx.ExternalApiKeys.Add(record);
        await ctx.SaveChangesAsync();
        return (record, plain);
    }

    public async Task RevokeAsync(Guid id)
    {
        await using var ctx = _db.CreateContext();
        var row = await ctx.ExternalApiKeys.FindAsync(id);
        if (row is null) return;
        ctx.ExternalApiKeys.Remove(row);
        await ctx.SaveChangesAsync();
    }

    public async Task<ExternalApiKey?> VerifyAsync(string plainKey)
    {
        if (string.IsNullOrWhiteSpace(plainKey)) return null;
        if (_db.Current is null) return null;
        var hashed = HashKey(plainKey);
        await using var ctx = _db.CreateContext();
        var match = await ctx.ExternalApiKeys.FirstOrDefaultAsync(k => k.HashedKey == hashed);
        if (match is null) return null;
        match.LastUsedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
        return match;
    }

    private static string GeneratePlainKey()
    {
        var buf = RandomNumberGenerator.GetBytes(32);
        return "vai_" + Convert.ToBase64String(buf).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string HashKey(string plain)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(plain));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
