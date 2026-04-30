using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Data;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.Skills;

public sealed class SkillRegistry : ISkillRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    // Captures the full content of any fenced block that could contain a skill definition.
    // The old pattern embedded a non-greedy \{…\} which stopped at the first } (same bug
    // as the vplugin ToolCallRegex).  We now capture the raw block text and use
    // ExtractBalancedJson + a content check to locate the actual JSON.
    private static readonly Regex SkillBlockRegex = new(
        @"```(?:json|vskill)?\s*([\s\S]*?)\s*```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Walks content and returns the first complete balanced JSON object.</summary>
    private static string? ExtractBalancedJson(string content)
    {
        int start = -1;
        for (int i = 0; i < content.Length; i++)
            if (content[i] == '{') { start = i; break; }
        if (start < 0) return null;
        int depth = 0; bool inStr = false, esc = false;
        for (int i = start; i < content.Length; i++)
        {
            char c = content[i];
            if (esc)    { esc = false; continue; }
            if (inStr)  { if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
            if (c == '"') { inStr = true; continue; }
            if (c == '{') depth++;
            else if (c == '}') { if (--depth == 0) return content[start..(i + 1)]; }
        }
        return null;
    }

    private readonly IDatabaseService _db;
    private readonly ISettingsService _settings;
    private readonly ILogger<SkillRegistry> _logger;

    public SkillRegistry(IDatabaseService db, ISettingsService settings, ILogger<SkillRegistry> logger)
    {
        _db = db;
        _settings = settings;
        _logger = logger;
    }

    private static string EnabledKey(Guid id) => $"skill.enabled.{id:N}";

    private void HydrateEnabled(Skill skill)
        => skill.Enabled = _settings.Get<bool>(EnabledKey(skill.Id), defaultValue: true);

    public async Task<IReadOnlyList<Skill>> ListAsync()
    {
        if (_db.Current is null) return Array.Empty<Skill>();
        await using var ctx = _db.CreateContext();
        var list = await ctx.Skills.Include(s => s.ContextFiles).OrderBy(s => s.Name).ToListAsync();
        foreach (var s in list) HydrateEnabled(s);
        return list;
    }

    public async Task<IReadOnlyList<Skill>> ListEnabledAsync()
    {
        var all = await ListAsync();
        return all.Where(s => s.Enabled).ToList();
    }

    public async Task<Skill?> GetAsync(Guid id)
    {
        if (_db.Current is null) return null;
        await using var ctx = _db.CreateContext();
        var s = await ctx.Skills.Include(x => x.ContextFiles).FirstOrDefaultAsync(x => x.Id == id);
        if (s is not null) HydrateEnabled(s);
        return s;
    }

    public Task SetEnabledAsync(Guid id, bool enabled)
    {
        _settings.Set(EnabledKey(id), enabled);
        return Task.CompletedTask;
    }

    public async Task<Skill> CreateAsync(SkillDefinition def)
    {
        await using var ctx = _db.CreateContext();
        var skill = new Skill
        {
            Name = def.Name,
            TriggerDescription = def.TriggerDescription,
            InstructionsMd = def.Instructions
        };
        // Each text reference gets its own row — preserves order and identity instead of jamming
        // them all into a single blob (which previously caused "I added 3 entries but only see 1").
        foreach (var t in def.ContextTexts.Where(s => !string.IsNullOrWhiteSpace(s)))
            skill.ContextFiles.Add(new SkillContextFile { Text = t });
        if (def.ContextTexts.Count == 0 && !string.IsNullOrWhiteSpace(def.ContextText))
            skill.ContextFiles.Add(new SkillContextFile { Text = def.ContextText });
        // File paths: read content into Text so the AI can actually use it; keep FilePath
        // as the display label so the user can see which file the content came from.
        foreach (var path in def.ContextFiles)
        {
            var fileText = await ReadContextFileContentAsync(path).ConfigureAwait(false);
            skill.ContextFiles.Add(new SkillContextFile { FilePath = path, Text = fileText });
        }
        ctx.Skills.Add(skill);
        await ctx.SaveChangesAsync();
        return skill;
    }

    public async Task<Skill> UpdateAsync(Guid id, SkillDefinition def)
    {
        await using var ctx = _db.CreateContext();
        var skill = await ctx.Skills.FirstOrDefaultAsync(s => s.Id == id)
            ?? throw new KeyNotFoundException("skill not found");

        // Wipe and re-add context files via the DbSet directly. The previous approach used
        // navigation-collection mutation (RemoveRange + Clear + Add) which the change tracker
        // sometimes mishandled — the parent UPDATE then reported "0 rows affected" on save
        // ("DbUpdateConcurrencyException") because EF generated a malformed plan.
        var existingFiles = await ctx.SkillContextFiles.Where(c => c.SkillId == id).ToListAsync();
        if (existingFiles.Count > 0)
            ctx.SkillContextFiles.RemoveRange(existingFiles);

        skill.Name = def.Name;
        skill.TriggerDescription = def.TriggerDescription;
        skill.InstructionsMd = def.Instructions;
        skill.UpdatedAt = DateTime.UtcNow;

        // One row per text reference — see CreateAsync for the rationale. The legacy single-blob
        // ContextText is still honored when no ContextTexts are present (covers older imports).
        foreach (var t in def.ContextTexts.Where(s => !string.IsNullOrWhiteSpace(s)))
            ctx.SkillContextFiles.Add(new SkillContextFile { SkillId = id, Text = t });
        if (def.ContextTexts.Count == 0 && !string.IsNullOrWhiteSpace(def.ContextText))
            ctx.SkillContextFiles.Add(new SkillContextFile { SkillId = id, Text = def.ContextText });
        foreach (var path in def.ContextFiles)
        {
            var fileText = await ReadContextFileContentAsync(path).ConfigureAwait(false);
            ctx.SkillContextFiles.Add(new SkillContextFile { SkillId = id, FilePath = path, Text = fileText });
        }

        try
        {
            await ctx.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Reload the entity from the DB and retry once — covers the case where a stale
            // change-tracker snapshot caused the optimistic check to fail.
            foreach (var entry in ctx.ChangeTracker.Entries())
                await entry.ReloadAsync();
            await ctx.SaveChangesAsync();
        }
        return skill;
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var ctx = _db.CreateContext();
        var skill = await ctx.Skills.FindAsync(id);
        if (skill is null) return;
        ctx.Skills.Remove(skill);
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Reads a file and returns its text content suitable for storing in
    /// <see cref="SkillContextFile.Text"/>.  Supports plain text, markdown, code files,
    /// and PDF (PdfPig text extraction).  Returns an empty string if the file does not
    /// exist or cannot be read.
    /// </summary>
    private static async Task<string?> ReadContextFileContentAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;
        const long maxBytes = 512 * 1024; // 512 KB hard cap — prevents giant files from blowing the prompt
        try
        {
            var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            if (ext == "pdf")
            {
                using var doc = UglyToad.PdfPig.PdfDocument.Open(filePath);
                var sb = new StringBuilder();
                foreach (var page in doc.GetPages())
                    sb.Append(page.Text).Append('\n');
                var full = sb.ToString();
                return full.Length > maxBytes ? full[..(int)maxBytes] + "\n…[truncated]" : full;
            }
            // Text / code / markdown — read as UTF-8.
            var bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            if (bytes.Length > maxBytes)
            {
                var text = Encoding.UTF8.GetString(bytes[..(int)maxBytes]);
                return text + "\n…[truncated]";
            }
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }

    public Task<SkillDefinition> ImportJsonAsync(string json)
    {
        var def = JsonSerializer.Deserialize<SkillDefinition>(json, JsonOpts)
            ?? throw new InvalidDataException("skill json did not parse");
        if (!string.Equals(def.Schema, "vskill/v1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("unsupported skill schema");
        return Task.FromResult(def);
    }

    /// <summary>
    /// Imports a skill from a file on disk.  Supports:
    /// <list type="bullet">
    ///   <item><b>.vskill.json</b> — VirtmaAi native format (strict schema check).</item>
    ///   <item><b>.json</b> — Flexible JSON with common field aliases so Claude exports work
    ///     (name/title, instructions/system_prompt/prompt/body,
    ///     description/trigger_description/trigger).</item>
    ///   <item><b>.md / .txt / any other text</b> — Entire file content becomes the
    ///     <c>Instructions</c> body; filename (without extension) becomes the name.</item>
    /// </list>
    /// </summary>
    public async Task<SkillDefinition> ImportFromFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("file not found", filePath);

        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        var rawText = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);

        // ── Native vskill format ────────────────────────────────────────────────────
        if (ext == "vskill" || (ext == "json" && rawText.Contains("vskill/v1", StringComparison.OrdinalIgnoreCase)))
        {
            return await ImportJsonAsync(rawText).ConfigureAwait(false);
        }

        // ── Generic JSON (Claude / other tools) ────────────────────────────────────
        if (ext == "json")
        {
            try
            {
                using var doc = JsonDocument.Parse(rawText);
                var root = doc.RootElement;
                string GetString(params string[] keys)
                {
                    foreach (var k in keys)
                        if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                            return v.GetString() ?? string.Empty;
                    return string.Empty;
                }
                return new SkillDefinition
                {
                    Name               = GetString("name", "title", "skill_name") is { Length: > 0 } n ? n
                                         : Path.GetFileNameWithoutExtension(filePath),
                    TriggerDescription = GetString("trigger_description", "description", "trigger", "when_to_use"),
                    Instructions       = GetString("instructions", "system_prompt", "prompt", "body", "content")
                };
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("could not parse JSON skill file: " + ex.Message, ex);
            }
        }

        // ── Markdown / plain-text fallback ─────────────────────────────────────────
        // Treat the entire file as the Instructions body; derive name from filename.
        return new SkillDefinition
        {
            Name               = Path.GetFileNameWithoutExtension(filePath),
            TriggerDescription = string.Empty,
            Instructions       = rawText
        };
    }

    public string ExportJson(Skill skill)
    {
        var def = new SkillDefinition
        {
            Name = skill.Name,
            TriggerDescription = skill.TriggerDescription,
            Instructions = skill.InstructionsMd,
            ContextFiles = skill.ContextFiles.Where(c => !string.IsNullOrEmpty(c.FilePath)).Select(c => c.FilePath!).ToList(),
            ContextTexts = skill.ContextFiles.Where(c => !string.IsNullOrEmpty(c.Text)).Select(c => c.Text!).ToList()
        };
        return JsonSerializer.Serialize(def, JsonOpts);
    }

    public bool TryDetectSkillBlock(string message, out string? json)
    {
        json = null;
        if (string.IsNullOrWhiteSpace(message)) return false;
        // Check fenced code blocks first (may contain nested braces, hence balanced scan).
        foreach (Match m in SkillBlockRegex.Matches(message))
        {
            var j = ExtractBalancedJson(m.Groups[1].Value);
            if (j is null) continue;
            if (j.Contains("vskill/v1", StringComparison.OrdinalIgnoreCase))
            { json = j; return true; }
        }
        // Fall back: the whole message is bare JSON.
        var trimmed = message.TrimStart();
        if (trimmed.StartsWith("{") && trimmed.Contains("\"schema\"") && trimmed.Contains("vskill/v1"))
        {
            json = message;
            return true;
        }
        return false;
    }
}
