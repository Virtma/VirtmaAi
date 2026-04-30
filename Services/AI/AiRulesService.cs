using System.Text;
using Microsoft.EntityFrameworkCore;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Data;

namespace VirtmaAi.Services.AI;

public sealed class AiRulesService : IAiRulesService
{
    private readonly IDatabaseService _db;

    public AiRulesService(IDatabaseService db) { _db = db; }

    public async Task<IReadOnlyList<AiRule>> ListAsync(bool enabledOnly = false)
    {
        if (_db.Current is null) return Array.Empty<AiRule>();
        await using var ctx = _db.CreateContext();
        var q = ctx.AiRules.AsQueryable();
        if (enabledOnly) q = q.Where(r => r.Enabled);
        return await q.OrderByDescending(r => r.Priority).ThenBy(r => r.Title).ToListAsync();
    }

    public async Task<AiRule> CreateAsync(AiRule rule)
    {
        await using var ctx = _db.CreateContext();
        rule.CreatedAt = rule.UpdatedAt = DateTime.UtcNow;
        ctx.AiRules.Add(rule);
        await ctx.SaveChangesAsync();
        return rule;
    }

    public async Task<AiRule> UpdateAsync(AiRule rule)
    {
        await using var ctx = _db.CreateContext();
        var existing = await ctx.AiRules.FindAsync(rule.Id)
            ?? throw new KeyNotFoundException("rule not found");
        existing.Title = rule.Title;
        existing.Body = rule.Body;
        existing.Priority = rule.Priority;
        existing.Enabled = rule.Enabled;
        existing.UpdatedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var ctx = _db.CreateContext();
        var existing = await ctx.AiRules.FindAsync(id);
        if (existing is null) return;
        ctx.AiRules.Remove(existing);
        await ctx.SaveChangesAsync();
    }

    public async Task SetEnabledAsync(Guid id, bool enabled)
    {
        await using var ctx = _db.CreateContext();
        var existing = await ctx.AiRules.FindAsync(id);
        if (existing is null) return;
        existing.Enabled = enabled;
        existing.UpdatedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
    }

    public async Task<string?> BuildSystemPromptBlockAsync()
    {
        var rules = await ListAsync(enabledOnly: true);
        if (rules.Count == 0) return null;
        var sb = new StringBuilder();
        sb.AppendLine("# Global Rules");
        sb.AppendLine();
        sb.AppendLine("These rules apply to every operation you perform, regardless of what the user asks. " +
                      "If any rule conflicts with a user request, follow the rule and explain why.");
        sb.AppendLine();
        foreach (var r in rules)
        {
            if (string.IsNullOrWhiteSpace(r.Title) && string.IsNullOrWhiteSpace(r.Body)) continue;
            if (!string.IsNullOrWhiteSpace(r.Title)) sb.Append("- **").Append(r.Title).Append("** — ");
            else sb.Append("- ");
            sb.AppendLine((r.Body ?? string.Empty).Trim());
        }
        return sb.ToString();
    }
}
