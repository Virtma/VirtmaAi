using VirtmaAi.Models.Entities;

namespace VirtmaAi.Services.Skills;

public interface ISkillRegistry
{
    Task<IReadOnlyList<Skill>> ListAsync();
    Task<IReadOnlyList<Skill>> ListEnabledAsync();
    Task<Skill?> GetAsync(Guid id);
    Task<Skill> CreateAsync(SkillDefinition def);
    Task<Skill> UpdateAsync(Guid id, SkillDefinition def);
    Task DeleteAsync(Guid id);
    Task SetEnabledAsync(Guid id, bool enabled);

    Task<SkillDefinition> ImportJsonAsync(string json);
    /// <summary>
    /// Imports a skill from a file. Handles .vskill.json (native), flexible .json
    /// (Claude / third-party format), and plain .md / .txt files.
    /// </summary>
    Task<SkillDefinition> ImportFromFileAsync(string filePath);
    string ExportJson(Skill skill);
    bool TryDetectSkillBlock(string message, out string? json);
}
